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
    public void RendersCanonicalWireForm(string input, string expected)
    {
        Assert.True(GoogleTimestamp.TryParse(input, out var timestamp));
        Assert.Equal(expected, timestamp.ToString());
    }

    [Fact]
    public void RejectsPrecisionItCannotRepresent()
    {
        // DateTimeOffset resolves to 100ns. Silently truncating health data timestamps would be
        // worse than refusing them, so a 9-digit fraction is rejected.
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
