namespace Kkdev92.HealthData.Authentication;

/// <summary>
/// An access token and what is known about its lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The token value is deliberately not exposed through <see cref="ToString"/>. Tokens end up in
/// logs by accident far more often than on purpose.
/// </para>
/// <para>
/// This SDK never persists a token. Storage, encryption and key management belong to the
/// consuming application.
/// </para>
/// </remarks>
public sealed class HealthDataAccessToken
{
    /// <summary>Creates a token.</summary>
    /// <param name="value">The bearer token value.</param>
    /// <param name="expiresAtUtc">When the token stops being valid, if known.</param>
    /// <param name="grantedScopes">The scopes the token was actually granted, if known.</param>
    public HealthDataAccessToken(
        string value,
        DateTimeOffset? expiresAtUtc = null,
        IReadOnlyList<string>? grantedScopes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value;
        ExpiresAtUtc = expiresAtUtc;
        GrantedScopes = grantedScopes ?? [];
    }

    /// <summary>The bearer token value.</summary>
    public string Value { get; }

    /// <summary>When the token expires, if the issuer said.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>
    /// The scopes actually granted.
    /// </summary>
    /// <remarks>
    /// Worth checking: a user can decline individual scopes on the consent screen, so what was
    /// requested and what was granted are not the same thing.
    /// </remarks>
    public IReadOnlyList<string> GrantedScopes { get; }

    /// <summary>Whether the token is expired at the given time, allowing for clock skew.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="skew">How early to treat the token as expired. Defaults to one minute.</param>
    public bool IsExpired(DateTimeOffset now, TimeSpan? skew = null)
        => ExpiresAtUtc is { } expiry && now >= expiry - (skew ?? TimeSpan.FromMinutes(1));

    /// <summary>Returns a description that never contains the token value.</summary>
    public override string ToString()
        => ExpiresAtUtc is { } expiry
            ? $"HealthDataAccessToken(expires {expiry:O})"
            : "HealthDataAccessToken(no expiry reported)";
}
