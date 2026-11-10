using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MessageHub.Connectors.Exports;

public sealed record ExportReaderOptions(string Account, IReadOnlyList<string> Self, TimeZoneInfo? TimeZone = null, bool RepairEncoding = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Account) || Account.Length > 256 || Self.Count is < 1 or > 20 || Self.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 512))
            throw new ArgumentException("Supply a stable account label and explicit owner identifiers.");
    }
}

public static class ExportText
{
    public static string Key(string value) => value.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    public static string Identity(string value)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && (uri.Host.Equals("linkedin.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase)))
            return "linkedin.com" + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return Key(value);
    }
    public static string Hash(params string[] parts) => Convert.ToHexString(SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(parts))).ToLowerInvariant();
    public static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Normalize(NormalizationForm.FormC);
    public static string Repair(string value)
    {
        try
        {
            var latin = Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return new UTF8Encoding(false, true).GetString(latin.GetBytes(value));
        }
        catch (ArgumentException) { return value; }
    }
    public static DateTimeOffset LinkedInDate(string value, TimeZoneInfo? zone)
    {
        value = value.Trim();
        if (value.EndsWith(" UTC", StringComparison.Ordinal) || value.EndsWith(" GMT", StringComparison.Ordinal))
        {
            var format = value.EndsWith(" UTC", StringComparison.Ordinal) ? "yyyy-MM-dd HH:mm:ss 'UTC'" : "yyyy-MM-dd HH:mm:ss 'GMT'";
            if (DateTimeOffset.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var explicitUtc)) return explicitUtc;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)) return offset.ToUniversalTime();
        if (zone is not null && DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local)) throw new InvalidDataException("Ambiguous or invalid local message date; an explicit offset is required.");
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        }
        throw new InvalidDataException("Unsupported export date; supply an explicit timezone for offset-free ISO dates.");
    }
}
