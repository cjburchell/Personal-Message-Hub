using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MessageHub.Core;

namespace MessageHub.Connectors.Exports;

public sealed partial class MessengerExportReader(string path, ExportReaderOptions options) : MessageExportReader(path, options, "messenger")
{
    private static readonly HashSet<string> SystemTypes = new(["Call", "Subscribe", "Unsubscribe", "ThreadName", "ThreadImage", "ChangeNickname", "Join", "Leave", "Unsent"], StringComparer.OrdinalIgnoreCase);
    [GeneratedRegex(@"_(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreadSuffix();
    private string Text(string value) => ExportText.Normalize(Options.RepairEncoding ? ExportText.Repair(value) : value);
    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new InvalidDataException("Invalid object or duplicate export properties.");
    }
    private static string? Identifier(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var id) || id.ValueKind == JsonValueKind.Null) return null;
        var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out var number)
            ? number.ToString(CultureInfo.InvariantCulture) : throw new InvalidDataException("Invalid native identifier.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) throw new InvalidDataException("Invalid native identifier size.");
        return text;
    }
    private string? Optional(JsonElement element, string name, int limit = 1024 * 1024)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected export text.");
        var text = value.GetString()!; if (text.Length > limit) throw new InvalidDataException("Export field exceeds limit.");
        return Text(text);
    }
    protected override IEnumerable<UnifiedMessage> Parse(byte[] data)
    {
        var json = data.AsMemory();
        if (data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        Object(root);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("participants", out var participantData) || participantData.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || participantData.GetArrayLength() is < 1 or > 1000)
            throw new InvalidDataException("Unsupported Messenger conversation JSON.");
        var participants = participantData.EnumerateArray().Select(x => { Object(x); return Optional(x, "name", 512) ?? throw new InvalidDataException("Missing participant name."); }).ToArray();
        if (participants.Any(string.IsNullOrWhiteSpace) || participants.Select(ExportText.Key).Distinct(StringComparer.Ordinal).Count() != participants.Length)
            throw new InvalidDataException("Ambiguous participant display names.");
        var conversation = Identifier(root, "thread_id");
        if (string.IsNullOrWhiteSpace(conversation))
        {
            var thread = Optional(root, "thread_path", 2048) ?? throw new InvalidDataException("A stable thread path is required.");
            var parts = thread.Replace('\\', '/').Split('/');
            if (thread.StartsWith('/') || parts.Any(x => x is "." or ".." || x.Contains(':')) || parts[^1].Length == 0)
                throw new InvalidDataException("Invalid thread path.");
            var suffix = ThreadSuffix().Match(parts[^1]);
            conversation = suffix.Success ? suffix.Groups[1].Value : parts[^1];
        }
        var ownerInThread = participants.Any(x => Self.Contains(ExportText.Identity(x)))
            || messages.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("sender_name", out var sender)
                && sender.ValueKind == JsonValueKind.String && Self.Contains(ExportText.Identity(Text(sender.GetString()!))));
        if (!ownerInThread) throw new InvalidDataException("Owner identifiers do not match conversation participants.");
        OwnerSeen = true;
        var title = Optional(root, "title", 4096) ?? "Messenger conversation";
        foreach (var item in messages.EnumerateArray())
        {
            Record(); Object(item);
            if (item.TryGetProperty("is_unsent", out var unsent))
            {
                if (unsent.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid unsent flag.");
                if (unsent.GetBoolean()) { Unsent++; continue; }
            }
            var type = Optional(item, "type", 64) ?? "Generic";
            if (SystemTypes.Contains(type)) { SystemRecords++; continue; }
            if (!type.Equals("Generic", StringComparison.OrdinalIgnoreCase) && !type.Equals("Share", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported Messenger record type.");
            var senderName = Optional(item, "sender_name", 512);
            if (string.IsNullOrWhiteSpace(senderName) || !item.TryGetProperty("timestamp_ms", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out var milliseconds))
                throw new InvalidDataException("Missing sender or millisecond timestamp.");
            var sent = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            var content = Optional(item, "content"); var media = new List<string>();
            foreach (var kind in new[] { "photos", "videos", "audio_files", "gifs", "files" })
                if (item.TryGetProperty(kind, out var array))
                {
                    if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid attachment list.");
                    if (array.GetArrayLength() > 0) media.Add(kind + " (" + array.GetArrayLength().ToString(CultureInfo.InvariantCulture) + ")");
                }
            foreach (var kind in new[] { "sticker", "share" })
                if (item.TryGetProperty(kind, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid attachment metadata.");
                    media.Add(kind);
                }
            var attachmentOnly = string.IsNullOrWhiteSpace(content) && media.Count > 0;
            if (attachmentOnly) { AttachmentOnly++; content = "[Attachment-only message: " + string.Join(", ", media) + ". Media and links were not imported.]"; }
            if (string.IsNullOrWhiteSpace(content)) { Empty++; continue; }
            var native = Identifier(item, "message_id");
            var id = Id(native, conversation, ExportText.Key(senderName), milliseconds.ToString(CultureInfo.InvariantCulture), content);
            yield return new(Source, id, conversation, senderName, participants.Where(x => ExportText.Key(x) != ExportText.Key(senderName)).ToArray(),
                title, content, sent, Self.Contains(ExportText.Identity(senderName)));
        }
    }
}
