using MessageHub.Core;
using MessageHub.Storage;

namespace MessageHub.Query;

public sealed class EmailArchiveQueries(SqliteMessageStore store)
{
    private static void Validate(string? value, int maximum)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maximum)) throw new ArgumentException("Invalid email query argument.");
    }
    public Task<EmailPage> SearchAsync(string? query = null, string? source = null, string? folder = null, string? contact = null,
        DateTimeOffset? start = null, DateTimeOffset? end = null, int offset = 0, int limit = 20,
        string? threadId = null, CancellationToken ct = default)
    {
        Validate(query, 1000); Validate(source, 200); Validate(folder, 2048); Validate(contact, 320); Validate(threadId, 200);
        if (offset is < 0 or > 1000000 || limit is < 1 or > 20 || start >= end) throw new ArgumentException("Invalid email query bounds.");
        return store.SearchEmailAsync(query, source, folder, contact?.Trim().ToLowerInvariant(), start, end, offset, limit, threadId, ct);
    }
    public async Task<EmailPage> ThreadAsync(string messageId, int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        Validate(messageId, 1000);
        if (string.IsNullOrWhiteSpace(messageId) || offset is < 0 or > 1000000 || limit is < 1 or > 20) throw new ArgumentException("Invalid thread query.");
        var thread = await store.GetEmailThreadIdAsync(messageId, ct);
        if (thread is null) return new([], null, ["No Thunderbird thread metadata exists for this message."]);
        return await SearchAsync(offset: offset, limit: limit, threadId: thread, ct: ct);
    }
    public Task<EmailPage> ContactAsync(string address, string? source = null, DateTimeOffset? start = null,
        DateTimeOffset? end = null, int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address) || !address.Contains('@')) throw new ArgumentException("Supply an email address.");
        return SearchAsync(source: source, contact: address, start: start, end: end, offset: offset, limit: limit, ct: ct);
    }
}
