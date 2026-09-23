using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Pins what the int64, timestamp and duration converters read and write, independently of how
/// they read it.
/// </summary>
/// <remarks>
/// <para>
/// Every int64, timestamp and duration in a response passes through one of these, which makes them
/// the hottest code in deserialization and the obvious place to stop turning each value into a
/// string before parsing it. What must not move while that happens is the answer: the same text
/// gives the same value, or the same refusal, however it arrived — as plain bytes, escaped, or
/// split across two buffers.
/// </para>
/// <para>
/// So the rule is stated as an equivalence as well as by example. For each converter, what it reads
/// from a JSON string is what the matching string parser returns for the unescaped text, checked
/// over generated input from a fixed seed.
/// </para>
/// </remarks>
public sealed class WirePrimitiveConverterTests
{
    private const int Cases = 2000;

    private static JsonSerializerOptions Options => HealthDataJson.ReadOptions;

    [Theory]
    [InlineData("\"72\"", 72L)]
    [InlineData("\"+5\"", 5L)]
    [InlineData("\"-0\"", 0L)]
    [InlineData("\"0072\"", 72L)]
    [InlineData("\"-9223372036854775808\"", long.MinValue)]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    [InlineData("72", 72L)]
    [InlineData("-5", -5L)]
    public void AnInt64IsReadFromAStringOrANumber(string json, long expected)
        => Assert.Equal(expected, Read(new Int64StringConverter(), Plain(json)));

