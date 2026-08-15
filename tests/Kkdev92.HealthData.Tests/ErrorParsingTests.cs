using System.Text;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Pins the Google error envelope shape defined by AIP-193.
/// </summary>
/// <remarks>
/// The important subtlety: <c>error.status</c> is the canonical status code
/// (<c>PERMISSION_DENIED</c>), while the API-specific reason such as <c>MISSING_OAUTH_SCOPE</c>
/// lives in a <c>google.rpc.ErrorInfo</c> detail. Reading the reason out of <c>status</c> is a
/// plausible mistake that produces wrong values only against a real service.
/// </remarks>
public sealed class ErrorParsingTests
{
    private static HealthDataError? Parse(string json)
        => HealthDataErrorParser.Parse(Encoding.UTF8.GetBytes(json));

    private const string FullEnvelope = """
        {
          "error": {
            "code": 403,
            "message": "User 1234 has not granted heart rate access.",
            "status": "PERMISSION_DENIED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.ErrorInfo",
                "reason": "MISSING_OAUTH_SCOPE",
                "domain": "health.googleapis.com",
                "metadata": { "scope": "googlehealth.activity_and_fitness.readonly" }
              }
            ]
          }
        }
        """;

    [Fact]
    public void ReadsTheEnvelopeFields()
    {
        var error = Parse(FullEnvelope)!;

        Assert.Equal(403, error.Code);
        Assert.Equal("PERMISSION_DENIED", error.Status);
        Assert.Equal("User 1234 has not granted heart rate access.", error.Message);
        Assert.Single(error.Details);
    }

    [Fact]
    public void ReasonComesFromErrorInfoNotFromStatus()
    {
        var error = Parse(FullEnvelope)!;

        Assert.Equal("MISSING_OAUTH_SCOPE", error.Reason);
        Assert.Equal("health.googleapis.com", error.Domain);

        // The canonical code remains available separately.
        Assert.Equal("PERMISSION_DENIED", error.Status);
    }

    [Fact]
    public void ReasonFallsBackToStatusWhenNoErrorInfoIsPresent()
    {
        var error = Parse("""{"error":{"code":404,"status":"NOT_FOUND"}}""")!;

        Assert.Equal("NOT_FOUND", error.Reason);
        Assert.Empty(error.Details);
    }

    [Fact]
    public void UnmodelledDetailTypesAreRetainedRaw()
    {
        var error = Parse("""
            {
              "error": {
                "code": 400,
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.BadRequest",
                    "fieldViolations": [ { "field": "filter", "description": "bad syntax" } ] }
                ]
              }
            }
            """)!;

        var detail = Assert.Single(error.Details);
        Assert.Equal(HealthDataErrorDetail.BadRequestType, detail.Type);
        Assert.False(detail.IsErrorInfo);

        // Nothing is discarded just because this SDK has no typed model for it.
        Assert.Equal("filter", detail.Raw.GetProperty("fieldViolations")[0].GetProperty("field").GetString());
    }

