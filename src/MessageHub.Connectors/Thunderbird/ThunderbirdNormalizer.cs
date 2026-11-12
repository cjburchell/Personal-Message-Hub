using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MessageHub.Core;
using MimeKit;

namespace MessageHub.Connectors.Thunderbird;

internal static partial class ThunderbirdNormalizer
{
    [GeneratedRegex(@"^(?:(?:re|fw|fwd):\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPrefix();
    [GeneratedRegex(@"(?:^|\s)(?:[+-]\d{4}|UT|UTC|GMT|EST|EDT|CST|CDT|MST|MDT|PST|PDT|[A-IK-Z])(?:\s*\([^\r\n]*\))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateZone();
    internal static string SubjectKey(string? text) => SubjectPrefix().Replace(text?.Trim() ?? "", "").Normalize().ToLowerInvariant();
    internal static string BodyHash(string text) => ThunderbirdArchive.Hash(text.Replace("\r\n", "\n").Trim().Normalize());
    private static string? Id(string? text)
    {
        var value = text?.Trim().Trim('<', '>');
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 998 || value.Any(char.IsControl)) throw new InvalidDataException("Invalid header identifier.");
        return value;
    }
    public static EmailDocument Normalize(MimeMessage mime, string source, HashSet<string> self, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 3 || mime.Headers.Count > 1000 || !mime.Headers.Any(x => x.Id is HeaderId.From or HeaderId.Subject or HeaderId.Date or HeaderId.MessageId))
            throw new InvalidDataException("Unrecognized or oversized message headers.");
        var participants = new List<EmailParticipant>();
        void Addresses(string role, InternetAddressList list)
        {
            foreach (var address in list.Mailboxes)
            {
                if (participants.Count >= 1000 || address.Address.Length > 320 || address.Name?.Length > 512) throw new InvalidDataException("Participant limit exceeded.");
                participants.Add(new(role, address.Address.Trim().ToLowerInvariant(), address.Name?.Normalize() ?? ""));
            }
        }
        Addresses("from", mime.From); Addresses("to", mime.To); Addresses("cc", mime.Cc); Addresses("bcc", mime.Bcc);
        var warnings = new List<string>();
        if (mime.Body?.ContentType.IsMimeType("multipart", "encrypted") == true || mime.Body?.ContentType.IsMimeType("application", "pkcs7-mime") == true)
            warnings.Add("encrypted_content_not_decrypted");
        if (!mime.From.Mailboxes.Any()) warnings.Add("missing_sender");
        var originalDate = mime.Headers[HeaderId.Date];
        if (originalDate?.Length > 1024) throw new InvalidDataException("Date header limit exceeded.");
        var validDate = MimeKit.Utils.DateUtils.TryParse(originalDate ?? "", out var date) && DateZone().IsMatch(originalDate ?? "");
        if (!validDate) { date = DateTimeOffset.UnixEpoch; warnings.Add("missing_or_invalid_date_uses_epoch"); }
        var normalized = MailNormalizer.Normalize(mime, source, "pending", "pending", date, mime.From.Mailboxes.Any(x => self.Contains(x.Address.ToLowerInvariant())));
        if (normalized.Body.Length > 1024 * 1024 || normalized.Subject?.Length > 4096) throw new InvalidDataException("Text limit exceeded.");
        var attachments = new List<EmailAttachment>(); var forwards = new List<EmailForward>(); var partNumber = 0;
        foreach (var part in mime.BodyParts)
        {
            ct.ThrowIfCancellationRequested(); if (++partNumber > 256) throw new InvalidDataException("MIME part limit exceeded.");
            if (part.ContentType.MimeType.Length > 256 || part.ContentDisposition?.Disposition.Length > 64)
                throw new InvalidDataException("MIME metadata limit exceeded.");
            if (part is MessagePart embedded && embedded.Message is not null)
            {
                var fileName = part.ContentDisposition?.FileName ?? part.ContentType.Name;
                if (fileName?.Length > 1024 || part.ContentId?.Length > 1024) throw new InvalidDataException("Attachment metadata limit exceeded.");
                var inner = Normalize(embedded.Message, source, self, ct, depth + 1);
                forwards.Add(new("embedded_forward", inner.Fingerprint, null, null));
                attachments.Add(new(partNumber, fileName, part.ContentType.MimeType, part.ContentId, part.ContentDisposition?.Disposition ?? "inline", null, null));
            }
            else if (part is MimePart leaf && (leaf.IsAttachment || !leaf.ContentType.IsMimeType("text", "plain") && !leaf.ContentType.IsMimeType("text", "html")))
            {
                if (leaf.FileName?.Length > 1024 || leaf.ContentId?.Length > 1024) throw new InvalidDataException("Attachment metadata limit exceeded.");
                using var sink = new HashSink();
                if (leaf.Content is not null) leaf.Content.DecodeTo(sink, ct);
                attachments.Add(new(partNumber, leaf.FileName, leaf.ContentType.MimeType, leaf.ContentId,
                    leaf.ContentDisposition?.Disposition ?? "inline", sink.Count, sink.Finish()));
            }
        }
        if (attachments.Count > 100 || forwards.Count > 20) throw new InvalidDataException("Attachment limit exceeded.");
        var bodyHash = BodyHash(normalized.Body); var subjectKey = SubjectKey(normalized.Subject);
        // Inline forward detection only creates advisory relationships; it never changes identity.
        foreach (var marker in new[] { "---------- Forwarded message ---------", "-----Original Message-----", "Begin forwarded message:" })
        {
            var at = normalized.Body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var forwarded = normalized.Body[(at + marker.Length)..].Replace("\r\n", "\n").TrimStart();
            var endHeaders = forwarded.IndexOf("\n\n", StringComparison.Ordinal);
            if (endHeaders >= 0 && endHeaders < 8192)
                forwards.Add(new("possible_inline_forward", null, BodyHash(forwarded[(endHeaders + 2)..]), subjectKey));
            break;
        }
        var rfc = Id(mime.MessageId);
        var references = mime.References.Select(Id).Append(Id(mime.InReplyTo)).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).ToArray();
        if (references.Length > 100) throw new InvalidDataException("Reference limit exceeded.");
        // Message-ID is corroborated by semantic content. Missing or reused IDs cannot overwrite other content.
        var fingerprint = ThunderbirdArchive.Hash(JsonSerializer.Serialize(new
        {
            policy = "email-v1", rfc, sent = date.ToUniversalTime().ToString("O"),
            participants = participants.Select(x => x.Role + ":" + x.Address).Distinct().Order(StringComparer.Ordinal),
            subject = normalized.Subject?.Normalize(), bodyHash,
            attachments = attachments.Select(x => new { x.FileName, x.MediaType, x.ContentId, x.Disposition, x.Sha256 }),
            embedded = forwards.Where(x => x.Kind == "embedded_forward").Select(x => x.Fingerprint)
        }));
        normalized = normalized with { SourceMessageId = "email-" + fingerprint, ConversationId = "email-" + fingerprint };
        return new(normalized, fingerprint, bodyHash, subjectKey, rfc, references, originalDate,
            validDate ? "header" : "unknown", participants, attachments, forwards, warnings);
    }
    private sealed class HashSink : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Count { get; private set; }
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Count += buffer.Length; if (Count > ThunderbirdArchive.MaximumMessageBytes) throw new InvalidDataException("Decoded attachment limit exceeded.");
            _hash.AppendData(buffer);
        }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Count; public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
