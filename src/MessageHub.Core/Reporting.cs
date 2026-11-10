namespace MessageHub.Core;

public static class MessageIdentity
{
    public static string For(UnifiedMessage message) =>
        $"{Uri.EscapeDataString(message.Source)}:{Uri.EscapeDataString(message.SourceMessageId)}";
}

public sealed record ReportCandidate(string Id, string Source, string ConversationId, DateTimeOffset SentAt,
    string Subject, string Excerpt, IReadOnlyList<string> Signals, string? DueDate);
public sealed record ReportFinding(string Kind, string Summary, IReadOnlyList<string> EvidenceIds);
public sealed record ReportAnalysis(IReadOnlyList<ReportFinding> Findings, int InputTokens = 0, int OutputTokens = 0);
public sealed record StoredReport(string Id, DateTimeOffset CreatedAt, DateTimeOffset Start, DateTimeOffset End, string Markdown);
public sealed record ActionProposal(string Id, string Source, string ConversationId, string EvidenceId, string Summary, string? DueDate);

public interface IReportAnalyzer
{
    // A local model digest participates in the cache key, so replacing weights invalidates cached output.
    Task<string> GetIdentityAsync(CancellationToken ct = default);
    Task<ReportAnalysis> AnalyzeAsync(IReadOnlyList<ReportCandidate> candidates, CancellationToken ct = default);
}
