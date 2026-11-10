using System.Net;
using System.Text;
using MessageHub.Core;

namespace MessageHub.Reporting;

public static class ReportRenderer
{
    public static string Text(string text) => WebUtility.HtmlEncode(new string(text.Where(c => !char.IsControl(c)).ToArray()))
        .Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_");

    public static string Render(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<UnifiedMessage> messages,
        IReadOnlyList<SourceStatus> statuses, IReadOnlyList<string> sources, IReadOnlyList<ReportCandidate> candidates,
        ReportAnalysis? analysis, string aiStatus)
    {
        var counts = ReportSummaryBuilder.Build(messages);
        var output = new StringBuilder();
        output.AppendLine($"# Message report: {start:u} to {end:u}");
        output.AppendLine("UTC interval includes the start and excludes the end.");
        output.AppendLine($"Total: {counts.MessageCount}; incoming: {counts.IncomingCount}; outgoing: {counts.OutgoingCount}");
        foreach (var pair in counts.BySource.OrderBy(x => x.Key, StringComparer.Ordinal)) output.AppendLine($"- {Text(pair.Key)}: {pair.Value}");
        output.AppendLine().AppendLine("## Source coverage and freshness");
        foreach (var provider in new[] { "gmail", "yahoo" })
            if (!statuses.Any(x => x.Source == provider || x.Source.StartsWith(provider + ":", StringComparison.Ordinal)))
                output.AppendLine($"- WARNING: {provider} has no recorded provider sync; imported messages alone do not prove inbox coverage.");
        foreach (var source in sources.Concat(statuses.Select(x => x.Source)).Distinct().Order(StringComparer.Ordinal))
        {
            var status = statuses.FirstOrDefault(x => x.Source == source);
            var label = Text(source);
            if (source == "demo") { output.AppendLine("- demo: synthetic imports only; no live-provider coverage."); continue; }
            if (status?.LastSuccessfulSync is not { } success)
                output.AppendLine($"- WARNING: {label}: never successfully synchronized; coverage is unknown.");
            else
            {
                output.AppendLine($"- {label}: last successful run started {success:u}.");
                if (success < end.AddHours(-24)) output.AppendLine($"  WARNING: {label}: stale by more than 24 hours relative to this report end.");
                if (success > end) output.AppendLine($"  Note: {label}: sync occurred after this historical window; this does not prove past coverage.");
            }
            if (status?.Error is not null) output.AppendLine($"- WARNING: {label}: latest sync failed; archived data may be incomplete.");
        }
        output.AppendLine("LinkedIn/Messenger live/export adapters are not implemented; normalized imports do not establish provider freshness.");
        if (messages.Count == 0) output.AppendLine("No archived messages in this interval. This does not prove there were no messages at the providers.");
        output.AppendLine().AppendLine("## Proposed signals to review");
        output.AppendLine("Heuristic candidates, not confirmed obligations. Reply status is inferred from the last archived message in each conversation within this interval.");
        if (candidates.Count == 0) output.AppendLine("No candidates in the bounded shortlist. This is not proof that no action is needed.");
        foreach (var candidate in candidates)
        {
            output.AppendLine($"- {string.Join(", ", candidate.Signals)}: {Text(candidate.Subject)} (evidence: `{candidate.Id}`)"
                + (candidate.DueDate is null ? "" : $"; explicit date: {candidate.DueDate} (date only; verify timezone and intent)"));
        }
        output.AppendLine().AppendLine("## Local AI analysis");
        output.AppendLine("Status: " + aiStatus + ".");
        if (analysis is not null)
        {
            output.AppendLine("AI findings are unverified suggestions; citations establish the evidence source, not factual correctness. No tools or inbox actions were invoked.");
            foreach (var finding in analysis.Findings)
                output.AppendLine($"- {finding.Kind}: {Text(finding.Summary)} (evidence: {string.Join(", ", finding.EvidenceIds.Select(x => "`" + x + "`"))})");
        }
        output.AppendLine().AppendLine("## Shortlisted evidence");
        output.AppendLine($"Analyzed shortlist: {candidates.Count} of {messages.Count} archived messages; at most {ReportCandidateSelector.MaximumCandidates} candidates. Subjects/excerpts are truncated; analysis is not exhaustive.");
        foreach (var candidate in candidates)
            output.AppendLine($"- `{candidate.Id}` | {Text(candidate.Source)} | {candidate.SentAt:u} | {Text(candidate.Subject)}: {Text(candidate.Excerpt)}");
        return output.ToString();
    }
}
