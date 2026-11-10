using System.Net;
using System.Text;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using MessageHub.Connectors;
using Xunit;

namespace MessageHub.Tests;

public sealed class GmailReaderTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); // No mailbox-mutation requests are permitted.
            return Task.FromResult(response(request));
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : Google.Apis.Http.IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(handler));
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GmailAdapterMapsProfilePaginationRawMimeAndReadOnlyRequests()
    {
        var requests = new List<Uri>();
        using var stream = new MemoryStream();
        ConnectorTests.Mime().WriteTo(stream);
        var raw = Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientFactory = new Factory(new Handler(request =>
            {
                var uri = request.RequestUri!;
                requests.Add(uri);
                return uri.AbsolutePath.Split('/').Last() switch
                {
                    "profile" => Json("{\"emailAddress\":\"me@example.test\",\"historyId\":\"100\"}"),
                    "messages" => Json("{\"messages\":[{\"id\":\"one\"}],\"nextPageToken\":\"next\"}"),
                    "history" => Json("{\"history\":[{\"messages\":[{\"id\":\"one\"},{\"id\":\"one\"}]}],\"historyId\":\"200\"}"),
                    _ => Json("{\"id\":\"one\",\"threadId\":\"thread\",\"raw\":\"" + raw + "\",\"internalDate\":\"1794312000000\",\"labelIds\":[\"SENT\"]}")
                };
            }))
        });
        var reader = new GmailReader(service);
        Assert.Equal(new GmailProfile("me@example.test", 100), await reader.GetProfileAsync(default));
        var page = await reader.ListAsync(DateTimeOffset.FromUnixTimeSeconds(1234567), "page-two", default);
        Assert.Equal("one", Assert.Single(page.Ids));
        Assert.Equal("next", page.NextPageToken);
        var history = await reader.HistoryAsync(100, "history-two", default);
        Assert.Equal("one", Assert.Single(history.Ids));
        Assert.Equal(200ul, history.HistoryId);
        var message = await reader.GetAsync("one", default);
        Assert.NotNull(message);
        Assert.True(message.IsSent);
        Assert.Equal(stream.ToArray(), message.Raw);
        Assert.Equal("thread", message.ThreadId);
        Assert.Contains(requests, x => Uri.UnescapeDataString(x.Query).Contains("q=after:1234567") && x.Query.Contains("pageToken=page-two"));
        Assert.Contains(requests, x => x.Query.Contains("startHistoryId=100") && x.Query.Contains("pageToken=history-two"));
        Assert.Contains(requests, x => x.Query.Contains("format=raw"));
    }

    [Fact]
    public async Task GmailAdapterDistinguishesExpiredHistoryAndDeletedMessages()
    {
        using var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientFactory = new Factory(new Handler(_ => Json("{\"error\":{\"code\":404,\"message\":\"synthetic not found\"}}", HttpStatusCode.NotFound)))
        });
        var reader = new GmailReader(service);
        await Assert.ThrowsAsync<HistoryExpiredException>(() => reader.HistoryAsync(1, null, default));
        Assert.Null(await reader.GetAsync("gone", default));
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("SPAM")]
    [InlineData("TRASH")]
    public async Task GmailAdapterSkipsExcludedLabelsEvenDuringIncrementalSync(string label)
    {
        using var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientFactory = new Factory(new Handler(_ => Json("{\"labelIds\":[\"" + label + "\"]}")))
        });
        Assert.Null(await new GmailReader(service).GetAsync("excluded", default));
    }
}
