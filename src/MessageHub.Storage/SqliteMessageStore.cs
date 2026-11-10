using System.Text.Json;
using MessageHub.Core;
using Microsoft.Data.Sqlite;

namespace MessageHub.Storage;

public sealed partial class SqliteMessageStore(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    private SqliteConnection Open() => new(_connectionString);

    // Serialize syncs across processes without holding a SQLite transaction during network I/O.
    public FileStream AcquireSyncLock() => new(
        new SqliteConnectionStringBuilder(_connectionString).DataSource + ".sync.lock",
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public async Task<SyncCheckpoint> GetCheckpointAsync(string source, CancellationToken ct = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor,last_successful_sync,last_error FROM sync_state WHERE source=$source";
        command.Parameters.AddWithValue("$source", source);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(null, null, null);
        return new(reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task CompleteSyncAsync(string source, string cursor, DateTimeOffset time, CancellationToken ct = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state(source,cursor,last_successful_sync,last_error) VALUES($s,$c,$t,NULL)
            ON CONFLICT(source) DO UPDATE SET cursor=$c,last_successful_sync=$t,last_error=NULL;
            """;
        command.Parameters.AddWithValue("$s", source);
        command.Parameters.AddWithValue("$c", cursor);
        command.Parameters.AddWithValue("$t", time.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordSyncFailureAsync(string source, CancellationToken ct = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state(source,last_error) VALUES($s,'Sync failed; retry or reauthorize locally.')
            ON CONFLICT(source) DO UPDATE SET last_error=excluded.last_error;
            """;
        command.Parameters.AddWithValue("$s", source);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dbPath = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using var connection = Open();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS messages (
              id TEXT PRIMARY KEY,
              source TEXT NOT NULL,
              source_message_id TEXT NOT NULL,
              conversation_id TEXT NOT NULL,
              sender TEXT NOT NULL,
              recipients_json TEXT NOT NULL,
              subject TEXT,
              body TEXT NOT NULL,
              sent_at TEXT NOT NULL,
              is_outgoing INTEGER NOT NULL CHECK(is_outgoing IN (0,1)),
              original_url TEXT,
              UNIQUE(source, source_message_id)
            );
            CREATE INDEX IF NOT EXISTS idx_messages_date ON messages(sent_at);
            CREATE INDEX IF NOT EXISTS idx_messages_conversation ON messages(source, conversation_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts
              USING fts5(sender, subject, body, content='messages', content_rowid='rowid');
            CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
              INSERT INTO messages_fts(rowid, sender, subject, body)
              VALUES(new.rowid, new.sender, new.subject, new.body);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
              INSERT INTO messages_fts(messages_fts, rowid, sender, subject, body)
              VALUES('delete', old.rowid, old.sender, old.subject, old.body);
            END;
            CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
              INSERT INTO messages_fts(messages_fts, rowid, sender, subject, body)
              VALUES('delete', old.rowid, old.sender, old.subject, old.body);
              INSERT INTO messages_fts(rowid, sender, subject, body)
              VALUES(new.rowid, new.sender, new.subject, new.body);
            END;
            CREATE TABLE IF NOT EXISTS sync_state (
              source TEXT PRIMARY KEY,
              last_successful_sync TEXT,
              cursor TEXT,
              last_error TEXT
            );
            CREATE TABLE IF NOT EXISTS action_items (
              id TEXT PRIMARY KEY,
              source TEXT NOT NULL,
              conversation_id TEXT NOT NULL,
              evidence_message_id TEXT NOT NULL,
              summary TEXT NOT NULL,
              due_at TEXT,
              status TEXT NOT NULL DEFAULT 'proposed'
            );
            CREATE TABLE IF NOT EXISTS reports (
              id TEXT PRIMARY KEY,
              created_at TEXT NOT NULL,
              range_start TEXT NOT NULL,
              range_end TEXT NOT NULL,
              markdown TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS report_analysis_cache (
              input_hash TEXT PRIMARY KEY,
              analysis_json TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpsertAsync(UnifiedMessage message, CancellationToken ct = default)
    {
        Validate(message);
        await using var connection = Open();
        await connection.OpenAsync(ct);
        return await UpsertAsync(connection, null, message, ct);
    }

    // A malformed file must never leave a partially imported archive.
    public async Task ImportAsync(IEnumerable<UnifiedMessage> messages, CancellationToken ct = default)
    {
        var items = messages.ToArray();
        foreach (var message in items) Validate(message);
        await using var connection = Open();
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        foreach (var message in items)
            await UpsertAsync(connection, transaction, message, ct);
        await transaction.CommitAsync(ct);
    }

    private static void Validate(UnifiedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.Source) || string.IsNullOrWhiteSpace(message.SourceMessageId)
            || string.IsNullOrWhiteSpace(message.ConversationId) || string.IsNullOrWhiteSpace(message.Sender)
            || message.Body is null || message.Recipients is null
            || message.Recipients.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Messages require source, source message ID, conversation ID, sender, body and valid recipients.");
    }

    private static async Task<bool> UpsertAsync(SqliteConnection connection, SqliteTransaction? transaction,
        UnifiedMessage message, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO messages(id,source,source_message_id,conversation_id,sender,recipients_json,
                                 subject,body,sent_at,is_outgoing,original_url)
            VALUES($id,$source,$sourceId,$thread,$sender,$recipients,$subject,$body,$sentAt,$outgoing,$url)
            ON CONFLICT(source,source_message_id) DO UPDATE SET
              conversation_id=excluded.conversation_id, sender=excluded.sender,
              recipients_json=excluded.recipients_json, subject=excluded.subject,
              body=excluded.body, sent_at=excluded.sent_at, is_outgoing=excluded.is_outgoing,
              original_url=excluded.original_url;
            """;
        cmd.Parameters.AddWithValue("$id", MessageIdentity.For(message));
        cmd.Parameters.AddWithValue("$source", message.Source);
        cmd.Parameters.AddWithValue("$sourceId", message.SourceMessageId);
        cmd.Parameters.AddWithValue("$thread", message.ConversationId);
        cmd.Parameters.AddWithValue("$sender", message.Sender);
        cmd.Parameters.AddWithValue("$recipients", JsonSerializer.Serialize(message.Recipients));
        cmd.Parameters.AddWithValue("$subject", (object?)message.Subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", message.Body);
        cmd.Parameters.AddWithValue("$sentAt", message.SentAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$outgoing", message.IsOutgoing ? 1 : 0);
        cmd.Parameters.AddWithValue("$url", (object?)message.OriginalUrl ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<MessageHit>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        await using var connection = Open();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id,m.source,m.sender,m.subject,m.body,m.sent_at,m.original_url
            FROM messages_fts f JOIN messages m ON m.rowid=f.rowid
            WHERE messages_fts MATCH $query
            ORDER BY bm25(messages_fts) LIMIT $limit;
            """;
        // Quote input as a single FTS phrase, rather than exposing operators to callers.
        cmd.Parameters.AddWithValue("$query", "\"" + query.Replace("\"", "\"\"") + "\"");
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var hits = new List<MessageHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            hits.Add(new MessageHit(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return hits;
    }

    public async Task<IReadOnlyList<UnifiedMessage>> BetweenAsync(DateTimeOffset start, DateTimeOffset end,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT source,source_message_id,conversation_id,sender,recipients_json,subject,
                   body,sent_at,is_outgoing,original_url FROM messages
            WHERE sent_at >= $start AND sent_at < $end ORDER BY sent_at;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToUniversalTime().ToString("O"));
        var messages = new List<UnifiedMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            messages.Add(new UnifiedMessage(reader.GetString(0),reader.GetString(1),reader.GetString(2),
                reader.GetString(3),JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [],
                reader.IsDBNull(5) ? null : reader.GetString(5),reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7)), reader.GetInt32(8) == 1,
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        return messages;
    }

    public async Task SetSyncStateAsync(string source, DateTimeOffset time, CancellationToken ct = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state(source,last_successful_sync,last_error) VALUES($s,$t,NULL)
            ON CONFLICT(source) DO UPDATE SET last_successful_sync=$t,last_error=NULL;
            """;
        cmd.Parameters.AddWithValue("$s",source);
        cmd.Parameters.AddWithValue("$t",time.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SourceStatus>> GetStatusesAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT source,last_successful_sync,last_error FROM sync_state ORDER BY source";
        var result = new List<SourceStatus>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new SourceStatus(reader.GetString(0), reader.IsDBNull(1) ? null :
                DateTimeOffset.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }
}
