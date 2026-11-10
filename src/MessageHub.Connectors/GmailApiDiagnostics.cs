using System.Text.Json;
using Google;

namespace MessageHub.Connectors;

public static class GmailApiDiagnostics
{
    // Emit only these literal codes and operator guidance, never provider messages or metadata.
    private static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["accessNotConfigured"] = "Enable Gmail API in the Google Cloud project that owns the OAuth client.",
        ["SERVICE_DISABLED"] = "Enable Gmail API in the Google Cloud project that owns the OAuth client.",
        ["insufficientPermissions"] = "Reauthorize locally and grant the requested Gmail read-only permission.",
        ["ACCESS_TOKEN_SCOPE_INSUFFICIENT"] = "Reauthorize locally and grant the requested Gmail read-only permission.",
        ["domainPolicy"] = "Ask your Google Workspace administrator to allow this app's Gmail access.",
        ["rateLimitExceeded"] = "Wait before retrying; check the project's Gmail API quotas.",
        ["userRateLimitExceeded"] = "Wait before retrying; the account's Gmail API request rate was exceeded.",
        ["dailyLimitExceeded"] = "Check the Google Cloud project's Gmail API daily quota.",
        ["quotaExceeded"] = "Check the Google Cloud project's Gmail API quotas before retrying.",
        ["RATE_LIMIT_EXCEEDED"] = "Wait before retrying; check the project's Gmail API quotas.",
        ["QUOTA_EXCEEDED"] = "Check the Google Cloud project's Gmail API quotas before retrying.",
        ["forbidden"] = "Google denied this operation; check account and administrator access policies."
    };

    public static string Describe(GoogleApiException exception)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in exception.Error?.Errors?.Take(16) ?? [])
            if (error?.Reason is { } reason && Known.ContainsKey(reason)) codes.Add(reason);

        // Newer Google errors also carry google.rpc.ErrorInfo in error.details.
        if (exception.Error?.ErrorResponseContent is { Length: > 0 and <= 65536 } content)
        {
            try
            {
                using var json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    foreach (var detail in details.EnumerateArray().Take(16))
                        if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("reason", out var reason)
                            && reason.ValueKind == JsonValueKind.String && reason.GetString() is { } code && Known.ContainsKey(code)) codes.Add(code);
            }
            catch (JsonException) { /* Unknown/malformed response remains private. */ }
        }

        var prefix = $"Gmail API returned HTTP {(int)exception.HttpStatusCode}.";
        return codes.Count == 0 ? prefix + " Reason unavailable or unrecognized; check API access, account policy and quotas locally."
            : prefix + " " + string.Join(" ", codes.Order(StringComparer.Ordinal).Select(code => $"{code}: {Known[code]}"));
    }
}
