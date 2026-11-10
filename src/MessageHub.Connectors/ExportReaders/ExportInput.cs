using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MessageHub.Connectors.Exports;

// Never extracts ZIPs or opens attachment paths supplied by message JSON.
internal sealed partial class ExportInput : IDisposable
{
    internal const long MaximumFileBytes = 32L * 1024 * 1024;
    internal const long MaximumTotalBytes = 256L * 1024 * 1024;
    internal const int MaximumFiles = 10000;
    private readonly ZipArchive? _zip;
    private readonly IncrementalHash _fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _readBytes;
    public IReadOnlyList<(string Name, Func<Stream> Open)> Files { get; }
    [GeneratedRegex(@"^message_\d+\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MessageFile();
    private static bool Selected(string name, string provider) => provider == "linkedin"
        ? Path.GetFileName(name).Equals("messages.csv", StringComparison.OrdinalIgnoreCase) : MessageFile().IsMatch(Path.GetFileName(name));
    private static string SafeName(string name)
    {
        name = name.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains('\0') || name.Split('/').Any(x => x is "." or ".." || x.Contains(':')))
            throw new InvalidDataException("Unsafe archive entry path.");
        return name;
    }
    public ExportInput(string path, string provider)
    {
        var selected = new List<(string Name, Func<Stream> Open)>();
        path = Path.GetFullPath(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked inputs are not supported.");
        try
        {
            if (Directory.Exists(path))
            {
                var pending = new Stack<string>(); pending.Push(path); var entries = 0;
                while (pending.TryPop(out var current))
                    foreach (var child in Directory.EnumerateFileSystemEntries(current))
                    {
                        if (++entries > 100000) throw new InvalidDataException("Too many export entries.");
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked inputs are not supported.");
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(child); continue; }
                        if (Selected(child, provider)) { var file = child; CheckLength(new FileInfo(file).Length); selected.Add((SafeName(Path.GetRelativePath(path, file)), () => File.OpenRead(file))); }
                    }
            }
            else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > 1024L * 1024 * 1024) throw new InvalidDataException("ZIP exceeds the input size limit.");
                _zip = ZipFile.OpenRead(path);
                if (_zip.Entries.Count > 100000) throw new InvalidDataException("Too many archive entries.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _zip.Entries)
                {
                    var name = SafeName(entry.FullName);
                    if (!names.Add(name)) throw new InvalidDataException("Duplicate archive entry paths.");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("ZIP links are not supported.");
                    if (!name.EndsWith('/') && Selected(name, provider)) { CheckLength(entry.Length); selected.Add((name, entry.Open)); }
                }
            }
            else if (Path.GetExtension(path).Equals(provider == "linkedin" ? ".csv" : ".json", StringComparison.OrdinalIgnoreCase))
            { CheckLength(new FileInfo(path).Length); selected.Add((Path.GetFileName(path), () => File.OpenRead(path))); }
            else throw new InvalidDataException("Use an export CSV/JSON, directory or ZIP.");
            if (selected.Count is < 1 or > MaximumFiles || provider == "linkedin" && selected.Count != 1)
                throw new InvalidDataException("No supported message files, or an ambiguous export selection.");
            Files = selected.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        }
        catch { _zip?.Dispose(); _fingerprint.Dispose(); throw; }
    }
    private static void CheckLength(long length) { if (length > MaximumFileBytes) throw new InvalidDataException("Message metadata file exceeds its size limit."); }
    public async Task<byte[]> ReadAsync((string Name, Func<Stream> Open) file, CancellationToken ct)
    {
        await using var stream = file.Open(); using var memory = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            _readBytes += count;
            if (memory.Length + count > MaximumFileBytes || _readBytes > MaximumTotalBytes) throw new InvalidDataException("Export metadata exceeds its read limit.");
            memory.Write(buffer, 0, count);
        }
        var bytes = memory.ToArray();
        _fingerprint.AppendData(Encoding.UTF8.GetBytes(file.Name)); _fingerprint.AppendData([0]); _fingerprint.AppendData(SHA256.HashData(bytes));
        return bytes;
    }
    public string FinishFingerprint() => Convert.ToHexString(_fingerprint.GetHashAndReset()).ToLowerInvariant();
    public void Dispose() { _zip?.Dispose(); _fingerprint.Dispose(); }
}