    /// <summary>
    /// JSON lets any character of a string be escaped, so the digits can arrive as escapes.
    /// </summary>
    [Theory]
    [InlineData("\\u0037\\u0032", 72L)]
    [InlineData("\\u002D5", -5L)]
    [InlineData("1\\u0032", 12L)]
    public void AnInt64WhoseDigitsAreEscapedReadsTheSame(string content, long expected)
        => Assert.Equal(expected, Read(new Int64StringConverter(), Plain(Quoted(content))));

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("0x10")]
    [InlineData("1e3")]
    [InlineData("5.0")]
    [InlineData("")]
    [InlineData("--5")]
    [InlineData("+-5")]
    [InlineData("٥")]
    [InlineData("\\u0665")]
    public void AnInt64StringThatIsNotOneIsRefused(string content)
        => Assert.Throws<JsonException>(() => Read(new Int64StringConverter(), Plain(Quoted(content))));

    [Theory]
    [InlineData(0L, "\"0\"")]
    [InlineData(-5L, "\"-5\"")]
    [InlineData(long.MinValue, "\"-9223372036854775808\"")]
    [InlineData(long.MaxValue, "\"9223372036854775807\"")]
    public void AnInt64IsWrittenAsAString(long value, string expected)
        => Assert.Equal(expected, Write(new Int64StringConverter(), value));

    /// <summary>
    /// The path a response takes, through a generated model, with the digits escaped.
    /// </summary>
    [Fact]
    public void AGeneratedModelReadsEscapedDigits()
    {
        var heartRate = JsonSerializer.Deserialize(
            """{"beatsPerMinute":"72"}""", HealthDataJson.ReadInfo<HeartRate>())!;

        Assert.Equal(72L, heartRate.BeatsPerMinute);
    }

    /// <summary>
    /// A reader over two buffers really does hand the converter a value in two pieces.
    /// </summary>
    /// <remarks>
    /// Every test below that splits its input relies on this. Without it they would pass while
    /// only ever exercising the contiguous case.
    /// </remarks>
    [Fact]
    public void SplittingTheInputPresentsTheValueAsASequence()
    {
        var reader = new Utf8JsonReader(Split("\"1234567890123\""));

        Assert.True(reader.Read());
        Assert.True(reader.HasValueSequence);
    }

    [Fact]
    public void AnInt64ReadsExactlyWhatLongTryParseReads()
    {
        var converter = new Int64StringConverter();

        foreach (var text in Int64Candidates(seed: 64))
        {
            long? expected = long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

            foreach (var (form, json) in Forms(text))
            {
                Assert.True(
                    Equals(expected, ReadOrRefuse(converter, json)),
                    $"'{text}' read {form} disagreed with long.TryParse.");
            }
        }
    }

    [Fact]
    public void ATimestampReadsExactlyWhatGoogleTimestampTryParseReads()
    {
        var converter = new GoogleTimestampConverter();

        foreach (var text in TimestampCandidates(seed: 3339))
        {
            GoogleTimestamp? expected = GoogleTimestamp.TryParse(text, out var value) ? value : null;

            foreach (var (form, json) in Forms(text))
            {
                Assert.True(
                    Equals(expected, ReadOrRefuse(converter, json)),
                    $"'{text}' read {form} disagreed with GoogleTimestamp.TryParse.");
            }
        }
    }

    [Fact]
    public void ADurationReadsExactlyWhatGoogleDurationTryParseReads()
    {
        var converter = new GoogleDurationConverter();

        foreach (var text in DurationCandidates(seed: 9))
        {
            GoogleDuration? expected = GoogleDuration.TryParse(text, out var value) ? value : null;

            foreach (var (form, json) in Forms(text))
            {
                Assert.True(
                    Equals(expected, ReadOrRefuse(converter, json)),
                    $"'{text}' read {form} disagreed with GoogleDuration.TryParse.");
            }
        }
    }

    /// <summary>
    /// What goes on the wire is the value's own rendering, in quotes, byte for byte.
    /// </summary>
    [Fact]
    public void EachConverterWritesWhatToStringRenders()
    {
        var random = new Random(1);

        for (var i = 0; i < Cases; i++)
        {
            var int64 = random.NextInt64(long.MinValue, long.MaxValue);
            Assert.Equal(Quoted(int64.ToString(CultureInfo.InvariantCulture)), Write(new Int64StringConverter(), int64));
        }

        foreach (var text in TimestampCandidates(seed: 3339))
        {
            if (GoogleTimestamp.TryParse(text, out var timestamp))
            {
                Assert.Equal(Quoted(timestamp.ToString()), Write(new GoogleTimestampConverter(), timestamp));
            }
        }

        foreach (var text in DurationCandidates(seed: 9))
        {
            if (GoogleDuration.TryParse(text, out var duration))
            {
                Assert.Equal(Quoted(duration.ToString()), Write(new GoogleDurationConverter(), duration));
            }
        }
    }

    [Theory]
    [InlineData("12")]
    [InlineData("true")]
    [InlineData("null")]
    public void ATimestampOrDurationThatIsNotAStringIsRefused(string json)
    {
        Assert.Throws<JsonException>(() => Read(new GoogleTimestampConverter(), Plain(json)));
        Assert.Throws<JsonException>(() => Read(new GoogleDurationConverter(), Plain(json)));
    }

    /// <summary>
    /// Integers of the shapes that matter at the edges, plus arbitrary strings over the characters
    /// a number parser has opinions about.
    /// </summary>
    private static IEnumerable<string> Int64Candidates(int seed)
    {
        yield return "0";
        yield return "-0";
        yield return "+0";
        yield return "9223372036854775807";
        yield return "9223372036854775808";
        yield return "-9223372036854775808";
        yield return "-9223372036854775809";
        yield return "00000000000000000000000000001";
        yield return "٥";
        yield return "５";

        var random = new Random(seed);
        const string alphabet = "0123456789000000000+-. eEx٥５";

        for (var i = 0; i < Cases; i++)
        {
            var length = random.Next(0, 24);
            var text = new StringBuilder(length);

            for (var j = 0; j < length; j++)
            {
                text.Append(alphabet[random.Next(alphabet.Length)]);
            }

            yield return text.ToString();
        }
    }

    /// <summary>
    /// Timestamps built from parts, each part sometimes valid and sometimes not.
    /// </summary>
    /// <remarks>
    /// Every candidate has a date in it. A time of day alone would be read against the clock, and a
    /// test that compares two reads of the same text would fail if midnight fell between them.
    /// </remarks>
    private static IEnumerable<string> TimestampCandidates(int seed)
    {
        yield return "2026-08-09T12:34:56Z";
        yield return "2026-08-09T12:34:56.789Z";
        yield return "2026-08-09T12:34:56.1234567Z";
        yield return "2026-08-09T12:34:56.12345678Z";
        yield return "2026-08-09T08:34:56-04:00";
        yield return "2026-08-09t12:34:56z";
        yield return "2026-08-09 12:34:56Z";
        yield return "2026-08-09T12:34:56";
        yield return "2026-08-09";
        yield return "2026-02-30T00:00:00Z";
        yield return "0001-01-01T00:00:00Z";
        yield return "9999-12-31T23:59:59.9999999Z";
        yield return string.Empty;

        var random = new Random(seed);
        string[] separators = ["T", "T", "T", "t", " "];
        string[] offsets = ["Z", "Z", "z", "+09:00", "-04:00", "+23:59", "+24:00", "+0900", string.Empty];

        for (var i = 0; i < Cases; i++)
        {
            var text = new StringBuilder();

            text.Append(CultureInfo.InvariantCulture, $"{random.Next(0, 10000):D4}-{random.Next(0, 14):D2}-{random.Next(0, 33):D2}");
            text.Append(separators[random.Next(separators.Length)]);
            text.Append(CultureInfo.InvariantCulture, $"{random.Next(0, 25):D2}:{random.Next(0, 61):D2}:{random.Next(0, 61):D2}");

            var digits = random.Next(-1, 11);

            if (digits >= 0)
            {
                text.Append('.');

                for (var j = 0; j < digits; j++)
                {
                    text.Append((char)('0' + random.Next(10)));
                }
            }

            text.Append(offsets[random.Next(offsets.Length)]);

            yield return text.ToString();
        }
    }

    /// <summary>
    /// Durations built from parts, each part sometimes valid and sometimes not.
    /// </summary>
    private static IEnumerable<string> DurationCandidates(int seed)
    {
        yield return "3s";
        yield return "1.5s";
        yield return "-0.000000001s";
        yield return "-14400s";
        yield return "1.0000000001s";
        yield return "315576000000s";
        yield return "1.5";
        yield return "s";
        yield return ".5s";
        yield return "3.s";
        yield return "3S";
        yield return string.Empty;

        var random = new Random(seed);
        string[] signs = [string.Empty, string.Empty, "-", "+", "--"];
        string[] suffixes = ["s", "s", "s", "S", string.Empty, "s "];

        for (var i = 0; i < Cases; i++)
        {
            var text = new StringBuilder(signs[random.Next(signs.Length)]);
            var whole = random.Next(0, 21);

            for (var j = 0; j < whole; j++)
            {
                text.Append((char)('0' + random.Next(10)));
            }

            var digits = random.Next(-1, 12);

            if (digits >= 0)
            {
                text.Append('.');

                for (var j = 0; j < digits; j++)
                {
                    text.Append((char)('0' + random.Next(10)));
                }
            }

            text.Append(suffixes[random.Next(suffixes.Length)]);

            yield return text.ToString();
        }
    }

    /// <summary>
    /// The same string as JSON three ways: as it is, with every character escaped, and split
    /// across two buffers.
    /// </summary>
    /// <remarks>
    /// The candidates never contain a quote, a backslash or a control character, so the plain form
    /// is valid JSON without escaping anything.
    /// </remarks>
    private static IEnumerable<(string Form, ReadOnlySequence<byte> Json)> Forms(string text)
    {
        yield return ("as it is", Plain(Quoted(text)));
        yield return ("fully escaped", Plain(Quoted(EscapeEveryCharacter(text))));
        yield return ("split across buffers", Split(Quoted(text)));
    }

    private static string Quoted(string content) => $"\"{content}\"";

    private static string EscapeEveryCharacter(string text)
    {
        var escaped = new StringBuilder(text.Length * 6);

        foreach (var character in text)
        {
            escaped.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:X4}");
        }

        return escaped.ToString();
    }

    private static ReadOnlySequence<byte> Plain(string json) => new(Encoding.UTF8.GetBytes(json));

    /// <summary>Two buffers, split in the middle of the value.</summary>
    private static ReadOnlySequence<byte> Split(string json)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        var at = Math.Max(1, utf8.Length / 2);

        var first = new Segment(utf8.AsMemory(0, at));
        var last = first.Append(utf8.AsMemory(at));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static T? Read<T>(JsonConverter<T> converter, ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        Assert.True(reader.Read());

        return converter.Read(ref reader, typeof(T), Options);
    }

    /// <summary>The value read, or null when the converter refused it with a JSON error.</summary>
    private static T? ReadOrRefuse<T>(JsonConverter<T> converter, ReadOnlySequence<byte> json)
        where T : struct
    {
        try
        {
            return Read(converter, json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Write<T>(JsonConverter<T> converter, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            converter.Write(writer, value, Options);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;

            return next;
        }
    }
}
