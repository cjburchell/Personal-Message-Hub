using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MessageHub.Connectors;
using MessageHub.Core;
using MimeKit;
using Xunit;

namespace MessageHub.Tests;

public sealed class ConnectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    internal static MimeMessage Mime(string body = "synthetic message")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.test"));
        message.Subject = "Synthetic subject";
        message.MessageId = "synthetic@example.test";
        message.Date = Now;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }
    private static GmailRawMessage Raw(string id, bool sent = false)
    {
        using var stream = new MemoryStream();
        Mime().WriteTo(stream);
        return new(id, "thread", stream.ToArray(), Now, sent);
    }
    private static async Task<List<MessageBatch>> Collect(IIncrementalMessageConnector connector, string? cursor = null)
    {
        var result = new List<MessageBatch>();
        await foreach (var batch in connector.FetchBatchesAsync(cursor, Now.AddDays(-30))) result.Add(batch);
        return result;
    }

    private sealed class GmailFake : IGmailReader
    {
        public List<(ulong Start, string? Token)> HistoryCalls { get; } = [];
        public List<string?> ListCalls { get; } = [];
        public Func<ulong, string?, GmailHistoryPage> History { get; set; } = (_, _) => new([], null, 200);
        public Func<string?, GmailIdPage> List { get; set; } = _ => new([], null);
        public Dictionary<string, GmailRawMessage?> Messages { get; } = [];
        public string Address { get; set; } = "me@example.test";
        public Task<GmailProfile> GetProfileAsync(CancellationToken ct) => Task.FromResult(new GmailProfile(Address, 100));
        public Task<GmailIdPage> ListAsync(DateTimeOffset since, string? pageToken, CancellationToken ct)
        { ListCalls.Add(pageToken); return Task.FromResult(List(pageToken)); }
        public Task<GmailHistoryPage> HistoryAsync(ulong start, string? pageToken, CancellationToken ct)
        { HistoryCalls.Add((start, pageToken)); return Task.FromResult(History(start, pageToken)); }
        public Task<GmailRawMessage?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Messages[id]);
    }

    [Fact]
    public async Task GmailFullSyncPagesAndUsesBaselineCapturedBeforeListing()
    {
        var fake = new GmailFake { List = token => token is null ? new(["one", "gone"], "next") : new(["two"], null) };
        fake.Messages["one"] = Raw("one"); fake.Messages["two"] = Raw("two", true); fake.Messages["gone"] = null;
        var batches = await Collect(new GmailConnector(fake, new("me@example.test", 100)));
        Assert.Equal(new string?[] { null, "next" }, fake.ListCalls);
        Assert.Empty(fake.HistoryCalls);
        Assert.Equal("100", batches[^1].CompletedCursor);
        var messages = batches.SelectMany(x => x.Messages).ToArray();
        Assert.Equal(2, messages.Length);
        Assert.False(messages[0].IsOutgoing); Assert.True(messages[1].IsOutgoing);
        Assert.Equal("thread", messages[0].ConversationId);
        Assert.Equal(Now, messages[0].SentAt);
    }

    [Fact]
    public async Task GmailIncrementalPagesUseOriginalCursorAndFinalHistoryId()
    {
        var fake = new GmailFake { History = (_, token) => token is null ? new(["one", "one"], "next", 201) : new([], null, 205) };
        fake.Messages["one"] = Raw("one");
        var batches = await Collect(new GmailConnector(fake, new("me@example.test", 100)), "90");
        Assert.Empty(fake.ListCalls);
        Assert.Equal(new (ulong, string?)[] { (90, null), (90, "next") }, fake.HistoryCalls);
        Assert.Single(batches.SelectMany(x => x.Messages));
        Assert.Equal("205", batches[^1].CompletedCursor);
    }

    [Fact]
    public async Task GmailExpiredHistoryFallsBackToFullScan()
    {
        var fake = new GmailFake { History = (_, _) => throw new HistoryExpiredException() };
        var batches = await Collect(new GmailConnector(fake, new("me@example.test", 100)), "1");
        Assert.Single(fake.ListCalls);
        Assert.Equal("100", batches[^1].CompletedCursor);
    }

    [Fact]
    public async Task GmailRejectsCorruptCursorAndChangedAccount()
    {
        var fake = new GmailFake();
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new GmailConnector(fake, new("me@example.test", 100)), "bad"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(new GmailConnector(fake, new("other@example.test", 100))));
        Assert.Empty(fake.ListCalls);
    }

    private sealed class YahooFake : IYahooReader
    {
        public uint Validity { get; set; } = 7;
        public List<(uint After, DateTimeOffset? Since)> Searches { get; } = [];
        public Task<IReadOnlyList<string>> GetFolderNamesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["Inbox", "Sent"]);
        public Task<MailFolderState> OpenReadOnlyAsync(string folder, CancellationToken ct) => Task.FromResult(new MailFolderState(folder, Validity, 3, folder == "Sent"));
        public Task<IReadOnlyList<uint>> SearchAsync(uint afterUid, DateTimeOffset? initialSince, CancellationToken ct)
        { Searches.Add((afterUid, initialSince)); return Task.FromResult<IReadOnlyList<uint>>([1, 2, 3, 4]); }
        public Task<YahooMessage?> GetAsync(uint uid, CancellationToken ct) => Task.FromResult<YahooMessage?>(new(Mime(), uid == 1 ? Now.AddDays(-40) : Now));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task YahooFullScanFiltersInitialWindowAndUidsBeyondSnapshot()
    {
        var fake = new YahooFake();
        var batches = await Collect(new YahooConnector(fake, "me@example.test"));
        var messages = batches.SelectMany(x => x.Messages).ToArray();
        Assert.Equal(4, messages.Length);
        Assert.Equal(4, messages.Select(x => x.SourceMessageId).Distinct().Count());
        Assert.Equal(2, messages.Count(x => x.IsOutgoing));
        Assert.All(fake.Searches, x => { Assert.Equal(0u, x.After); Assert.Equal(Now.AddDays(-30), x.Since); });
        var cursor = JsonSerializer.Deserialize<Dictionary<string, YahooCursor>>(batches[^1].CompletedCursor!)!;
        Assert.Equal(new YahooCursor(7, 3), cursor["Sent"]);
    }

    [Fact]
    public async Task YahooIncrementalUsesUidsAndResetRescansThirtyDays()
    {
        var fake = new YahooFake();
        var connector = new YahooConnector(fake, "me@example.test");
        var first = await Collect(connector);
        var next = await Collect(connector, first[^1].CompletedCursor);
        Assert.Empty(next.SelectMany(x => x.Messages));
        Assert.All(fake.Searches.Skip(2), x => { Assert.Equal(3u, x.After); Assert.Null(x.Since); });
        fake.Validity = 8;
        var reset = await Collect(connector, first[^1].CompletedCursor);
        Assert.Equal(4, reset.SelectMany(x => x.Messages).Count());
        Assert.All(fake.Searches.Skip(4), x => { Assert.Equal(0u, x.After); Assert.NotNull(x.Since); });
        Assert.Contains(":8:", reset[0].Messages[0].SourceMessageId);
    }

    [Fact]
    public void NormalizationRemovesHtmlScriptsAndRemoteResourcesWithoutFetching()
    {
        var mime = Mime();
        mime.Body = new TextPart("html") { Text = "<html><head><style>hidden</style></head><body><p>Hello &amp; welcome</p><script>secret()</script><img src='https://example.test/track'><p>Next</p></body></html>" };
        var message = MailNormalizer.Normalize(mime, "example", "id", "thread", Now, false);
        Assert.Contains("Hello & welcome", message.Body);
        Assert.Contains("Next", message.Body);
        Assert.DoesNotContain("secret", message.Body);
        Assert.DoesNotContain("hidden", message.Body);
        Assert.DoesNotContain("https://", message.Body);
        Assert.DoesNotContain("<", message.Body);
        Assert.Equal("sender@example.test", message.Sender);
    }

    [Fact]
    public void SourcesAreAccountIsolatedAndCaseNormalized()
    {
        Assert.Equal(MailNormalizer.AccountSource("gmail", "ME@example.test"), MailNormalizer.AccountSource("gmail", "me@example.test"));
        Assert.NotEqual(MailNormalizer.AccountSource("gmail", "me@example.test"), MailNormalizer.AccountSource("gmail", "other@example.test"));
        Assert.DoesNotContain("@", MailNormalizer.AccountSource("gmail", "me@example.test"));
    }
}
