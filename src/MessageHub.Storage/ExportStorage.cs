using System.Text.Json;
using MessageHub.Core;
using Microsoft.Data.Sqlite;

namespace MessageHub.Storage;

public sealed partial class SqliteMessageStore
{
    public async Task<ExportImportResult> ImportExportAsync(IMessageExportReader reader, bool dryRun = false, CancellationToken ct = default)
    {
        if (dryRun)
        {
            var count = 0;
            await foreach (var message in reader.ReadAsync(ct)) { ValidateExportMessage(reader, message); count++; }
            ValidateExportCompletion(reader, count); ct.ThrowIfCancellationRequested();
            return new(reader.Source, reader.Provider, true, reader.Summary, null);
        }
        using var syncLock = AcquireSyncLock();
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var processed = 0;
        await foreach (var message in reader.ReadAsync(ct))
        {
            ValidateExportMessage(reader, message);
            await UpsertAsync(connection, transaction, message, ct); processed++;
        }
        ValidateExportCompletion(reader, processed); ct.ThrowIfCancellationRequested();
        var id = "export-" + Guid.NewGuid().ToString("N");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO export_imports(id,source,provider,imported_at,summary_json) VALUES($id,$source,$provider,$time,$summary)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$source", reader.Source);
        command.Parameters.AddWithValue("$provider", reader.Provider); command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(reader.Summary));
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
        return new(reader.Source, reader.Provider, false, reader.Summary, id);
    }
    private static void ValidateExportMessage(IMessageExportReader reader, UnifiedMessage message)
    {
        Validate(message);
        if (message.Source != reader.Source) throw new InvalidDataException("Export source mismatch.");
    }
    private static void ValidateExportCompletion(IMessageExportReader reader, int count)
    {
        var summary = reader.Summary;
        if (!summary.Completed || summary.FileCount < 1 || summary.MessageCount != count || summary.RecordCount != count + summary.SkippedRecords
            || string.IsNullOrWhiteSpace(summary.InputFingerprint)) throw new InvalidDataException("Export did not complete consistently.");
    }
    public async Task<IReadOnlyList<ExportImportInfo>> GetLatestExportImportsAsync(CancellationToken ct = default)
    {
        await using var connection = Open(); await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,e.source,e.provider,e.imported_at,e.summary_json FROM export_imports e
            WHERE NOT EXISTS(SELECT 1 FROM export_imports n WHERE n.source=e.source
              AND (n.imported_at>e.imported_at OR (n.imported_at=e.imported_at AND n.id>e.id)))
            ORDER BY e.source;
            """;
        var imports = new List<ExportImportInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) imports.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)),
            JsonSerializer.Deserialize<ExportReadSummary>(reader.GetString(4)) ?? throw new InvalidDataException("Invalid import provenance.")));
        return imports;
    }
}
