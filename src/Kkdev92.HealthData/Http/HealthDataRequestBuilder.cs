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

    /// <summary>Adds a field mask query parameter.</summary>
    public HealthDataRequestBuilder AddQuery(string wireName, GoogleFieldMask? value)
        => AddQuery(wireName, value is null || value.Value.IsEmpty ? null : value.Value.ToString());

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
