using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Golden tests for path template expansion.
/// </summary>
/// <remarks>
/// <para>
/// The rules are Google's, stated in <c>google/api/http.proto</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>{var}</c>: "all characters except <c>[-_.~0-9a-zA-Z]</c> are percent-encoded".
///   </description></item>
///   <item><description>
///     <c>{+var}</c>: "all characters except <c>[-_.~/0-9a-zA-Z]</c> are percent-encoded".
///   </description></item>
/// </list>
/// <para>
/// Google's own note explains why the multi-segment form deliberately does not follow RFC 6570
/// reserved expansion: reserved expansion leaves <c>?</c> and <c>#</c> alone, "which would lead
/// to invalid URLs".
/// </para>
/// <para>
/// These are pinned because Google's official .NET client does the opposite: it skips escaping
/// entirely for <c>{+var}</c>. Matching that implementation would produce malformed requests for
/// resource ids containing reserved characters.
/// </para>
/// </remarks>
public sealed class UriTemplateTests
{
    private static string Expand(string template, params (string Key, string Value)[] values)
        => UriTemplate.Expand(template, values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));

    [Fact]
    public void MultiSegmentExpansionPreservesSeparators()
    {
        Assert.Equal(
            "v4/users/me/dataTypes/heart-rate/dataPoints/abc",
            Expand("v4/{+name}", ("name", "users/me/dataTypes/heart-rate/dataPoints/abc")));
    }

    [Fact]
    public void MultiSegmentExpansionEncodesEveryOtherReservedCharacter()
    {
        // A '?' left literal here would truncate the path and turn the rest into a query string.
        Assert.Equal(
            "v4/users/me/dataPoints/a%3Fb%23c",
            Expand("v4/{+name}", ("name", "users/me/dataPoints/a?b#c")));

        Assert.Equal(
            "v4/a/b%3Ac%26d%3De",
            Expand("v4/{+name}", ("name", "a/b:c&d=e")));
    }

    [Fact]
    public void MultiSegmentExpansionEncodesPercentSoValuesNeverDoubleDecode()
    {
        // An id that already contains a percent sign must arrive as that literal id.
        Assert.Equal("v4/a/100%25", Expand("v4/{+name}", ("name", "a/100%")));
    }

    [Fact]
    public void SingleSegmentExpansionEncodesTheSeparatorItself()
    {
        // The SMART Health Links paths use single-segment variables.
        Assert.Equal(
            "v4/shl/m/abc%2Fdef",
            Expand("v4/shl/m/{externalShlId}", ("externalShlId", "abc/def")));
    }

    [Fact]
    public void LiteralSuffixesSurviveExpansion()
    {
        // The custom-method suffix is part of the template, not of the value.
        Assert.Equal(
            "v4/users/me/dataTypes/steps/dataPoints:rollUp",
            Expand("v4/{+parent}/dataPoints:rollUp", ("parent", "users/me/dataTypes/steps")));
    }

    [Fact]
    public void UnreservedCharactersAreNeverEncoded()
    {
        Assert.Equal(
            "v4/a-b_c.d~e/0123456789",
            Expand("v4/{+name}", ("name", "a-b_c.d~e/0123456789")));
    }

    [Fact]
    public void SpacesAndNonAsciiAreEncoded()
    {
        Assert.Equal("v4/a%20b", Expand("v4/{+name}", ("name", "a b")));
        Assert.Equal("v4/%E5%81%A5%E5%BA%B7", Expand("v4/{+name}", ("name", "健康")));
    }

    [Fact]
    public void TemplateWithoutVariablesIsReturnedUnchanged()
        => Assert.Equal("v4/users", UriTemplate.Expand("v4/users", new Dictionary<string, string>()));

    [Fact]
    public void MissingValueFailsLoudly()
        => Assert.Throws<InvalidOperationException>(() => Expand("v4/{+name}"));

    [Fact]
    public void UnterminatedVariableIsRejected()
        => Assert.Throws<ArgumentException>(() => Expand("v4/{+name", ("name", "x")));

    [Fact]
    public void EscapeHelpersMatchTheDocumentedCharacterSets()
    {
        // The two sets differ by exactly one character: '/'.
        const string sample = "a/b c?d";

        Assert.Equal("a/b%20c%3Fd", UriTemplate.EscapeMultiSegment(sample));
        Assert.Equal("a%2Fb%20c%3Fd", UriTemplate.EscapeSingleSegment(sample));
    }

    /// <summary>
    /// Every character, alone and inside a resource name, escapes by the documented rule.
    /// </summary>
    /// <remarks>
    /// The rule, spelled out as its definition: split on <c>/</c>, escape each segment as a single
    /// segment, join. Checked for every character outside the surrogate range, so a character the
    /// escaper lets through that the rule does not — or the reverse — cannot hide between the
    /// examples above. The long form reaches the vectorised search a single character does not.
    /// </remarks>
    [Fact]
    public void EveryCharacterEscapesByTheDocumentedRule()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var character = (char)code;

            if (char.IsSurrogate(character))
            {
                continue;
            }

            foreach (var value in new[] { character.ToString(), $"users/me/dataTypes/{character}/dataPoints/abc123" })
            {
                var byTheRule = string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

                Assert.Equal(byTheRule, UriTemplate.EscapeMultiSegment(value));
            }
        }
    }
}
