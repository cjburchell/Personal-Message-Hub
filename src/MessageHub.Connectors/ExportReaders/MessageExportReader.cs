using System.Globalization;
using System.Runtime.CompilerServices;
using MessageHub.Core;

namespace MessageHub.Connectors.Exports;

public abstract class MessageExportReader(string path, ExportReaderOptions options, string provider) : IMessageExportReader
{
    protected ExportReaderOptions Options { get; } = ValidateOptions(options);
    public string Source { get; } = MailNormalizer.AccountSource(provider, options.Account.Normalize());
    public string Provider { get; } = provider;
    protected HashSet<string> Self { get; } = options.Self.Select(ExportText.Identity).ToHashSet(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _occurrences = new(StringComparer.Ordinal);
    private int _used, _files, _records, _messages, _derived;
    protected int Unsent, SystemRecords, Empty, AttachmentOnly;
    protected bool OwnerSeen;
    private DateTimeOffset? _first, _last;
    private bool _completed;
    private string? _fingerprint;
    public ExportReadSummary Summary => new(_completed, _files, _records, _messages, Unsent, SystemRecords, Empty, AttachmentOnly, _derived, _first, _last, _fingerprint);
    private static ExportReaderOptions ValidateOptions(ExportReaderOptions value) { value.Validate(); return value; }
    protected void Record() { if (++_records > 250000) throw new InvalidDataException("Export record limit exceeded."); }
    protected string Id(string? native, params string[] parts)
    {
        if (!string.IsNullOrWhiteSpace(native)) return "native:" + ExportText.Hash(parts[0], native);
        _derived++; var hash = ExportText.Hash(parts); var occurrence = _occurrences.GetValueOrDefault(hash) + 1; _occurrences[hash] = occurrence;
        return "derived:" + hash + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
    }
    protected abstract IEnumerable<UnifiedMessage> Parse(byte[] data);
    public async IAsyncEnumerable<UnifiedMessage> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("Create a new reader for each import attempt.");
        using var input = new ExportInput(path, Provider);
        foreach (var file in input.Files)
        {
            cancellationToken.ThrowIfCancellationRequested(); var bytes = await input.ReadAsync(file, cancellationToken); _files++;
            foreach (var message in Parse(bytes))
            {
                cancellationToken.ThrowIfCancellationRequested(); _messages++;
                _first = _first is null || message.SentAt < _first ? message.SentAt : _first;
                _last = _last is null || message.SentAt > _last ? message.SentAt : _last;
                yield return message;
            }
        }
        if (_messages > 0 && !OwnerSeen) throw new InvalidDataException("Owner identifiers did not match this export.");
        cancellationToken.ThrowIfCancellationRequested(); _fingerprint = input.FinishFingerprint(); _completed = true;
    }
}
