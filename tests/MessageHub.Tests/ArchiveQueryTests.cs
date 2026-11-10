using MessageHub.Core;
using MessageHub.Query;
using MessageHub.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessageHub.Tests;

public sealed class ArchiveQueryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pmh-query-" + Guid.NewGuid().ToString("N"));
    private SqliteMessageStore Store => new(Path.Combine(_directory, "test.db"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T13:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static UnifiedMessage Message(string id = "id") => new("example", id, "thread", "sender@example.test", [], "Payment deadline", "Please confirm. Payment is due Friday.", Now.AddDays(-1), false);
    private sealed class Answerer : IQuestionAnswerer
    {
        public int Calls { get; private set; }
        public bool Invalid { get; set; }
        public string Digest { get; set; } = "digest-one";
        public Task<string> GetIdentityAsync(CancellationToken ct = default) => Task.FromResult(Digest);
        public Task<AnswerAnalysis> AnswerAsync(string question, IReadOnlyList<ArchiveEvidence> evidence, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new AnswerAnalysis([new("A payment is due Friday; verify the date.", [Invalid ? "fabricated:id" : evidence[0].Id])], 300, 40)); }
    }
    private sealed class BlockingAnswerer : IQuestionAnswerer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> GetIdentityAsync(CancellationToken ct = default) => Task.FromResult("blocking-model");
        public async Task<AnswerAnalysis> AnswerAsync(string question, IReadOnlyList<ArchiveEvidence> evidence, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
        { Started.TrySetResult(); await Release.Task.WaitAsync(ct); return new([new("Verify deadline.", [evidence[0].Id])]); }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public async Task NaturalDeadlineQueryReturnsBoundedCitedEvidenceAndUnknownCoverage()
    {
        await Store.InitializeAsync(); await Store.ImportAsync([Message(), Message("old") with { SentAt = Now.AddDays(-40) }]);
        using var query = new ArchiveQueryService(Store, timeProvider: new Clock());
        var result = await query.AskAsync("What deadlines are coming up?", 7);
        Assert.Equal("example:id", Assert.Single(result.Evidence).Id);
        Assert.Equal("sender@example.test", result.Evidence[0].Sender);
        Assert.Equal(1, result.Counts.MessageCount); Assert.Empty(result.Statements);
        Assert.Contains(result.Warnings, x => x.Contains("no provider sync"));
        Assert.Equal("Evidence only", result.AnalysisStatus);
    }

    [Fact]
    public async Task NewerSentMessageSuppressesLatestIncomingFlagEvenIfItDoesNotMatchKeywords()
    {
        await Store.InitializeAsync(); await Store.ImportAsync([Message(), Message("reply") with { Body = "Confirmed.", Subject = "Re: meeting", SentAt = Now.AddHours(-1), IsOutgoing = true }]);
        using var query = new ArchiveQueryService(Store, timeProvider: new Clock());
        var evidence = Assert.Single((await query.SearchAsync("deadline", 7)).Evidence);
        Assert.False(evidence.LatestInConversation);
        Assert.False(evidence.IsOutgoing);
    }

    [Fact]
    public async Task LocalAnswerCacheReusesRepeatedQuestionAndInvalidatesOnModelChange()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        var answerer = new Answerer(); using var query = new ArchiveQueryService(Store, answerer, new Clock());
        var first = await query.AskAsync("What is due?", useAi: true);
        Assert.Single(first.Statements); Assert.False(first.Cached);
        var second = await query.AskAsync("What is due?", useAi: true);
        Assert.True(second.Cached); Assert.Equal(0, second.OutputTokens); Assert.Equal(1, answerer.Calls);
        answerer.Digest = "digest-two";
        await query.AskAsync("What is due?", useAi: true); Assert.Equal(2, answerer.Calls);
    }

    [Fact]
    public async Task InvalidAiCitationsFallBackAndEmptyMatchesSkipGeneration()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        var answerer = new Answerer { Invalid = true }; using var query = new ArchiveQueryService(Store, answerer, new Clock());
        var result = await query.AskAsync("deadline", useAi: true);
        Assert.Empty(result.Statements); Assert.Contains("invalid citations", result.AnalysisStatus);
        Assert.DoesNotContain("fabricated", System.Text.Json.JsonSerializer.Serialize(result));
        var empty = await query.AskAsync("unmatchedword", useAi: true);
        Assert.Empty(empty.Evidence); Assert.Equal(1, answerer.Calls);
        Assert.Contains(empty.Warnings, x => x.Contains("does not prove"));
    }

    [Fact]
    public async Task RetrievalIsLiteralAndClippedAndFullMessageReadsDiscloseTruncation()
    {
        await Store.InitializeAsync(); await Store.ImportAsync(Enumerable.Range(0,30).Select(i => Message("id"+i) with { Body = "Deadline? " + new string('\u4E00',11000), ConversationId = "thread"+i }));
        using var query = new ArchiveQueryService(Store, timeProvider: new Clock());
        var result = await query.SearchAsync("deadline OR nonexistent");
        Assert.Equal(30, result.Counts.MessageCount);
        Assert.InRange(result.Evidence.Count,1,10);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result.Evidence).Length<=ArchiveQueryService.MaximumEvidenceBytes);
        Assert.All(result.Evidence, x => Assert.True(x.Truncated));
        var message = await query.GetMessageAsync("example:id0"); Assert.NotNull(message); Assert.True(message.Truncated); Assert.Equal(10000,message.Body.Length);
        Assert.Null(await query.GetMessageAsync("example:id0' OR 1=1 --"));
    }

    [Fact]
    public async Task ConcurrentGenerationIsRejectedAndCancellationReleasesTheGate()
    {
        await Store.InitializeAsync(); await Store.UpsertAsync(Message());
        var answerer = new BlockingAnswerer(); using var query = new ArchiveQueryService(Store, answerer, new Clock());
        using var cancel = new CancellationTokenSource();
        var first = query.AskAsync("deadline", useAi: true, ct: cancel.Token);
        await answerer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<QueryBusyException>(() => query.AskAsync("deadline", useAi: true));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        answerer.Release.SetResult();
        Assert.Single((await query.AskAsync("deadline", useAi: true)).Statements);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task OutOfRangeWindowsAreRejected(int days)
    {
        await Store.InitializeAsync(); using var query = new ArchiveQueryService(Store, timeProvider: new Clock());
        await Assert.ThrowsAsync<ArgumentException>(() => query.SearchAsync("deadline",days));
    }
}
