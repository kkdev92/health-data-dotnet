using System.Net;
using System.Reflection;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Requests;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Enforces the SDK's privacy rules as tests rather than as intentions.
/// </summary>
/// <remarks>
/// Each of these encodes a way health data or a credential could leak by accident. They are
/// cheap to keep and they fail loudly if someone reintroduces the pattern.
/// </remarks>
public sealed class PrivacyGuardTests
{
    [Fact]
    public void NoHealthModelGeneratesAToStringThatPrintsItsValues()
    {
        // A record's generated ToString() prints every property, so a log line that interpolates
        // a model would print a person's measurements. Models are classes for this reason, which
        // means they inherit object.ToString().
        var models = typeof(HealthDataApiMetadata).Assembly
            .GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Namespace == "Kkdev92.HealthData.Models")
            .Where(t => t.GetProperties().Any(p =>
                p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).Length > 0))
            .ToArray();

        Assert.NotEmpty(models);

        foreach (var model in models)
        {
            var toString = model.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);

            Assert.True(
                toString?.DeclaringType == typeof(object),
                $"{model.Name} overrides ToString(). A health model must not render its own values.");
        }
    }

    [Fact]
    public void AModelWithValuesRendersNothingIdentifying()
    {
        var point = new DataPoint
        {
            Name = "users/1234567890/dataTypes/heart-rate/dataPoints/abc",
            HeartRate = new HeartRate { BeatsPerMinute = 58 },
        };

        var rendered = point.ToString()!;

        Assert.DoesNotContain("1234567890", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("58", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("heart-rate", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiExceptionMessagesCarryNoPayload()
    {
        // Google's own error messages quote user ids and data types, and an exception message is
        // the string most likely to reach a log.
        var error = HealthDataErrorParser.Parse(System.Text.Encoding.UTF8.GetBytes(
            """
            {"error":{"code":403,"status":"PERMISSION_DENIED",
            "message":"User 1234567890 has not granted heart rate access.",
            "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"MISSING_OAUTH_SCOPE"}]}}
            """));

        var exception = new HealthDataApiException(HttpStatusCode.Forbidden, "health.users.getProfile", error);

        Assert.DoesNotContain("1234567890", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("heart rate", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("granted", exception.Message, StringComparison.Ordinal);

        // A reason that looks like a reason is kept, and is what a caller branches on.
        Assert.Contains("MISSING_OAUTH_SCOPE", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reason that is not machine-readable does not reach the message.
    /// </summary>
    /// <remarks>
    /// The reason is a string off the wire and the sender is not necessarily Google. A proxy, a
    /// test double, or a service that reflects input back can put a newline in it and forge a
    /// second log line, or echo something out of the request. This message is the one part of an
    /// error the SDK says is safe to log, so it keeps the reason only when this contract documents
    /// it — not when it merely looks documented.
    /// </remarks>
    [Theory]
    [InlineData("MISSING_OAUTH_SCOPE\nWARN user=alice token=ya29.secret", "alice")]
    [InlineData("users/1234567890/dataTypes/heart-rate", "1234567890")]
    [InlineData("Bearer ya29.a0AfH6SMB", "ya29")]
    [InlineData("permission denied for this user", "denied")]

    // Shaped exactly like a documented reason, and not one. The shape test that used to live here
    // admitted every one of these: upper case, digits and underscores prove nothing about whether
    // a value is a code, a secret, or somebody's user id.
    [InlineData("CLIENT_SECRET_ABC", "CLIENT_SECRET")]
    [InlineData("ABC123_SECRET", "ABC123")]
    [InlineData("GOCSPX_ECHOED_BACK", "GOCSPX")]
    [InlineData("1234567890", "1234567890")]
    [InlineData("58", "58")]
    public void AReasonThatIsNotMachineReadableStaysOutOfTheMessage(string reason, string mustNotAppear)
    {
        var json = Envelope(reason);

        var error = HealthDataErrorParser.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        var exception = new HealthDataApiException(HttpStatusCode.Forbidden, "health.users.getProfile", error);

        Assert.DoesNotContain(mustNotAppear, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', exception.Message);

        // Dropped from the message, not from the object: a caller with somewhere safe to put it
        // still has the whole envelope.
        Assert.Equal(reason, exception.Reason);
    }

    /// <summary>An over-long reason is dropped whatever characters it uses.</summary>
    [Fact]
    public void AnOverlongReasonStaysOutOfTheMessage()
    {
        var reason = new string('A', 200);

        var json = Envelope(reason);

        var error = HealthDataErrorParser.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        var exception = new HealthDataApiException(HttpStatusCode.Forbidden, "health.users.getProfile", error);

        Assert.DoesNotContain(reason, exception.Message, StringComparison.Ordinal);
        Assert.Equal(reason, exception.Reason);
    }

    /// <summary>
    /// An AIP-193 envelope whose only variable is the reason.
    /// </summary>
    /// <remarks>
    /// Escaped by hand rather than with <c>JsonSerializer</c>, because this assembly runs with
    /// reflection-based serialization disabled — the same setting the SDK ships with, which is
    /// why the tests keep it on.
    /// </remarks>
    private static string Envelope(string reason)
    {
        // A placeholder rather than interpolation, so the JSON reads as JSON.
        const string Template =
            """
            {"error":{"code":403,"status":"PERMISSION_DENIED","details":[
              {"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"__REASON__"}]}}
            """;

        var escaped = reason
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return Template.Replace("__REASON__", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveParseFailuresDoNotEchoTheValue()
    {
        // A timestamp or duration can itself be part of a health record.
        var timestamp = Assert.Throws<FormatException>(() => GoogleTimestamp.Parse("2026-08-10T12:34:56.123456789Z"));
        Assert.DoesNotContain("2026", timestamp.Message, StringComparison.Ordinal);

        var duration = Assert.Throws<FormatException>(() => GoogleDuration.Parse("99999.9999999999s"));
        Assert.DoesNotContain("99999", duration.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteContractNeverSendsAServerOwnedValue()
    {
        // The privacy rule in the other direction: not leaking outward, but not echoing back a
        // value the service owns, which would be rejected or silently ignored.
        Assert.NotEmpty(HealthDataOutputOnlyProperties.ByType);

        foreach (var (type, outputOnly) in HealthDataOutputOnlyProperties.ByType)
        {
            var writeInfo = HealthDataJson.WriteOptions.GetTypeInfo(type);
            var written = writeInfo.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var name in outputOnly)
            {
                Assert.DoesNotContain(name, written);
            }
        }
    }

    [Fact]
    public void DiagnosticTagsExcludeAnythingIdentifying()
    {
        // The tag list is deliberately short; a resource name embeds both the user and the data
        // type, so no URL tag exists at all.
        var tags = typeof(Diagnostics.HealthDataActivityTags)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.DoesNotContain("url.full", tags);
        Assert.DoesNotContain("url.path", tags);
        Assert.DoesNotContain("googlehealth.user_id", tags);
        Assert.DoesNotContain("googlehealth.data_type", tags);

        Assert.Contains("googlehealth.operation_id", tags);
    }

    [Fact]
    public void NoPublicApiAcceptsOrExposesARawCredentialAsAProperty()
    {
        // Client options must not become a place to park a token. Credentials live in the
        // pipeline (ADR-0007), not on the client.
        var optionProperties = typeof(HealthDataClientOptions).GetProperties().Select(p => p.Name);

        foreach (var forbidden in new[] { "AccessToken", "RefreshToken", "ClientSecret", "ApiKey", "UserId" })
        {
            Assert.DoesNotContain(forbidden, optionProperties);
        }
    }

    [Fact]
    public void NoShippingSourceFileWritesToALogOrTheConsole()
    {
        // The privacy rules above all constrain *what* the SDK renders. This one removes the
        // channel: an SDK that never writes a log cannot leak through one, which is why the
        // documented guarantee is phrased that way. A debugging `Console.WriteLine` left behind in
        // a handler would defeat every other guard here, and nothing else would catch it.
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot.Value, "src"), "*.cs", SearchOption.AllDirectories))
        {
            // Build output under a package directory is a copy of what is already checked.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var call in new[]
            {
                "Console.Write",
                "Console.Error",
                "Trace.Write",
                "Debug.Write",
                "ILogger",
                "LogInformation",
                "LogWarning",
                "LogError",
                "LogDebug",
                "LogTrace",
            })
            {
                if (text.Contains(call, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {call}");
                }
            }
        }

        Assert.Empty(offenders);
    }
}
