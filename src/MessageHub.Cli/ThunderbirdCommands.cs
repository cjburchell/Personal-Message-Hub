using MessageHub.Connectors.Thunderbird;
using MessageHub.Storage;

namespace MessageHub.Cli;

internal static class ThunderbirdCommands
{
    public static async Task RunAsync(string[] args, SqliteMessageStore store, CancellationToken ct)
    {
        if (args[0] == "email-original")
        {
            if (args.Length != 3 || !args[1].StartsWith("email-ref-", StringComparison.Ordinal) || args[1].Length != 74)
                throw new ArgumentException("Use email-original <reference-id> <new-output-file>.");
            await store.InitializeAsync(ct);
            var bytes = await store.GetEmailOriginalAsync(args[1], ct) ?? throw new ArgumentException("Reference not found.");
            await using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(bytes, ct);
            Console.WriteLine("Preserved original written locally. Mbox records retain their source framing; treat all contents and attachments as untrusted.");
            return;
        }
        if (args.Length < 2) throw new ArgumentException("Supply a Thunderbird profile, mail directory, mbox or EML input.");
        string? account = null; var self = new List<string>(); var folders = new List<string>(); var dry = args[0] == "discover-thunderbird";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not ("--self" or "--folder") && !seen.Add(option)) throw new ArgumentException("Repeated option.");
            string Value() => ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException("Missing value.");
            switch (option)
            {
                case "--account": account = Value(); break;
                case "--self": self.Add(Value()); break;
                case "--folder": folders.Add(Value()); break;
                case "--dry-run": dry = true; break;
                default: throw new ArgumentException("Unknown Thunderbird option.");
            }
        }
        if (args[0] == "discover-thunderbird")
        {
            var inventory = new ThunderbirdArchive(args[1], "discovery", ["owner@example.test"], folders).Discover(ct);
            Console.WriteLine($"Discovered {inventory.Count} supported message files. Folder labels below are private local metadata; no database was opened.");
            foreach (var folder in inventory.GroupBy(x => (x.Folder, x.Format)))
                Console.WriteLine($"{Terminal(folder.Key.Folder)} | {folder.Key.Format} | {folder.Count()} files | {folder.Sum(x => x.Bytes)} bytes");
            return;
        }
        var archive = new ThunderbirdArchive(args[1], account ?? throw new ArgumentException("An account label is required."), self, folders);
        var result = await new LocalEmailImportService(store).ImportAsync(archive, dry, ct);
        Console.WriteLine($"{(dry ? "Dry run" : "Local import")}: {result.Files} selected files; {result.Processed} indexable records processed this run; {result.Issues} issues. Complete: {result.Complete}.");
        Console.WriteLine("Local snapshot only; missing/undownloaded messages and malformed files can limit coverage. No provider freshness is established. No AI or network calls were made.");
        if (dry) Console.WriteLine("No database was opened or changed.");
        if (!result.Complete) Environment.ExitCode = 1;
    }
    private static string Terminal(string value) => new(value.Where(c => !char.IsControl(c)).ToArray());
}
