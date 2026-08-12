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
/// the documented millisecond precision, and no greater precision is assumed. A value with more
/// than seven fractional digits is rejected rather than silently truncated, because silently
/// losing precision on health data is worse than failing loudly.
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
    {
        result = default;

        if (string.IsNullOrEmpty(value) || !HasRepresentableFraction(value))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        result = new GoogleTimestamp(parsed);
        return true;
    }

    /// <summary>
    /// Rejects fractions finer than <see cref="DateTimeOffset"/> can represent.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// happily truncates a nanosecond-precision fraction. For health data, losing precision
    /// without saying so is not acceptable, so the value is rejected instead.
    /// </remarks>
    private static bool HasRepresentableFraction(string value)
    {
        var dot = value.IndexOf('.', StringComparison.Ordinal);

        if (dot < 0)
        {
            return true;
        }

        var digits = 0;

        for (var i = dot + 1; i < value.Length && char.IsAsciiDigit(value[i]); i++)
        {
            digits++;
        }

        return digits <= MaxFractionalDigits;
    }

    /// <summary>
    /// Renders the canonical wire representation, for example <c>2026-08-09T12:34:56.789Z</c>.
    /// </summary>
    /// <remarks>
    /// Fractional digits are emitted in groups of three (0, 3 or 6), matching the canonical
    /// protobuf JSON form. Formatting is invariant so output never varies by locale.
    /// </remarks>
    public override string ToString()
    {
        // Ticks within the current second, at 100-nanosecond resolution.
        var fractionTicks = Value.UtcDateTime.Ticks % TimeSpan.TicksPerSecond;

        var format = fractionTicks == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'"
            : fractionTicks % (TimeSpan.TicksPerMillisecond) == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'"
            : "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'ffffff'Z'";

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
