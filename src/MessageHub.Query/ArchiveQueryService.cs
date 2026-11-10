using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageHub.Core;
using MessageHub.Storage;

namespace MessageHub.Query;

public sealed class QueryBusyException : Exception;
public sealed class ArchiveQueryService(SqliteMessageStore store, IQuestionAnswerer? answerer = null, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _generation = new(1, 1);
    public const int MaximumEvidenceBytes = 10000;
    public bool AiAvailable => answerer is not null;

    public async Task<ArchiveAnswer> SearchAsync(string query, int days = 30, CancellationToken ct = default)
    {
        var terms = QueryTerms.Extract(query);
        if (days is < 1 or > 365) throw new ArgumentException("Choose 1 through 365 days.");
        var end = _time.GetUtcNow(); var start = end.AddDays(-days);
        IReadOnlyList<ArchiveEvidence> evidence;
        ReportSummary counts;
        IReadOnlyList<SourceStatus> statuses;
        IReadOnlyList<string> sources;
        using (store.AcquireSyncLock())
        {
            counts = await store.CountBetweenAsync(start, end, ct);
            evidence = terms.Count == 0 ? [] : await store.SearchTermsAsync(terms, start, end, ct: ct);
            statuses = await store.GetStatusesAsync(ct);
            sources = await store.GetArchivedSourcesAsync(ct);
        }
        var bounded = evidence.ToList();
        while (bounded.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(bounded).Length > MaximumEvidenceBytes) bounded.RemoveAt(bounded.Count - 1);
        var warnings = Coverage(statuses, sources, end).ToList();
        warnings.Add("Keyword retrieval is limited to ten ranked excerpts and may omit relevant messages. Counts cover the entire selected interval.");
        if (bounded.Count == 0) warnings.Add("No archived keyword matches. This does not prove there are no relevant messages or obligations.");
        return new(start, end, counts, warnings, bounded, [], "Evidence only", false, 0, 0);
    }

    public async Task<ArchiveAnswer> AskAsync(string question, int days = 30, bool useAi = false, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(question) > 4000) throw new ArgumentException("Question is too large.");
        var search = await SearchAsync(question, days, ct);
        if (!useAi || answerer is null || search.Evidence.Count == 0)
            return search with { AnalysisStatus = useAi && answerer is null ? "Local AI is not configured; showing evidence" : search.AnalysisStatus };
        if (JsonSerializer.SerializeToUtf8Bytes(new { question, start = search.Start, end = search.End, untrustedEvidence = search.Evidence }).Length > 16000)
            return search with { AnalysisStatus = "Question and evidence exceed the local AI budget; showing evidence" };
        if (!await _generation.WaitAsync(0, ct)) throw new QueryBusyException();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                var identity = await answerer.GetIdentityAsync(deadline.Token);
                // Minute precision avoids duplicate generation on repeated clicks without reusing relative-time answers indefinitely.
                var minute = search.End.ToUniversalTime().ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
                var input = JsonSerializer.Serialize(new { question, days, minute, search.Counts, search.Evidence });
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pmh-qa-v1\n" + identity + "\n" + input)));
                var cached = await store.GetAnalysisCacheAsync(key, ct);
                var analysis = cached is null ? await answerer.AnswerAsync(question, search.Evidence, search.Start, search.End, deadline.Token)
                    : JsonSerializer.Deserialize<AnswerAnalysis>(cached) ?? throw new InvalidDataException();
                Validate(analysis, search.Evidence);
                ct.ThrowIfCancellationRequested();
                if (cached is null) await store.SaveAnalysisCacheAsync(key, JsonSerializer.Serialize(analysis), ct);
                return search with { Statements = analysis.Statements, Cached = cached is not null,
                    AnalysisStatus = cached is null ? "Local AI suggestions; verify cited evidence" : "Cached local AI suggestions; verify cited evidence",
                    InputTokens = cached is null ? analysis.InputTokens : 0, OutputTokens = cached is null ? analysis.OutputTokens : 0 };
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            { return search with { AnalysisStatus = "Local AI is unavailable or returned invalid citations; showing evidence" }; }
        }
        finally { _generation.Release(); }
    }

    public static void Validate(AnswerAnalysis analysis, IReadOnlyList<ArchiveEvidence> evidence)
    {
        var ids = evidence.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (analysis.Statements is null || analysis.Statements.Count > 6 || analysis.InputTokens < 0 || analysis.OutputTokens < 0 || analysis.OutputTokens > 900)
            throw new InvalidDataException("Invalid answer.");
        foreach (var statement in analysis.Statements)
            if (statement is null || string.IsNullOrWhiteSpace(statement.Text) || statement.Text.Length > 400
                || statement.EvidenceIds is null || statement.EvidenceIds.Count is < 1 or > 3 || statement.EvidenceIds.Any(x => x is null || !ids.Contains(x)))
                throw new InvalidDataException("Answer must cite retrieved evidence.");
    }

    public async Task<ArchiveMessage?> GetMessageAsync(string id, CancellationToken ct = default)
    {
        ValidateId(id);
        return await store.GetBoundedMessageAsync(id, ct);
    }
    public async Task<ArchiveReport?> GetReportAsync(string id, CancellationToken ct = default)
    {
        ValidateId(id); return await store.GetBoundedReportAsync(id, ct);
    }
    public Task<IReadOnlyList<StoredReport>> ListReportsAsync(CancellationToken ct = default) => store.GetReportsAsync(ct);
    public async Task<IReadOnlyList<SourceStatus>> StatusAsync(CancellationToken ct = default) =>
        (await store.GetStatusesAsync(ct)).Select(x => x with { Error = x.Error is null ? null : "Latest sync failed; archived coverage may be incomplete." }).ToArray();
    private static void ValidateId(string id) { if (string.IsNullOrWhiteSpace(id) || id.Length > 1000) throw new ArgumentException("Invalid archive ID."); }
    private static IEnumerable<string> Coverage(IReadOnlyList<SourceStatus> statuses, IReadOnlyList<string> sources, DateTimeOffset end)
    {
        foreach (var provider in new[] { "gmail", "yahoo" })
            if (!statuses.Any(x => x.Source == provider || x.Source.StartsWith(provider + ":", StringComparison.Ordinal)))
                yield return provider + ": no provider sync recorded; imports alone do not prove coverage.";
        foreach (var source in sources.Concat(statuses.Select(x => x.Source)).Distinct())
        {
            var status = statuses.FirstOrDefault(x => x.Source == source);
            if (source == "demo") { yield return "demo: synthetic data only."; continue; }
            if (status?.LastExportImport is { } import)
            {
                yield return $"{source}: export snapshot imported {import.ImportedAt:u}; coverage is unknown and import time is not provider freshness.";
                if (import.Summary.SkippedRecords > 0 || import.Summary.AttachmentOnlyMessages > 0)
                    yield return $"{source}: {import.Summary.SkippedRecords} skipped export records; {import.Summary.AttachmentOnlyMessages} attachment-only placeholders. Media was not imported.";
                continue;
            }
            if (status?.LastSuccessfulSync is not { } success) yield return source + ": never successfully synchronized; coverage is unknown.";
            else if (success < end.AddHours(-24)) yield return source + ": last successful sync is more than 24 hours old.";
            if (status?.Error is not null) yield return source + ": latest sync failed; coverage may be incomplete.";
        }
        yield return "LinkedIn/Messenger use selected export snapshots; attachment content is not indexed. Empty local counts do not establish empty provider inboxes.";
    }
    public void Dispose() => _generation.Dispose();
}
