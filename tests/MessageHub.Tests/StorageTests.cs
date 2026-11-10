using MessageHub.Core;
using MessageHub.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessageHub.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pmh-test-" + Guid.NewGuid().ToString("N"));
    private SqliteMessageStore Store => new(Path.Combine(_directory, "nested", "test.db"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    private static UnifiedMessage Message(string id = "m1", string source = "example") =>
        new(source, id, "t1", "sender@example.test", ["me@example.test"], "Meeting", "Original body", Now, false);

    public void Dispose()
    {
        // Disposed connections remain pooled; Windows cannot delete their database files until cleared.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task InitializationIsRepeatableAndDataSurvivesReopening()
    {
        await Store.InitializeAsync();
        await Store.UpsertAsync(Message());
        await Store.InitializeAsync();
        Assert.Single(await Store.SearchAsync("Original"));
        Assert.Empty(await Store.GetStatusesAsync());
    }

    [Fact]
    public async Task UpsertDoesNotDuplicateAndSearchFindsUpdatedBody()
    {
        var store = Store;
        await store.InitializeAsync();
        await store.UpsertAsync(Message());
        await store.UpsertAsync(Message() with { Body = "Revised meeting body" });
        Assert.Single(await store.SearchAsync("Revised"));
        Assert.Empty(await store.SearchAsync("Original"));
        Assert.Single(await store.BetweenAsync(Now.AddDays(-1), Now.AddDays(1)));
    }

    [Fact]
    public async Task IdentityIncludesSourceAndEscapesSeparators()
    {
        await Store.InitializeAsync();
        await Store.ImportAsync([Message("b:c", "a"), Message("c", "a:b"), Message("b:c", "other")]);
        Assert.Equal(3, (await Store.SearchAsync("Original")).Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public async Task ImportIsIdempotentAndDoesNotClaimSourceSynchronization()
    {
        await Store.InitializeAsync();
        await Store.ImportAsync([Message(), Message("m2")]);
        await Store.ImportAsync([Message(), Message("m2")]);
        Assert.Equal(2, (await Store.BetweenAsync(Now, Now.AddSeconds(1))).Count);
        Assert.Empty(await Store.GetStatusesAsync());
    }

    [Fact]
    public async Task InvalidImportWritesNothing()
    {
        await Store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ImportAsync([Message(), Message("invalid") with { Sender = null! }]));
        Assert.Empty(await Store.SearchAsync("Original"));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackEntireImportAndSearchIndex()
    {
        await Store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "nested", "test.db")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_test BEFORE INSERT ON messages WHEN new.source_message_id='reject' BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => Store.ImportAsync([Message(), Message("reject")]));
        Assert.Empty(await Store.SearchAsync("Original"));
        Assert.Empty(await Store.BetweenAsync(Now, Now.AddDays(1)));
    }

    [Fact]
    public async Task ReportRangeUsesUtcAndIncludesStartButExcludesEnd()
    {
        await Store.InitializeAsync();
        await Store.ImportAsync([
            Message("before") with { SentAt = Now.AddTicks(-1) },
            Message("start") with { SentAt = Now.ToOffset(TimeSpan.FromHours(-5)) },
            Message("inside") with { SentAt = Now.AddDays(1), IsOutgoing = true, Source = "other" },
            Message("end") with { SentAt = Now.AddDays(7) }
        ]);
        var report = ReportSummaryBuilder.Build(await Store.BetweenAsync(Now, Now.AddDays(7)));
        Assert.Equal(2, report.MessageCount);
        Assert.Equal(1, report.IncomingCount);
        Assert.Equal(1, report.OutgoingCount);
        Assert.Equal(1, report.BySource["OTHER"]);
    }

    [Theory]
    [InlineData("sender", 1)]
    [InlineData("Meeting", 1)]
    [InlineData("Original body", 1)]
    [InlineData("Original OR missing", 0)]
    [InlineData("\"Original\"", 1)]
    [InlineData("", 0)]
    [InlineData("\"", 0)]
    public async Task SearchUsesLiteralPhrases(string query, int expected)
    {
        await Store.InitializeAsync();
        await Store.UpsertAsync(Message());
        Assert.Equal(expected, (await Store.SearchAsync(query)).Count);
    }

    [Fact]
    public async Task SyncStatusIsUpdatedWithoutDuplicates()
    {
        await Store.InitializeAsync();
        await Store.SetSyncStateAsync("demo", Now.AddDays(-1));
        await Store.SetSyncStateAsync("demo", Now);
        var status = Assert.Single(await Store.GetStatusesAsync());
        Assert.Equal("demo", status.Source);
        Assert.Equal(Now, status.LastSuccessfulSync);
        Assert.Null(status.Error);
    }

    [Fact]
    public void EmptySummaryHasNoMessagesOrSources()
    {
        var report = ReportSummaryBuilder.Build([]);
        Assert.Equal(0, report.MessageCount);
        Assert.Equal(0, report.IncomingCount);
        Assert.Equal(0, report.OutgoingCount);
        Assert.Empty(report.BySource);
    }
}
