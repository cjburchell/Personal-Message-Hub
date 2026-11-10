using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using MessageHub.Core;
using MessageHub.Query;
using MessageHub.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace MessageHub.Web;

public static class WebBootstrap
{
    public sealed record LoginInput(string Password);
    public sealed record AskInput(string Question, int Days = 30, bool UseAi = false);

    public static async Task<WebApplication> CreateAsync(WebOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        if (options.Port is < 1 or > 65535) throw new ArgumentException("Invalid port.");
        var password = new WebPassword(options.PasswordFile);
        await password.EnsureConfiguredAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = "Production", WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        // Ignore environment/launch-profile endpoint declarations; this host has one literal loopback listener.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, options.Port);
            server.Limits.MaxRequestBodySize = 8192;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
        {
            cookie.Cookie.Name = "pmh.session"; cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict; cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(30); cookie.SlidingExpiration = false;
            cookie.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            cookie.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = "X-PMH-CSRF"; antiforgery.Cookie.Name = "pmh.csrf";
            antiforgery.Cookie.HttpOnly = true; antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddRateLimiter(rate =>
        {
            rate.RejectionStatusCode = 429;
            rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter(
                "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            rate.AddFixedWindowLimiter("login", limiter => { limiter.PermitLimit = 5; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; });
        });
        var store = new SqliteMessageStore(options.Archive.DatabasePath); await store.InitializeAsync();
        builder.Services.AddSingleton(store); builder.Services.AddSingleton(password);
        if (!string.IsNullOrWhiteSpace(options.Archive.Model))
            builder.Services.AddSingleton<IQuestionAnswerer>(_ => new OllamaQuestionAnswerer(options.Archive.Model, options.Archive.OllamaEndpoint));
        builder.Services.AddSingleton<ArchiveQueryService>();
        configure?.Invoke(builder);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<ArchiveQueryService>(); // Validate local model configuration before listening.
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Request.ContentLength > 8192) { context.Response.StatusCode = 413; return; }
            var remote = context.Connection.RemoteIpAddress;
            if (context.Request.Host.Value != $"127.0.0.1:{options.Port}" || remote is not null && !IPAddress.IsLoopback(remote)
                || context.Request.Headers["Sec-Fetch-Site"] == "cross-site"
                || context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin != options.Origin)
            { context.Response.StatusCode = 403; return; }
            try { await next(); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 499; }
            catch (Exception ex)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = ex is QueryBusyException ? 503 : ex is ArgumentException or BadHttpRequestException ? 400 : 503;
                    await context.Response.WriteAsJsonAsync(new { error = "Request could not complete. Check your input or try again later." });
                }
            }
            if (context.Request.Path.StartsWithSegments("/api"))
                app.Logger.LogWarning("Local API {Method} {Route} returned {Status}", context.Request.Method,
                    (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown", context.Response.StatusCode);
        });
        app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST")
            {
                if (context.Request.Headers.Origin != options.Origin || !context.Request.HasJsonContentType()) { context.Response.StatusCode = 403; return; }
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
                // Enforce the limit for chunked JSON too, including when hosted by TestServer.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var bytes = new byte[8193]; var total = 0;
                try
                {
                    int count;
                    while ((count = await context.Request.Body.ReadAsync(bytes.AsMemory(total), deadline.Token)) > 0)
                    {
                        total += count;
                        if (total > 8192) { context.Response.StatusCode = 413; return; }
                    }
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 408; return; }
                var original = context.Request.Body;
                using var buffered = new MemoryStream(bytes, 0, total, writable: false);
                context.Request.Body = buffered;
                try { await next(); }
                finally { context.Request.Body = original; }
                return;
            }
            await next();
        });
        app.UseDefaultFiles(); app.UseStaticFiles();
        app.MapGet("/api/csrf", (HttpContext ctx, IAntiforgery csrf) => Results.Ok(new { token = csrf.GetAndStoreTokens(ctx).RequestToken }));
        app.MapPost("/api/login", async (LoginInput input, HttpContext ctx, WebPassword secret) =>
        {
            if (input.Password is null || !await secret.VerifyAsync(input.Password)) return Results.Unauthorized();
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "archive-owner")], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) });
            return Results.Ok();
        }).RequireRateLimiting("login");
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/session", async (ArchiveQueryService archive, HttpContext ctx) =>
            Results.Ok(new { aiAvailable = archive.AiAvailable, expires = (await ctx.AuthenticateAsync()).Properties?.ExpiresUtc }));
        api.MapPost("/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); });
        api.MapPost("/ask", async (AskInput input, ArchiveQueryService archive, HttpContext ctx) =>
            Results.Ok(await archive.AskAsync(input.Question, input.Days, input.UseAi, ctx.RequestAborted)));
        api.MapGet("/search", async (string query, int? days, ArchiveQueryService archive, HttpContext ctx) =>
            Results.Ok(await archive.SearchAsync(query, days ?? 30, ctx.RequestAborted)));
        api.MapGet("/message", async (string id, ArchiveQueryService archive, HttpContext ctx) =>
            await archive.GetMessageAsync(id, ctx.RequestAborted) is { } message ? Results.Ok(message) : Results.NotFound());
        api.MapGet("/reports", async (ArchiveQueryService archive, HttpContext ctx) => Results.Ok(await archive.ListReportsAsync(ctx.RequestAborted)));
        api.MapGet("/report", async (string id, ArchiveQueryService archive, HttpContext ctx) =>
            await archive.GetReportAsync(id, ctx.RequestAborted) is { } report ? Results.Ok(report) : Results.NotFound());
        api.MapGet("/sources", async (ArchiveQueryService archive, HttpContext ctx) => Results.Ok(await archive.StatusAsync(ctx.RequestAborted)));
        return app;
    }
}
