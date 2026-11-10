using MessageHub.Core;

namespace MessageHub.Storage;

public sealed class SyncCoordinator(SqliteMessageStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<int> SyncAsync(IIncrementalMessageConnector connector, CancellationToken ct = default)
    {
        using var syncLock = store.AcquireSyncLock();
        var state = await store.GetCheckpointAsync(connector.Source, ct);
        var started = _time.GetUtcNow();
        var count = 0;
        string? cursor = null;
        try
        {
            await foreach (var batch in connector.FetchBatchesAsync(state.Cursor, started.AddDays(-30), ct))
            {
                if (cursor is not null) throw new InvalidDataException("Batch followed a completed checkpoint.");
                if (batch.Messages.Any(x => x.Source != connector.Source))
                    throw new InvalidDataException("Batch source does not match connector.");
                await store.ImportAsync(batch.Messages, ct);
                count += batch.Messages.Count;
                cursor = batch.CompletedCursor;
            }
            if (string.IsNullOrWhiteSpace(cursor)) throw new InvalidDataException("Connector did not complete.");
            await store.CompleteSyncAsync(connector.Source, cursor, started, ct);
            return count;
        }
        catch
        {
            // Preserve the old checkpoint and last success. Already committed batches replay idempotently.
            // Never store provider exception messages, which can contain account or message data.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await store.RecordSyncFailureAsync(connector.Source, timeout.Token); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or OperationCanceledException) { }
            throw;
        }
    }
}
