using System.Diagnostics;

namespace Kkdev92.HealthData.Diagnostics;

/// <summary>
/// Tag names emitted on Google Health activities.
/// </summary>
/// <remarks>
/// The list is deliberately short. Anything that could identify a person or reveal what was
/// measured is excluded.
/// </remarks>
public static class HealthDataActivityTags
{
    /// <summary>The Discovery operation id, for example <c>health.users.getProfile</c>.</summary>
    public const string OperationId = "googlehealth.operation_id";

    /// <summary>The Google Health API version.</summary>
    public const string ApiVersion = "googlehealth.api_version";

    /// <summary>The HTTP method.</summary>
    public const string HttpRequestMethod = "http.request.method";

    /// <summary>The service host.</summary>
    public const string ServerAddress = "server.address";

    /// <summary>The HTTP status code, once a response has been received.</summary>
    public const string HttpResponseStatusCode = "http.response.status_code";

    /// <summary>The failure reason, using the OpenTelemetry convention.</summary>
    public const string ErrorType = "error.type";

    /// <summary>The zero-based retry attempt number.</summary>
    public const string RetryAttempt = "retry.attempt";
}

/// <summary>
/// The <see cref="ActivitySource"/> this SDK emits on.
/// </summary>
/// <remarks>
/// <para>
/// One activity per logical operation. It is a parent of the built-in
/// <c>System.Net.Http</c> activity and outlives it, because it also covers reading and
/// deserializing the response body.
/// </para>
/// <para>
/// <strong>The request URL is never recorded.</strong> A Google Health resource name embeds both
/// the user and the data type, as in
/// <c>users/1234/dataTypes/heart-rate/dataPoints/abc</c>. Recording it would put a user
/// identifier and a health category into every trace.
/// </para>
/// <para>
/// Consumers should be aware that .NET's built-in HTTP client instrumentation does record
/// <c>url.full</c>, and that its redaction covers only the query string, not the path. Anyone
/// exporting traces from an application that uses this SDK should redact or drop that attribute.
/// This is called out in SECURITY.md.
/// </para>
/// </remarks>
public static class HealthDataDiagnostics
{
    /// <summary>The activity source name.</summary>
    public const string SourceName = HealthDataApiMetadata.ActivitySourceName;

    /// <summary>The shared activity source.</summary>
    public static ActivitySource Source { get; } = new(SourceName, ThisAssemblyVersion);

    private static string ThisAssemblyVersion =>
        typeof(HealthDataDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Starts an activity for an operation, or returns <see langword="null"/> when nothing is
    /// listening.
    /// </summary>
    /// <remarks>
    /// Returning null when unsampled is the point: no tags are computed and no allocation is made
    /// on the hot path.
    /// </remarks>
    public static Activity? StartOperation(Http.HealthDataOperationDescriptor descriptor, Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(baseAddress);

        var activity = Source.StartActivity(descriptor.Id, ActivityKind.Client);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag(HealthDataActivityTags.OperationId, descriptor.Id);
        activity.SetTag(HealthDataActivityTags.ApiVersion, descriptor.ApiVersion);
        activity.SetTag(HealthDataActivityTags.HttpRequestMethod, descriptor.Method.Method);
        activity.SetTag(HealthDataActivityTags.ServerAddress, baseAddress.Host);

        return activity;
    }

    /// <summary>Records the outcome of an operation.</summary>
    public static void RecordResponse(Activity? activity, System.Net.HttpStatusCode statusCode)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(HealthDataActivityTags.HttpResponseStatusCode, (int)statusCode);

        if ((int)statusCode >= 400)
        {
            // The status code is the error type, per OpenTelemetry convention. The service's
            // message is deliberately not recorded.
            activity.SetTag(HealthDataActivityTags.ErrorType, ((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            activity.SetStatus(ActivityStatusCode.Error);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    /// <summary>Records a failure that produced no response.</summary>
    public static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(exception);

        // The type only. An exception message can carry payload fragments.
        activity.SetTag(HealthDataActivityTags.ErrorType, exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error);
    }
}
