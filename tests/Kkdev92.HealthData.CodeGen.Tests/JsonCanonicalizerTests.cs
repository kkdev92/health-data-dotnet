using System.Text;
using Kkdev92.HealthData.CodeGen.Specifications;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// Canonicalization is what makes the specification snapshot byte-stable.
/// </summary>
/// <remarks>
/// The Google Health API Discovery endpoint returns randomized object key order on every request.
/// Verified 2026-08-09: four consecutive fetches produced four different SHA-256 values at an
/// identical byte length, with no semantic difference.
/// </remarks>
public sealed class JsonCanonicalizerTests
{
    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Canonical(string json)
        => new UTF8Encoding(false).GetString(JsonCanonicalizer.Canonicalize(Utf8(json)));

    [Fact]
    public void DifferentKeyOrderProducesIdenticalOutput()
    {
        const string a = """{"b":1,"a":2,"c":{"z":true,"y":false}}""";
        const string b = """{"c":{"y":false,"z":true},"a":2,"b":1}""";

        Assert.Equal(Canonical(a), Canonical(b));
    }

    [Fact]
    public void ArrayOrderIsPreserved()
    {
        // Discovery arrays are semantic: parameterOrder, enum and scopes must not be reordered.
        var canonical = Canonical("""{"enum":["GAMMA","ALPHA","BETA"]}""");

        Assert.Contains(""""["GAMMA""""[1..], canonical, StringComparison.Ordinal);
        Assert.True(
            canonical.IndexOf("GAMMA", StringComparison.Ordinal) <
            canonical.IndexOf("ALPHA", StringComparison.Ordinal));
        Assert.True(
            canonical.IndexOf("ALPHA", StringComparison.Ordinal) <
            canonical.IndexOf("BETA", StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalizingIsIdempotent()
    {
        var once = Canonical("""{"b":1,"a":[1,2,{"d":4,"c":3}]}""");
        var twice = Canonical(once);

        Assert.Equal(once, twice);
        Assert.True(JsonCanonicalizer.IsCanonical(Utf8(once)));
    }

    [Fact]
    public void OutputUsesLfAndEndsWithANewline()
    {
        var canonical = Canonical("""{"a":1}""");

        Assert.DoesNotContain('\r', canonical);
        Assert.EndsWith("\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesSurviveUnchanged()
    {
        const string source = """
            {"s":"a \"quoted\" value","n":1,"big":9007199254740993,"f":1.5,"t":true,"nul":null,"empty":{},"arr":[]}
            """;

        using var round = System.Text.Json.JsonDocument.Parse(JsonCanonicalizer.Canonicalize(Utf8(source)));
        var root = round.RootElement;

        Assert.Equal("a \"quoted\" value", root.GetProperty("s").GetString());
        Assert.Equal(1, root.GetProperty("n").GetInt32());
        Assert.Equal(1.5, root.GetProperty("f").GetDouble());
        Assert.True(root.GetProperty("t").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("nul").ValueKind);

        // Written raw, so precision beyond double survives rather than being reformatted.
        Assert.Equal("9007199254740993", root.GetProperty("big").GetRawText());
    }

    [Fact]
    public void DetectsNonCanonicalInput()
        => Assert.False(JsonCanonicalizer.IsCanonical(Utf8("""{"b":1,"a":2}""")));
}
