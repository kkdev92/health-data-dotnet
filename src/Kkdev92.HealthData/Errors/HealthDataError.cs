using System.Text.Json;

namespace Kkdev92.HealthData;

/// <summary>
/// A structured error returned by the Google Health API.
/// </summary>
/// <remarks>
/// <para>
/// Google's error envelope is specified by AIP-193:
/// </para>
/// <code>
/// {
///   "error": {
///     "code": 403,
///     "message": "...",
///     "status": "PERMISSION_DENIED",
///     "details": [ { "@type": "type.googleapis.com/google.rpc.ErrorInfo", ... } ]
///   }
/// }
/// </code>
/// <para>
/// Note that <c>code</c> carries the HTTP status, not the gRPC code, and that <c>status</c> is
/// the canonical code name. The API-specific reason such as <c>MISSING_OAUTH_SCOPE</c> lives in
/// an <c>ErrorInfo</c> detail, not in <c>status</c>.
/// </para>
/// <para>
/// <see cref="Message"/> is captured but deliberately never surfaced in an exception message: it
/// can quote request content, and error text tends to end up in logs.
/// </para>
/// </remarks>
public sealed class HealthDataError
{
    /// <summary>The HTTP status code the service reported inside the body.</summary>
    public int Code { get; init; }

    /// <summary>The human-readable message. Not included in exception messages.</summary>
    public string? Message { get; init; }

    /// <summary>The canonical status code name, for example <c>PERMISSION_DENIED</c>.</summary>
    public string? Status { get; init; }

    /// <summary>The structured details, in the order the service returned them.</summary>
    public IReadOnlyList<HealthDataErrorDetail> Details { get; init; } = [];

    /// <summary>
    /// The API-specific reason, for example <c>MISSING_OAUTH_SCOPE</c>.
    /// </summary>
    /// <remarks>
    /// Taken from the first <c>google.rpc.ErrorInfo</c> detail. Falls back to
    /// <see cref="Status"/> when the service sends no <c>ErrorInfo</c>, so that callers always
    /// have something machine-readable to branch on.
    /// </remarks>
    public string? Reason
        => Details.FirstOrDefault(d => d.IsErrorInfo)?.Reason ?? Status;

    /// <summary>The service that produced the error, for example <c>health.googleapis.com</c>.</summary>
    public string? Domain => Details.FirstOrDefault(d => d.IsErrorInfo)?.Domain;
}

/// <summary>
/// One entry of <c>error.details</c>.
/// </summary>
/// <remarks>
/// The raw JSON is retained so that a detail type this SDK does not model is still available to
/// the caller rather than discarded.
/// </remarks>
public sealed class HealthDataErrorDetail
{
    /// <summary>The <c>google.rpc.ErrorInfo</c> type URL.</summary>
    public const string ErrorInfoType = "type.googleapis.com/google.rpc.ErrorInfo";

    /// <summary>The <c>google.rpc.RetryInfo</c> type URL.</summary>
    public const string RetryInfoType = "type.googleapis.com/google.rpc.RetryInfo";

    /// <summary>The <c>google.rpc.LocalizedMessage</c> type URL.</summary>
    public const string LocalizedMessageType = "type.googleapis.com/google.rpc.LocalizedMessage";

    /// <summary>The <c>google.rpc.Help</c> type URL.</summary>
    public const string HelpType = "type.googleapis.com/google.rpc.Help";

    /// <summary>The <c>google.rpc.BadRequest</c> type URL.</summary>
    public const string BadRequestType = "type.googleapis.com/google.rpc.BadRequest";

    /// <summary>The <c>@type</c> URL of this detail.</summary>
    public string? Type { get; init; }

    /// <summary>The API-specific reason, when this detail is an <c>ErrorInfo</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>The producing service, when this detail is an <c>ErrorInfo</c>.</summary>
    public string? Domain { get; init; }

    /// <summary>The retry delay, when this detail is a <c>RetryInfo</c>.</summary>
    public GoogleDuration? RetryDelay { get; init; }

    /// <summary>The unmodified detail payload.</summary>
    public JsonElement Raw { get; init; }

    /// <summary>True when this detail carries an API-specific reason.</summary>
    public bool IsErrorInfo => string.Equals(Type, ErrorInfoType, StringComparison.Ordinal);

    /// <summary>True when this detail carries a server-suggested retry delay.</summary>
    public bool IsRetryInfo => string.Equals(Type, RetryInfoType, StringComparison.Ordinal);
}
