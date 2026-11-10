using System.Globalization;
using System.Text;
using MessageHub.Core;
using Microsoft.VisualBasic.FileIO;

namespace MessageHub.Connectors.Exports;

public sealed class LinkedInExportReader(string path, ExportReaderOptions options) : MessageExportReader(path, options, "linkedin")
{
    protected override IEnumerable<UnifiedMessage> Parse(byte[] data)
    {
        // The platform CSV parser handles quoted commas, doubled quotes and multiline fields without a new package.
        using var stream = new MemoryStream(data, writable: false);
        using var parser = new TextFieldParser(stream, new UTF8Encoding(false, true), detectEncoding: true)
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException("Missing CSV header.");
        var names = headers.Select((name, index) => (Name: name.Trim().TrimStart('\uFEFF').ToUpperInvariant(), Index: index)).ToArray();
        if (names.Length > 128 || names.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Invalid or duplicate CSV headers.");
        var columns = names.ToDictionary(x => x.Name, x => x.Index, StringComparer.Ordinal);
        foreach (var required in new[] { "CONVERSATION ID", "FROM", "TO", "DATE", "CONTENT" })
            if (!columns.ContainsKey(required)) throw new InvalidDataException("Unsupported LinkedIn CSV header.");
        while (!parser.EndOfData)
        {
            Record(); var row = parser.ReadFields() ?? throw new InvalidDataException("Invalid CSV row.");
            if (row.Length != headers.Length || row.Any(x => x.Length > 1024 * 1024)) throw new InvalidDataException("Invalid CSV record size.");
            string Field(string name) => columns.TryGetValue(name, out var index) ? ExportText.Normalize(row[index]) : "";
            if (Field("FOLDER").Trim().ToUpperInvariant() is "DRAFT" or "DRAFTS") { SystemRecords++; continue; }
            var conversation = Field("CONVERSATION ID").Trim(); var sender = Field("FROM").Trim();
            var senderProfile = Field("SENDER PROFILE URL").Trim(); var content = Field("CONTENT");
            if (conversation.Length is < 1 or > 2048 || sender.Length is < 1 or > 512 || senderProfile.Length > 2048)
                throw new InvalidDataException("Missing conversation or sender.");
            var recipients = Field("RECIPIENT PROFILE URLS").Split([';', ',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (recipients.Length == 0) recipients = Field("TO").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (recipients.Length > 1000 || recipients.Any(x => x.Length > 2048)) throw new InvalidDataException("Invalid recipients.");
            var senderIdentity = senderProfile.Length > 0 ? ExportText.Identity(senderProfile) : ExportText.Identity(sender);
            // Prefer profile URLs when the owner supplied URL identifiers; names remain an explicit fallback option.
            var profileOwner = Self.Any(x => x.StartsWith("linkedin.com/", StringComparison.Ordinal) || x.StartsWith("urn:li:", StringComparison.Ordinal));
            var outgoing = senderProfile.Length > 0 && profileOwner ? Self.Contains(senderIdentity)
                : Self.Contains(senderIdentity) || Self.Contains(ExportText.Identity(sender));
            OwnerSeen |= outgoing || recipients.Any(x => Self.Contains(ExportText.Identity(x))) || Self.Contains(ExportText.Identity(Field("TO")));
            var sent = ExportText.LinkedInDate(Field("DATE"), Options.TimeZone);
            if (string.IsNullOrWhiteSpace(content)) { Empty++; continue; }
            var native = Field("MESSAGE ID").Trim(); if (native.Length > 2048) throw new InvalidDataException("Message ID exceeds limit.");
            var subject = Field("SUBJECT"); if (subject.Length == 0) subject = Field("CONVERSATION TITLE");
            if (subject.Length > 4096) throw new InvalidDataException("Subject exceeds limit.");
            var id = Id(native, conversation, senderIdentity, sent.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), content, Field("SUBJECT"));
            yield return new(Source, id, conversation, sender, recipients.Distinct(StringComparer.Ordinal).ToArray(),
                subject.Length == 0 ? null : subject, content, sent, outgoing);
        }
    }
}
