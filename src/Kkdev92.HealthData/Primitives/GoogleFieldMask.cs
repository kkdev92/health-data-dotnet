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

    /// <summary>
    /// Parses the comma-separated wire representation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Syntax only. Whether <c>age</c> is a field of the message being patched is the service's
    /// question and it answers it; whether <c>a..b</c> could be a field path of anything is this
    /// side's, and answering it here turns a 400 into a line number.
    /// </para>
    /// <para>
    /// An empty mask throws rather than parsing to nothing. <c>field_mask.proto</c> says libraries
    /// "have various different behaviors in the face of empty masks" and tells service authors to
    /// special-case it, so there is no meaning here to preserve. Returning <c>default</c> meant the
    /// request builder dropped the parameter, and an omitted mask has a documented meaning under
    /// AIP-134 — "replace fields which are present" — which is a decision the caller did not make.
    /// </para>
    /// <para>
    /// <c>*</c> is accepted as itself: AIP-134 requires update methods to support it as "full
    /// replace", and it is not a field path.
    /// </para>
    /// </remarks>
    /// <exception cref="FormatException">
    /// The value is empty, contains an empty segment, or contains something that is not a field
    /// path.
    /// </exception>
    public static GoogleFieldMask Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Trim().Length == 0)
        {
            throw new FormatException(
                "A field mask cannot be empty. The wire meaning of an empty mask is undefined - "
                + "field_mask.proto says implementations differ - so it cannot be sent as one, and "
                + "it is not the same as sending no mask at all. Omit the mask to leave it unset, "
                + "or pass \"*\" for a full replacement.");
        }

        // TrimEntries, but not RemoveEmptyEntries: "a,,b" used to arrive as two paths, so a typo
        // in the middle of a mask silently narrowed what was being written.
        var paths = value.Split(',', StringSplitOptions.TrimEntries);

        foreach (var path in paths)
        {
            if (path.Length == 0)
            {
                throw new FormatException(
                    $"The field mask '{value}' has an empty path in it. Each comma separates one "
                    + "field path, so a doubled or trailing comma is a path that names nothing.");
            }

            if (!IsFieldPath(path))
            {
                throw new FormatException(
                    $"'{path}' is not a field path. Google documents these as lower camel case "
                    + "names, dot-separated for nested fields, for example "
                    + "\"age,interval.startTime\".");
            }
        }

        return new GoogleFieldMask(paths);
    }

    /// <summary>Whether a single path is syntactically a field path.</summary>
    /// <remarks>
    /// Deliberately loose about the casing. Google documents lower camel case, and the mask is
    /// stated by the caller against a message this type knows nothing about, so a wire name whose
    /// shape this side did not anticipate should reach the service rather than be refused here.
    /// What is checked is structure: dot-separated segments, each non-empty and made of
    /// name characters.
    /// </remarks>
    private static bool IsFieldPath(string path)
    {
        // AIP-134 requires update methods to support "*". It is not a path, but it is a mask.
        if (path == "*")
        {
            return true;
        }

        foreach (var segment in path.Split('.'))
        {
            if (segment.Length == 0)
            {
                return false;
            }

            foreach (var character in segment)
            {
                if (!char.IsLetterOrDigit(character) && character != '_')
                {
                    return false;
                }
            }
        }

        return true;
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
