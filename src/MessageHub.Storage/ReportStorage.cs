using MessageHub.Core;
using Microsoft.Data.Sqlite;

namespace MessageHub.Storage;

public sealed partial class SqliteMessageStore
{
    public async Task<MessageHit?> GetMessageAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,source,sender,subject,body,sent_at,original_url FROM messages WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new MessageHit(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6)) : null;
    }

    public FileStream AcquireReportLock() => new(
        new SqliteConnectionStringBuilder(_connectionString).DataSource + ".report.lock",
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public async Task<IReadOnlyList<string>> GetArchivedSourcesAsync(CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT source FROM messages ORDER BY source";
        var sources = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sources.Add(reader.GetString(0));
        return sources;
    }

    public async Task<string?> GetAnalysisCacheAsync(string hash, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT analysis_json FROM report_analysis_cache WHERE input_hash=$hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SaveAnalysisCacheAsync(string hash, string json, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO report_analysis_cache(input_hash,analysis_json) VALUES($hash,$json) ON CONFLICT(input_hash) DO UPDATE SET analysis_json=$json";
        cmd.Parameters.AddWithValue("$hash", hash); cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredReport?> GetReportAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,created_at,range_start,range_end,markdown FROM reports WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReport(reader) : null;
    }

    public async Task<IReadOnlyList<StoredReport>> GetReportsAsync(CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,created_at,range_start,range_end,'' FROM reports ORDER BY created_at DESC LIMIT 50";
        var reports = new List<StoredReport>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) reports.Add(ReadReport(reader));
        return reports;
    }

    private static StoredReport ReadReport(SqliteDataReader reader) => new(reader.GetString(0),
        DateTimeOffset.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2)),
        DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4));

    public async Task SaveReportAsync(StoredReport report, IReadOnlyList<ActionProposal> proposals, CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO reports(id,created_at,range_start,range_end,markdown) VALUES($id,$created,$start,$end,$markdown) ON CONFLICT(id) DO NOTHING";
        cmd.Parameters.AddWithValue("$id", report.Id); cmd.Parameters.AddWithValue("$created", report.CreatedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$start", report.Start.ToUniversalTime().ToString("O")); cmd.Parameters.AddWithValue("$end", report.End.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$markdown", report.Markdown);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return;
        foreach (var proposal in proposals)
        {
            using var action = connection.CreateCommand(); action.Transaction = transaction;
            action.CommandText = """
                INSERT INTO action_items(id,source,conversation_id,evidence_message_id,summary,due_at,status)
                SELECT $id,$source,$thread,$evidence,$summary,$due,'proposed'
                WHERE EXISTS(SELECT 1 FROM messages WHERE id=$evidence AND source=$source AND conversation_id=$thread)
                ON CONFLICT(id) DO UPDATE SET summary=excluded.summary,due_at=excluded.due_at
                WHERE action_items.status='proposed';
                """;
            action.Parameters.AddWithValue("$id", proposal.Id); action.Parameters.AddWithValue("$source", proposal.Source);
            action.Parameters.AddWithValue("$thread", proposal.ConversationId); action.Parameters.AddWithValue("$evidence", proposal.EvidenceId);
            action.Parameters.AddWithValue("$summary", proposal.Summary); action.Parameters.AddWithValue("$due", (object?)proposal.DueDate ?? DBNull.Value);
            await action.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
