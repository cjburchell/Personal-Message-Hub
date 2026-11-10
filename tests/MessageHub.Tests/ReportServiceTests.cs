using MessageHub.Core;
using MessageHub.Reporting;
using MessageHub.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessageHub.Tests;

public sealed class ReportServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pmh-report-" + Guid.NewGuid().ToString("N"));
    private SqliteMessageStore Store => new(Path.Combine(_directory, "test.db"));
    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-11-10T13:00:00Z");
    private static UnifiedMessage Message(string id = "id") => new("example", id, "thread", "sender@example.test", [],
        "Invoice", "Please reply. Payment is due 2026-11-14.", End.AddDays(-1), false);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => End; }
    private sealed class Analyzer : IReportAnalyzer
    {
        public int Calls { get; private set; }
        public string Digest { get; set; } = "digest-one";
        public bool Invalid { get; set; }
        public bool Fail { get; set; }
        public bool Cancel { get; set; }
        public Action? OnAnalyze { get; set; }
        public IReadOnlyList<ReportCandidate> Received { get; private set; } = [];
        public Task<string> GetIdentityAsync(CancellationToken ct = default) => Task.FromResult(Digest);
        public Task<ReportAnalysis> AnalyzeAsync(IReadOnlyList<ReportCandidate> candidates, CancellationToken ct = default)
        {
            Calls++; Received = candidates;
            OnAnalyze?.Invoke();
            if (Fail) throw new HttpRequestException("synthetic-private-error-marker");
            if (Cancel) throw new OperationCanceledException(ct);
            return Task.FromResult(new ReportAnalysis([new("action", "Review payment.", [Invalid ? "fabricated:id" : candidates[0].Id])], 400, 50));
        }
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    private async Task<long> Count(string table)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "test.db")}");
        await connection.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table; // Test-owned constant table names only.
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ReportPersistsWithCitationsProposalsAndCacheReusesEvidenceButInvalidatesOnModelOrContentChange()
    {
        await Store.InitializeAsync(); await Store.ImportAsync([Message()]);
        var analyzer = new Analyzer(); var service = new ReportService(Store, analyzer, new Clock());
        var first = await service.BuildAsync(End.AddDays(-7), End);
        Assert.Contains("example:id", first.Markdown);
        Assert.Contains("Review payment.", first.Markdown);
        Assert.Equal(first, await Store.GetReportAsync(first.Id));
        Assert.Equal(3, await Count("action_items"));
        var second = await service.BuildAsync(End.AddDays(-7).AddSeconds(1), End.AddSeconds(1));
        Assert.Contains("cached local analysis", second.Markdown);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(3, await Count("action_items"));
        analyzer.Digest = "digest-two";
        await service.BuildAsync(End.AddDays(-7), End);
        Assert.Equal(2, analyzer.Calls);
        await Store.UpsertAsync(Message() with { Body = "Please reply. A revised payment is due 2026-11-15." });
        await service.BuildAsync(End.AddDays(-7), End);
        Assert.Equal(3, analyzer.Calls);
    }

    [Fact]
    public async Task InvalidCitationsAndAiFailureFallBackWithoutLeakingErrorsOrCaching()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        var analyzer = new Analyzer { Invalid = true };
        var invalid = await new ReportService(Store, analyzer).BuildAsync(End.AddDays(-7), End);
        Assert.Contains("unavailable or invalid output", invalid.Markdown);
        Assert.DoesNotContain("fabricated:id", invalid.Markdown);
        Assert.Equal(0, await Count("report_analysis_cache"));
        analyzer.Invalid = false; analyzer.Fail = true;
        var failed = await new ReportService(Store, analyzer).BuildAsync(End.AddDays(-7), End);
        Assert.DoesNotContain("synthetic-private-error-marker", failed.Markdown);
        Assert.Contains("Total: 1", failed.Markdown);
        Assert.Equal(0, await Count("report_analysis_cache"));
    }

    [Fact]
    public async Task EmptyArchiveSkipsAiAndExplainsUnknownCoverage()
    {
        await Store.InitializeAsync(); var analyzer = new Analyzer();
        var report = await new ReportService(Store, analyzer).BuildAsync(End.AddDays(-7), End);
        Assert.Equal(0, analyzer.Calls);
        Assert.Contains("gmail has no recorded provider sync", report.Markdown);
        Assert.Contains("yahoo has no recorded provider sync", report.Markdown);
        Assert.Contains("does not prove there were no messages", report.Markdown);
    }

    [Fact]
    public async Task CoverageDistinguishesStaleFailedAndImportedOnlySources()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        await Store.CompleteSyncAsync("gmail:synthetic", "cursor", End.AddDays(-2));
        await Store.RecordSyncFailureAsync("gmail:synthetic");
        var report = await new ReportService(Store).BuildAsync(End.AddDays(-7), End);
        Assert.Contains("stale by more than 24 hours", report.Markdown);
        Assert.Contains("latest sync failed", report.Markdown);
        Assert.Contains("example: never successfully synchronized", report.Markdown);
    }

    [Fact]
    public async Task WorkerRestartAndRepeatDoNotRegenerateTheCompletedWindow()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message() with { SentAt = End.AddDays(-2) });
        var analyzer = new Analyzer(); var syncs = 0;
        var schedule = new WeeklySchedule(TimeZoneInfo.Utc);
        var job = new WeeklyReportJob(Store, new ReportService(Store, analyzer), schedule, new Clock(), _ => { syncs++; return Task.CompletedTask; });
        var report = await job.RunDueAsync(); Assert.NotNull(report);
        Assert.Null(await job.RunDueAsync());
        Assert.Null(await new WeeklyReportJob(Store, new ReportService(Store, analyzer), schedule, new Clock()).RunDueAsync());
        Assert.Equal(1, syncs); Assert.Equal(1, analyzer.Calls); Assert.Equal(1, await Count("reports"));
    }

    [Fact]
    public async Task PayloadIsByteBoundedEvenForUnicodeAndLargeIdentifiers()
    {
        await Store.InitializeAsync();
        await Store.ImportAsync(Enumerable.Range(0, 50).Select(i => Message("id" + i) with
        { ConversationId = "thread" + i, Body = "Please reply? " + new string('\u4E00', 600) }));
        var analyzer = new Analyzer(); await new ReportService(Store, analyzer).BuildAsync(End.AddDays(-7), End);
        Assert.InRange(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(analyzer.Received).Length, 1, ReportCandidateSelector.MaximumPayloadBytes);
        Assert.InRange(analyzer.Received.Count, 1, ReportCandidateSelector.MaximumCandidates);
    }

    [Fact]
    public async Task EvidenceAndModelOutputCannotCreateActiveMarkdownOrHtml()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message() with { Subject = "![track](https://example.test/track)<script>bad()</script>" });
        var report = await new ReportService(Store).BuildAsync(End.AddDays(-7), End);
        Assert.DoesNotContain("![track]", report.Markdown);
        Assert.DoesNotContain("<script>", report.Markdown);
    }

    [Fact]
    public async Task CancellationDoesNotSaveAnIncompleteReport()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReportService(Store, new Analyzer { Cancel = true, OnAnalyze = cancellation.Cancel })
            .BuildAsync(End.AddDays(-7), End, ct: cancellation.Token));
        Assert.Equal(0, await Count("reports"));
    }

    [Fact]
    public async Task ReportAndActionProposalsRollBackTogetherWhenPersistenceFails()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "test.db")}"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_proposal BEFORE INSERT ON action_items BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => new ReportService(Store).BuildAsync(End.AddDays(-7), End));
        Assert.Equal(0, await Count("reports")); Assert.Equal(0, await Count("action_items"));
    }

    [Fact]
    public async Task ProposedDatesRefreshWithoutOverwritingReviewedActionState()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        var service = new ReportService(Store);
        await service.BuildAsync(End.AddDays(-7), End);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "test.db")}");
        await connection.OpenAsync(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE action_items SET status='reviewed' WHERE summary LIKE 'reply:%'";
        await command.ExecuteNonQueryAsync();
        await Store.UpsertAsync(Message() with { Body = "Please reply; payment deadline 2026-11-15." });
        await service.BuildAsync(End.AddDays(-7), End);
        command.CommandText = "SELECT due_at FROM action_items WHERE status='proposed' AND summary LIKE 'deadline:%'";
        Assert.Equal("2026-11-15", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT due_at FROM action_items WHERE status='reviewed'";
        Assert.Equal("2026-11-14", await command.ExecuteScalarAsync());
    }
}
