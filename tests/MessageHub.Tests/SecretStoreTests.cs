using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Auth.OAuth2.Requests;
using MessageHub.Connectors;
using Xunit;

namespace MessageHub.Tests;

public sealed class SecretStoreTests
{
    private sealed class Receiver : ICodeReceiver
    {
        public string RedirectUri => "http://127.0.0.1:12345/authorize/";
        public AuthorizationCodeRequestUrl? Request { get; private set; }
        public Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken ct)
        {
            Request = url;
            return Task.FromResult(new AuthorizationCodeResponseUrl { Code = "synthetic-code" });
        }
    }

    [Fact]
    public async Task GmailConsentRequestsOnlyReadonlyWithPkceAndOfflineRefresh()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsSecretStore(Path.Combine(Path.GetTempPath(), "pmh-unused-" + Guid.NewGuid().ToString("N")));
        using var flow = GmailAuthentication.Flow(new ClientSecrets { ClientId = "synthetic-client", ClientSecret = "synthetic-secret" }, store);
        var receiver = new Receiver();
        var consent = new GmailAuthentication.ConsentReceiver(receiver);
        var request = flow.CreateAuthorizationCodeRequest(consent.RedirectUri, out var verifier);
        await consent.ReceiveCodeAsync(request, default);
        Assert.Same(request, receiver.Request);
        var query = Uri.UnescapeDataString(request.Build().Query);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Contains("code_challenge=" + challenge, query);
        Assert.Contains("code_challenge_method=S256", query);
        Assert.Contains("access_type=offline", query);
        Assert.Contains("prompt=consent", query);
        Assert.Contains("scope=https://www.googleapis.com/auth/gmail.readonly&", query + "&");
    }

    [Fact]
    public async Task DpapiStoreRoundTripsGoogleAndYahooSecretsWithoutPlaintextAndRejectsTampering()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is deliberately unavailable elsewhere.
        var directory = Path.Combine(Path.GetTempPath(), "pmh-secret-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WindowsSecretStore(directory);
            await store.StoreAsync("gmail", new TokenResponse { RefreshToken = "synthetic-refresh-token", AccessToken = "synthetic-access-token", ExpiresInSeconds = 3600 });
            await store.StoreAsync("gmail-client", new ClientSecrets { ClientId = "synthetic-client", ClientSecret = "synthetic-client-secret" });
            await store.StoreAsync("yahoo", new YahooCredentials("me@example.test", "synthetic-app-password"));
            Assert.Equal("synthetic-refresh-token", (await store.GetAsync<TokenResponse>("gmail")).RefreshToken);
            Assert.Equal("synthetic-client-secret", (await store.GetAsync<ClientSecrets>("gmail-client")).ClientSecret);
            Assert.Equal("synthetic-app-password", (await store.GetAsync<YahooCredentials>("yahoo")).AppPassword);
            Assert.DoesNotContain("synthetic-app-password", (await store.GetAsync<YahooCredentials>("yahoo")).ToString());
            foreach (var file in Directory.GetFiles(directory))
                Assert.DoesNotContain("synthetic-", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            await store.DeleteAsync<YahooCredentials>("yahoo");
            Assert.Null(await store.GetAsync<YahooCredentials>("yahoo"));
            await GmailAuthentication.ForgetAsync(store);
            Assert.Empty(Directory.GetFiles(directory));
            await store.StoreAsync("tamper", "synthetic-private-value");
            var path = Assert.Single(Directory.GetFiles(directory));
            await File.WriteAllBytesAsync(path, new byte[32]);
            await Assert.ThrowsAsync<CryptographicException>(async () => { if (OperatingSystem.IsWindows()) await store.GetAsync<string>("tamper"); });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
