using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageHub.Core;
using Microsoft.Data.Sqlite;

namespace MessageHub.Storage;

public sealed partial class SqliteMessageStore
{
    private static string EmailHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static SqliteCommand EmailCommand(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static async Task MigrateEmailArchiveAsync(SqliteConnection db, CancellationToken ct)
    {
        using var tx = db.BeginTransaction();
        using var schema = EmailCommand(db, tx, "CREATE TABLE IF NOT EXISTS pmh_schema_migrations(version INTEGER PRIMARY KEY);");
        await schema.ExecuteNonQueryAsync(ct);
        using var version = EmailCommand(db, tx, "SELECT COUNT(*) FROM pmh_schema_migrations WHERE version=1");
        if (Convert.ToInt32(await version.ExecuteScalarAsync(ct)) == 1) { await tx.CommitAsync(ct); return; }
        using var cmd = EmailCommand(db, tx, """
            CREATE TABLE email_threads(id TEXT PRIMARY KEY,source TEXT NOT NULL);
            CREATE TABLE email_thread_keys(source TEXT NOT NULL,key TEXT NOT NULL,thread_id TEXT NOT NULL,PRIMARY KEY(source,key));
            CREATE INDEX email_thread_keys_thread ON email_thread_keys(thread_id);
            CREATE TABLE email_thread_aliases(id TEXT PRIMARY KEY,target TEXT NOT NULL);
            CREATE TABLE email_data(message_id TEXT PRIMARY KEY,source TEXT NOT NULL,fingerprint TEXT NOT NULL,
              body_hash TEXT NOT NULL,subject_key TEXT NOT NULL,rfc_message_id TEXT,thread_id TEXT NOT NULL,
              original_date TEXT,date_quality TEXT NOT NULL,attachments_json TEXT NOT NULL,warnings_json TEXT NOT NULL,
              UNIQUE(source,fingerprint));
            CREATE INDEX email_data_thread ON email_data(thread_id);
            CREATE INDEX email_data_rfc ON email_data(source,rfc_message_id);
            CREATE INDEX email_data_body ON email_data(source,body_hash,subject_key);
            CREATE TABLE email_contacts(address TEXT PRIMARY KEY);
            CREATE TABLE email_participants(message_id TEXT NOT NULL,role TEXT NOT NULL,address TEXT NOT NULL,name TEXT NOT NULL,
              PRIMARY KEY(message_id,role,address));
            CREATE INDEX email_contact_history ON email_participants(address,message_id);
            CREATE TABLE email_originals(sha256 TEXT PRIMARY KEY,bytes BLOB NOT NULL);
            CREATE TABLE email_roots(id TEXT PRIMARY KEY,source TEXT NOT NULL,path TEXT NOT NULL);
            CREATE TABLE email_occurrences(id TEXT PRIMARY KEY,message_id TEXT,source TEXT NOT NULL,root_id TEXT NOT NULL,
              relative_path TEXT NOT NULL,folder TEXT NOT NULL,format TEXT NOT NULL,offset INTEGER NOT NULL,
              bytes INTEGER NOT NULL,sha256 TEXT NOT NULL,issue TEXT);
            CREATE INDEX email_occurrences_message ON email_occurrences(message_id);
            CREATE TABLE email_checkpoints(root_id TEXT NOT NULL,relative_path TEXT NOT NULL,snapshot_bytes INTEGER NOT NULL,
              snapshot_hash TEXT NOT NULL,offset INTEGER NOT NULL,complete INTEGER NOT NULL,issues INTEGER NOT NULL,PRIMARY KEY(root_id,relative_path));
            CREATE TABLE email_file_issues(root_id TEXT NOT NULL,relative_path TEXT NOT NULL,code TEXT NOT NULL,offset INTEGER NOT NULL,
              PRIMARY KEY(root_id,relative_path));
            CREATE TABLE email_import_status(source TEXT PRIMARY KEY,imported_at TEXT NOT NULL,files INTEGER NOT NULL,
              processed INTEGER NOT NULL,issues INTEGER NOT NULL,complete INTEGER NOT NULL);
            CREATE TABLE email_forward_evidence(message_id TEXT NOT NULL,kind TEXT NOT NULL,target_fingerprint TEXT,body_hash TEXT,subject_key TEXT);
            CREATE INDEX email_forward_message ON email_forward_evidence(message_id);
            INSERT INTO pmh_schema_migrations(version) VALUES(1);
            """);
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<EmailCheckpoint?> GetEmailCheckpointAsync(string root, string path, CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = EmailCommand(db, null, "SELECT snapshot_bytes,snapshot_hash,offset,complete,issues FROM email_checkpoints WHERE root_id=$r AND relative_path=$p", ("$r", root), ("$p", path));
        await using var row = await cmd.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? new(row.GetInt64(0), row.GetString(1), row.GetInt64(2), row.GetBoolean(3), row.GetInt32(4)) : null;
    }
    public async Task SetEmailFileIssueAsync(string root, string path, long offset, bool failed, CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = EmailCommand(db, null, failed ? "INSERT INTO email_file_issues VALUES($r,$p,'unreadable_or_changed_file',$o) ON CONFLICT(root_id,relative_path) DO UPDATE SET offset=$o"
            : "DELETE FROM email_file_issues WHERE root_id=$r AND relative_path=$p", ("$r", root), ("$p", path), ("$o", offset));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task SetEmailImportStatusAsync(EmailImportStatus status, CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = EmailCommand(db, null, """
            INSERT INTO email_import_status VALUES($s,$t,$f,$p,$i,$c)
            ON CONFLICT(source) DO UPDATE SET imported_at=$t,files=$f,processed=$p,issues=$i,complete=$c
            """, ("$s", status.Source), ("$t", status.ImportedAt.ToUniversalTime().ToString("O")), ("$f", status.Files),
            ("$p", status.Processed), ("$i", status.Issues), ("$c", status.Complete));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<EmailImportStatus>> GetEmailImportStatusesAsync(CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = EmailCommand(db, null, "SELECT source,imported_at,files,processed,issues,complete FROM email_import_status ORDER BY source");
        await using var row = await cmd.ExecuteReaderAsync(ct); var result = new List<EmailImportStatus>();
        while (await row.ReadAsync(ct)) result.Add(new(row.GetString(0), DateTimeOffset.Parse(row.GetString(1)), row.GetInt32(2), row.GetInt32(3), row.GetInt32(4), row.GetBoolean(5)));
        return result;
    }
    public async Task SaveEmailBatchAsync(string source, string root, string rootPath, EmailInput input,
        EmailCheckpoint checkpoint, IReadOnlyList<LocalEmailRecord> records, CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct); using var tx = db.BeginTransaction();
        using (var cmd = EmailCommand(db, tx, "INSERT INTO email_roots VALUES($r,$s,$p) ON CONFLICT(id) DO NOTHING", ("$r", root), ("$s", source), ("$p", rootPath))) await cmd.ExecuteNonQueryAsync(ct);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested(); string? id = null;
            if (record.Document is { } email)
            {
                if (email.Message.Source != source) throw new InvalidDataException("Email source mismatch.");
                Validate(email.Message);
                var thread = await ResolveEmailThreadAsync(db, tx, email, ct);
                var message = email.Message with { ConversationId = thread }; id = MessageIdentity.For(message);
                await UpsertAsync(db, tx, message, ct);
                using (var cmd = EmailCommand(db, tx, """
                    INSERT INTO email_data VALUES($id,$s,$fp,$bh,$sk,$rfc,$thread,$date,$quality,$a,$w)
                    ON CONFLICT(message_id) DO UPDATE SET thread_id=$thread,attachments_json=$a,warnings_json=$w
                    """, ("$id", id), ("$s", source), ("$fp", email.Fingerprint), ("$bh", email.BodyHash), ("$sk", email.SubjectKey),
                    ("$rfc", email.RfcMessageId), ("$thread", thread), ("$date", email.OriginalDate), ("$quality", email.DateQuality),
                    ("$a", JsonSerializer.Serialize(email.Attachments)), ("$w", JsonSerializer.Serialize(email.Warnings)))) await cmd.ExecuteNonQueryAsync(ct);
                using (var cmd = EmailCommand(db, tx, "DELETE FROM email_participants WHERE message_id=$id; DELETE FROM email_forward_evidence WHERE message_id=$id", ("$id", id))) await cmd.ExecuteNonQueryAsync(ct);
                foreach (var p in email.Participants)
                {
                    using var cmd = EmailCommand(db, tx, "INSERT OR IGNORE INTO email_contacts VALUES($a); INSERT OR IGNORE INTO email_participants VALUES($id,$r,$a,$n)",
                        ("$a", p.Address), ("$id", id), ("$r", p.Role), ("$n", p.Name)); await cmd.ExecuteNonQueryAsync(ct);
                }
                foreach (var f in email.Forwards)
                {
                    using var cmd = EmailCommand(db, tx, "INSERT INTO email_forward_evidence VALUES($id,$k,$fp,$bh,$sk)", ("$id", id), ("$k", f.Kind),
                        ("$fp", f.Fingerprint), ("$bh", f.BodyHash), ("$sk", f.Subject)); await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            var hash = Convert.ToHexString(SHA256.HashData(record.Original)).ToLowerInvariant();
            using (var cmd = EmailCommand(db, tx, "INSERT OR IGNORE INTO email_originals VALUES($h,$b)", ("$h", hash), ("$b", record.Original))) await cmd.ExecuteNonQueryAsync(ct);
            var reference = "email-ref-" + EmailHash(source + "\n" + root + "\n" + input.RelativePath + "\n" + record.Offset + "\n" + hash);
            using (var cmd = EmailCommand(db, tx, "INSERT OR IGNORE INTO email_occurrences VALUES($ref,$id,$s,$root,$p,$folder,$format,$o,$b,$h,$issue)",
                ("$ref", reference), ("$id", id), ("$s", source), ("$root", root), ("$p", input.RelativePath), ("$folder", input.Folder),
                ("$format", input.Format), ("$o", record.Offset), ("$b", record.Original.Length), ("$h", hash), ("$issue", record.Issue))) await cmd.ExecuteNonQueryAsync(ct);
        }
        using (var cmd = EmailCommand(db, tx, """
            INSERT INTO email_checkpoints VALUES($r,$p,$b,$h,$o,$c,$i)
            ON CONFLICT(root_id,relative_path) DO UPDATE SET snapshot_bytes=$b,snapshot_hash=$h,offset=$o,complete=$c,issues=$i
            """, ("$r", root), ("$p", input.RelativePath), ("$b", checkpoint.SnapshotBytes), ("$h", checkpoint.SnapshotHash),
            ("$o", checkpoint.Offset), ("$c", checkpoint.Complete), ("$i", checkpoint.Issues))) await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
    private static async Task<string> ResolveEmailThreadAsync(SqliteConnection db, SqliteTransaction tx, EmailDocument email, CancellationToken ct)
    {
        var keys = email.References.Append(email.RfcMessageId ?? "fingerprint:" + email.Fingerprint).Distinct(StringComparer.Ordinal).ToArray();
        var threads = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            using var cmd = EmailCommand(db, tx, "SELECT thread_id FROM email_thread_keys WHERE source=$s AND key=$k", ("$s", email.Message.Source), ("$k", key));
            if (await cmd.ExecuteScalarAsync(ct) is string thread) threads.Add(thread);
        }
        var chosen = threads.FirstOrDefault() ?? "email-thread-" + EmailHash(email.Message.Source + "\n" + email.Fingerprint);
        using (var cmd = EmailCommand(db, tx, "INSERT OR IGNORE INTO email_threads VALUES($id,$s)", ("$id", chosen), ("$s", email.Message.Source))) await cmd.ExecuteNonQueryAsync(ct);
        foreach (var other in threads.Where(x => x != chosen))
        {
            using var cmd = EmailCommand(db, tx, """
                UPDATE action_items SET conversation_id=$to WHERE evidence_message_id IN(SELECT message_id FROM email_data WHERE thread_id=$from);
                UPDATE messages SET conversation_id=$to WHERE id IN(SELECT message_id FROM email_data WHERE thread_id=$from);
                UPDATE email_data SET thread_id=$to WHERE thread_id=$from;
                UPDATE email_thread_keys SET thread_id=$to WHERE thread_id=$from;
                UPDATE email_thread_aliases SET target=$to WHERE target=$from;
                INSERT INTO email_thread_aliases VALUES($from,$to) ON CONFLICT(id) DO UPDATE SET target=$to;
                """, ("$to", chosen), ("$from", other)); await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var key in keys)
        {
            using var cmd = EmailCommand(db, tx, "INSERT INTO email_thread_keys VALUES($s,$k,$t) ON CONFLICT(source,key) DO UPDATE SET thread_id=$t",
                ("$s", email.Message.Source), ("$k", key), ("$t", chosen)); await cmd.ExecuteNonQueryAsync(ct);
        }
        return chosen;
    }
    // CLI-only export: callers supply an opaque reference, never a stored source path to reopen.
    public async Task<byte[]?> GetEmailOriginalAsync(string reference, CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = EmailCommand(db, null, "SELECT b.bytes FROM email_originals b JOIN email_occurrences o ON o.sha256=b.sha256 WHERE o.id=$id", ("$id", reference));
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }
}
