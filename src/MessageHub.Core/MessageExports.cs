namespace MessageHub.Core;

public sealed record ExportReadSummary(bool Completed, int FileCount, int RecordCount, int MessageCount,
    int SkippedUnsent, int SkippedSystem, int SkippedEmpty, int AttachmentOnlyMessages, int ContentDerivedIds,
    DateTimeOffset? EarliestMessage, DateTimeOffset? LatestMessage, string? InputFingerprint)
{
    public int SkippedRecords => SkippedUnsent + SkippedSystem + SkippedEmpty;
}
public interface IMessageExportReader
{
    string Source { get; }
    string Provider { get; }
    ExportReadSummary Summary { get; }
    IAsyncEnumerable<UnifiedMessage> ReadAsync(CancellationToken cancellationToken = default);
}
public sealed record ExportImportInfo(string Id, string Source, string Provider, DateTimeOffset ImportedAt, ExportReadSummary Summary);
public sealed record ExportImportResult(string Source, string Provider, bool DryRun, ExportReadSummary Summary, string? ImportId);
