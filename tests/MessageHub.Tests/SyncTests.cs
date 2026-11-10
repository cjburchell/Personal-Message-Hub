using System.Runtime.CompilerServices;
using MessageHub.Core;
using MessageHub.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessageHub.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pmh-sync-" + Guid.NewGuid().ToString("N"));
    private SqliteMessageStore Store => new(Path.Combine(_directory, "test.db"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Connector : IIncrementalMessageConnector
    {
        public string Source => "synthetic";
        public bool Fail { get; set; }
        public bool Cancel { get; set; }
        public bool NoCompletion { get; set; }
        public string? ReceivedCursor { get; private set; }
        public DateTimeOffset ReceivedSince { get; private set; }
        public async IAsyncEnumerable<MessageBatch> FetchBatchesAsync(string? cursor, DateTimeOffset initialSince,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedCursor = cursor;
            ReceivedSince = initialSince;
            await Task.CompletedTask;
            yield return new([new("synthetic", "id", "thread", "sender@example.test", [], null, "synthetic evidence", Now, false)]);
            if (Fail) throw new IOException("synthetic-private-error-marker");
            if (Cancel) throw new OperationCanceledException();
            if (!NoCompletion) yield return new([], "new-checkpoint");
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task FailedSyncPreservesCheckpointAndSuccessThenReplayClearsError()
    {
        await Store.InitializeAsync();
        var previous = Now.AddDays(-1);
        await Store.CompleteSyncAsync("synthetic", "old-checkpoint", previous);
        var connector = new Connector { Fail = true };
        var coordinator = new SyncCoordinator(Store, new Clock());
        await Assert.ThrowsAsync<IOException>(() => coordinator.SyncAsync(connector));
        var failed = await Store.GetCheckpointAsync("synthetic");
        Assert.Equal("old-checkpoint", failed.Cursor);
        Assert.Equal(previous, failed.LastSuccessfulSync);
        Assert.NotNull(failed.Error);
        Assert.DoesNotContain("synthetic-private-error-marker", failed.Error);
        Assert.Single(await Store.SearchAsync("evidence"));
        connector.Fail = false;
        Assert.Equal(1, await coordinator.SyncAsync(connector));
        var success = await Store.GetCheckpointAsync("synthetic");
        Assert.Equal("old-checkpoint", connector.ReceivedCursor);
        Assert.Equal(Now.AddDays(-30), connector.ReceivedSince);
        Assert.Equal("new-checkpoint", success.Cursor);
        Assert.Equal(Now, success.LastSuccessfulSync);
        Assert.Null(success.Error);
        Assert.Single(await Store.SearchAsync("evidence"));
    }

    [Fact]
    public async Task FirstFailureAndCancellationNeverClaimSuccessfulSync()
    {
        await Store.InitializeAsync();
        var connector = new Connector { Cancel = true };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new SyncCoordinator(Store).SyncAsync(connector));
        var state = await Store.GetCheckpointAsync("synthetic");
        Assert.Null(state.LastSuccessfulSync);
        Assert.Null(state.Cursor);
        Assert.NotNull(state.Error);
    }

    [Fact]
    public async Task IncompleteStreamCannotAdvanceCheckpoint()
    {
        await Store.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new SyncCoordinator(Store).SyncAsync(new Connector { NoCompletion = true }));
        Assert.Null((await Store.GetCheckpointAsync("synthetic")).Cursor);
    }

    [Fact]
    public async Task SyncLockExcludesOtherProcessesAndReleasesAfterFailure()
    {
        await Store.InitializeAsync();
        using (Store.AcquireSyncLock()) Assert.Throws<IOException>(() => Store.AcquireSyncLock());
        await Assert.ThrowsAsync<IOException>(() => new SyncCoordinator(Store).SyncAsync(new Connector { Fail = true }));
        using var next = Store.AcquireSyncLock();
        Assert.NotNull(next);
    }
}
