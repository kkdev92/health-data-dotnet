namespace Kkdev92.HealthData.Tests;

public sealed class GoogleDurationTests
{
    [Theory]
    [InlineData("3s", 3, 0)]
    [InlineData("0s", 0, 0)]
    [InlineData("-14400s", -14400, 0)]
    [InlineData("1.5s", 1, 500_000_000)]
    [InlineData("0.000000001s", 0, 1)]
    [InlineData("-0.000000001s", 0, -1)]
    [InlineData("+7s", 7, 0)]
    [InlineData("315576000000s", 315576000000, 0)]
    public void ParsesWireForms(string wire, long seconds, int nanos)
    {
        Assert.True(GoogleDuration.TryParse(wire, out var duration));
        Assert.Equal(seconds, duration.Seconds);
        Assert.Equal(nanos, duration.Nanos);
    }

    [Theory]
    [InlineData(3, 0, "3s")]
    [InlineData(0, 0, "0s")]
    [InlineData(-14400, 0, "-14400s")]
    [InlineData(1, 500_000_000, "1.500s")]
    [InlineData(0, 1, "0.000000001s")]
    [InlineData(0, -1, "-0.000000001s")]
    [InlineData(0, 1_000, "0.000001s")]
    public void RendersCanonicalWireForm(long seconds, int nanos, string expected)
        => Assert.Equal(expected, new GoogleDuration(seconds, nanos).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("s")]
    [InlineData("abcs")]
    [InlineData("1.s")]
    [InlineData(".5s")]
    [InlineData("1.0000000001s")] // finer than nanoseconds
    public void RejectsInvalidWireForms(string wire)
        => Assert.False(GoogleDuration.TryParse(wire, out _));

    [Fact]
    public void RoundTripsNanosecondPrecisionLosslessly()
    {
        // The reason this is not a TimeSpan: TimeSpan resolves to 100ns and would drop the last
        // two digits (ADR-0008).
        const string wire = "12.123456789s";

        Assert.True(GoogleDuration.TryParse(wire, out var duration));
        Assert.Equal(123_456_789, duration.Nanos);
        Assert.Equal(wire, duration.ToString());
    }

    [Fact]
    public void ConvertsToTimeSpanWithDocumentedLoss()
    {
        var duration = new GoogleDuration(1, 123_456_789);

        // 100ns resolution: the final two digits are gone by definition.
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1_234_567), duration.ToTimeSpan());
    }

    [Fact]
    public void RejectsMixedSigns()
        => Assert.Throws<ArgumentException>(() => new GoogleDuration(1, -1));

    [Fact]
    public void RejectsOutOfRangeNanos()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleDuration(0, 1_000_000_000));

    /// <summary>
    /// A duration holds what a protobuf Duration can, about ten thousand years either way, and
    /// nothing more.
    /// </summary>
    /// <remarks>
    /// <c>duration.proto</c> bounds the seconds to 315,576,000,000 either side of zero, and every
    /// official JSON parser refuses a value past that, so the service cannot take one. Holding one
    /// anyway was not harmless: converted to a <see cref="TimeSpan"/> the tick count wrapped around
    /// into a wrong answer, and at <see cref="long.MinValue"/> the wire form could not be rendered.
    /// </remarks>
    [Theory]
    [InlineData(315_576_000_001L)]
    [InlineData(-315_576_000_001L)]
    [InlineData(1_000_000_000_000L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void RefusesSecondsBeyondWhatAProtobufDurationCarries(long seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleDuration(seconds, 0));

    [Theory]
    [InlineData("315576000001s")]
    [InlineData("-315576000001s")]
    [InlineData("1000000000000s")]
    [InlineData("9223372036854775807s")]
    public void RefusesAWireFormBeyondThatRange(string wire)
    {
        Assert.False(GoogleDuration.TryParse(wire, out _));
        Assert.Throws<FormatException>(() => GoogleDuration.Parse(wire));
    }

    /// <summary>
    /// Refusing a value does not repeat it: a duration can be part of a health record.
    /// </summary>
    [Fact]
    public void AnOutOfRangeValueIsNotQuotedInTheMessage()
    {
        var seconds = Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleDuration(987_654_321_098, 0));
        var nanos = Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleDuration(0, 1_234_567_890));

        Assert.DoesNotContain("987654321098", seconds.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890", nanos.Message, StringComparison.Ordinal);
    }

    /// <summary>A span longer than a protobuf Duration carries cannot be made into one.</summary>
    [Fact]
    public void RefusesATimeSpanBeyondThatRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleDuration.FromTimeSpan(TimeSpan.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleDuration.FromTimeSpan(TimeSpan.MinValue));
    }

    /// <summary>
    /// Both ends of the range are held, rendered, read back and converted exactly.
    /// </summary>
    [Theory]
    [InlineData(315_576_000_000L, 999_999_999, "315576000000.999999999s")]
    [InlineData(-315_576_000_000L, -999_999_999, "-315576000000.999999999s")]
    public void TheEndsOfTheRangeRoundTrip(long seconds, int nanos, string wire)
    {
        var duration = new GoogleDuration(seconds, nanos);

        Assert.Equal(wire, duration.ToString());
        Assert.Equal(duration, GoogleDuration.Parse(wire));
        Assert.Equal(TimeSpan.FromTicks((seconds * TimeSpan.TicksPerSecond) + (nanos / 100)), duration.ToTimeSpan());
    }
}