    [Fact]
    public void RetryInfoIsParsedAsADuration()
    {
        var error = Parse("""
            {
              "error": {
                "code": 429,
                "status": "RESOURCE_EXHAUSTED",
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "30s" }
                ]
              }
            }
            """)!;

        var detail = Assert.Single(error.Details);
        Assert.True(detail.IsRetryInfo);
        Assert.Equal(new GoogleDuration(30, 0), detail.RetryDelay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"something":"else"}""")]
    [InlineData("""{"error":{"code":500,"details":""" /* truncated mid-document */)]
    public void MalformedOrAbsentEnvelopesYieldNull(string json)
        => Assert.Null(Parse(json));

    [Fact]
    public async Task StreamParsingRespectsTheByteBound()
    {
        // A hostile or merely enormous error body must not be buffered wholesale.
        var oversized = """{"error":{"code":500,"message":" """ + new string('x', 200_000) + "\"}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(oversized));

        var error = await HealthDataErrorParser.ParseAsync(stream, maxBytes: 1024, TestContext.Current.CancellationToken);

        // Truncated JSON does not parse, and that is the correct outcome: the status code already
        // carried the result.
        Assert.Null(error);
        Assert.True(stream.Position <= 1024 * 2);
    }

    [Fact]
    public void ExceptionMessageCarriesOnlyOperationStatusAndReason()
    {
        var exception = new HealthDataApiException(
            System.Net.HttpStatusCode.Forbidden,
            "health.users.getProfile",
            Parse(FullEnvelope));

        Assert.Contains("health.users.getProfile", exception.Message, StringComparison.Ordinal);
        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MISSING_OAUTH_SCOPE", exception.Message, StringComparison.Ordinal);

        // The service message names a user and a data type. Neither may reach the message, which
        // is the string most likely to be logged.
        Assert.DoesNotContain("1234", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("heart rate", exception.Message, StringComparison.Ordinal);

        // It is still reachable for callers that have somewhere safe to put it.
        Assert.Equal("User 1234 has not granted heart rate access.", exception.Error!.Message);
    }

    /// <summary>
    /// <c>ErrorInfo.metadata</c> is read into a dictionary rather than left in the raw JSON.
    /// </summary>
    /// <remarks>
    /// This is where the answer to "which scope" lives. The reason says MISSING_OAUTH_SCOPE, which
    /// a caller holding the obvious scope already knew; the metadata names the one that is
    /// actually missing. Reaching it used to mean walking Raw, a JsonElement, for a value the
    /// parser had in its hand.
    /// </remarks>
    [Fact]
    public void ErrorInfoMetadataIsTyped()
    {
        var error = Parse("""
            {"error":{"code":403,"status":"PERMISSION_DENIED","message":"Forbidden",
            "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo",
            "reason":"MISSING_OAUTH_SCOPE","domain":"health.googleapis.com",
            "metadata":{"scope":"https://www.googleapis.com/auth/googlehealth.sleep.readonly",
            "method":"google.health.v4.Users.GetProfile"}}]}}
            """);

        var detail = Assert.Single(error!.Details, d => d.IsErrorInfo);

        Assert.NotNull(detail.Metadata);
        Assert.Equal(
            "https://www.googleapis.com/auth/googlehealth.sleep.readonly",
            detail.Metadata["scope"]);
        Assert.Equal("google.health.v4.Users.GetProfile", detail.Metadata["method"]);
    }

    [Fact]
    public void ADetailWithNoMetadataHasNone()
    {
        // Null rather than an empty dictionary: "the service sent none" and "the service sent an
        // empty one" are different answers, and only one of them is worth a branch.
        var error = Parse("""
            {"error":{"code":403,"status":"PERMISSION_DENIED","message":"Forbidden",
            "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"X"}]}}
            """);

        Assert.Null(Assert.Single(error!.Details).Metadata);
    }

    [Fact]
    public void ANonStringMetadataValueIsSkippedRatherThanRendered()
    {
        // google.rpc.ErrorInfo declares map<string, string>. Anything else is the service sending
        // a shape this cannot hold, and stringifying it would put a guess where a caller branches.
        var error = Parse("""
            {"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Slow down",
            "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"RATE_LIMIT",
            "metadata":{"quota":"per-minute","limit":300,"nested":{"a":"b"}}}]}}
            """);

        var metadata = Assert.Single(error!.Details).Metadata;

        Assert.Equal("per-minute", Assert.Single(metadata!).Value);
    }

    [Fact]
    public void MetadataStaysOutOfTheExceptionMessage()
    {
        // The values can name a method, a resource, or whatever the service decides to add. The
        // message is the part this SDK says is safe to log, so none of it goes there.
        var error = Parse("""
            {"error":{"code":403,"status":"PERMISSION_DENIED","message":"Forbidden",
            "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo",
            "reason":"MISSING_OAUTH_SCOPE",
            "metadata":{"method":"google.health.v4.Users.GetProfile","user":"1234567890"}}]}}
            """);

        var exception = new HealthDataApiException(
            System.Net.HttpStatusCode.Forbidden, "health.users.getProfile", error);

        Assert.DoesNotContain("GetProfile", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890", exception.Message, StringComparison.Ordinal);

        // Still reachable for a caller with somewhere safe to put it.
        Assert.Equal("1234567890", Assert.Single(error!.Details, d => d.IsErrorInfo).Metadata!["user"]);
    }
}
