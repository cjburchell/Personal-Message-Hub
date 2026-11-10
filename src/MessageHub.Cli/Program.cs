using System.Text.Json;
using MessageHub.Core;
using MessageHub.Storage;

var dbPath = Environment.GetEnvironmentVariable("MESSAGEHUB_DB")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalMessageHub", "messages.db");
var store = new SqliteMessageStore(dbPath);
var command = args.FirstOrDefault()?.ToLowerInvariant();
if (command is null || command is "help" or "--help")
{
    Console.WriteLine("Personal Message Hub (PMH-001)");
    Console.WriteLine("Commands: init | import-demo | import-json <file> | search <phrase> | report [days] | status");
    Console.WriteLine("Database: " + dbPath);
    return;
}
try
{
    await store.InitializeAsync();
    switch (command)
    {
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
            break;
        case "status":
            var statuses = await store.GetStatusesAsync();
            if (statuses.Count == 0) Console.WriteLine("No sources synchronized. Live connectors are not configured.");
            foreach (var s in statuses)
                Console.WriteLine($"{s.Source}: {s.LastSuccessfulSync?.ToString("u") ?? "never"} {s.Error}");
            break;
        default:
            throw new ArgumentException($"Unknown command: {command}");
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or Microsoft.Data.Sqlite.SqliteException)
{
    // Do not echo JSON or database values: they may contain private message content.
    Console.Error.WriteLine("Command failed. Check arguments, JSON message fields, and database/file access. Use --help for usage.");
    Environment.ExitCode = 1;
}
