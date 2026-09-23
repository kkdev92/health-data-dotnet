using System.Globalization;

namespace Kkdev92.HealthData;

/// <summary>
/// A Google API timestamp: an RFC 3339 instant in UTC.
/// </summary>
/// <remarks>
/// <para>
/// Google documents this format as an "RFC3339 timestamp in UTC time" with the shape
/// <c>yyyy-MM-ddTHH:mm:ss.SSSZ</c>. Values are normalized to UTC on parse, so a value carrying an
/// offset is converted rather than rejected.
/// </para>
/// <para>
/// The underlying <see cref="DateTimeOffset"/> resolves to 100 nanoseconds. That is finer than
/// the documented millisecond precision, and no greater precision is assumed. A value with a
/// fraction finer than that is rejected rather than silently truncated, because silently losing
/// precision on health data is worse than failing loudly — and for the same reason, the wire form
/// written back carries every digit the value holds.
/// </para>
/// </remarks>
public readonly struct GoogleTimestamp : IEquatable<GoogleTimestamp>, IComparable<GoogleTimestamp>
{
    private const int MaxFractionalDigits = 7;

    /// <summary>Creates a timestamp, converting to UTC.</summary>
    public GoogleTimestamp(DateTimeOffset value) => Value = value.ToUniversalTime();

    /// <summary>The instant, always in UTC.</summary>
    public DateTimeOffset Value { get; }

    /// <summary>Converts to <see cref="DateTimeOffset"/>.</summary>
    public static implicit operator DateTimeOffset(GoogleTimestamp timestamp) => timestamp.Value;

    /// <summary>Converts from <see cref="DateTimeOffset"/>.</summary>
    public static implicit operator GoogleTimestamp(DateTimeOffset value) => new(value);

    /// <summary>Creates a timestamp from a <see cref="DateTimeOffset"/>.</summary>
    public static GoogleTimestamp FromDateTimeOffset(DateTimeOffset value) => new(value);

    /// <summary>Converts to a <see cref="DateTimeOffset"/>.</summary>
    public DateTimeOffset ToDateTimeOffset() => Value;

    /// <summary>Parses an RFC 3339 timestamp.</summary>
    /// <exception cref="FormatException">The value is not a valid RFC 3339 timestamp.</exception>
    public static GoogleTimestamp Parse(string value)
        => TryParse(value, out var result)
            // The offending text is deliberately not echoed: it may be part of a health payload.
            ? result
            : throw new FormatException("Value is not a valid RFC 3339 timestamp.");

    /// <summary>Attempts to parse an RFC 3339 timestamp.</summary>
    public static bool TryParse(string? value, out GoogleTimestamp result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Parses the RFC 3339 <c>date-time</c> production, and nothing wider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A four-digit year, two-digit month and day, <c>T</c>, a time with seconds, an optional
    /// fraction, and an offset: <c>Z</c>, or <c>+hh:mm</c> or <c>-hh:mm</c>. Section 5.6 allows the
    /// <c>T</c> and the <c>Z</c> in lower case, so either case is read.
    /// </para>
    /// <para>
    /// Written to the grammar rather than handed to <see cref="DateTimeOffset"/>'s own parsing,
    /// which reads far more: a time of day on its own as that time today, a date as midnight, a
    /// date-time without an offset as UTC. Each of those is a guess the caller did not make, and the
    /// first gives a different answer on every day it runs.
    /// </para>
    /// <para>
    /// A fraction finer than 100 nanoseconds is refused rather than truncated: a non-zero digit
    /// past the seventh would be dropped. Zeros past it are read, which is what lets the nine-digit
    /// form this type writes be read back. A leap second is refused as well:
    /// <see cref="DateTimeOffset"/> cannot hold one, and a protobuf timestamp does not either.
    /// </para>
    /// </remarks>
    internal static bool TryParse(ReadOnlySpan<char> text, out GoogleTimestamp result)
    {
        result = default;

        if (text.Length < 20
            || text[4] != '-' || text[7] != '-' || text[10] is not ('T' or 't') || text[13] != ':' || text[16] != ':'
            || !TryReadDigits(text[..4], out var year)
            || !TryReadDigits(text[5..7], out var month)
            || !TryReadDigits(text[8..10], out var day)
            || !TryReadDigits(text[11..13], out var hour)
            || !TryReadDigits(text[14..16], out var minute)
            || !TryReadDigits(text[17..19], out var second)
            || !TryReadFraction(text[19..], out var fractionTicks, out var rest)
            || !TryReadOffset(rest, out var offset))
        {
            return false;
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        // The offset is subtracted as ticks rather than handed to DateTimeOffset, which refuses an
        // offset beyond fourteen hours. The grammar allows up to 23:59, and the instant is exact.
        var utcTicks = new DateTime(year, month, day, hour, minute, second).Ticks + fractionTicks - offset.Ticks;

        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        result = new GoogleTimestamp(new DateTimeOffset(utcTicks, TimeSpan.Zero));
        return true;
    }

    /// <summary>
    /// Reads an optional <c>time-secfrac</c> as ticks, and returns what follows it.
    /// </summary>
    private static bool TryReadFraction(ReadOnlySpan<char> text, out long ticks, out ReadOnlySpan<char> rest)
    {
        ticks = 0;
        rest = text;

        if (text is not ['.', .. var afterDot])
        {
            return true;
        }

        var digits = afterDot.IndexOfAnyExceptInRange('0', '9');

        if (digits < 0)
        {
            digits = afterDot.Length;
        }

        // A dot with no digits is not a fraction. Past the seventh digit only zeros can be held:
        // anything else is finer than a tick, and dropping it would change the value unannounced.
        if (digits == 0
            || (digits > MaxFractionalDigits && afterDot[MaxFractionalDigits..digits].ContainsAnyExcept('0')))
        {
            return false;
        }

        var held = Math.Min(digits, MaxFractionalDigits);

        if (!TryReadDigits(afterDot[..held], out var fraction))
        {
            return false;
        }

        // The digits are the most significant ones: ".5" is 5,000,000 ticks, not 5.
        for (var i = held; i < MaxFractionalDigits; i++)
        {
            fraction *= 10;
        }

        ticks = fraction;
        rest = afterDot[digits..];
        return true;
    }

    /// <summary>Reads a <c>time-offset</c>: <c>Z</c>, or a sign, two-digit hours, a colon and minutes.</summary>
    private static bool TryReadOffset(ReadOnlySpan<char> text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        if (text is ['Z' or 'z'])
        {
            return true;
        }

        if (text is not [('+' or '-') and var sign, _, _, ':', _, _]
            || !TryReadDigits(text[1..3], out var hours)
            || !TryReadDigits(text[4..6], out var minutes)
            || hours > 23
            || minutes > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);

        if (sign == '-')
        {
            offset = -offset;
        }

        return true;
    }

    /// <summary>Reads ASCII digits, and only those: the grammar's <c>DIGIT</c> is 0 to 9.</summary>
    private static bool TryReadDigits(ReadOnlySpan<char> digits, out int value)
        => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Renders the canonical wire representation, for example <c>2026-08-09T12:34:56.789Z</c>.
    /// </summary>
    /// <remarks>
    /// Fractional digits are emitted in groups of three (0, 3, 6 or 9), matching the canonical
    /// protobuf JSON form. A value finer than a microsecond takes nine digits, the last two always
    /// zero: it holds a seventh digit, and writing six would drop it — the precision loss parsing
    /// refuses to cause. Formatting is invariant so output never varies by locale.
    /// </remarks>
    public override string ToString()
    {
        // Ticks within the current second, at 100-nanosecond resolution.
        var fractionTicks = Value.UtcDateTime.Ticks % TimeSpan.TicksPerSecond;

        var format = fractionTicks == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'"
            : fractionTicks % TimeSpan.TicksPerMillisecond == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'"
            : fractionTicks % TimeSpan.TicksPerMicrosecond == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'ffffff'Z'"
            : "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'00Z'";

        return Value.UtcDateTime.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public bool Equals(GoogleTimestamp other) => Value.Equals(other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GoogleTimestamp other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public int CompareTo(GoogleTimestamp other) => Value.CompareTo(other.Value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(GoogleTimestamp left, GoogleTimestamp right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(GoogleTimestamp left, GoogleTimestamp right) => !left.Equals(right);

    /// <summary>Less-than operator.</summary>
    public static bool operator <(GoogleTimestamp left, GoogleTimestamp right) => left.CompareTo(right) < 0;

    /// <summary>Less-than-or-equal operator.</summary>
    public static bool operator <=(GoogleTimestamp left, GoogleTimestamp right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(GoogleTimestamp left, GoogleTimestamp right) => left.CompareTo(right) > 0;

    /// <summary>Greater-than-or-equal operator.</summary>
    public static bool operator >=(GoogleTimestamp left, GoogleTimestamp right) => left.CompareTo(right) >= 0;
}
