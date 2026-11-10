using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageHub.Core;
using MessageHub.Storage;

namespace MessageHub.Reporting;

public sealed class ReportService(SqliteMessageStore store, IReportAnalyzer? analyzer = null, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private const string Policy = "pmh-report-v1";

    public async Task<StoredReport> BuildAsync(DateTimeOffset start, DateTimeOffset end, string? reportId = null, CancellationToken ct = default)
    {
        if (start >= end || end - start > TimeSpan.FromDays(366)) throw new ArgumentException("Invalid report interval.");
        using var reportLock = store.AcquireReportLock();
        if (reportId is not null && await store.GetReportAsync(reportId, ct) is { } existing) return existing;
        IReadOnlyList<UnifiedMessage> messages;
        IReadOnlyList<SourceStatus> statuses;
        IReadOnlyList<string> sources;
        using (store.AcquireSyncLock())
        {
            messages = await store.BetweenAsync(start, end, ct);
            statuses = await store.GetStatusesAsync(ct);
            sources = await store.GetArchivedSourcesAsync(ct);
        }
        var candidates = ReportCandidateSelector.Select(messages).ToList();
        // Serialized UTF-8, including metadata/escaping, has a hard budget rather than an estimated token count.
        while (candidates.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(candidates).Length > ReportCandidateSelector.MaximumPayloadBytes)
            candidates.RemoveAt(candidates.Count - 1);
        ReportAnalysis? analysis = null;
        var aiStatus = analyzer is null ? "disabled" : "skipped: no shortlisted messages";
        if (analyzer is not null && candidates.Count > 0)
        {
            try
            {
                var identity = await analyzer.GetIdentityAsync(ct);
                var key = Hash(Policy + "\n" + identity + "\n" + JsonSerializer.Serialize(candidates));
                var cached = await store.GetAnalysisCacheAsync(key, ct);
                analysis = cached is null ? await analyzer.AnalyzeAsync(candidates, ct)
                    : JsonSerializer.Deserialize<ReportAnalysis>(cached) ?? throw new InvalidDataException("Invalid analysis cache.");
                Validate(analysis, candidates);
                if (cached is null) await store.SaveAnalysisCacheAsync(key, JsonSerializer.Serialize(analysis), ct);
                aiStatus = cached is null ? $"local model; input tokens {analysis.InputTokens}, output tokens {analysis.OutputTokens}"
                    : "cached local analysis; no generation tokens used";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                analysis = null;
                aiStatus = "unavailable or invalid output; deterministic signals only";
            }
        }
        ct.ThrowIfCancellationRequested();
        var id = reportId ?? "report-" + Guid.NewGuid().ToString("N");
        var markdown = ReportRenderer.Render(start, end, messages, statuses, sources, candidates, analysis, aiStatus);
        var proposals = candidates.SelectMany(candidate => candidate.Signals.Where(x => x is "action" or "reply" or "deadline")
            .Select(kind => new ActionProposal(Hash(kind + ":" + candidate.Id), candidate.Source, candidate.ConversationId,
                candidate.Id, kind + ": " + candidate.Subject, candidate.DueDate))).ToArray();
        var report = new StoredReport(id, _time.GetUtcNow(), start, end, markdown);
        // AI findings are advisory report text; only deterministic proposals enter action_items.
        await store.SaveReportAsync(report, proposals, ct);
        return report;
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static void Validate(ReportAnalysis analysis, IReadOnlyList<ReportCandidate> candidates)
    {
        if (analysis.Findings is null || analysis.Findings.Count > 8 || analysis.InputTokens < 0 || analysis.OutputTokens < 0)
            throw new InvalidDataException("Invalid analysis.");
        var byId = candidates.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var item in analysis.Findings)
        {
            if (item is null || item.Kind is not ("important" or "reply" or "deadline" or "action")
                || string.IsNullOrWhiteSpace(item.Summary) || item.Summary.Length > 240
                || item.EvidenceIds is null || item.EvidenceIds.Count is < 1 or > 3
                || item.EvidenceIds.Any(id => id is null || !byId.ContainsKey(id))
                || item.Kind == "reply" && item.EvidenceIds.Any(id => !byId[id].Signals.Contains("reply")))
                throw new InvalidDataException("Uncited or invalid analysis finding.");
        }
    }
}
