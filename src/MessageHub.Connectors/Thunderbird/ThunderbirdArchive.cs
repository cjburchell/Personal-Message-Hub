using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MessageHub.Core;
using MimeKit;
using MimeKit.IO;

namespace MessageHub.Connectors.Thunderbird;

public sealed class ThunderbirdArchive : ILocalEmailArchive
{
    public const int MaximumMessageBytes = 32 * 1024 * 1024;
    public string RootPath { get; }
    public string RootId { get; }
    public string Source { get; }
    private readonly HashSet<string> _self;
    private readonly string[] _folders;
    public ThunderbirdArchive(string path, string account, IReadOnlyList<string> self, IReadOnlyList<string>? folders = null)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 256 || self.Count is < 1 or > 20
            || self.Any(x => x.Length > 320 || !MailboxAddress.TryParse(x, out var address) || address.Address != x))
            throw new ArgumentException("Supply an account label and owner email addresses.");
        RootPath = Path.GetFullPath(path);
        _self = self.Select(x => x.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        _folders = folders?.ToArray() ?? [];
        if (_folders.Length > 100 || _folders.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 2048)) throw new ArgumentException("Invalid folder selection.");
        Source = MailNormalizer.AccountSource("thunderbird", account.Normalize());
        RootId = Hash("thunderbird-v1\n" + Source + "\n" + RootPath + "\n" + string.Join('\n', _self.Order(StringComparer.Ordinal)));
    }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked inputs are not supported.");
    }
    public IReadOnlyList<EmailInput> Discover(CancellationToken ct = default)
    {
        CheckPath(RootPath); var result = new List<EmailInput>(); var count = 0;
        var root = Directory.Exists(RootPath) ? RootPath : Path.GetDirectoryName(RootPath)!;
        var pending = new Stack<string>(); pending.Push(RootPath);
        // A profile may contain credentials and unrelated databases. Inspect only Mail/ImapMail when present.
        if (Directory.Exists(RootPath))
        {
            var mailRoots = new[] { "Mail", "ImapMail" }.Select(x => Path.Combine(RootPath, x)).Where(Directory.Exists).ToArray();
            if (mailRoots.Length > 0) { pending.Clear(); foreach (var mail in mailRoots) pending.Push(mail); }
        }
        while (pending.TryPop(out var path))
        {
            ct.ThrowIfCancellationRequested(); CheckPath(path);
            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > 1000000) throw new InvalidDataException("Too many input entries.");
                    pending.Push(child);
                }
                continue;
            }
            var ext = Path.GetExtension(path);
            if (ext.Equals(".msf", StringComparison.OrdinalIgnoreCase)) continue;
            var parent = Path.GetFileName(Path.GetDirectoryName(path));
            var perMessage = ext.Equals(".eml", StringComparison.OrdinalIgnoreCase) || parent is "cur" or "new";
            if (!perMessage && ext.Length > 0 && !ext.Equals(".mbox", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var prefix = new byte[5]; var read = stream.Read(prefix);
            var mbox = read == 5 && prefix.AsSpan().SequenceEqual("From "u8);
            if (!perMessage && !mbox && !(stream.Length == 0 && ext.Equals(".mbox", StringComparison.OrdinalIgnoreCase))) continue;
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var folder = mbox ? relative : Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? ".";
            if (parent is "cur" or "new") folder = Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? ".";
            if (folder.Length == 0) folder = ".";
            folder = folder.Replace(".sbd/", "/", StringComparison.OrdinalIgnoreCase);
            if (_folders.Length > 0 && !_folders.Contains(folder, StringComparer.Ordinal)) continue;
            if (relative.Length > 4096 || folder.Length > 2048 || result.Count >= 100000) throw new InvalidDataException("Input inventory limit exceeded.");
            result.Add(new(relative, folder, mbox || !perMessage ? "mbox" : "eml", stream.Length));
        }
        return result.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
    }
    public ThunderbirdFile Open(EmailInput input) => new(this, input, _self);
    ILocalEmailFile ILocalEmailArchive.Open(EmailInput input) => Open(input);
}

