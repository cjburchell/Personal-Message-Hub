using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MessageHub.Core;
using MessageHub.Query;
using MessageHub.Storage;
using MessageHub.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MessageHub.Tests;

public sealed class WebTests
{
    private const string Password = "synthetic-local-passphrase-123";
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pmh-web-test-" + Guid.NewGuid().ToString("N"));
        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        private readonly Dictionary<string,string> _cookies = [];
        public string? Csrf { get; private set; }
        public List<string> SetCookies { get; } = [];
        public static async Task<Fixture> Create(Action<WebApplicationBuilder>? configure = null)
        {
            var fixture = new Fixture();
            var passwordFile = Path.Combine(fixture.Root, "auth", "web.password.hash");
            await new WebPassword(passwordFile).SetAsync(Password);
            var store = new SqliteMessageStore(Path.Combine(fixture.Root,"test.db")); await store.InitializeAsync();
            await store.UpsertAsync(new("example","id","thread","sender@example.test",[],"Deadline <script>bad()</script>","synthetic-private-marker: payment is due Friday.",DateTimeOffset.UtcNow.AddDays(-1),false));
            fixture.App = await WebBootstrap.CreateAsync(new(new(Path.Combine(fixture.Root,"test.db"),null,null),passwordFile), builder =>
            { builder.WebHost.UseTestServer(); builder.Logging.ClearProviders(); configure?.Invoke(builder); });
            await fixture.App.StartAsync(); fixture.Client=fixture.App.GetTestClient(); fixture.Client.BaseAddress=new Uri("http://127.0.0.1:5080");
            return fixture;
        }
        public async Task<HttpResponseMessage> Send(string path, object? body=null, bool csrf=true, string? origin="http://127.0.0.1:5080")
        {
            using var request=new HttpRequestMessage(body is null?HttpMethod.Get:HttpMethod.Post,path);
            if(_cookies.Count>0)request.Headers.Add("Cookie",string.Join("; ",_cookies.Select(x=>x.Key+"="+x.Value)));
            if(body is not null){request.Content=JsonContent.Create(body); if(origin is not null)request.Headers.Add("Origin",origin); if(csrf && Csrf is not null)request.Headers.Add("X-PMH-CSRF",Csrf);}
            var response=await Client.SendAsync(request);
            if(response.Headers.TryGetValues("Set-Cookie",out var values))foreach(var value in values){SetCookies.Add(value);var pair=value.Split(';')[0].Split('=',2);_cookies[pair[0]]=pair[1];}
            return response;
        }
        public async Task RefreshCsrf(){using var response=await Send("/api/csrf"); response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Csrf=json.RootElement.GetProperty("token").GetString();}
        public async Task Login(){await RefreshCsrf();using var response=await Send("/api/login",new{password=Password});Assert.Equal(HttpStatusCode.OK,response.StatusCode);await RefreshCsrf();}
        public async ValueTask DisposeAsync(){Client.Dispose();await App.StopAsync();await App.DisposeAsync();SqliteConnection.ClearAllPools();Directory.Delete(Root,true);}
    }

    [Fact]
    public async Task EveryArchiveEndpointRequiresAuthenticationAndResponsesPreventCaching()
    {
        await using var fixture=await Fixture.Create();
        foreach(var path in new[]{"/api/session","/api/search?query=deadline","/api/message?id=example:id","/api/reports","/api/report?id=missing","/api/sources"})
        {
            using var response=await fixture.Send(path);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);Assert.DoesNotContain("synthetic-private-marker",await response.Content.ReadAsStringAsync());
        }
        using var shell=await fixture.Send("/");Assert.Equal(HttpStatusCode.OK,shell.StatusCode);
        Assert.Contains("frame-ancestors 'none'",shell.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("synthetic-private-marker",await shell.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PasswordLoginAllowsReadOnlyQueriesAndLogoutInvalidatesSession()
    {
        await using var fixture=await Fixture.Create(); await fixture.Login();
        Assert.Contains(fixture.SetCookies, x => x.StartsWith("pmh.session=", StringComparison.Ordinal)
            && x.Contains("httponly", StringComparison.OrdinalIgnoreCase) && x.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        using var search=await fixture.Send("/api/search?query=deadline&days=7");Assert.Equal(HttpStatusCode.OK,search.StatusCode);
        Assert.Contains("synthetic-private-marker",await search.Content.ReadAsStringAsync());
        using var answer=await fixture.Send("/api/ask",new{question="What deadlines are coming up?",days=7,useAi=false});Assert.Equal(HttpStatusCode.OK,answer.StatusCode);
        Assert.Contains("example:id",await answer.Content.ReadAsStringAsync());
        using var missingWrite=await fixture.Send("/api/sync",new{});Assert.Equal(HttpStatusCode.NotFound,missingWrite.StatusCode);
        using var logout=await fixture.Send("/api/logout",new{});Assert.Equal(HttpStatusCode.OK,logout.StatusCode);
        using var locked=await fixture.Send("/api/message?id=example:id");Assert.Equal(HttpStatusCode.Unauthorized,locked.StatusCode);
    }

    [Fact]
    public async Task CsrfAndOriginAndHostRestrictionsApplyBeforeArchiveAccess()
    {
        await using var fixture=await Fixture.Create();await fixture.Login();
        using var noCsrf=await fixture.Send("/api/ask",new{question="deadline"},csrf:false);Assert.Equal(HttpStatusCode.BadRequest,noCsrf.StatusCode);
        using var crossOrigin=await fixture.Send("/api/ask",new{question="deadline"},origin:"https://example.test");Assert.Equal(HttpStatusCode.Forbidden,crossOrigin.StatusCode);
        using var noOrigin=await fixture.Send("/api/ask",new{question="deadline"},origin:null);Assert.Equal(HttpStatusCode.Forbidden,noOrigin.StatusCode);
        using var hostRequest=new HttpRequestMessage(HttpMethod.Get,"/api/csrf");hostRequest.Headers.Host="example.test:5080";
        using var badHost=await fixture.Client.SendAsync(hostRequest);Assert.Equal(HttpStatusCode.Forbidden,badHost.StatusCode);
        var remote=await fixture.App.GetTestServer().SendAsync(context=>{context.Request.Method="GET";context.Request.Path="/";context.Request.Host=new("127.0.0.1:5080");context.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");});
        Assert.Equal(403,remote.Response.StatusCode);
    }

    [Fact]
    public async Task LoginIsRateLimitedAndPasswordHashesAreNotPlaintext()
    {
        await using var fixture=await Fixture.Create();await fixture.RefreshCsrf();
        for(var i=0;i<5;i++){using var wrong=await fixture.Send("/api/login",new{password="synthetic-wrong"});Assert.Equal(HttpStatusCode.Unauthorized,wrong.StatusCode);}
        using var limited=await fixture.Send("/api/login",new{password=Password});Assert.Equal((HttpStatusCode)429,limited.StatusCode);
        Assert.DoesNotContain(Password,await File.ReadAllTextAsync(Path.Combine(fixture.Root,"auth","web.password.hash")));
    }

    [Fact]
    public async Task MissingPasswordFailsClosedAndOversizedPayloadsAreRejected()
    {
        var path=Path.Combine(Path.GetTempPath(),"pmh-missing-"+Guid.NewGuid().ToString("N"),"web.password.hash");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WebBootstrap.CreateAsync(new(new("unused.db",null,null),path)));
        await using var fixture=await Fixture.Create();await fixture.Login();
        using var large=await fixture.Send("/api/ask",new{question=new string('x',9000)});Assert.Equal((HttpStatusCode)413,large.StatusCode);
        using var invalid=await fixture.Send("/api/ask",new{question="deadline",days=999});Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        Assert.DoesNotContain("synthetic-private-marker",await invalid.Content.ReadAsStringAsync());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task SessionExpiresAfterThirtyMinutesAndTicketsCannotCrossServerInstances()
    {
        var clock = new Clock();
        await using var fixture = await Fixture.Create(builder => builder.Services.PostConfigure<CookieAuthenticationOptions>(
            CookieAuthenticationDefaults.AuthenticationScheme, options => options.TimeProvider = clock));
        await fixture.Login();
        var cookie = fixture.SetCookies.Last(x => x.StartsWith("pmh.session=", StringComparison.Ordinal)).Split(';')[0];
        await using var second = await Fixture.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session"); request.Headers.Add("Cookie", cookie);
        using var rejected = await second.Client.SendAsync(request); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        clock.Now = DateTimeOffset.UtcNow.AddMinutes(31);
        using var expired = await fixture.Send("/api/session"); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }
}
