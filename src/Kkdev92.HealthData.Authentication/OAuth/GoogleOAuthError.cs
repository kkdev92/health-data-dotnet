using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Kkdev92.HealthData.Authentication.OAuth;

/// <summary>
/// The token endpoint's error response, as RFC 6749 section 5.2 defines it.
/// </summary>
/// <remarks>
/// <para>
/// This is surfaced, where <see cref="HealthDataApiException"/> deliberately withholds the Health
/// API's own error text. The two are not the same kind of thing. A Health API message quotes user
/// ids and data types, so it belongs behind a property rather than in an exception message that
/// will end up in a log. A token endpoint response contains no health data at all — it describes
/// the client's own configuration, and RFC 6749 says in as many words that
/// <see cref="ErrorDescription"/> exists "to assist the client developer in understanding the
/// error that occurred". Hiding it would work against the only reason it is sent.
/// </para>
/// <para>
/// <see cref="ErrorDescription"/> and <see cref="ErrorUri"/> are exactly what the server sent,
/// unfiltered. They are not safe to log without looking at them: RFC 6749 lets a server put
/// anything in those fields, and one that echoes a submitted value back would be handing you an
/// authorization code or a client secret. <see cref="ToString"/> carries neither, so an instance
/// reaching a logger by accident is safe; reading the properties is a deliberate act.
/// </para>
/// </remarks>
public sealed class GoogleOAuthError
{
    /// <summary>
    /// The error code, for example <c>invalid_grant</c>.
    /// </summary>
    /// <remarks>
    /// RFC 6749 defines <c>invalid_request</c>, <c>invalid_client</c>, <c>invalid_grant</c>,
    /// <c>unauthorized_client</c>, <c>unsupported_grant_type</c> and <c>invalid_scope</c>. It is
    /// a string rather than an enum because an authorization server may send another.
    /// </remarks>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Human-readable detail, present at the server's discretion.</summary>
    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }

    /// <summary>A page describing the error, present at the server's discretion.</summary>
    [JsonPropertyName("error_uri")]
    public string? ErrorUri { get; init; }

    /// <summary>
    /// Google's refinement of the error, when it sends one.
    /// </summary>
    /// <remarks>
    /// Not in RFC 6749; Google documents it as the way to tell a revoked grant apart from a
    /// session-control policy having ended the session, which arrives as <c>invalid_grant</c> with
    /// <c>"error_subtype": "invalid_rapt"</c>. The two want different responses — the first needs
    /// consent again, the second needs the user to reauthenticate — so the distinction is worth
    /// having. Verified against Google's OAuth 2.0 documentation on 2026-08-12.
    /// </remarks>
    [JsonPropertyName("error_subtype")]
    public string? ErrorSubtype { get; init; }

    /// <summary>True when the response carried nothing usable.</summary>
    public bool IsEmpty
        => Error is null && ErrorDescription is null && ErrorUri is null && ErrorSubtype is null;

    /// <summary>
    /// The error code, and nothing the server wrote in prose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a logging framework calls when an instance of this type is handed to it, which
    /// is the accident worth designing for. So it carries only <see cref="KnownCode"/> — a value
    /// from a fixed list — and never <see cref="ErrorDescription"/> or <see cref="ErrorUri"/>.
    /// </para>
    /// <para>
    /// It used to filter those two to a character set and truncate them. That stops a newline
    /// forging a log line and stops a server filling a log, and it does nothing at all about the
    /// case that matters: a server, or a proxy in front of one, echoing a submitted value back.
    /// An authorization code and a client secret are both perfectly ordinary text. Read
    /// <see cref="ErrorDescription"/> directly when you have somewhere safe to put it.
    /// </para>
    /// </remarks>
    public override string ToString() => KnownCode(Error) ?? "no error details";

    /// <summary>
    /// The error code when it is one the specifications define, and null otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An allowlist rather than a shape. A shape was the previous answer — 64 characters of ASCII
    /// letters, digits, underscore, hyphen and dot — and a Google client secret
    /// (<c>GOCSPX-…</c>), an authorization code and a base64url bearer token all fit inside it. A
    /// server that reflects a submitted value into <c>error</c> would have had it printed for the
    /// same reason one that reflects into <c>error_description</c> would.
    /// </para>
    /// <para>
    /// The cost is that a code Google adds later will not appear in an exception message until
    /// this list grows. It is still on <see cref="Error"/>, which is where code that branches on it
    /// should be reading anyway.
    /// </para>
    /// </remarks>
    internal static string? KnownCode(string? code)
        => code is not null && Defined.Contains(code) ? code : null;

    /// <summary>
    /// Every error code RFC 6749 and RFC 8628 define, for both endpoints.
    /// </summary>
    /// <remarks>
    /// Taken from the RFC texts on 2026-08-12: RFC 6749 sections 4.1.2.1, 4.2.2.1 and 5.2, and
    /// RFC 8628 section 3.5 for the device flow.
    /// </remarks>
    private static readonly FrozenSet<string> Defined = new[]
    {
        // RFC 6749, the token endpoint (5.2).
        "invalid_request",
        "invalid_client",
        "invalid_grant",
        "unauthorized_client",
        "unsupported_grant_type",
        "invalid_scope",

        // RFC 6749, the authorization endpoint (4.1.2.1 and 4.2.2.1).
        "access_denied",
        "unsupported_response_type",
        "server_error",
        "temporarily_unavailable",

        // RFC 8628, the device flow (3.5).
        "authorization_pending",
        "slow_down",
        "expired_token",
    }.ToFrozenSet(StringComparer.Ordinal);
}
