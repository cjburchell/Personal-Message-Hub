using System.Globalization;
using System.Text.RegularExpressions;
using MessageHub.Core;
using MessageHub.Reporting;
using MessageHub.Storage;

namespace MessageHub.Cli;

internal static class ReportCommands
{
    private sealed record Options(int Days, bool Ai, string? Model, bool Once, bool Sync, DateTimeOffset? End, WeeklySchedule Schedule);

    private static Options Parse(string[] args)
    {
        var worker = args[0] == "worker";
        var days = 7; var ai = false; var once = false; var sync = false;
        string? model = null;
        var zone = Environment.GetEnvironmentVariable("MESSAGEHUB_REPORT_TIMEZONE") ?? "America/Toronto";
        var day = DayOfWeek.Monday; var hour = 8;
        DateTimeOffset? end = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            var key = option.StartsWith("--", StringComparison.Ordinal) ? option : "days";
            if (!seen.Add(key)) throw new ArgumentException("Repeated report option.");
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            switch (option)
            {
                case "--ai": ai = true; break;
                case "--model": model = Value(); break;
                case "--once" when worker: once = true; break;
                case "--sync" when worker: sync = true; break;
                case "--timezone" when worker: zone = Value(); break;
                case "--day" when worker:
                    if (!Enum.TryParse(Value(), ignoreCase: true, out day) || !Enum.IsDefined(day)) throw new ArgumentException("Invalid weekday.");
                    break;
                case "--hour" when worker:
                    if (!int.TryParse(Value(), out hour) || hour is < 0 or > 23) throw new ArgumentException("Invalid hour.");
                    break;
                case "--end" when !worker:
                    var value = Value();
                    if (!Regex.IsMatch(value, @"(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
                        || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        throw new ArgumentException("Use an ISO timestamp with an offset.");
                    end = parsed;
                    break;
                default:
                    if (worker || !int.TryParse(option, out days) || days is < 1 or > 365) throw new ArgumentException("Invalid report option.");
                    break;
            }
        }
        if (model is not null && !ai) throw new ArgumentException("Use --ai with --model.");
        model ??= ai ? Environment.GetEnvironmentVariable("MESSAGEHUB_OLLAMA_MODEL") : null;
        if (ai && string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Select an installed local model with --model.");
        return new(days, ai, model, once, sync, end, new WeeklySchedule(worker ? TimeZoneInfo.FindSystemTimeZoneById(zone) : TimeZoneInfo.Utc, day, hour));
    }

    public static async Task RunAsync(string[] args, SqliteMessageStore store, CancellationToken ct)
    {
        var options = Parse(args);
        var endpoint = Environment.GetEnvironmentVariable("MESSAGEHUB_OLLAMA_ENDPOINT");
        using var analyzer = options.Ai ? new OllamaAnalyzer(options.Model!, endpoint is null ? null : new Uri(endpoint)) : null;
        var reports = new ReportService(store, analyzer);
        if (args[0] == "report")
        {
            var end = options.End ?? DateTimeOffset.UtcNow;
            var report = await reports.BuildAsync(end.AddDays(-options.Days), end, ct: ct);
            Console.WriteLine(report.Markdown);
            Console.WriteLine("Saved report: " + report.Id);
            return;
        }
        async Task Sync(CancellationToken token)
        {
            foreach (var provider in new[] { "gmail", "yahoo" })
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                try { await MailCommands.RunAsync(["sync", provider], store, timeout.Token); }
                catch (Exception) when (!token.IsCancellationRequested)
                { Console.Error.WriteLine(provider + " sync did not complete; report coverage will reflect available status."); }
            }
        }
        var job = new WeeklyReportJob(store, reports, options.Schedule, beforeReport: options.Sync ? Sync : null);
        Console.WriteLine($"Weekly worker: {options.Schedule.Day} at {options.Schedule.Hour:00}:00 in {options.Schedule.TimeZone.Id}. Latest due interval only; reports remain local.");
        do
        {
            try
            {
                var report = await job.RunDueAsync(ct);
                if (report is not null) Console.WriteLine("Saved report: " + report.Id);
                else if (options.Once) Console.WriteLine("Latest weekly report already exists.");
            }
            catch (Exception) when (!options.Once && !ct.IsCancellationRequested)
            { Console.Error.WriteLine("Weekly report did not complete; retrying on the next poll. Check database access and concurrent processes."); }
            if (options.Once) return;
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        } while (!ct.IsCancellationRequested);
    }
}
