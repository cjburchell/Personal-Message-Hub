using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MessageHub.Core;
using MimeKit;

namespace MessageHub.Connectors;

public sealed record YahooCredentials(string Address, string AppPassword)
{
    public override string ToString() => "YahooCredentials { [redacted] }";
}
public sealed record MailFolderState(string Name, uint UidValidity, uint UpperUid, bool IsSent);
public sealed record YahooCursor(uint UidValidity, uint LastUid);
public sealed record YahooMessage(MimeMessage Mime, DateTimeOffset InternalDate);

public interface IYahooReader : IAsyncDisposable
{
    Task<IReadOnlyList<string>> GetFolderNamesAsync(CancellationToken ct);
    Task<MailFolderState> OpenReadOnlyAsync(string folder, CancellationToken ct);
    Task<IReadOnlyList<uint>> SearchAsync(uint afterUid, DateTimeOffset? initialSince, CancellationToken ct);
    Task<YahooMessage?> GetAsync(uint uid, CancellationToken ct);
}

public sealed class YahooReader : IYahooReader
{
    private readonly ImapClient _client;
    private IMailFolder? _folder;
    private string? _sentName;

    public YahooReader() : this(new ImapClient { Timeout = 60000 }) { }
    internal YahooReader(ImapClient client) => _client = client;

    public async Task ConnectAsync(YahooCredentials credentials, CancellationToken ct)
    {
        await _client.ConnectAsync("imap.mail.yahoo.com", 993, SecureSocketOptions.SslOnConnect, ct);
        // No protocol logger or custom certificate validation callback: platform TLS validation is mandatory.
        await _client.AuthenticateAsync(credentials.Address, credentials.AppPassword, ct);
    }

    public async Task<IReadOnlyList<string>> GetFolderNamesAsync(CancellationToken ct)
    {
        var folders = await _client.GetFoldersAsync(_client.PersonalNamespaces[0], cancellationToken: ct);
        var sent = folders.FirstOrDefault(x => (x.Attributes & FolderAttributes.Sent) != 0)
            ?? folders.FirstOrDefault(x => x.FullName is "Sent" or "Sent Items");
        if (sent is null) throw new InvalidOperationException("Sent folder not found.");
        _sentName = sent.FullName;
        return new[] { _client.Inbox.FullName, sent.FullName }.Distinct().ToArray();
    }

    public async Task<MailFolderState> OpenReadOnlyAsync(string folder, CancellationToken ct)
    {
        if (_folder?.IsOpen == true) await _folder.CloseAsync(expunge: false, ct);
        _folder = await _client.GetFolderAsync(folder, ct);
        await _folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var next = _folder.UidNext?.Id ?? throw new InvalidDataException("Missing UIDNEXT.");
        return new(folder, _folder.UidValidity, next - 1, folder == _sentName);
    }

    public async Task<IReadOnlyList<uint>> SearchAsync(uint afterUid, DateTimeOffset? initialSince, CancellationToken ct)
    {
        if (afterUid == uint.MaxValue) return [];
        SearchQuery query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(afterUid + 1), UniqueId.MaxValue));
        if (initialSince is { } since)
            query = query.And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)));
        return (await _folder!.SearchAsync(query, ct)).Select(x => x.Id).ToArray();
    }

    public async Task<YahooMessage?> GetAsync(uint uid, CancellationToken ct)
    {
        var id = new UniqueId(uid);
        var summaries = await _folder!.FetchAsync(new[] { id }, MessageSummaryItems.InternalDate, ct);
        if (summaries.Count == 0) return null; // Expunged concurrently.
        var date = summaries[0].InternalDate ?? throw new InvalidDataException("Missing internal date.");
        return new(await _folder!.GetMessageAsync(id, ct), date);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { if (_client.IsConnected) await _client.DisconnectAsync(quit: true, timeout.Token); }
        finally { _client.Dispose(); }
    }
}

public sealed class YahooConnector(IYahooReader reader, string account) : IIncrementalMessageConnector
{
    public string Source { get; } = MailNormalizer.AccountSource("yahoo", account);

    public async IAsyncEnumerable<MessageBatch> FetchBatchesAsync(string? cursor, DateTimeOffset initialSince,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var checkpoints = cursor is null ? new Dictionary<string, YahooCursor>()
            : JsonSerializer.Deserialize<Dictionary<string, YahooCursor>>(cursor)
                ?? throw new InvalidDataException("Invalid Yahoo checkpoint.");
        var next = new Dictionary<string, YahooCursor>();
        foreach (var folderName in await reader.GetFolderNamesAsync(cancellationToken))
        {
            var folder = await reader.OpenReadOnlyAsync(folderName, cancellationToken);
            var incremental = checkpoints.TryGetValue(folder.Name, out var previous) && previous is not null && previous.UidValidity == folder.UidValidity;
            var after = incremental ? previous!.LastUid : 0;
            if (after > folder.UpperUid) throw new InvalidDataException("UID checkpoint exceeds mailbox state.");
            var ids = await reader.SearchAsync(after, incremental ? null : initialSince, cancellationToken);
            foreach (var chunk in ids.Where(x => x > after && x <= folder.UpperUid).Distinct().Order().Chunk(100))
            {
                var messages = new List<UnifiedMessage>();
                foreach (var uid in chunk)
                {
                    var message = await reader.GetAsync(uid, cancellationToken);
                    if (message is null || (!incremental && message.InternalDate < initialSince)) continue;
                    var id = Uri.EscapeDataString(folder.Name) + ":" + folder.UidValidity.ToString(CultureInfo.InvariantCulture)
                        + ":" + uid.ToString(CultureInfo.InvariantCulture);
                    var conversation = message.Mime.References.FirstOrDefault() ?? message.Mime.InReplyTo ?? message.Mime.MessageId ?? id;
                    messages.Add(MailNormalizer.Normalize(message.Mime, Source, id, conversation, message.InternalDate, folder.IsSent));
                }
                yield return new(messages);
            }
            next[folder.Name] = new(folder.UidValidity, folder.UpperUid);
        }
        if (next.Count == 0) throw new InvalidDataException("No mailboxes selected.");
        yield return new([], JsonSerializer.Serialize(next));
    }
}
