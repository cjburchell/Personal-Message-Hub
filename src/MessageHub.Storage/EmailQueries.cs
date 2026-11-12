using System.Text.Json;
using MessageHub.Core;

namespace MessageHub.Storage;

public sealed partial class SqliteMessageStore
{
    public async Task<EmailMetadata?> GetEmailMetadataAsync(string id, CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = EmailCommand(db, null, "SELECT rfc_message_id,original_date,date_quality,attachments_json,warnings_json FROM email_data WHERE message_id=$id", ("$id", id));
        string? rfc; string? date; string quality; EmailAttachment[] attachments; string[] warnings;
        await using (var row = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct)) return null;
            rfc = row.IsDBNull(0) ? null : row.GetString(0); date = row.IsDBNull(1) ? null : row.GetString(1); quality = row.GetString(2);
            attachments = JsonSerializer.Deserialize<EmailAttachment[]>(row.GetString(3)) ?? [];
            warnings = JsonSerializer.Deserialize<string[]>(row.GetString(4)) ?? [];
        }
        if (rfc is not null)
        {
            using var conflict = EmailCommand(db, null, "SELECT COUNT(*) FROM email_data e JOIN email_data original ON original.source=e.source WHERE original.message_id=$id AND e.rfc_message_id=original.rfc_message_id AND e.message_id<>$id", ("$id", id));
            if (Convert.ToInt32(await conflict.ExecuteScalarAsync(ct)) > 0) warnings = [.. warnings, "reused_message_id_conflicting_content_thread_may_be_ambiguous"];
        }
        var participants = new List<EmailParticipant>(); var sources = new List<EmailSourceReference>(); var relations = new List<EmailRelation>(); var clipped = false;
        using (var query = EmailCommand(db, null, "SELECT role,address,SUBSTR(name,1,120),LENGTH(name)>120 FROM email_participants WHERE message_id=$id ORDER BY role,address LIMIT 51", ("$id", id)))
        await using (var row = await query.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct)) { participants.Add(new(row.GetString(0), row.GetString(1), row.GetString(2))); clipped |= row.GetBoolean(3); }
        using (var query = EmailCommand(db, null, "SELECT id,SUBSTR(folder,1,200),format,offset,bytes,sha256,LENGTH(folder)>200 FROM email_occurrences WHERE message_id=$id ORDER BY id LIMIT 21", ("$id", id)))
        await using (var row = await query.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct)) { sources.Add(new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetInt64(4), row.GetString(5))); clipped |= row.GetBoolean(6); }
        using (var query = EmailCommand(db, null, """
            SELECT DISTINCT target.message_id,f.kind FROM email_forward_evidence f
              JOIN email_data origin ON origin.message_id=f.message_id JOIN email_data target ON target.source=origin.source
            WHERE f.message_id=$id AND target.message_id<>$id AND (target.fingerprint=f.target_fingerprint
              OR (f.kind='possible_inline_forward' AND target.body_hash=f.body_hash AND target.subject_key=f.subject_key))
            UNION
            SELECT DISTINCT origin.message_id,'forwarded_by:'||f.kind FROM email_forward_evidence f
              JOIN email_data origin ON origin.message_id=f.message_id JOIN email_data target ON target.source=origin.source
            WHERE target.message_id=$id AND origin.message_id<>$id AND (target.fingerprint=f.target_fingerprint
              OR (f.kind='possible_inline_forward' AND target.body_hash=f.body_hash AND target.subject_key=f.subject_key))
            LIMIT 21
            """, ("$id", id)))
        await using (var row = await query.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct)) relations.Add(new(row.GetString(0), row.GetString(1)));
        var truncated = clipped || participants.Count > 50 || sources.Count > 20 || relations.Count > 20 || attachments.Length > 20
            || attachments.Any(x => x.FileName?.Length > 200 || x.ContentId?.Length > 200);
        static string? Clip(string? value) => value is { Length: > 200 } ? value[..200] : value;
        var result = new EmailMetadata(rfc, date, quality, participants.Take(50).ToArray(),
            attachments.Take(20).Select(x => x with { FileName = Clip(x.FileName), ContentId = Clip(x.ContentId) }).ToArray(),
            sources.Take(20).ToArray(), relations.Take(20).ToArray(), warnings, truncated);
        // Bound aggregate metadata, including long names and folders, without losing the truncation signal.
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > 16000)
        {
            result = result with { Truncated = true };
            if (result.Participants.Count > 0) result = result with { Participants = result.Participants.SkipLast(1).ToArray() };
            else if (result.Sources.Count > 0) result = result with { Sources = result.Sources.SkipLast(1).ToArray() };
            else if (result.Attachments.Count > 0) result = result with { Attachments = result.Attachments.SkipLast(1).ToArray() };
            else if (result.Relations.Count > 0) result = result with { Relations = result.Relations.SkipLast(1).ToArray() };
            else { result = result with { OriginalDate = Clip(result.OriginalDate), RfcMessageId = Clip(result.RfcMessageId) }; break; }
        }
        return result;
    }

    public async Task<EmailPage> SearchEmailAsync(string? query, string? source = null, string? folder = null,
        string? contact = null, DateTimeOffset? start = null, DateTimeOffset? end = null,
        int offset = 0, int limit = 20, string? threadId = null, CancellationToken ct = default)
    {
        if (offset is < 0 or > 1000000 || limit is < 1 or > 20 || start >= end || query?.Length > 1000)
            throw new ArgumentException("Invalid email query bounds.");
        await using var db = Open(); await db.OpenAsync(ct);
        if (threadId is not null)
        {
            using var resolve = EmailCommand(db, null, "SELECT target FROM email_thread_aliases WHERE id=$id", ("$id", threadId));
            threadId = await resolve.ExecuteScalarAsync(ct) as string ?? threadId;
        }
        var fts = !string.IsNullOrWhiteSpace(query);
        using var cmd = EmailCommand(db, null, $"""
            SELECT m.id,m.source,m.conversation_id,SUBSTR(m.sender,1,200),SUBSTR(m.subject,1,120),SUBSTR(m.body,1,500),m.sent_at,
              LENGTH(m.sender)>200 OR LENGTH(COALESCE(m.subject,''))>120 OR LENGTH(m.body)>500
            FROM messages m JOIN email_data e ON e.message_id=m.id
            {(fts ? "JOIN messages_fts ON messages_fts.rowid=m.rowid" : "")}
            WHERE ($s IS NULL OR m.source=$s) AND ($start IS NULL OR m.sent_at >= $start) AND ($end IS NULL OR m.sent_at < $end)
              AND ($thread IS NULL OR e.thread_id=$thread)
              AND ($folder IS NULL OR EXISTS(SELECT 1 FROM email_occurrences o WHERE o.message_id=m.id AND o.folder=$folder))
              AND ($contact IS NULL OR EXISTS(SELECT 1 FROM email_participants p WHERE p.message_id=m.id AND p.address=$contact))
              {(fts ? "AND messages_fts MATCH $q" : "")}
            ORDER BY m.sent_at,m.id LIMIT $limit OFFSET $offset
            """, ("$s", source), ("$folder", folder), ("$contact", contact), ("$start", start?.ToUniversalTime().ToString("O")),
            ("$end", end?.ToUniversalTime().ToString("O")), ("$thread", threadId), ("$limit", limit + 1), ("$offset", offset));
        if (fts) cmd.Parameters.AddWithValue("$q", "\"" + query!.Replace("\"", "\"\"") + "\"");
        var results = new List<EmailSearchHit>();
        await using (var row = await cmd.ExecuteReaderAsync(ct))
            while (await row.ReadAsync(ct)) results.Add(new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
                row.IsDBNull(4) ? null : row.GetString(4), row.GetString(5), DateTimeOffset.Parse(row.GetString(6)), row.GetBoolean(7)));
        var more = results.Count > limit; var page = results.Take(limit).ToList();
        while (page.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(page).Length > 24000) { page.RemoveAt(page.Count - 1); more = true; }
        return new(page, more ? offset + page.Count : null,
            ["Local Thunderbird snapshots only; no provider completeness or freshness guarantee. Folder occurrences are retained across moves/compaction; they are not current mailbox state.",
             "Results are ordered by UTC message date then ID. Missing/invalid dates use Unix epoch and carry metadata warnings. Forward links are evidence relationships, not duplicate removal.",
             "Pagination is not a frozen snapshot; rerun from offset zero after an import. Bodies and metadata may be truncated."]);
    }
    public async Task<string?> GetEmailThreadIdAsync(string messageId, CancellationToken ct = default)
    {
        await using var db = Open(); await db.OpenAsync(ct);
        using var cmd = EmailCommand(db, null, "SELECT thread_id FROM email_data WHERE message_id=$id", ("$id", messageId));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
