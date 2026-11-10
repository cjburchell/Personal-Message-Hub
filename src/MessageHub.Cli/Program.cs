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
    Console.WriteLine("Personal Message Hub (PMH-005)");
    Console.WriteLine("Exports: import-linkedin <input> --account <label> --self <profile-url-or-name> [--self <alias>] [--timezone <zone>] [--dry-run]");
    Console.WriteLine("         import-messenger <input> --account <label> --self <display-name> [--self <alias>] [--repair-encoding] [--dry-run] | imports");
    Console.WriteLine("Local interfaces: run src/MessageHub.Web or src/MessageHub.Mcp; see docs/local-interfaces.md.");
    Console.WriteLine("Commands: init | import-demo | import-json <file> | search <phrase> | report [days] [--end <ISO>] [--ai --model <local-model>] | reports | report-show <id> | status");
    Console.WriteLine("Worker: worker [--once] [--sync] [--ai --model <local-model>] [--timezone <zone>] [--day Monday] [--hour 8]");
    Console.WriteLine("Evidence: message <id> (prints the full archived message locally)");
    Console.WriteLine("Database: " + dbPath);
    Console.WriteLine("Mail: auth gmail <desktop-client.json> | auth yahoo <address> | sync <gmail|yahoo> | forget <gmail|yahoo>");
    Console.WriteLine("Live sync requires Windows, disk encryption and restricted filesystem access; messages are unencrypted in SQLite.");
    return;
}
try
{
    args[0] = command;
    using var cancellation = new CancellationTokenSource();
    if (command != "worker") cancellation.CancelAfter(TimeSpan.FromMinutes(10));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    if (command is not ("import-linkedin" or "import-messenger"))
        await store.InitializeAsync(cancellation.Token);
    switch (command)
    {
        case "import-linkedin":
        case "import-messenger":
            await ExportCommands.RunAsync(args, store, cancellation.Token);
            break;
        case "imports":
            if (args.Length != 1) throw new ArgumentException("Usage: imports");
            var imports = await store.GetLatestExportImportsAsync(cancellation.Token);
            if (imports.Count == 0) Console.WriteLine("No official export imports recorded.");
            foreach (var import in imports)
                Console.WriteLine($"{import.Source}: imported {import.ImportedAt:u}; {import.Summary.MessageCount} message rows; newest accepted message {import.Summary.LatestMessage?.ToString("u") ?? "none"}. Snapshot coverage unknown.");
            break;
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
                Console.WriteLine(TerminalText($"[{hit.Source}] {hit.SentAt:u} {hit.Sender}: {hit.Subject} (id: {hit.Id})"));
            break;
        case "report":
        case "worker":
            await ReportCommands.RunAsync(args, store, cancellation.Token);
            break;
        case "reports":
            if (args.Length != 1) throw new ArgumentException("Usage: reports");
            foreach (var report in await store.GetReportsAsync(cancellation.Token))
                Console.WriteLine($"{report.Id} | {report.Start:u} to {report.End:u} | created {report.CreatedAt:u}");
            break;
        case "report-show":
            if (args.Length != 2) throw new ArgumentException("Usage: report-show <id>");
            var savedReport = await store.GetReportAsync(args[1], cancellation.Token) ?? throw new ArgumentException("Report not found.");
            Console.WriteLine(savedReport.Markdown);
            break;
        case "message":
            if (args.Length != 2) throw new ArgumentException("Usage: message <id>");
            var evidence = await store.GetMessageAsync(args[1], cancellation.Token) ?? throw new ArgumentException("Message not found.");
            Console.WriteLine(TerminalText($"{evidence.Id} | {evidence.Source} | {evidence.SentAt:u} | {evidence.Sender} | {evidence.Subject}"));
            Console.WriteLine(TerminalText(evidence.Body));
            break;
        case "status":
            var statuses = await store.GetStatusesAsync();
            if (statuses.Count == 0) Console.WriteLine("No sources synchronized. Authorization may exist; no sync has been recorded.");
            foreach (var s in statuses)
                Console.WriteLine(s.LastExportImport is { } export
                    ? $"{s.Source}: export snapshot imported {export.ImportedAt:u}; live sync not established."
                    : $"{s.Source}: {s.LastSuccessfulSync?.ToString("u") ?? "never"} {s.Error}");
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
        _ when command is "import-linkedin" or "import-messenger" => "Export import failed. Check format, owner identifiers, timestamps, input limits and file access. Incomplete transactions are rolled back. See docs/export-imports.md.",
        PlatformNotSupportedException => "Live connectors require Windows DPAPI credential storage.",
        OperationCanceledException => "Operation cancelled or timed out. Retry to resume from the last completed checkpoint.",
        Google.GoogleApiException api => MessageHub.Connectors.GmailApiDiagnostics.Describe(api),
        Google.Apis.Auth.OAuth2.Responses.TokenResponseException => "Gmail authorization failed. Reauthorize locally.",
        MailKit.Security.AuthenticationException => "Yahoo authentication failed. Check the address and app password locally.",
        MailKit.Security.SslHandshakeException => "TLS validation failed. Check system time and certificate trust.",
        System.Security.Cryptography.CryptographicException => "The credential store could not be decrypted. Reauthorize using this Windows account.",
        _ => "Check usage, input files, local authorization, network access and database permissions; another sync may be running."
    };
    Console.Error.WriteLine("Command failed. " + reason + " See docs/mail-setup.md; use --help for usage.");
    Environment.ExitCode = 1;
}

static string TerminalText(string text) => new(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
