using System.Runtime.Versioning;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;

namespace MessageHub.Connectors;

[SupportedOSPlatform("windows")]
public static class GmailAuthentication
{
    internal sealed class ConsentReceiver(ICodeReceiver receiver) : ICodeReceiver
    {
        public string RedirectUri => receiver.RedirectUri;
        public Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken ct)
        {
            var request = (GoogleAuthorizationCodeRequestUrl)url;
            // Explicit reauthorization must obtain a refresh token even after a local forget.
            request.AccessType = "offline";
            request.Prompt = "consent";
            return receiver.ReceiveCodeAsync(request, ct);
        }
    }

    internal static PkceGoogleAuthorizationCodeFlow Flow(ClientSecrets secrets, WindowsSecretStore store) => new(
        new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = [GmailService.Scope.GmailReadonly],
            DataStore = store
        });

    public static async Task AuthorizeAsync(string clientJsonPath, WindowsSecretStore store, CancellationToken ct)
    {
        await using var stream = File.OpenRead(clientJsonPath);
        using (var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct))
            if (!document.RootElement.TryGetProperty("installed", out _))
                throw new ArgumentException("Use an OAuth client of type Desktop app.");
        stream.Position = 0;
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        using var flow = Flow(secrets, store);
        // The library uses state validation, PKCE and a temporary loopback listener in the system browser.
        await new AuthorizationCodeInstalledApp(flow, new ConsentReceiver(new LocalServerCodeReceiver(
            "Authorization completed. You may close this window.",
            LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp))).AuthorizeAsync("gmail", ct);
        await store.StoreAsync("gmail-client", secrets);
    }

    public static async Task<GmailService> OpenAsync(WindowsSecretStore store, CancellationToken ct)
    {
        var secrets = await store.GetAsync<ClientSecrets>("gmail-client")
            ?? throw new InvalidOperationException("Authorize Gmail first.");
        var token = await store.GetAsync<TokenResponse>("gmail")
            ?? throw new InvalidOperationException("Authorize Gmail first.");
        var credential = new UserCredential(Flow(secrets, store), "gmail", token);
        // Refresh is noninteractive. A scheduled sync must never open a browser.
        if (!await credential.RefreshTokenAsync(ct)) throw new InvalidOperationException("Reauthorize Gmail.");
        return new GmailService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "PersonalMessageHub" });
    }

    public static async Task ForgetAsync(WindowsSecretStore store)
    {
        await store.DeleteAsync<TokenResponse>("gmail");
        await store.DeleteAsync<ClientSecrets>("gmail-client");
    }
}
