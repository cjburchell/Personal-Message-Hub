namespace MessageHub.Core;

public sealed record UnifiedMessage(
    string Source,
    string SourceMessageId,
    string ConversationId,
    string Sender,
    IReadOnlyList<string> Recipients,
    string? Subject,
    string Body,
    DateTimeOffset SentAt,
    bool IsOutgoing,
    string? OriginalUrl = null);

public sealed record MessageHit(
    string Id,
    string Source,
    string Sender,
    string? Subject,
    string Body,
    DateTimeOffset SentAt,
    string? OriginalUrl);

public sealed record SourceStatus(string Source, DateTimeOffset? LastSuccessfulSync, string? Error);

public sealed record ReportSummary(int MessageCount, int IncomingCount, int OutgoingCount,
    IReadOnlyDictionary<string, int> BySource);
