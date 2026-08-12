using System.Net;

namespace Kkdev92.HealthData;

/// <summary>
/// Thrown when the Google Health API returns an error status.
/// </summary>
/// <remarks>
/// <para>
/// The message contains only the operation id, the HTTP status, and the machine-readable reason.
/// It never contains the service's human-readable message, the request or response body, an
/// access token, or any health value. Google's own messages quote user ids and data types, and
/// an exception message is very likely to end up in a log. The full envelope remains available
/// on <see cref="Error"/> for callers that have somewhere safe to put it.
/// </para>
/// <para>
/// Reasons are strings, not an enum: the service may return one this SDK has never heard of.
/// Known values are listed in <see cref="HealthDataErrorReasons"/>.
/// </para>
/// </remarks>
public sealed class HealthDataApiException : HttpRequestException
{
    /// <summary>Creates an exception for a failed operation.</summary>
    public HealthDataApiException(
        HttpStatusCode statusCode,
        string? operationId = null,
        HealthDataError? error = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(BuildMessage(statusCode, operationId, error?.Reason), innerException, statusCode)
    {
        OperationId = operationId;
        Error = error;
        RetryAfter = retryAfter;
    }

    /// <summary>The Discovery operation id that failed, when known.</summary>
    public string? OperationId { get; }

    /// <summary>The parsed error envelope, when the service returned one.</summary>
    public HealthDataError? Error { get; }

    /// <summary>
    /// The machine-readable reason, for example <c>MISSING_OAUTH_SCOPE</c>.
    /// </summary>
    public string? Reason => Error?.Reason;

    /// <summary>
    /// How long to wait before retrying, when the service said.
    /// </summary>
    /// <remarks>
    /// Taken from the <c>Retry-After</c> header, or from a <c>google.rpc.RetryInfo</c> detail.
    /// The Google Health rate-limit documentation describes 429 responses but does not state that
    /// a <c>Retry-After</c> header is sent, so this is frequently <see langword="null"/> and a
    /// caller must have its own backoff rather than depending on it.
    /// </remarks>
    public TimeSpan? RetryAfter { get; }

    /// <summary>True when the failure was a rate limit.</summary>
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests;

    private static string BuildMessage(HttpStatusCode statusCode, string? operationId, string? reason)
    {
        var status = $"{(int)statusCode} {statusCode}";
        var safe = Sanitize(reason);

        return (operationId, safe) switch
        {
            (null, null) => $"The Google Health API returned {status}.",
            (not null, null) => $"'{operationId}' failed with {status}.",
            (null, not null) => $"The Google Health API returned {status} ({safe}).",
            _ => $"'{operationId}' failed with {status} ({safe}).",
        };
    }

    /// <summary>
    /// Keeps a reason only when it looks like the machine-readable code it is meant to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason arrives off the wire, and this message is the one thing about an error that the
    /// SDK promises is safe to log. Google sends SCREAMING_SNAKE_CASE, but the field is a string
    /// and the sender is not always Google: a proxy, a test double or a service that reflects
    /// input back can put anything there — a newline that forges a second log line, a value
    /// copied out of the request, a paragraph.
    /// </para>
    /// <para>
    /// So the message carries the reason only when it is one this contract documents, or one of
    /// the canonical status codes it falls back to. Not when it merely looks like one: a shape
    /// test — upper case, digits and underscores, and short — was the first answer here and it
    /// admits <c>CLIENT_SECRET_ABC</c>, <c>ABC123_SECRET</c> and a bare numeric id just as readily
    /// as <c>MISSING_OAUTH_SCOPE</c>. A secret is shaped like an identifier; only a list of what
    /// the service actually says tells them apart.
    /// </para>
    /// <para>
    /// Anything else is left out of the message and stays on <see cref="Error"/> for a caller that
    /// has somewhere safe to put it. <see cref="Reason"/> itself is unfiltered on purpose — it is
    /// for branching on, not for logging.
    /// </para>
    /// </remarks>
    private static string? Sanitize(string? reason)
        => HealthDataErrorReasons.IsDocumented(reason) || IsCanonicalStatus(reason) ? reason : null;

    /// <summary>
    /// Whether the value is one of the canonical status codes <c>Reason</c> falls back to.
    /// </summary>
    /// <remarks>
    /// <see cref="HealthDataError.Reason"/> prefers <c>ErrorInfo.reason</c> and falls back to the
    /// error's <c>status</c>, which is a <c>google.rpc.Code</c> name. Those are a fixed list from
    /// Google's API design guide rather than from this API's Discovery document, so they are here
    /// rather than generated.
    /// </remarks>
    private static bool IsCanonicalStatus(string? status) => status is
        "CANCELLED" or "UNKNOWN" or "INVALID_ARGUMENT" or "DEADLINE_EXCEEDED" or
        "NOT_FOUND" or "ALREADY_EXISTS" or "PERMISSION_DENIED" or "UNAUTHENTICATED" or
        "RESOURCE_EXHAUSTED" or "FAILED_PRECONDITION" or "ABORTED" or "OUT_OF_RANGE" or
        "UNIMPLEMENTED" or "INTERNAL" or "UNAVAILABLE" or "DATA_LOSS";
}
