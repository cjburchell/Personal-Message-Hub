using System.Globalization;
using System.Text.RegularExpressions;

namespace MessageHub.Core;

public static partial class ReportCandidateSelector
{
    public const int MaximumCandidates = 20;
    public const int MaximumExcerptCharacters = 500;
    public const int MaximumSubjectCharacters = 120;
    public const int MaximumPayloadBytes = 12000;

    [GeneratedRegex(@"\b(please|could you|can you|would you|need you|action required|reply|respond)\b|\?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Request();
    [GeneratedRegex(@"\b(due|deadline|by|expires?|urgent|important|interview|offer|invoice)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Important();
    [GeneratedRegex(@"\b(due|deadline|expires?|by (monday|tuesday|wednesday|thursday|friday|saturday|sunday|today|tomorrow|\d{4}-\d{2}-\d{2}))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Deadline();
    [GeneratedRegex(@"\b(?:due|deadline|expires?|by)\s*(?:(?:on|is|date)\s*)?[:\-]?\s*(?<date>\d{4}-\d{2}-\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Date();

    public static IReadOnlyList<ReportCandidate> Select(IReadOnlyList<UnifiedMessage> messages)
    {
        var latest = messages.GroupBy(x => (x.Source, x.ConversationId))
            .ToDictionary(x => x.Key, x => x.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.IsOutgoing).First());
        var candidates = new List<ReportCandidate>();
        foreach (var message in messages)
        {
            // Bound regex work as well as the evidence passed to a model. Truncation is disclosed in reports.
            var subject = Clip(message.Subject ?? "(no subject)", MaximumSubjectCharacters);
            var excerpt = Clip(message.Body, MaximumExcerptCharacters);
            var text = subject + " " + excerpt;
            var signals = new List<string>();
            var request = !message.IsOutgoing && Request().IsMatch(text);
            if (request && ReferenceEquals(latest[(message.Source, message.ConversationId)], message)) signals.Add("reply");
            if (signals.Contains("reply")) signals.Add("action");
            if (Deadline().IsMatch(text)) signals.Add("deadline");
            if (Important().IsMatch(text)) signals.Add("important");
            if (signals.Count == 0) continue;
            var date = Date().Matches(text).Select(x => x.Groups["date"].Value)
                .FirstOrDefault(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            // Dates are date-only evidence, never inferred time zones or invented relative-date resolutions.
            candidates.Add(new(MessageIdentity.For(message), message.Source, message.ConversationId,
                message.SentAt, subject, excerpt, signals, signals.Contains("deadline") ? date : null));
        }
        return candidates.OrderByDescending(x => x.Signals.Contains("deadline"))
            .ThenByDescending(x => x.Signals.Contains("reply"))
            .ThenByDescending(x => x.Signals.Count).ThenByDescending(x => x.SentAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal).Take(MaximumCandidates).ToArray();
    }

    public static string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit];
}
