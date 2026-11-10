using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Google;
using Google.Apis.Gmail.v1;
using MessageHub.Core;
using MimeKit;

namespace MessageHub.Connectors;

public sealed record GmailProfile(string Address, ulong HistoryId);
public sealed record GmailIdPage(IReadOnlyList<string> Ids, string? NextPageToken);
public sealed record GmailHistoryPage(IReadOnlyList<string> Ids, string? NextPageToken, ulong HistoryId);
public sealed record GmailRawMessage(string Id, string ThreadId, byte[] Raw, DateTimeOffset ReceivedAt, bool IsSent);
public sealed class HistoryExpiredException : Exception;

// The adapter exposes only read APIs. Fake implementations require no Google credentials.
public interface IGmailReader
{
    Task<GmailProfile> GetProfileAsync(CancellationToken ct);
    Task<GmailIdPage> ListAsync(DateTimeOffset since, string? pageToken, CancellationToken ct);
    Task<GmailHistoryPage> HistoryAsync(ulong start, string? pageToken, CancellationToken ct);
    Task<GmailRawMessage?> GetAsync(string id, CancellationToken ct);
}

public sealed class GmailReader(GmailService service) : IGmailReader
{
    public async Task<GmailProfile> GetProfileAsync(CancellationToken ct)
    {
        var profile = await service.Users.GetProfile("me").ExecuteAsync(ct);
        return new(profile.EmailAddress, profile.HistoryId ?? throw new InvalidDataException("Missing history ID."));
    }

    public async Task<GmailIdPage> ListAsync(DateTimeOffset since, string? pageToken, CancellationToken ct)
    {
        var request = service.Users.Messages.List("me");
        request.Q = "after:" + since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " -in:drafts";
        request.MaxResults = 100;
        request.PageToken = pageToken;
        var page = await request.ExecuteAsync(ct);
        return new(page.Messages?.Select(x => x.Id).ToArray() ?? [], page.NextPageToken);
    }

    public async Task<GmailHistoryPage> HistoryAsync(ulong start, string? pageToken, CancellationToken ct)
    {
        var request = service.Users.History.List("me");
        request.StartHistoryId = start;
        request.MaxResults = 100;
        request.PageToken = pageToken;
        try
        {
            var page = await request.ExecuteAsync(ct);
            return new(page.History?.SelectMany(x => x.Messages ?? []).Select(x => x.Id).Distinct().ToArray() ?? [],
                page.NextPageToken, page.HistoryId ?? throw new InvalidDataException("Missing history ID."));
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        { throw new HistoryExpiredException(); }
    }

    public async Task<GmailRawMessage?> GetAsync(string id, CancellationToken ct)
    {
        var request = service.Users.Messages.Get("me", id);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
        try
        {
            var message = await request.ExecuteAsync(ct);
            if (message.LabelIds?.Any(x => x is "DRAFT" or "SPAM" or "TRASH") == true) return null;
            var base64 = message.Raw.Replace('-', '+').Replace('_', '/');
            var raw = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            return new(message.Id, message.ThreadId, raw,
                DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate ?? throw new InvalidDataException("Missing date.")),
                message.LabelIds?.Contains("SENT") == true);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound) { return null; }
    }
}

public sealed class GmailConnector(IGmailReader reader, GmailProfile profile) : IIncrementalMessageConnector
{
    public string Source { get; } = MailNormalizer.AccountSource("gmail", profile.Address);

    public async IAsyncEnumerable<MessageBatch> FetchBatchesAsync(string? cursor, DateTimeOffset initialSince,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Capture a baseline before the full scan; changes during it are replayed next run.
        var baseline = await reader.GetProfileAsync(cancellationToken);
        if (!string.Equals(baseline.Address, profile.Address, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authenticated account changed.");
        if (cursor is not null && !ulong.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("Invalid Gmail checkpoint.");
        GmailHistoryPage? history = null;
        if (cursor is not null)
        {
            try { history = await reader.HistoryAsync(ulong.Parse(cursor, CultureInfo.InvariantCulture), null, cancellationToken); }
            catch (HistoryExpiredException) { /* Fall back to the documented 30-day full scan. */ }
        }
        string? pageToken = null;
        ulong checkpoint = baseline.HistoryId;
        do
        {
            IReadOnlyList<string> ids;
            if (history is not null)
            {
                ids = history.Ids;
                pageToken = history.NextPageToken;
                checkpoint = history.HistoryId;
            }
            else
            {
                var page = await reader.ListAsync(initialSince, pageToken, cancellationToken);
                ids = page.Ids;
                pageToken = page.NextPageToken;
            }
            var messages = new List<UnifiedMessage>();
            foreach (var id in ids.Distinct())
            {
                var message = await reader.GetAsync(id, cancellationToken);
                if (message is null) continue; // Deleted between listing and retrieval.
                using var stream = new MemoryStream(message.Raw, writable: false);
                var mime = await MimeMessage.LoadAsync(stream, cancellationToken);
                messages.Add(MailNormalizer.Normalize(mime, Source, message.Id, message.ThreadId,
                    message.ReceivedAt, message.IsSent));
            }
            yield return new(messages);
            if (history is not null && pageToken is not null)
                history = await reader.HistoryAsync(ulong.Parse(cursor!, CultureInfo.InvariantCulture), pageToken, cancellationToken);
        } while (pageToken is not null);
        yield return new([], checkpoint.ToString(CultureInfo.InvariantCulture));
    }
}
