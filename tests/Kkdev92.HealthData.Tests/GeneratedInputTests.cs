using System.Net;
using System.Text;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Rules checked over generated input rather than chosen examples.
/// </summary>
/// <remarks>
/// <para>
/// Most tests here pin behaviour with examples, which is right when the interesting inputs are
/// known. These cover the opposite case: a rule that has to hold for every string a stranger can
/// send, where the input that breaks it is by definition the one nobody thought of. The reason
/// sanitisation below is the generalisation of a defect this SDK actually shipped — five
/// hand-written counter-examples pinned the fix, and these assert the rule the fix was meant to
/// establish.
/// </para>
/// <para>
/// Generated from a fixed seed rather than by a property-testing library. Two reasons. The seed
/// makes a failure reproducible from the test name alone, without a shrinking report to read. And
/// the only library Scorecard recognises for C# is FsCheck, whose C# surface exposes F# function
/// types; adding an F# dependency to a repository that ships none, to satisfy a score, is the
/// wrong trade. The generators below are a few lines and cover the shapes that matter here.
/// </para>
/// </remarks>
public sealed class GeneratedInputTests
{
    private const int Cases = 2000;

    /// <summary>
    /// Strings of the kinds that have caused trouble, plus arbitrary ones.
    /// </summary>
    /// <remarks>
    /// Weighted rather than uniform. A uniform random string almost never looks like a credential
    /// or contains a newline, so a uniform generator would spend two thousand cases proving that
    /// unremarkable input is unremarkable.
    /// </remarks>
    private static IEnumerable<string> Strings(int seed)
    {
        var random = new Random(seed);

        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-./:?#%&= \t\r\n\"'\\";

        // Shapes worth hitting deliberately: a credential, a log-forging newline, wire-shaped
        // codes near the allowlist, and the empty and whitespace edges.
        yield return string.Empty;
        yield return " ";
        yield return "\n";
        yield return "PERMISSION_DENIED\nINFO: injected";
        yield return "GOCSPX-abcdefghijklmnopqrstuvwxyz12";
        yield return "ya29.a0AfH6SMB_not_a_real_token";
        yield return "AIzaSyD-fake-key-value-for-testing-only";
        yield return "MISSING_OAUTH_SCOPE";      // documented
        yield return "PERMISSION_DENIED";        // canonical status
        yield return "NOT_A_REAL_REASON";        // shaped like one, and is not

        for (var i = 0; i < Cases; i++)
        {
            var length = random.Next(0, 60);
            var builder = new StringBuilder(length);

            for (var c = 0; c < length; c++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            yield return builder.ToString();
        }
    }

    [Fact]
    public void NoStringOffTheWireEverReachesAnExceptionMessage()
    {
        // The reason field arrives from the network. An earlier version kept anything shaped like
        // an identifier, which is also the shape of a client secret. A reason may appear in the
        // message only when it is a value this SDK publishes.
        foreach (var reason in Strings(seed: 1))
        {
            var exception = new HealthDataApiException(
                HttpStatusCode.Forbidden,
                "health.users.getProfile",
                ErrorWithReason(reason));

            // Asserted on the shape of the message rather than by substring. "Does not contain"
            // is not the invariant and cannot be: a reason of "" or " " is inside every English
            // sentence, so that phrasing failed on input the SDK handles correctly. What is
            // actually promised is that the message is one of two forms, and that the second is
            // reached only by a value this SDK publishes.
            var kept = HealthDataErrorReasons.IsDocumented(reason) || IsCanonicalStatus(reason);

            var expected = kept
                ? $"'health.users.getProfile' failed with 403 Forbidden ({reason})."
                : "'health.users.getProfile' failed with 403 Forbidden.";

            Assert.Equal(expected, exception.Message, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void AMessageIsAlwaysOneLine()
    {
        // A reason carrying a newline would forge a second entry in whatever log the message is
        // written to. The allowlist stops that, and this says so independently: the two could
        // drift apart if the allowlist ever gained a value with whitespace in it.
        foreach (var reason in Strings(seed: 2))
        {
            var exception = new HealthDataApiException(
                HttpStatusCode.BadRequest,
                "health.users.getProfile",
                ErrorWithReason(reason));

            Assert.DoesNotContain('\n', exception.Message);
            Assert.DoesNotContain('\r', exception.Message);
        }
    }

    [Fact]
    public void APathParameterCannotWidenTheRequestPath()
    {
        // Path parameters carry user-controlled values: a data type id, a data point id. If one
        // could contribute an unescaped separator, a caller could aim a request at a resource
        // they did not name. A simple expansion must never add structure.
        foreach (var value in Strings(seed: 3))
        {
            if (value.Length == 0)
            {
                continue;
            }

            var expanded = UriTemplate.Expand(
                "v4/dataTypes/{dataType}/dataPoints",
                new Dictionary<string, string> { ["dataType"] = value });

            Assert.Equal(3, expanded.Count(c => c == '/'));
            Assert.DoesNotContain('?', expanded);
            Assert.DoesNotContain('#', expanded);
        }
    }

    [Fact]
    public void AnOpenEnumRoundTripsWhateverItIsGiven()
    {
        // Forward compatibility is a promise about every value Google might add, not about the
        // ones in today's snapshot, so it is worth stating over generated input
        // (ADR-0005).
        foreach (var value in Strings(seed: 4))
        {
            var parsed = new SleepStage.Types.Type(value);

            Assert.Equal(value, parsed.Value, StringComparer.Ordinal);
            Assert.Equal(value, parsed.ToString(), StringComparer.Ordinal);
        }
    }

    /// <summary>An error carrying the reason the way the wire delivers one.</summary>
    /// <remarks>
    /// Reason is not settable: it reads the ErrorInfo detail, falling back to status. Building it
    /// through the detail is what the deserializer does, so this exercises the same path.
    /// </remarks>
    private static HealthDataError ErrorWithReason(string reason) => new()
    {
        Code = 403,
        Status = "PERMISSION_DENIED",
        Details =
        [
            new HealthDataErrorDetail
            {
                Type = HealthDataErrorDetail.ErrorInfoType,
                Reason = reason,
            },
        ],
    };

    /// <summary>Mirrors the canonical status list the exception also accepts.</summary>
    private static bool IsCanonicalStatus(string? status) => status is
        "CANCELLED" or "UNKNOWN" or "INVALID_ARGUMENT" or "DEADLINE_EXCEEDED" or
        "NOT_FOUND" or "ALREADY_EXISTS" or "PERMISSION_DENIED" or "UNAUTHENTICATED" or
        "RESOURCE_EXHAUSTED" or "FAILED_PRECONDITION" or "ABORTED" or "OUT_OF_RANGE" or
        "UNIMPLEMENTED" or "INTERNAL" or "UNAVAILABLE" or "DATA_LOSS";
}
