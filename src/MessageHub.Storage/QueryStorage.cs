using MessageHub.Core;

namespace MessageHub.Storage;

public sealed partial class SqliteMessageStore
{
    public async Task<ArchiveMessage?> GetBoundedMessageAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,source,SUBSTR(sender,1,200),SUBSTR(subject,1,500),SUBSTR(body,1,10000),sent_at,
                LENGTH(sender)>200 OR LENGTH(COALESCE(subject,''))>500 OR LENGTH(body)>10000
            FROM messages WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), reader.GetInt32(6) == 1) : null;
    }

    public async Task<ArchiveReport?> GetBoundedReportAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,range_start,range_end,SUBSTR(markdown,1,20000),LENGTH(markdown)>20000 FROM reports WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)),
            DateTimeOffset.Parse(reader.GetString(2)), reader.GetString(3), reader.GetInt32(4) == 1) : null;
    }

    public async Task<ReportSummary> CountBetweenAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT source,COUNT(*),SUM(is_outgoing) FROM messages WHERE sent_at >= $start AND sent_at < $end GROUP BY source ORDER BY source";
        cmd.Parameters.AddWithValue("$start", start.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToUniversalTime().ToString("O"));
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0; var outgoing = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { sources.Add(reader.GetString(0), reader.GetInt32(1)); total += reader.GetInt32(1); outgoing += reader.GetInt32(2); }
        return new(total, total - outgoing, outgoing, sources);
    }

    public async Task<IReadOnlyList<ArchiveEvidence>> SearchTermsAsync(IReadOnlyList<string> terms,
        DateTimeOffset start, DateTimeOffset end, int limit = 10, CancellationToken ct = default)
    {
        if (terms.Count is < 1 or > 8 || terms.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 50))
            throw new ArgumentException("Supply one to eight bounded search terms.");
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id,m.source,m.conversation_id,m.sent_at,SUBSTR(COALESCE(m.subject,''),1,120),SUBSTR(m.body,1,500),m.is_outgoing,
                NOT EXISTS(SELECT 1 FROM messages n WHERE n.source=m.source AND n.conversation_id=m.conversation_id
                    AND n.sent_at < $end AND (n.sent_at > m.sent_at OR (n.sent_at=m.sent_at AND n.is_outgoing > m.is_outgoing))),
                LENGTH(m.body)>500 OR LENGTH(COALESCE(m.subject,''))>120 OR LENGTH(m.sender)>200,SUBSTR(m.sender,1,200)
            FROM messages_fts JOIN messages m ON m.rowid=messages_fts.rowid
            WHERE messages_fts MATCH $query AND m.sent_at >= $start AND m.sent_at < $end
            ORDER BY bm25(messages_fts),m.sent_at DESC,m.id LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", string.Join(" OR ", terms.Select(x => "\"" + x.Replace("\"", "\"\"") + "\"")));
        cmd.Parameters.AddWithValue("$start", start.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10));
        var messages = new List<ArchiveEvidence>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) messages.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetInt32(6) == 1,
            reader.GetInt32(7) == 1, reader.GetInt32(8) == 1, reader.GetString(9)));
        return messages;
    }
}
