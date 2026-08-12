namespace Kkdev92.HealthData;

/// <summary>
/// A Google API field mask: an ordered set of field paths in lower camel case.
/// </summary>
/// <remarks>
/// <para>
/// Google documents the wire form as a "String where field names are separated by a comma" using
/// "lower-camel naming conventions", for example <c>"age,userConfiguredRunningStrideLengthMm"</c>.
/// </para>
/// <para>
/// In Discovery revision 20260805 this format appears <em>only</em> as the <c>updateMask</c>
/// query parameter, on four operations, and never inside a schema. It is therefore a query
/// serialization concern and needs no JSON converter (ADR-0008).
/// </para>
/// <para>
/// No protobuf runtime is involved: the mask is a value object over strings.
/// </para>
/// </remarks>
public readonly struct GoogleFieldMask : IEquatable<GoogleFieldMask>
{
    private readonly string[]? _paths;

    /// <summary>Creates a field mask from field paths.</summary>
    /// <exception cref="ArgumentException">A path is null, empty, or contains a comma.</exception>
    public GoogleFieldMask(params IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var materialized = paths.ToArray();

        foreach (var path in materialized)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A field mask path must not be empty.", nameof(paths));
            }

            if (path.Contains(',', StringComparison.Ordinal))
            {
                throw new ArgumentException("A field mask path must not contain a comma.", nameof(paths));
            }
        }

        _paths = materialized;
    }

    /// <summary>The field paths, in the order supplied.</summary>
    public IReadOnlyList<string> Paths => _paths ?? [];

    /// <summary>True when the mask names no fields.</summary>
    public bool IsEmpty => Paths.Count == 0;

    /// <summary>Parses the comma-separated wire representation.</summary>
    public static GoogleFieldMask Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Length == 0
            ? default
            : new GoogleFieldMask(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Renders the comma-separated wire representation.</summary>
    public override string ToString() => string.Join(',', Paths);

    /// <inheritdoc />
    public bool Equals(GoogleFieldMask other) => Paths.SequenceEqual(other.Paths, StringComparer.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GoogleFieldMask other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);

        foreach (var path in Paths)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(GoogleFieldMask left, GoogleFieldMask right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(GoogleFieldMask left, GoogleFieldMask right) => !left.Equals(right);
}
