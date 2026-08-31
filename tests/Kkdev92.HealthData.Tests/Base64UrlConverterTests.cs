using System.Text;
using System.Text.Json;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The converter for Discovery's <c>byte</c> format.
/// </summary>
/// <remarks>
/// <para>
/// Internal, and reachable only because the shipping assembly opens its internals to this project.
/// It had no test at all, on the reasoning that no schema on the public surface uses the format
/// today — but the generator's type map still names it, so a contract that grew a <c>byte</c>
/// field would start using a converter nothing had ever exercised.
/// </para>
/// <para>
/// Driven directly rather than through <see cref="JsonSerializer"/>: reflection is disabled for
/// this SDK, so serializing a bare <c>byte[]</c> has no contract to resolve. Reading and writing
/// the converter itself is also closer to what is being checked.
/// </para>
/// <para>
/// The asymmetry below is the point. Google specifies a "padded, base64-encoded string of bytes,
/// encoded with a URL and filename safe alphabet", so writing has to emit the padding — and
/// <c>Base64Url.EncodeToString</c> omits it. Reading accepts either form.
/// </para>
/// </remarks>
public sealed class Base64UrlConverterTests
{
    private static readonly Base64UrlBytesConverter Converter = new();

    [Theory]
    // 0xfb 0xff 0xbf sets the bits that differ between the two alphabets: standard base64 spells
    // them with '+' and '/', which is exactly what must not appear here.
    [InlineData(new byte[] { 0xfb, 0xff, 0xbf }, "-_-_")]
    [InlineData(new byte[] { 0xfb, 0xff, 0xbf, 0x01 }, "-_-_AQ==")]
    [InlineData(new byte[] { 0x00 }, "AA==")]
    public void WritesPaddedBase64Url(byte[] value, string expected)
        => Assert.Equal($"\"{expected}\"", Write(value));

    [Theory]
    [InlineData("-_-_AQ==")]   // padded, which is what Google's format specifies
    [InlineData("-_-_AQ")]     // unpadded, which is what most encoders produce
    public void ReadsPaddedAndUnpadded(string encoded)
        => Assert.Equal(new byte[] { 0xfb, 0xff, 0xbf, 0x01 }, Read($"\"{encoded}\""));

    [Fact]
    public void RoundTrips()
    {
        var value = new byte[] { 0xfb, 0xff, 0xbf, 0x01, 0x02, 0x03 };

        Assert.Equal(value, Read(Write(value)));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    public void RefusesAnythingButAString(string json)
        => Assert.Throws<JsonException>(() => Read(json));

    [Fact]
    public void RefusesTextThatIsNotBase64Url()
        => Assert.ThrowsAny<Exception>(() => Read("\"not base64!\""));

    [Fact]
    public void RefusesToWriteNull()
        => Assert.Throws<ArgumentNullException>(() => Write(null!));

    private static string Write(byte[] value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Converter.Write(writer, value, JsonSerializerOptions.Default);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static byte[] Read(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();

        return Converter.Read(ref reader, typeof(byte[]), JsonSerializerOptions.Default);
    }
}
