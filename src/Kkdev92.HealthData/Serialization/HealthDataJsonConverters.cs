using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kkdev92.HealthData.Serialization;

/// <summary>
/// Reads and writes <c>int64</c> values, which Google transmits as JSON strings.
/// </summary>
/// <remarks>
/// Google's Discovery type reference declares 64-bit integers as <c>"type": "string"</c> with
/// <c>"format": "int64"</c>, because JSON numbers cannot carry the full 64-bit range safely.
/// Discovery revision 20260826 uses this in 36 places, including <c>HeartRate.beatsPerMinute</c>
/// and <c>Steps.count</c>.
/// </remarks>
public sealed class Int64StringConverter : JsonConverter<long>
{
    /// <inheritdoc />
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            // A bare number is accepted on read for robustness, even though Google sends a string.
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String when long.TryParse(
                reader.ReadText(stackalloc char[JsonStrings.StackLength]),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => throw new JsonException("Expected a 64-bit integer encoded as a JSON string."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>Reads and writes <see cref="GoogleTimestamp"/> as an RFC 3339 string.</summary>
public sealed class GoogleTimestampConverter : JsonConverter<GoogleTimestamp>
{
    /// <inheritdoc />
    public override GoogleTimestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected an RFC 3339 timestamp encoded as a JSON string.");
        }

        return GoogleTimestamp.TryParse(reader.ReadText(stackalloc char[JsonStrings.StackLength]), out var value)
            ? value
            : throw new JsonException("Value is not a valid RFC 3339 timestamp.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, GoogleTimestamp value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>Reads and writes <see cref="GoogleDuration"/> as a seconds-suffixed string.</summary>
public sealed class GoogleDurationConverter : JsonConverter<GoogleDuration>
{
    /// <inheritdoc />
    public override GoogleDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected a duration encoded as a JSON string.");
        }

        return GoogleDuration.TryParse(reader.ReadText(stackalloc char[JsonStrings.StackLength]), out var value)
            ? value
            : throw new JsonException("Value is not a valid Google API duration.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, GoogleDuration value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Reads and writes byte arrays using base64url with padding.
/// </summary>
/// <remarks>
/// <para>
/// Google's Discovery type reference specifies <c>"format": "byte"</c> as a "padded,
/// base64-encoded string of bytes, encoded with a URL and filename safe alphabet". That is
/// <em>not</em> what <see cref="System.Text.Json"/> does by default, which uses the standard
/// alphabet with <c>+</c> and <c>/</c>.
/// </para>
/// <para>
/// Internal because no schema reachable from the public surface uses the <c>byte</c> format today:
/// the only two occurrences belong to the excluded SMART Health Links operations. It exists so the
/// emitter has something correct to reference if that changes, and generated code lives in this
/// assembly. Promoting it to public later is an additive change.
/// </para>
/// </remarks>
internal sealed class Base64UrlBytesConverter : JsonConverter<byte[]>
{
    /// <inheritdoc />
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected base64url-encoded bytes as a JSON string.");
        }

        // Base64Url reads the padded and unpadded forms alike, so the alphabet substitution this
        // used to do by hand is not needed. Writing still spells it out: Google's format specifies
        // padding and Base64Url.EncodeToString omits it.
        return Base64Url.DecodeFromChars(reader.GetString());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_'));
    }
}

/// <summary>
/// Reads the current JSON string into a buffer rather than into a new string.
/// </summary>
/// <remarks>
/// Every int64, timestamp and duration in a response arrives as a JSON string, and reading it with
/// <see cref="Utf8JsonReader.GetString"/> allocated a string per value only to parse it and drop it.
/// <see cref="Utf8JsonReader.CopyString(Span{char})"/> writes the same characters — unescaped, and
/// joined when the value spans two buffers — into memory the caller already has, so the parser sees
/// exactly the text it saw before.
/// </remarks>
file static class JsonStrings
{
    /// <summary>
    /// The most characters read into the caller's buffer. Every value the converters accept is far
    /// shorter; a longer one is read as a string, exactly as before.
    /// </summary>
    public const int StackLength = 128;

    extension(in Utf8JsonReader reader)
    {
        public ReadOnlySpan<char> ReadText(Span<char> buffer)
        {
            var encodedLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;

            // Unescaping never lengthens a string, and no UTF-8 sequence becomes more UTF-16 code
            // units than it has bytes, so the encoded length bounds what CopyString writes.
            return encodedLength <= buffer.Length ? buffer[..reader.CopyString(buffer)] : reader.GetString();
        }
    }
}
