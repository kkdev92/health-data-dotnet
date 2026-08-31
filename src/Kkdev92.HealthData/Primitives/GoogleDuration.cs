using System.Globalization;

namespace Kkdev92.HealthData;

/// <summary>
/// A Google API duration: a signed span expressed as seconds and nanoseconds.
/// </summary>
/// <remarks>
/// <para>
/// The wire format is documented as "String ends in suffix 's' preceded by number of seconds,
/// with nanoseconds as fractional seconds", for example <c>"3s"</c>, <c>"1.5s"</c> or
/// <c>"-0.000000001s"</c>.
/// </para>
/// <para>
/// Seconds and nanoseconds are stored separately rather than as a <see cref="TimeSpan"/> because
/// <see cref="TimeSpan"/> resolves to 100 nanoseconds and would silently discard the last two
/// digits of a nanosecond-precision value (ADR-0008).
/// </para>
/// <para>
/// Discovery revision 20260826 uses this format in 28 places, including every <c>utcOffset</c>
/// on a health record.
/// </para>
/// </remarks>
public readonly struct GoogleDuration : IEquatable<GoogleDuration>
{
    /// <summary>Nanoseconds in one second.</summary>
    private const int NanosPerSecond = 1_000_000_000;

    /// <summary>Creates a duration from whole seconds and a nanosecond adjustment.</summary>
    /// <param name="seconds">Whole seconds.</param>
    /// <param name="nanos">
    /// Nanoseconds in the range -999,999,999 to 999,999,999. When <paramref name="seconds"/> is
    /// non-zero the two must share a sign, matching the protobuf duration contract.
    /// </param>
    public GoogleDuration(long seconds, int nanos)
    {
        if (nanos is <= -NanosPerSecond or >= NanosPerSecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nanos), nanos, "Nanoseconds must be between -999999999 and 999999999.");
        }

        if (seconds > 0 && nanos < 0)
        {
            throw new ArgumentException("Seconds and nanoseconds must not have opposite signs.", nameof(nanos));
        }

        if (seconds < 0 && nanos > 0)
        {
            throw new ArgumentException("Seconds and nanoseconds must not have opposite signs.", nameof(nanos));
        }

        Seconds = seconds;
        Nanos = nanos;
    }

    /// <summary>Whole seconds.</summary>
    public long Seconds { get; }

    /// <summary>The nanosecond component, with the same sign as <see cref="Seconds"/>.</summary>
    public int Nanos { get; }

    /// <summary>A zero duration.</summary>
    public static GoogleDuration Zero => default;

    /// <summary>
    /// Converts to a <see cref="TimeSpan"/>, rounding toward zero at 100-nanosecond resolution.
    /// </summary>
    /// <remarks>
    /// Lossy by definition when the value carries sub-100-nanosecond precision. Use
    /// <see cref="Seconds"/> and <see cref="Nanos"/> when exactness matters.
    /// </remarks>
    public TimeSpan ToTimeSpan()
        => TimeSpan.FromTicks((Seconds * TimeSpan.TicksPerSecond) + (Nanos / 100));

    /// <summary>Creates a duration from a <see cref="TimeSpan"/>.</summary>
    public static GoogleDuration FromTimeSpan(TimeSpan value)
    {
        var seconds = value.Ticks / TimeSpan.TicksPerSecond;
        var nanos = (int)(value.Ticks % TimeSpan.TicksPerSecond) * 100;
        return new GoogleDuration(seconds, nanos);
    }

    /// <summary>Parses the wire representation, for example <c>"1.5s"</c>.</summary>
    /// <exception cref="FormatException">The value is not a valid Google duration.</exception>
    public static GoogleDuration Parse(string value)
        => TryParse(value, out var result)
            ? result
            // The offending text is deliberately not echoed: this type is used on health payloads.
            : throw new FormatException("Value is not a valid Google API duration.");

    /// <summary>Attempts to parse the wire representation.</summary>
    public static bool TryParse(string? value, out GoogleDuration result)
    {
        result = default;

        if (string.IsNullOrEmpty(value) || value[^1] != 's')
        {
            return false;
        }

        var body = value.AsSpan(0, value.Length - 1);

        if (body.IsEmpty)
        {
            return false;
        }

        var negative = body[0] == '-';

        if (negative || body[0] == '+')
        {
            body = body[1..];
        }

        var dot = body.IndexOf('.');
        var wholePart = dot < 0 ? body : body[..dot];
        var fractionPart = dot < 0 ? [] : body[(dot + 1)..];

        if (wholePart.IsEmpty || (dot >= 0 && fractionPart.IsEmpty))
        {
            return false;
        }

        if (!long.TryParse(wholePart, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var nanos = 0;

        if (!fractionPart.IsEmpty)
        {
            // More than nanosecond precision is not representable and is rejected rather than
            // silently truncated.
            if (fractionPart.Length > 9)
            {
                return false;
            }

            if (!int.TryParse(fractionPart, NumberStyles.None, CultureInfo.InvariantCulture, out nanos))
            {
                return false;
            }

            for (var i = fractionPart.Length; i < 9; i++)
            {
                nanos *= 10;
            }
        }

        result = negative ? new GoogleDuration(-seconds, -nanos) : new GoogleDuration(seconds, nanos);
        return true;
    }

    /// <summary>
    /// Renders the wire representation.
    /// </summary>
    /// <remarks>
    /// Fractional digits are emitted in groups of three (0, 3, 6 or 9), which is the canonical
    /// protobuf JSON form. Formatting is invariant so output never varies by locale.
    /// </remarks>
    public override string ToString()
    {
        var nanos = Math.Abs(Nanos);
        var sign = Seconds < 0 || Nanos < 0 ? "-" : string.Empty;
        var seconds = Math.Abs(Seconds).ToString(CultureInfo.InvariantCulture);

        if (nanos == 0)
        {
            return $"{sign}{seconds}s";
        }

        var fraction = nanos.ToString("D9", CultureInfo.InvariantCulture);

        var digits = (nanos % 1_000_000) == 0 ? 3
            : (nanos % 1_000) == 0 ? 6
            : 9;

        return $"{sign}{seconds}.{fraction[..digits]}s";
    }

    /// <inheritdoc />
    public bool Equals(GoogleDuration other) => Seconds == other.Seconds && Nanos == other.Nanos;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GoogleDuration other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Seconds, Nanos);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(GoogleDuration left, GoogleDuration right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(GoogleDuration left, GoogleDuration right) => !left.Equals(right);
}
