using System.Text;

namespace Kkdev92.HealthData.Http;

/// <summary>
/// Expands the URI path templates that appear in the Google Health API Discovery document.
/// </summary>
/// <remarks>
/// <para>
/// Two variable forms occur, and they escape differently. Google specifies both in
/// <c>google/api/http.proto</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>{var}</c> is a single path segment: "all characters except <c>[-_.~0-9a-zA-Z]</c> are
///     percent-encoded". A <c>/</c> inside the value is therefore encoded as <c>%2F</c>.
///   </description></item>
///   <item><description>
///     <c>{+var}</c> spans multiple segments: "all characters except <c>[-_.~/0-9a-zA-Z]</c> are
///     percent-encoded". The separators survive; everything else, including <c>?</c>,
///     <c>#</c> and <c>%</c>, does not.
///   </description></item>
/// </list>
/// <para>
/// The multi-segment form deliberately <em>does not</em> follow RFC 6570 section 3.2.3 reserved
/// expansion. Google's own note explains why: reserved expansion leaves <c>?</c> and <c>#</c>
/// alone, "which would lead to invalid URLs".
/// </para>
/// <para>
/// This matters in practice. Google's official .NET client skips escaping entirely for
/// <c>{+var}</c>, so a resource id containing a reserved character produces a malformed request.
/// This SDK follows the specification rather than that implementation, and pins the behaviour
/// with golden tests.
/// </para>
/// <para>
/// <see cref="Uri.EscapeDataString(string)"/> preserves exactly <c>-._~0-9A-Za-z</c>, which is
/// the single-segment rule verbatim, so it is used as the primitive for both forms.
/// </para>
/// </remarks>
public static class UriTemplate
{
    /// <summary>
    /// Expands a template such as <c>v4/{+name}:exportExerciseTcx</c>.
    /// </summary>
    /// <param name="template">The path template, relative to the service root.</param>
    /// <param name="values">Path parameter values keyed by their wire names.</param>
    /// <returns>The expanded, escaped relative path.</returns>
    /// <exception cref="ArgumentException">The template is malformed.</exception>
    /// <exception cref="InvalidOperationException">A referenced parameter has no value.</exception>
    public static string Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        if (template.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var result = new StringBuilder(template.Length + 32);
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);

            if (open < 0)
            {
                result.Append(template, index, template.Length - index);
                break;
            }

            result.Append(template, index, open - index);

            var close = template.IndexOf('}', open);

            if (close < 0)
            {
                throw new ArgumentException($"Unterminated variable in path template '{template}'.", nameof(template));
            }

            var name = template[(open + 1)..close];
            var multiSegment = name.StartsWith('+');

            if (multiSegment)
            {
                name = name[1..];
            }

            if (!values.TryGetValue(name, out var value) || value is null)
            {
                throw new InvalidOperationException(
                    $"Path template '{template}' requires a value for '{name}'.");
            }

            result.Append(multiSegment ? EscapeMultiSegment(value) : Uri.EscapeDataString(value));
            index = close + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// Escapes a multi-segment value, preserving <c>/</c> and encoding everything else that is
    /// not unreserved.
    /// </summary>
    public static string EscapeMultiSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return value;
        }

        // Splitting on '/' and escaping each part is exactly the documented rule: the separator
        // survives, and every other reserved character is encoded.
        var segments = value.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }

    /// <summary>Escapes a single path segment, encoding <c>/</c> as <c>%2F</c>.</summary>
    public static string EscapeSingleSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(value);
    }
}
