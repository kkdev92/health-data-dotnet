namespace Kkdev92.HealthData.Authentication.OAuth;

/// <summary>
/// What to ask for when sending someone to Google's consent screen.
/// </summary>
/// <remarks>
/// <para>
/// A type rather than six parameters, two of which were <see langword="bool"/>. Read at a call
/// site, <c>CreateAuthorizationUrl(scopes, state, pkce, true, false)</c> says nothing about what
/// those two decide, and getting them the wrong way round is a consent screen that either does not
/// return a refresh token or re-prompts somebody who already agreed.
/// </para>
/// <para>
/// The defaults are the ones the Google Health setup guide describes for a server-side
/// application: ask for offline access, do not force the consent screen.
/// </para>
/// </remarks>
public sealed record GoogleAuthorizationUrlOptions
{
    /// <summary>The scopes to request.</summary>
    /// <remarks>
    /// <see cref="HealthDataScopes.ReadOnly"/> and its siblings are generated from the contract, so
    /// an application that only reads can ask for exactly that without matching on scope names.
    /// </remarks>
    public required IEnumerable<string> Scopes { get; init; }

    /// <summary>An opaque value echoed back, used to defend against CSRF.</summary>
    public string? State { get; init; }

    /// <summary>A PKCE challenge, recommended for any client that cannot keep a secret.</summary>
    public PkceCodeChallenge? Pkce { get; init; }

    /// <summary>
    /// Whether to request a refresh token.
    /// </summary>
    /// <remarks>
    /// Sends <c>access_type=offline</c>, which the Google Health setup guide names as the way to
    /// obtain one. Without it the grant lasts as long as the access token does.
    /// </remarks>
    public bool OfflineAccess { get; init; } = true;

    /// <summary>
    /// Whether to force the consent screen even if the person already approved.
    /// </summary>
    /// <remarks>
    /// Sends <c>prompt=consent</c>, which the setup guide names as the way to re-request after
    /// changing scopes. Off by default: showing it to somebody who has already agreed is a worse
    /// experience than not, and Google returns the refresh token on the first grant.
    /// </remarks>
    public bool ForceConsent { get; init; }

    /// <summary>An email address to preselect an account.</summary>
    public string? LoginHint { get; init; }
}
