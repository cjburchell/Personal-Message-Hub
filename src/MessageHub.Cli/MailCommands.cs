using System.Runtime.Versioning;
using MessageHub.Connectors;
using MessageHub.Storage;
using MimeKit;

namespace MessageHub.Cli;

internal static class MailCommands
{
    public static async Task RunAsync(string[] args, SqliteMessageStore store, CancellationToken ct)
    {
        if (args.Length < 2 || args[1] is not ("gmail" or "yahoo")
            || (args[0] == "auth" && args.Length != 3)
            || (args[0] is "sync" or "forget" && args.Length != 2))
            throw new ArgumentException("Use auth gmail <desktop-client.json> | auth yahoo <address> | sync <gmail|yahoo> | forget <gmail|yahoo>.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live integrations currently require Windows DPAPI.");
        await RunWindowsAsync(args, store, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(string[] args, SqliteMessageStore store, CancellationToken ct)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalMessageHub", "secrets");
        Directory.CreateDirectory(directory);
        // Serialize token refresh/setup across CLI processes as well as database writes.
        using var credentialLock = new FileStream(Path.Combine(directory, "credentials.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var secrets = new WindowsSecretStore(directory);
        var provider = args[1];
        switch (args[0])
        {
            case "auth" when provider == "gmail":
                await GmailAuthentication.AuthorizeAsync(args[2], secrets, ct);
                using (var service = await GmailAuthentication.OpenAsync(secrets, ct))
                {
                    var profile = await new GmailReader(service).GetProfileAsync(ct);
                    await secrets.StoreAsync("gmail-address", profile.Address);
                }
                Console.WriteLine("Gmail authorization saved with read-only access in Windows DPAPI.");
                break;
            case "auth":
                if (!MailboxAddress.TryParse(args[2], out var address) || address.Address != args[2])
                    throw new ArgumentException("Use a full Yahoo address.");
                if (Console.IsInputRedirected) throw new ArgumentException("Use an interactive terminal for the hidden app-password prompt.");
                Console.Write("Yahoo app password (hidden; not your normal password): ");
                var password = await ReadPasswordAsync(ct);
                if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("An app password is required.");
                await secrets.StoreAsync("yahoo", new YahooCredentials(address.Address, password));
                Console.WriteLine("Yahoo credentials saved with Windows DPAPI. Run sync yahoo to validate connectivity.");
                break;
            case "forget":
                if (provider == "gmail")
                {
                    await GmailAuthentication.ForgetAsync(secrets);
                    await secrets.DeleteAsync<string>("gmail-address");
                }
                else await secrets.DeleteAsync<YahooCredentials>("yahoo");
                Console.WriteLine("Local credentials removed. Archived messages remain. Revoke access in the provider's account settings if needed.");
                break;
            case "sync":
                string? source = null;
                try
                {
                    int count;
                    if (provider == "gmail")
                    {
                        var account = await secrets.GetAsync<string>("gmail-address") ?? throw new InvalidOperationException("Authorize Gmail first.");
                        source = MailNormalizer.AccountSource("gmail", account);
                        using var service = await GmailAuthentication.OpenAsync(secrets, ct);
                        var reader = new GmailReader(service);
                        // Bind the checkpoint to the account approved during setup, even after credential changes.
                        count = await new SyncCoordinator(store).SyncAsync(new GmailConnector(reader, new(account, 0)), ct);
                    }
                    else
                    {
                        var credentials = await secrets.GetAsync<YahooCredentials>("yahoo") ?? throw new InvalidOperationException("Configure Yahoo first.");
                        source = MailNormalizer.AccountSource("yahoo", credentials.Address);
                        await using var reader = new YahooReader();
                        await reader.ConnectAsync(credentials, ct);
                        count = await new SyncCoordinator(store).SyncAsync(new YahooConnector(reader, credentials.Address), ct);
                    }
                    Console.WriteLine($"Sync completed: {count} messages processed (updates included). Use status for source freshness.");
                }
                catch
                {
                    if (source is not null)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await store.RecordSyncFailureAsync(source, timeout.Token);
                    }
                    throw;
                }
                break;
        }
    }

    private static async Task<string> ReadPasswordAsync(CancellationToken ct)
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable) { await Task.Delay(50, ct); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }
}