public sealed class GoogleTimestampTests
{
    [Fact]
    public void ParsesDocumentedFormat()
    {
        Assert.True(GoogleTimestamp.TryParse("2026-08-09T12:34:56.789Z", out var timestamp));
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 12, 34, 56, 789, TimeSpan.Zero), timestamp.Value);
    }

    [Fact]
    public void NormalizesAnOffsetToUtc()
    {
        Assert.True(GoogleTimestamp.TryParse("2026-08-09T08:34:56-04:00", out var timestamp));
        Assert.Equal(TimeSpan.Zero, timestamp.Value.Offset);
        Assert.Equal(12, timestamp.Value.Hour);
    }

    [Theory]
    [InlineData("2026-08-09T12:34:56Z", "2026-08-09T12:34:56Z")]
    [InlineData("2026-08-09T12:34:56.789Z", "2026-08-09T12:34:56.789Z")]
    [InlineData("2026-08-09T12:34:56.000Z", "2026-08-09T12:34:56Z")]
    [InlineData("2026-08-09T12:34:56.123456Z", "2026-08-09T12:34:56.123456Z")]
    [InlineData("2026-08-09T12:34:56.1234567Z", "2026-08-09T12:34:56.123456700Z")]
    [InlineData("2026-08-09T12:34:56.0000001Z", "2026-08-09T12:34:56.000000100Z")]
    public void RendersCanonicalWireForm(string input, string expected)
    {
        Assert.True(GoogleTimestamp.TryParse(input, out var timestamp));
        Assert.Equal(expected, timestamp.ToString());
    }

    /// <summary>
    /// Whatever the type holds, its wire form gives back exactly.
    /// </summary>
    /// <remarks>
    /// Parsing refuses a fraction finer than it can hold rather than drop digits. Writing has to
    /// keep the same promise, or a value read from the service and sent back loses its last digit
    /// on the way out.
    /// </remarks>
    [Fact]
    public void EveryValueSurvivesARoundTripThroughItsWireForm()
    {
        var random = new Random(3339);

        for (var i = 0; i < 2000; i++)
        {
            var ticks = random.NextInt64(DateTimeOffset.MinValue.UtcTicks, DateTimeOffset.MaxValue.UtcTicks);
            var timestamp = new GoogleTimestamp(new DateTimeOffset(ticks, TimeSpan.Zero));

            Assert.Equal(timestamp, GoogleTimestamp.Parse(timestamp.ToString()));
        }
    }

    /// <summary>
    /// What RFC 3339 does not allow is refused, rather than read as something else.
    /// </summary>
    /// <remarks>
    /// A parser that takes whatever the framework's own date parsing takes reads a time of day on
    /// its own as that time today, a date as midnight, and a date-time without an offset as UTC.
    /// Each is a guess the caller did not make, and the first gives a different answer on every day
    /// it runs. The rest are what the grammar has no room for: a field missing or out of range, a
    /// leap second, whitespace, digits that are not ASCII.
    /// </remarks>
    [Theory]
    [InlineData("12:34")]
    [InlineData("12:34:56Z")]
    [InlineData("2026-08-09")]
    [InlineData("08/09/2026")]
    [InlineData("Sun, 09 Aug 2026 12:34:56 GMT")]
    [InlineData("2026-08-09T12:34:56")]
    [InlineData("2026-08-09 12:34:56Z")]
    [InlineData("2026-08-09T12:34Z")]
    [InlineData("2026-8-9T12:34:56Z")]
    [InlineData("2026-08-09T12:34:56.Z")]
    [InlineData("2026-08-09T12:34:56+0900")]
    [InlineData("2026-08-09T12:34:56+09")]
    [InlineData(" 2026-08-09T12:34:56Z")]
    [InlineData("2026-08-09T12:34:56Z ")]
    [InlineData("2026-08-09T24:00:00Z")]
    [InlineData("2026-08-09T12:60:00Z")]
    [InlineData("2026-08-09T12:34:60Z")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("0000-01-01T00:00:00Z")]
    [InlineData("2026-08-09T12:34:56+24:00")]
    [InlineData("２０２６-08-09T12:34:56Z")]
    public void RefusesWhatRfc3339DoesNotAllow(string input)
    {
        Assert.False(GoogleTimestamp.TryParse(input, out _));
        Assert.Throws<FormatException>(() => GoogleTimestamp.Parse(input));
    }

    /// <summary>
    /// What RFC 3339 does allow is read, including the forms that look unusual.
    /// </summary>
    /// <remarks>
    /// Section 5.6 allows <c>t</c> and <c>z</c> in lower case, and section 4.3 gives <c>-00:00</c>
    /// as UTC with an unknown local offset. An offset is two digits of hours up to 23, which is wider
    /// than any zone in use; the instant it names is still exact.
    /// </remarks>
    [Theory]
    [InlineData("2026-08-09t12:34:56z", "2026-08-09T12:34:56Z")]
    [InlineData("2026-08-09T12:34:56.5Z", "2026-08-09T12:34:56.500Z")]
    [InlineData("2026-08-09T12:34:56-00:00", "2026-08-09T12:34:56Z")]
    [InlineData("2026-08-09T23:30:00+23:59", "2026-08-08T23:31:00Z")]
    [InlineData("2026-08-09T00:00:00.1234567-04:00", "2026-08-09T04:00:00.123456700Z")]
    [InlineData("2026-08-09T12:34:56.123456700Z", "2026-08-09T12:34:56.123456700Z")]
    [InlineData("2026-08-09T12:34:56.5000000000Z", "2026-08-09T12:34:56.500Z")]
    [InlineData("0001-01-01T00:00:00Z", "0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.9999999Z", "9999-12-31T23:59:59.999999900Z")]
    public void ReadsEveryFormRfc3339Allows(string input, string expected)
        => Assert.Equal(expected, GoogleTimestamp.Parse(input).ToString());

    [Fact]
    public void RejectsPrecisionItCannotRepresent()
    {
        // DateTimeOffset resolves to 100ns. Silently truncating health data timestamps would be
        // worse than refusing them, so a fraction with a digit finer than that is rejected.
        Assert.False(GoogleTimestamp.TryParse("2026-08-09T12:34:56.123456789Z", out _));
        Assert.Throws<FormatException>(() => GoogleTimestamp.Parse("2026-08-09T12:34:56.123456789Z"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-timestamp")]
    public void RejectsInvalidInput(string input)
        => Assert.False(GoogleTimestamp.TryParse(input, out _));

    [Fact]
    public void ExceptionMessageDoesNotEchoTheValue()
    {
        // A timestamp can be part of a health payload; it must not leak through an exception
        // message.
        var exception = Assert.Throws<FormatException>(() => GoogleTimestamp.Parse("2026-08-09T12:34:56.123456789Z"));
        Assert.DoesNotContain("2026", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdersByInstantRegardlessOfTheOffsetItWasParsedFrom()
    {
        // The same instant written two ways compares equal; an earlier one compares smaller.
        // Eight operators and CompareTo, none of which anything reached before this test.
        var noon = GoogleTimestamp.Parse("2026-08-09T12:00:00Z");
        var alsoNoon = GoogleTimestamp.Parse("2026-08-09T08:00:00-04:00");
        var later = GoogleTimestamp.Parse("2026-08-09T12:00:00.001Z");

        Assert.True(noon == alsoNoon);
        Assert.False(noon != alsoNoon);
        Assert.Equal(0, noon.CompareTo(alsoNoon));
        Assert.Equal(noon.GetHashCode(), alsoNoon.GetHashCode());

        Assert.True(noon < later);
        Assert.True(noon <= later);
        Assert.True(noon <= alsoNoon);
        Assert.True(later > noon);
        Assert.True(later >= noon);
        Assert.True(alsoNoon >= noon);

        Assert.False(later < noon);
        Assert.False(noon > later);
    }

    [Fact]
    public void ConvertsImplicitlyToAndFromDateTimeOffsetAsUtc()
    {
        var local = new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.FromHours(-4));

        GoogleTimestamp timestamp = local;
        DateTimeOffset back = timestamp;

        Assert.Equal(TimeSpan.Zero, timestamp.Value.Offset);
        Assert.Equal(local, back);
        Assert.Equal(timestamp, GoogleTimestamp.FromDateTimeOffset(local));
        Assert.Equal(back, timestamp.ToDateTimeOffset());
    }
}

public sealed class GoogleFieldMaskTests
{
    [Fact]
    public void RendersCommaSeparatedPaths()
        => Assert.Equal("age,userConfiguredWalkingStrideLengthMm",
            new GoogleFieldMask("age", "userConfiguredWalkingStrideLengthMm").ToString());

    [Fact]
    public void ParsesCommaSeparatedPaths()
    {
        var mask = GoogleFieldMask.Parse("age, name");

        Assert.Equal(["age", "name"], mask.Paths);
        Assert.False(mask.IsEmpty);
    }

    [Fact]
    public void DefaultIsEmpty()
    {
        Assert.True(default(GoogleFieldMask).IsEmpty);
        Assert.Equal(string.Empty, default(GoogleFieldMask).ToString());
    }

    [Fact]
    public void RejectsPathsContainingASeparator()
        => Assert.Throws<ArgumentException>(() => new GoogleFieldMask("age,name"));

    [Fact]
    public void ComparesByPathSequence()
    {
        Assert.Equal(new GoogleFieldMask("a", "b"), new GoogleFieldMask("a", "b"));
        Assert.NotEqual(new GoogleFieldMask("a", "b"), new GoogleFieldMask("b", "a"));
    }

    /// <summary>
    /// An empty mask is not a mask that means "nothing", and not one that means "everything".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>field_mask.proto</c> says libraries "have various different behaviors in the face of
    /// empty masks", and tells service authors to special-case it. So the wire meaning is
    /// genuinely undefined, and there is nothing for this side to convert it into.
    /// </para>
    /// <para>
    /// It used to parse to <c>default</c>, which the request builder then treated as "no mask
    /// supplied" — a documented meaning under AIP-134, "replace fields which are present", and not
    /// the one the caller wrote. Undefined turned into something specific, silently, one layer
    /// below where anybody was looking.
    /// </para>
    /// </remarks>
    [Fact]
    public void ParsingAnEmptyMaskThrowsRatherThanMeaningSomethingElse()
    {
        var thrown = Assert.Throws<FormatException>(() => GoogleFieldMask.Parse(""));

        Assert.Contains("empty", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a,,b")]
    [InlineData(",")]
    [InlineData("a,")]
    [InlineData(",a")]
    [InlineData("   ")]
    public void AnEmptySegmentIsNotSilentlyDropped(string value)
    {
        // "a,,b" used to parse as "a,b". The caller wrote three paths and two went out, which is a
        // typo the service never sees and the caller never hears about.
        Assert.Throws<FormatException>(() => GoogleFieldMask.Parse(value));
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a b")]
    public void APathThatIsNotAFieldPathIsRejected(string value)
    {
        // Syntax only. Whether 'age' is a field of the message being patched is the service's
        // question; whether 'a..b' could be a field path of anything is this side's.
        Assert.Throws<FormatException>(() => GoogleFieldMask.Parse(value));
    }

    [Fact]
    public void TheSpecialFullReplacementMaskParses()
    {
        // AIP-134: update methods "MUST support the special value * meaning full replace". It is
        // not a field path, so the syntax check has to know about it.
        var mask = GoogleFieldMask.Parse("*");

        Assert.Equal(["*"], mask.Paths);
        Assert.False(mask.IsEmpty);
    }

    [Fact]
    public void ANestedPathParses()
    {
        var mask = GoogleFieldMask.Parse("interval.startTime,steps.count");

        Assert.Equal(["interval.startTime", "steps.count"], mask.Paths);
    }
}
