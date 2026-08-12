using System.Security.Cryptography;
using Kkdev92.HealthData.Webhooks.Tink;

namespace Kkdev92.HealthData.Webhooks;

/// <summary>Why a webhook signature was rejected.</summary>
public enum WebhookSignatureFailure
{
    /// <summary>The signature is valid.</summary>
    None,

    /// <summary>No signature header was present.</summary>
    Missing,

    /// <summary>The header was not valid base64, or was too short to contain a Tink prefix.</summary>
    Malformed,

    /// <summary>The signature names a key that is not in the keyset, even after a refresh.</summary>
    UnknownKey,

    /// <summary>The key is known but the signature does not verify against the payload.</summary>
    Invalid,
}

/// <summary>The outcome of verifying a webhook signature.</summary>
public sealed class WebhookSignatureResult
{
    private WebhookSignatureResult(bool isValid, WebhookSignatureFailure failure, uint? keyId)
    {
        IsValid = isValid;
        Failure = failure;
        KeyId = keyId;
    }

    /// <summary>Whether the payload is authentic.</summary>
    public bool IsValid { get; }

    /// <summary>Why verification failed.</summary>
    public WebhookSignatureFailure Failure { get; }

    /// <summary>The key id named by the signature, when it could be read.</summary>
    public uint? KeyId { get; }

    internal static WebhookSignatureResult Valid(uint keyId) => new(true, WebhookSignatureFailure.None, keyId);

    internal static WebhookSignatureResult Failed(WebhookSignatureFailure failure, uint? keyId = null)
        => new(false, failure, keyId);
}

/// <summary>
/// Verifies the signature Google puts on every webhook notification.
/// </summary>
/// <remarks>
/// <para>
/// The wire format, verified against Google's published keyset and Tink's own source on
/// 2026-08-10:
/// </para>
/// <code>
/// header     GOOGLE-HEALTH-API-SIGNATURE, base64
/// decoded    [0x01][keyId big-endian, 4 bytes][DER ECDSA signature]
/// algorithm  ECDSA P-256 with SHA-256
/// signed     the raw request body, exactly as received
/// </code>
/// <para>
/// The five-byte prefix is a key hint, not part of the signature. Tink appends a <c>0x00</c> byte
/// to the signed message only for the <c>LEGACY</c> output prefix type; Google publishes
/// <c>TINK</c> keys, so the signature covers the body unchanged.
/// </para>
/// <para>
/// Verification runs on the bytes as received. Deserializing first and re-serializing would
/// change whitespace and key order and the signature would never match, which is why the
/// receive-verify-parse order is mandatory.
/// </para>
/// </remarks>
public sealed class HealthDataWebhookSignatureVerifier(HealthDataWebhookKeyProvider keyProvider)
{
    /// <summary>The header Google sends the signature in.</summary>
    public const string SignatureHeaderName = "GOOGLE-HEALTH-API-SIGNATURE";

    private const int TinkPrefixLength = 5;

    /// <summary>
    /// The longest signature header worth decoding.
    /// </summary>
    /// <remarks>
    /// Five bytes of Tink prefix and a DER ECDSA P-256 signature come to about seventy-five bytes,
    /// which is a hundred or so in base64. This leaves room and still refuses a header sent to see
    /// how large a buffer it can make somebody allocate.
    /// </remarks>
    private const int MaximumSignatureHeaderLength = 512;

    private readonly HealthDataWebhookKeyProvider _keyProvider =
        keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));

    /// <summary>Verifies a signature against the raw request body.</summary>
    /// <param name="rawBody">The body exactly as received. Never a re-serialized form.</param>
    /// <param name="signatureHeader">The value of the signature header.</param>
    /// <param name="cancellationToken">Cancels a keyset refresh.</param>
    public async Task<WebhookSignatureResult> VerifyAsync(
        ReadOnlyMemory<byte> rawBody,
        string? signatureHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return WebhookSignatureResult.Failed(WebhookSignatureFailure.Missing);
        }

        // The header comes from whoever sent the request, and the buffer below is sized from it.
        // A P-256 signature with a five-byte Tink prefix is under a hundred bytes; anything past
        // this is not a signature that could verify, so it is refused before anything is allocated.
        if (signatureHeader.Length > MaximumSignatureHeaderLength)
        {
            return WebhookSignatureResult.Failed(WebhookSignatureFailure.Malformed);
        }

        if (!TryDecode(signatureHeader, out var keyId, out var derSignature))
        {
            return WebhookSignatureResult.Failed(WebhookSignatureFailure.Malformed);
        }

        var keys = await _keyProvider.GetKeysAsync(cancellationToken).ConfigureAwait(false);
        var key = Find(keys, keyId);

        if (key is null)
        {
            // Keys rotate every 30 days, so an unknown id is expected occasionally and is worth
            // one refresh before giving up.
            keys = await _keyProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
            key = Find(keys, keyId);
        }

        if (key is null)
        {
            // Fail closed. An unverifiable payload is not processed.
            return WebhookSignatureResult.Failed(WebhookSignatureFailure.UnknownKey, keyId);
        }

        using var ecdsa = ECDsa.Create(key.Parameters);

        var verified = ecdsa.VerifyData(
            rawBody.Span,
            derSignature,
            key.HashAlgorithm,
            DSASignatureFormat.Rfc3279DerSequence);

        return verified
            ? WebhookSignatureResult.Valid(keyId)
            : WebhookSignatureResult.Failed(WebhookSignatureFailure.Invalid, keyId);
    }

    private static TinkEcdsaPublicKey? Find(IReadOnlyList<TinkEcdsaPublicKey> keys, uint keyId)
        => keys.FirstOrDefault(k => k.KeyId == keyId);

    /// <summary>Splits the base64 header into the key id and the DER signature.</summary>
    private static bool TryDecode(string signatureHeader, out uint keyId, out byte[] derSignature)
    {
        keyId = 0;
        derSignature = [];

        Span<byte> buffer = new byte[((signatureHeader.Length * 3) / 4) + 4];

        if (!Convert.TryFromBase64String(signatureHeader.Trim(), buffer, out var written))
        {
            return false;
        }

        if (written <= TinkPrefixLength)
        {
            return false;
        }

        var decoded = buffer[..written];

        // Version byte. Tink writes 0x01 for the TINK output prefix.
        if (decoded[0] != 0x01)
        {
            return false;
        }

        keyId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(decoded[1..TinkPrefixLength]);
        derSignature = decoded[TinkPrefixLength..].ToArray();
        return true;
    }
}
