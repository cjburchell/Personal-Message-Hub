namespace MessageHub.Core;

public sealed record ArchiveEvidence(string Id, string Source, string ConversationId, DateTimeOffset SentAt,
    string Subject, string Excerpt, bool IsOutgoing, bool LatestInConversation, bool Truncated, string Sender = "");
public sealed record AnswerStatement(string Text, IReadOnlyList<string> EvidenceIds);
public sealed record AnswerAnalysis(IReadOnlyList<AnswerStatement> Statements, int InputTokens = 0, int OutputTokens = 0);
public interface IQuestionAnswerer
{
    Task<string> GetIdentityAsync(CancellationToken ct = default);
    Task<AnswerAnalysis> AnswerAsync(string question, IReadOnlyList<ArchiveEvidence> evidence,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
}
public sealed record ArchiveAnswer(DateTimeOffset Start, DateTimeOffset End, ReportSummary Counts,
    IReadOnlyList<string> Warnings, IReadOnlyList<ArchiveEvidence> Evidence, IReadOnlyList<AnswerStatement> Statements,
    string AnalysisStatus, bool Cached, int InputTokens, int OutputTokens);
public sealed record ArchiveMessage(string Id, string Source, string Sender, string? Subject, string Body,
    DateTimeOffset SentAt, bool Truncated);
public sealed record ArchiveReport(string Id, DateTimeOffset Start, DateTimeOffset End, string Markdown, bool Truncated);
