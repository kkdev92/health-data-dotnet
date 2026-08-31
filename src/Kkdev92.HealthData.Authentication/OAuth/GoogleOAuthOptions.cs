using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Authentication.OAuth;

/// <summary>
/// Configuration for Google's OAuth 2.0 endpoints.
/// </summary>
/// <remarks>
/// Endpoint URLs verified against the Google Health API setup guide on 2026-08-10.
/// </remarks>
public sealed class GoogleOAuthOptions
{
    /// <summary>
    /// Google's authorization endpoint.
    /// </summary>
    /// <remarks>
    /// Must be HTTPS, or loopback. The user is sent here, and over plaintext the page asking them
    /// to approve access is one anybody on the path can replace.
    /// </remarks>
    public Uri AuthorizationEndpoint
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            RequireSecure(value, nameof(AuthorizationEndpoint));
            field = value;
        }
    } = new("https://accounts.google.com/o/oauth2/v2/auth");

    /// <summary>
    /// Google's token endpoint.
    /// </summary>
    /// <remarks>
    /// Must be HTTPS, or loopback. The authorization code, the refresh token and the client secret
    /// are all sent here, so a plaintext endpoint would put them on the wire in the clear — and an
    /// override exists for test servers, which is exactly the setting most likely to be left in
    /// place by accident.
    /// </remarks>
    public Uri TokenEndpoint
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            RequireSecure(value, nameof(TokenEndpoint));
            field = value;
        }
    } = new("https://oauth2.googleapis.com/token");

    /// <summary>
    /// Whether endpoints outside Google and loopback are permitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default. The token endpoint receives the authorization code, the refresh token and,
    /// for a confidential client, the client secret. Requiring HTTPS bounds who can read those in
    /// transit but not who receives them, so a wrong endpoint sends Google credentials to whoever
    /// owns it — and answers plausibly enough that nothing looks broken.
    /// </para>
    /// <para>
    /// Turn it on for an OAuth emulator or a corporate gateway that terminates the flow. Loopback
    /// needs no flag: the credentials have not left the machine.
    /// </para>
    /// </remarks>
    public bool AllowCustomCredentialEndpoints { get; init; }

    /// <summary>
    /// Rejects endpoints that Google credentials should not be sent to.
    /// </summary>
    /// <remarks>
    /// Checked here, when the client is built, rather than in each property setter. A setter
    /// cannot see <see cref="AllowCustomCredentialEndpoints"/> reliably: in an object initializer
    /// the members run in written order, so the same configuration would be accepted or rejected
    /// depending on which line came first.
    /// </remarks>
    internal static GoogleOAuthOptions RequireGoogleOrLocalEndpoints(GoogleOAuthOptions options)
    {
        if (options.AllowCustomCredentialEndpoints)
        {
            return options;
        }

        Require(options.AuthorizationEndpoint, "https://accounts.google.com", nameof(AuthorizationEndpoint));
        Require(options.TokenEndpoint, "https://oauth2.googleapis.com", nameof(TokenEndpoint));

        return options;

        static void Require(Uri endpoint, string expected, string name)
        {
            if (endpoint.IsLoopback || SecureUri.IsSameOrigin(endpoint, new Uri(expected)))
            {
                return;
            }

            throw new ArgumentException(
                $"'{SecureUri.Describe(endpoint)}' is not {expected} or a loopback address, and Google "
                + "credentials are sent to it. Set AllowCustomCredentialEndpoints to true if this "
                + "endpoint is deliberate, such as an emulator or a gateway you operate.",
                name);
        }
    }

    /// <summary>Rejects an endpoint that credentials should not travel to.</summary>
    private static void RequireSecure(Uri endpoint, string name)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The endpoint must be absolute.", name);
        }

        if (SecureUri.IsHttpsOrLoopback(endpoint))
        {
            return;
        }

        throw new ArgumentException(
            $"'{SecureUri.Describe(endpoint)}' is not HTTPS. Credentials are sent to this endpoint; use "
            + "HTTPS, or a loopback address for a local test server.",
            name);
    }

    /// <summary>The OAuth client id.</summary>
    /// <remarks>
    /// Rejected when blank. <c>required</c> guarantees that it was assigned, not that it means
    /// anything, and an empty one produces a perfectly well-formed authorization URL that Google
    /// answers with a 400 naming no cause. Failing here moves that to startup, next to the
    /// configuration that is actually wrong.
    /// </remarks>
    public required string ClientId
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    /// <summary>
    /// The OAuth client secret, for confidential clients only.
    /// </summary>
    /// <remarks>
    /// A desktop, mobile or single-page app cannot keep this secret. Leave it null and use PKCE
    /// instead. This SDK never persists it.
    /// </remarks>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// The redirect URI registered for this client.
    /// </summary>
    /// <remarks>
    /// Sent as <see cref="Uri.OriginalString"/>, because Google compares it to the registration
    /// character for character and <see cref="Uri.ToString"/> normalises. Give it the same string
    /// that was registered, absolute, and it will arrive unchanged.
    /// </remarks>
    public required Uri RedirectUri
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!value.IsAbsoluteUri)
            {
                throw new ArgumentException(
                    "The redirect URI must be absolute, since Google matches it against the registered value.",
                    nameof(value));
            }

            field = value;
        }
    }
}
