using System.Net;

namespace Kkdev92.HealthData.Authentication.OAuth;

/// <summary>
/// Thrown when Google's token endpoint refuses an exchange or a refresh.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="HealthDataApiException"/> because it is a different failure: the
/// Health API rejecting a call says something about the user's data or grant, whereas this says
/// the application's own credentials or redirect URI are wrong. Both derive from
/// <see cref="HttpRequestException"/>, so a caller that does not care can catch that.
/// </para>
/// <para>
/// The message carries the status code, and the error code when it is one RFC 6749 or RFC 8628
/// defines. Nothing else — not <c>error_description</c>, and not an error code outside that list.
/// The reason is the same for both: those fields are whatever the server chose to send, a server
/// that reflects a submitted value back is a server that has put an authorization code or a client
/// secret in them, and a secret is shaped exactly like an identifier. An allowlist is the only
/// filter that tells them apart.
/// </para>
/// <para>
/// The fields RFC 6749 defines, plus Google's <c>error_subtype</c>, are on <see cref="Error"/> for
/// a caller that has somewhere safe to put them. A field none of those name is not kept: this
/// deserializes into a fixed contract rather than a bag, which is the same trade the rest of the
/// SDK makes.
/// </para>
/// <para>
/// The consequence worth knowing: the exception message is safe to paste into a bug report, and
/// <see cref="Error"/> is not. That is the same division <see cref="HealthDataApiException"/>
/// makes, for the same reason.
/// </para>
/// </remarks>
public sealed class GoogleOAuthException : HttpRequestException
{
    /// <summary>Creates an exception for a rejected token request.</summary>
    public GoogleOAuthException(
        HttpStatusCode statusCode,
        GoogleOAuthError? error = null,
        Exception? innerException = null)
        : base(BuildMessage(statusCode, error), innerException, statusCode)
    {
        Error = error;
    }

    /// <summary>The parsed RFC 6749 error response, when the server sent one.</summary>
    public GoogleOAuthError? Error { get; }

    /// <summary>
    /// The error code, for example <c>invalid_grant</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unfiltered on purpose: it is for branching on, not for logging. A server is free to send
    /// something other than the codes below, so compare it rather than printing it.
    /// </para>
    /// <para>
    /// <c>invalid_grant</c> is the one worth handling, and it means more than one thing. RFC 6749
    /// defines it as a grant that is invalid, expired or revoked, that does not match the redirect
    /// URI it was obtained with, or that was issued to another client. A withdrawn refresh token
    /// arrives this way, and so does a changed client id. Consent again is the remedy for the
    /// first and no help at all for the second, so it is worth checking the configuration before
    /// sending the user anywhere.
    /// </para>
    /// </remarks>
    public string? ErrorCode => Error?.Error;

    private static string BuildMessage(HttpStatusCode statusCode, GoogleOAuthError? error)
    {
        var status = $"{(int)statusCode} {statusCode}";

        // A code from the fixed list or nothing. Checking the shape instead — which is what this
        // did first — let GOCSPX-… and a base64url bearer token through, because a secret is
        // shaped exactly like an identifier.
        return GoogleOAuthError.KnownCode(error?.Error) is { } code
            ? $"Google's token endpoint returned {status} ({code})."
            : $"Google's token endpoint returned {status}.";
    }
}
