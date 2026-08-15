using System.Text;

namespace Kkdev92.HealthData.Http;

/// <summary>
/// Builds the relative request URI for a generated operation.
/// </summary>
/// <remarks>
/// Wire names are used exactly as Discovery declares them. A query parameter called
/// <c>pageSize</c> is emitted as <c>pageSize</c>, never reshaped by a naming convention.
/// </remarks>
public sealed class HealthDataRequestBuilder
{
    private readonly Dictionary<string, string> _pathParameters = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, string>> _queryParameters = [];

    /// <summary>Creates a builder for the given path template.</summary>
    public HealthDataRequestBuilder(string pathTemplate)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathTemplate);
        PathTemplate = pathTemplate;
    }

    /// <summary>The path template being expanded.</summary>
    public string PathTemplate { get; }

    /// <summary>Sets a path parameter by its wire name.</summary>
    public HealthDataRequestBuilder SetPath(string wireName, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(wireName);
        ArgumentNullException.ThrowIfNull(value);

        _pathParameters[wireName] = value;
        return this;
    }

    /// <summary>Adds a query parameter, ignoring it when the value is absent.</summary>
    /// <remarks>
    /// Absent and empty are different: an explicitly empty string is still sent, because some
    /// filters treat it as meaningful.
    /// </remarks>
    public HealthDataRequestBuilder AddQuery(string wireName, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(wireName);

        if (value is not null)
        {
            _queryParameters.Add(new KeyValuePair<string, string>(wireName, value));
        }

        return this;
    }

    /// <summary>Adds a boolean query parameter using the lowercase JSON spelling.</summary>
    public HealthDataRequestBuilder AddQuery(string wireName, bool? value)
        => AddQuery(wireName, value is null ? null : value.Value ? "true" : "false");

    /// <summary>Adds an integer query parameter, formatted invariantly.</summary>
    public HealthDataRequestBuilder AddQuery(string wireName, int? value)
        => AddQuery(wireName, value?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Adds a field mask query parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null means the caller set no mask, and no parameter goes out — which AIP-134 defines as
    /// "replace fields which are present".
    /// </para>
    /// <para>
    /// An empty mask is not the same thing and is not treated as one. It used to be: both arrived
    /// here and both dropped the parameter, so "I set a mask" and "my mask names nothing" produced
    /// the same request. The second is a contradiction the caller has to resolve, and the wire
    /// meaning of an empty mask is undefined in any case. <see cref="GoogleFieldMask.Parse"/>
    /// refuses to build one; this catches the other way in, <c>default(GoogleFieldMask)</c>, which
    /// no constructor sees.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The mask is present and names no fields.</exception>
    public HealthDataRequestBuilder AddQuery(string wireName, GoogleFieldMask? value)
    {
        if (value is { IsEmpty: true })
        {
            throw new ArgumentException(
                $"The '{wireName}' field mask names no fields. An empty mask has no defined meaning "
                + "on the wire, and sending nothing instead would mean \"replace the fields present "
                + "in the body\", which is a different request. Omit the mask for that, or name the "
                + "fields to write.",
                nameof(value));
        }

        return AddQuery(wireName, value?.ToString());
    }

    /// <summary>Builds the relative URI, escaped according to the Google path template rules.</summary>
    public string Build()
    {
        var path = UriTemplate.Expand(PathTemplate, _pathParameters);

        if (_queryParameters.Count == 0)
        {
            return path;
        }

        var builder = new StringBuilder(path.Length + (_queryParameters.Count * 16));
        builder.Append(path);
        var separator = '?';

        // Query parameter order follows the order they were added, which the generator emits in
        // a fixed order, so the same request always produces the same URL.
        foreach (var (name, value) in _queryParameters)
        {
            builder.Append(separator)
                .Append(Uri.EscapeDataString(name))
                .Append('=')
                .Append(Uri.EscapeDataString(value));

            separator = '&';
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => Build();
}
