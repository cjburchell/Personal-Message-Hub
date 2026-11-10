using System.Net;
using Google;
using Google.Apis.Requests;
using MessageHub.Connectors;
using Xunit;

namespace MessageHub.Tests;

public sealed class GmailApiDiagnosticsTests
{
    private static GoogleApiException Error(string? reason = null, string? response = null) => new("gmail", "synthetic-private-exception")
    {
        HttpStatusCode = HttpStatusCode.Forbidden,
        Error = new RequestError
        {
            Code = 403, Message = "synthetic-private-message", ErrorResponseContent = response,
            Errors = reason is null ? [] : [new SingleError { Reason = reason, Message = "synthetic-private-detail", Location = "synthetic-private-location" }]
        }
    };

    [Theory]
    [InlineData("accessNotConfigured", "Enable Gmail API")]
    [InlineData("insufficientPermissions", "Reauthorize")]
    [InlineData("domainPolicy", "administrator")]
    [InlineData("rateLimitExceeded", "Wait before retrying")]
    [InlineData("userRateLimitExceeded", "Wait before retrying")]
    [InlineData("dailyLimitExceeded", "daily quota")]
    public void KnownCodesProvideGuidanceWithoutPrivateFields(string code, string guidance)
    {
        var output = GmailApiDiagnostics.Describe(Error(code));
        Assert.Contains("HTTP 403", output); Assert.Contains(code, output); Assert.Contains(guidance, output);
        Assert.DoesNotContain("synthetic-private", output);
    }

    [Theory]
    [InlineData("SERVICE_DISABLED", "Enable Gmail API")]
    [InlineData("ACCESS_TOKEN_SCOPE_INSUFFICIENT", "Reauthorize")]
    public void ModernErrorInfoIsRecognizedWithoutEmittingMetadata(string code, string guidance)
    {
        var response = System.Text.Json.JsonSerializer.Serialize(new { error = new { details = new[] { new { reason = code, metadata = new { secret = "synthetic-private-value" } } } } });
        var output = GmailApiDiagnostics.Describe(Error(response: response));
        Assert.Contains(code, output); Assert.Contains(guidance, output); Assert.DoesNotContain("synthetic-private", output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-json synthetic-private")]
    [InlineData("{\"error\":{\"details\":[{\"reason\":\"synthetic-private\"}]}}")]
    [InlineData("{\"error\":{\"details\":42}}")]
    public void UnknownAndMalformedErrorsRemainPrivate(string? response)
    {
        var output = GmailApiDiagnostics.Describe(Error("synthetic-private-reason\n\u001b", response));
        Assert.Contains("unrecognized", output); Assert.DoesNotContain("synthetic-private", output);
    }

    [Fact]
    public void MissingErrorAndOversizedRawResponseUseSafeFallback()
    {
        Assert.Contains("unrecognized", GmailApiDiagnostics.Describe(new GoogleApiException("gmail", "synthetic-private")));
        Assert.Contains("unrecognized", GmailApiDiagnostics.Describe(Error(response: new string('x', 65537))));
    }
}
