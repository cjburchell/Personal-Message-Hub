using System.Security.Cryptography;
using System.Text;
using MessageHub.Core;
using MimeKit;
using HtmlAgilityPack;

namespace MessageHub.Connectors;

public static class MailNormalizer
{
    public static string AccountSource(string provider, string address) => provider + ":" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant()))).ToLowerInvariant();

    private static string PlainText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//head|//iframe|//object") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();
        return HtmlEntity.DeEntitize(string.Join(" ", document.DocumentNode.DescendantsAndSelf()
            .Where(x => x.NodeType == HtmlNodeType.Text).Select(x => x.InnerText)));
    }

    public static UnifiedMessage Normalize(MimeMessage mime, string source, string id, string conversation,
        DateTimeOffset sentAt, bool outgoing)
    {
        // Never persist HTML, attachments or remote-resource markup as a message body.
        var body = mime.TextBody ?? (mime.HtmlBody is { } html ? PlainText(html) : "");
        return new UnifiedMessage(source, id, conversation,
            mime.From.Mailboxes.FirstOrDefault()?.Address ?? "unknown@example.test",
            mime.To.Mailboxes.Concat(mime.Cc.Mailboxes).Concat(mime.Bcc.Mailboxes)
                .Select(x => x.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            mime.Subject, body, sentAt.ToUniversalTime(), outgoing);
    }
}
