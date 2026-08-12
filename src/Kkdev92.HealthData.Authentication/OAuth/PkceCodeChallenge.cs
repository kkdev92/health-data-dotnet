using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Kkdev92.HealthData.Authentication.OAuth;

/// <summary>
/// A PKCE verifier and its S256 challenge (RFC 7636).
/// </summary>
/// <remarks>
/// <para>
/// The Google Health setup guide does not mention PKCE, so this is offered rather than imposed.
/// It is the right default for any client that cannot keep a secret (a desktop app, a mobile app,
/// a CLI), and Google's authorization endpoint accepts it.
/// </para>
/// <para>
/// Only S256 is produced. The <c>plain</c> method exists in RFC 7636 for constrained clients and
/// offers no protection worth having on .NET.
/// </para>
/// </remarks>
public sealed class PkceCodeChallenge
{
    /// <summary>The RFC 7636 unreserved character set for a code verifier.</summary>
    private const string VerifierAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    private PkceCodeChallenge(string codeVerifier, string codeChallenge)
    {
        CodeVerifier = codeVerifier;
        CodeChallenge = codeChallenge;
    }

    /// <summary>The secret held by the client and sent when redeeming the authorization code.</summary>
    public string CodeVerifier { get; }

    /// <summary>The value sent on the authorization request.</summary>
    public string CodeChallenge { get; }

    /// <summary>The challenge method, always <c>S256</c>.</summary>
    public static string CodeChallengeMethod => "S256";

    /// <summary>
    /// Creates a new verifier and challenge.
    /// </summary>
    /// <param name="verifierLength">
    /// Verifier length in characters. RFC 7636 requires 43 to 128; the default of 64 is
    /// comfortably inside that and gives well over 128 bits of entropy.
    /// </param>
    public static PkceCodeChallenge Create(int verifierLength = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(verifierLength, 43);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(verifierLength, 128);

        // RandomNumberGenerator, not Random: this value is a secret.
        var verifier = RandomNumberGenerator.GetString(VerifierAlphabet, verifierLength);

        return new PkceCodeChallenge(verifier, ComputeChallenge(verifier));
    }

    /// <summary>
    /// Rebuilds the pair from a verifier that was stored between the two requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authorization redirect and the callback are two separate requests, and a server that
    /// restarts between them — or that runs more than one instance — cannot keep the object from
    /// the first in memory for the second. Storing the verifier and reconstructing here is what
    /// makes the flow survive that; <see cref="Create(int)"/> alone confines it to one process.
    /// </para>
    /// <para>
    /// The verifier is a secret. Store it somewhere a session cookie or a database row is
    /// appropriate, scoped to the pending authorization, and delete it once redeemed.
    /// </para>
    /// </remarks>
    /// <param name="codeVerifier">The verifier produced by <see cref="Create(int)"/>.</param>
    public static PkceCodeChallenge FromVerifier(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        ArgumentOutOfRangeException.ThrowIfLessThan(codeVerifier.Length, 43);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(codeVerifier.Length, 128);

        if (codeVerifier.Any(c => !VerifierAlphabet.Contains(c, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A code verifier may contain only the RFC 7636 unreserved characters.",
                nameof(codeVerifier));
        }

        return new PkceCodeChallenge(codeVerifier, ComputeChallenge(codeVerifier));
    }

    /// <summary>Recomputes the S256 challenge for an existing verifier.</summary>
    public static string ComputeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));

        // base64url without padding, as RFC 7636 requires.
        return Base64Url.EncodeToString(hash);
    }

    /// <summary>Returns a description that never contains the verifier.</summary>
    public override string ToString() => $"PkceCodeChallenge(S256, challenge={CodeChallenge})";
}
