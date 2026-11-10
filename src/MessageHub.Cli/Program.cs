using System.Text.Json;
using MessageHub.Core;
using MessageHub.Storage;
using MessageHub.Cli;

var dbPath = Environment.GetEnvironmentVariable("MESSAGEHUB_DB")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalMessageHub", "messages.db");
var store = new SqliteMessageStore(dbPath);
var command = args.FirstOrDefault()?.ToLowerInvariant();
if (command is null || command is "help" or "--help")
{
    Console.WriteLine("Personal Message Hub (PMH-002)");
    Console.WriteLine("Commands: init | import-demo | import-json <file> | search <phrase> | report [days] | status");
    Console.WriteLine("Database: " + dbPath);
    Console.WriteLine("Mail: auth gmail <desktop-client.json> | auth yahoo <address> | sync <gmail|yahoo> | forget <gmail|yahoo>");
    Console.WriteLine("Live sync requires Windows, disk encryption and restricted filesystem access; messages are unencrypted in SQLite.");
    return;
}
try
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    await store.InitializeAsync(cancellation.Token);
    switch (command)
    {
        case "auth":
        case "sync":
        case "forget":
            await MailCommands.RunAsync(args, store, cancellation.Token);
            break;
        case "init":
            Console.WriteLine("Initialized database: " + dbPath);
            break;
        case "import-demo":
            var now = DateTimeOffset.UtcNow;
            var demo = new[]
            {
                new UnifiedMessage("demo", "001", "thread-1", "recruiter@example.test", ["you@example.test"],
                    "Availability", "Could you share your availability for a call next week?", now.AddDays(-2), false),
                new UnifiedMessage("demo", "002", "thread-1", "you@example.test", ["recruiter@example.test"],
                    "Re: Availability", "Tuesday afternoon works for me.", now.AddDays(-1), true),
                new UnifiedMessage("demo", "003", "thread-2", "library@example.test", ["you@example.test"],
                    "Library reminder", "Your borrowed book is due Friday.", now.AddDays(-3), false),
            };
            await store.ImportAsync(demo);
            await store.SetSyncStateAsync("demo", DateTimeOffset.UtcNow);
            Console.WriteLine($"Imported {demo.Length} synthetic messages (safe to re-run).");
            break;
        case "import-json":
            if (args.Length < 2) throw new ArgumentException("Usage: import-json <file>");
            var json = await File.ReadAllTextAsync(args[1]);
            var imported = JsonSerializer.Deserialize<List<UnifiedMessage>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Expected a JSON array of messages.");
            await store.ImportAsync(imported);
            // Deliberately not setting sync status: importing a file is not a live source synchronization.
            Console.WriteLine($"Imported {imported.Count} messages from JSON.");
            break;
        case "search":
            if (args.Length < 2) throw new ArgumentException("Usage: search <phrase>");
            foreach (var hit in await store.SearchAsync(string.Join(' ', args.Skip(1))))
                Console.WriteLine($"[{hit.Source}] {hit.SentAt:u} {hit.Sender}: {hit.Subject} (id: {hit.Id})");
            break;
        case "report":
            var days = 7;
            if (args.Length > 2 || (args.Length == 2 && !int.TryParse(args[1], out days)) || days is < 1 or > 365)
                throw new ArgumentException("Usage: report [days], where days is an integer from 1 to 365.");
            var end = DateTimeOffset.UtcNow;
            var start = end.AddDays(-days);
            var messages = await store.BetweenAsync(start, end);
            var summary = ReportSummaryBuilder.Build(messages);
            Console.WriteLine($"# Message report: {start:yyyy-MM-dd} to {end:yyyy-MM-dd} (UTC)");
            Console.WriteLine($"Total: {summary.MessageCount}; incoming: {summary.IncomingCount}; outgoing: {summary.OutgoingCount}");
            foreach (var source in summary.BySource.OrderBy(x => x.Key))
                Console.WriteLine($"- {source.Key}: {source.Value}");
            Console.WriteLine("Note: deterministic counts only; AI analysis and action resolution are not implemented.");
            Console.WriteLine("Counts cover the local archive only. Use status to check source freshness and failed syncs.");
            break;
        case "status":
            var statuses = await store.GetStatusesAsync();
            if (statuses.Count == 0) Console.WriteLine("No sources synchronized. Authorization may exist; no sync has been recorded.");
            foreach (var s in statuses)
                Console.WriteLine($"{s.Source}: {s.LastSuccessfulSync?.ToString("u") ?? "never"} {s.Error}");
            break;
        default:
            throw new ArgumentException($"Unknown command: {command}");
    }
}
catch (Exception ex)
{
    // Do not echo JSON or database values: they may contain private message content.
    var reason = ex switch
    {
        PlatformNotSupportedException => "Live connectors require Windows DPAPI credential storage.",
        OperationCanceledException => "Operation cancelled or timed out. Retry to resume from the last completed checkpoint.",
        Google.GoogleApiException api => $"Gmail API returned HTTP {(int)api.HttpStatusCode}. Check API access or retry later.",
        Google.Apis.Auth.OAuth2.Responses.TokenResponseException => "Gmail authorization failed. Reauthorize locally.",
        MailKit.Security.AuthenticationException => "Yahoo authentication failed. Check the address and app password locally.",
        MailKit.Security.SslHandshakeException => "TLS validation failed. Check system time and certificate trust.",
        System.Security.Cryptography.CryptographicException => "The credential store could not be decrypted. Reauthorize using this Windows account.",
        _ => "Check usage, input files, local authorization, network access and database permissions; another sync may be running."
    };
    Console.Error.WriteLine("Command failed. " + reason + " See docs/mail-setup.md; use --help for usage.");
    Environment.ExitCode = 1;
}
