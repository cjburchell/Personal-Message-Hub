namespace MessageHub.Core;

// Checkpoints are provider-specific opaque values. Only the final batch may carry one.
public sealed record MessageBatch(IReadOnlyList<UnifiedMessage> Messages, string? CompletedCursor = null);
public sealed record SyncCheckpoint(string? Cursor, DateTimeOffset? LastSuccessfulSync, string? Error);

public interface IIncrementalMessageConnector
{
    string Source { get; }
    IAsyncEnumerable<MessageBatch> FetchBatchesAsync(string? cursor, DateTimeOffset initialSince,
        CancellationToken cancellationToken = default);
}