public sealed class ThunderbirdFile : ILocalEmailFile
{
    private readonly FileStream _stream;
    private readonly EmailInput _input;
    private readonly ThunderbirdArchive _archive;
    private readonly HashSet<string> _self;
    private readonly DateTime _modified;
    public long Length { get; }
    internal ThunderbirdFile(ThunderbirdArchive archive, EmailInput input, HashSet<string> self)
    {
        _archive = archive; _input = input; _self = self;
        var root = Directory.Exists(archive.RootPath) ? archive.RootPath : Path.GetDirectoryName(archive.RootPath)!;
        var path = Path.GetFullPath(Path.Combine(root, input.RelativePath));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Input outside root.");
        ThunderbirdArchive.CheckPath(path);
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        Length = _stream.Length; _modified = File.GetLastWriteTimeUtc(path);
    }
    public async Task<string> HashPrefixAsync(long length, CancellationToken ct = default)
    {
        if (length < 0 || length > Length) throw new ArgumentOutOfRangeException(nameof(length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); _stream.Position = 0;
        var buffer = new byte[65536]; long remaining = length;
        while (remaining > 0)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new IOException("Input changed."); hash.AppendData(buffer, 0, read); remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public async Task VerifyAsync(string hash, CancellationToken ct = default)
    {
        if (_stream.Length != Length || File.GetLastWriteTimeUtc(_stream.Name) != _modified || await HashPrefixAsync(Length, ct) != hash)
            throw new IOException("Input changed; close Thunderbird or use a private snapshot.");
    }
    public async IAsyncEnumerable<LocalEmailRecord> ReadAsync(long offset, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (offset < 0 || offset > Length || _input.Format == "eml" && offset != 0 && offset != Length) throw new InvalidDataException("Invalid checkpoint.");
        if (Length == 0 && _input.Format == "eml") { ct.ThrowIfCancellationRequested(); yield return new(0, 0, [], null, "empty_eml"); yield break; }
        while (offset < Length)
        {
            ct.ThrowIfCancellationRequested(); _stream.Position = offset;
            var stop = Math.Min(Length, offset + ThunderbirdArchive.MaximumMessageBytes);
            using var bounded = new BoundStream(_stream, offset, stop, leaveOpen: true);
            var options = ParserOptions.Default.Clone(); options.MaxMimeDepth = 32; options.MaxAddressGroupDepth = 8; options.RespectContentLength = false;
            var parser = new MimeParser(options, bounded, _input.Format == "mbox" ? MimeFormat.Mbox : MimeFormat.Entity, persistent: true);
            MimeMessage? mime = null; var malformed = false;
            try { mime = await parser.ParseMessageAsync(ct); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException) { malformed = true; }
            // After an mbox framing failure, resynchronization would risk silently dropping messages.
            if (malformed && _input.Format == "mbox") throw new InvalidDataException("Unreadable mailbox boundary.");
            var next = _input.Format == "eml" ? Length : offset + parser.Position;
            if (next <= offset || stop < Length && next >= stop || next - offset > ThunderbirdArchive.MaximumMessageBytes)
            { mime?.Dispose(); throw new InvalidDataException("Message exceeds the bounded parser window."); }
            var bytes = new byte[checked((int)(next - offset))];
            // Read exact source bytes without seeking the parser's stream while MIME parts still reference it.
            var done = 0;
            while (done < bytes.Length)
            {
                var read = await RandomAccess.ReadAsync(_stream.SafeFileHandle, bytes.AsMemory(done), offset + done, ct);
                if (read == 0) throw new IOException("Input changed."); done += read;
            }
            if (_input.Format == "mbox" && next == Length && bytes[^1] != (byte)'\n')
            { mime?.Dispose(); throw new InvalidDataException("Incomplete final mailbox record."); }
            EmailDocument? document = null; string? issue = malformed ? "malformed_eml" : null;
            using (mime)
            {
                if (mime is not null)
                    try { document = ThunderbirdNormalizer.Normalize(mime, _archive.Source, _self, ct); }
                    catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or NotSupportedException)
                    { issue = "unindexable_mime"; }
            }
            yield return new(offset, next, bytes, document, issue); offset = next;
        }
    }
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
