using MessageHub.Connectors.Exports;
using MessageHub.Core;
using MessageHub.Storage;

namespace MessageHub.Cli;

internal static class ExportCommands
{
    public static async Task RunAsync(string[] args, SqliteMessageStore store, CancellationToken ct)
    {
        if (args.Length < 2) throw new ArgumentException("Supply an export file, directory or ZIP.");
        string? account = null; TimeZoneInfo? zone = null; var self = new List<string>(); var dryRun = false; var repair = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var option = args[i];
            if (option != "--self" && !seen.Add(option)) throw new ArgumentException("Repeated export option.");
            string Value() => ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)
                ? args[i] : throw new ArgumentException("Missing export option value.");
            switch (option)
            {
                case "--account": account = Value(); break;
                case "--self": self.Add(Value()); break;
                case "--dry-run": dryRun = true; break;
                case "--timezone" when args[0] == "import-linkedin": zone = TimeZoneInfo.FindSystemTimeZoneById(Value()); break;
                case "--repair-encoding" when args[0] == "import-messenger": repair = true; break;
                default: throw new ArgumentException("Unknown export option.");
            }
        }
        var options = new ExportReaderOptions(account ?? throw new ArgumentException("An account label is required."), self, zone, repair);
        IMessageExportReader reader = args[0] == "import-linkedin" ? new LinkedInExportReader(args[1], options) : new MessengerExportReader(args[1], options);
        if (!dryRun) await store.InitializeAsync(ct);
        var result = await store.ImportExportAsync(reader, dryRun, ct); var summary = result.Summary;
        Console.WriteLine($"{(dryRun ? "Dry run" : "Import complete")}: {summary.MessageCount} message rows processed from {summary.FileCount} metadata files; source {result.Source}.");
        Console.WriteLine($"Skipped: {summary.SkippedUnsent} unsent, {summary.SkippedSystem} system, {summary.SkippedEmpty} empty. Attachment-only placeholders: {summary.AttachmentOnlyMessages}.");
        if (summary.ContentDerivedIds > 0) Console.WriteLine($"Warning: {summary.ContentDerivedIds} rows use content-derived IDs; edited or partially overlapping exports have deduplication limits. See docs/export-imports.md.");
        Console.WriteLine("Export snapshot only. Import time and message dates do not establish current provider coverage or successful live sync.");
        if (dryRun) Console.WriteLine("No database was opened or changed.");
        else Console.WriteLine("Provenance: " + result.ImportId);
    }
}
