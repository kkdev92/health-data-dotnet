using System.Security.Cryptography;
using System.Text.Json;

namespace Kkdev92.HealthData.Webhooks.Tink;

/// <summary>
/// One ECDSA public key from a Tink keyset.
/// </summary>
internal sealed class TinkEcdsaPublicKey
{
    public required uint KeyId { get; init; }

    public required ECParameters Parameters { get; init; }

    public required HashAlgorithmName HashAlgorithm { get; init; }

    /// <summary>The 5-byte prefix a signature from this key carries.</summary>
    public required byte[] OutputPrefix { get; init; }
}

/// <summary>
/// Parses the Tink keyset Google publishes for webhook signatures.
/// </summary>
/// <remarks>
/// <para>
/// No Tink dependency. The keyset is JSON, and the key material inside it is a serialized
/// <c>google.crypto.tink.EcdsaPublicKey</c> protobuf with only four fields, three of which are
/// needed. Reading those directly is a few dozen lines and keeps the package at zero third-party
/// runtime dependencies (ADR-0002).
/// </para>
/// <para>
/// Verified against the live keyset on 2026-08-10: five enabled keys, all
/// <c>outputPrefixType: TINK</c>, ECDSA P-256, SHA-256, DER signature encoding.
/// </para>
/// <para>
/// Every field number and enum value below was checked against Tink's own
/// <c>proto/common.proto</c>, <c>proto/ecdsa.proto</c> and <c>proto/tink.proto</c> on 2026-08-11.
/// Worth doing rather than recalling: a summary consulted the same day had <c>SHA256</c> and
/// <c>SHA384</c> the other way round, which would have meant verifying signatures under the wrong
/// digest and rejecting every genuine notification.
/// </para>
/// </remarks>
internal static class TinkKeysetParser
{
    private const string EcdsaPublicKeyTypeUrl = "type.googleapis.com/google.crypto.tink.EcdsaPublicKey";

    // google.crypto.tink.HashType, from proto/common.proto. The numbering is neither alphabetical
    // nor ordered by strength: SHA1 = 1, SHA384 = 2, SHA256 = 3, SHA512 = 4, SHA224 = 5. Reading
    // it as "3 must be SHA384" is an easy and silent mistake, and one that secondary sources
    // really do make.
    private const int HashSha256 = 3;

    /// <summary>Parses every enabled ECDSA verification key in a keyset.</summary>
    /// <exception cref="InvalidOperationException">The keyset is malformed or uses an unsupported key.</exception>
    public static IReadOnlyList<TinkEcdsaPublicKey> Parse(ReadOnlySpan<byte> utf8Json)
    {
        // Read straight from the span, so the keyset is not copied a second time on the way in.
        var reader = new Utf8JsonReader(utf8Json);
        using var document = JsonDocument.ParseValue(ref reader);

        if (!document.RootElement.TryGetProperty("key", out var keys) || keys.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The Tink keyset contains no 'key' array.");
        }

        var result = new List<TinkEcdsaPublicKey>();

        foreach (var key in keys.EnumerateArray())
        {
            // Only an explicit ENABLED is trusted. A missing status used to mean the key was kept,
            // which is fail-open in the one place that must not be: this key decides whether a
            // webhook payload is authentic. Disabled, destroyed, absent and unrecognised all mean
            // the same thing here — do not verify with it.
            if (!key.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || !string.Equals(status.GetString(), "ENABLED", StringComparison.Ordinal))
            {
                continue;
            }

            var keyData = key.GetProperty("keyData");

            if (!string.Equals(keyData.GetProperty("typeUrl").GetString(), EcdsaPublicKeyTypeUrl, StringComparison.Ordinal))
            {
                // A keyset may hold other primitives; they are simply not ours to use.
                continue;
            }

            var outputPrefixType = key.GetProperty("outputPrefixType").GetString();

            if (!string.Equals(outputPrefixType, "TINK", StringComparison.Ordinal))
            {
                // RAW carries no prefix and LEGACY appends a 0x00 byte to the signed message.
                // Google publishes only TINK keys; anything else must fail rather than be guessed at.
                throw new InvalidOperationException(
                    $"Unsupported Tink output prefix type '{outputPrefixType}'. Only TINK is supported.");
            }

            var keyId = key.GetProperty("keyId").GetUInt32();
            var material = Convert.FromBase64String(keyData.GetProperty("value").GetString()!);

            result.Add(ParseEcdsaPublicKey(keyId, material));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("The Tink keyset contains no enabled ECDSA verification key.");
        }

        return result;
    }

    private static TinkEcdsaPublicKey ParseEcdsaPublicKey(uint keyId, byte[] material)
    {
        var (hashType, curve, encoding, x, y) = ReadEcdsaFields(material);

        if (x is null || y is null)
        {
            throw new InvalidOperationException("The Tink ECDSA key is missing its public point.");
        }

        // google.crypto.tink.EllipticCurveType: NIST_P256 = 2.
        if (curve != 2)
        {
            throw new InvalidOperationException($"Unsupported Tink curve '{curve}'. Only NIST_P256 is supported.");
        }

        // google.crypto.tink.EcdsaSignatureEncoding: DER = 2.
        if (encoding != 2)
        {
            throw new InvalidOperationException($"Unsupported Tink signature encoding '{encoding}'. Only DER is supported.");
        }

        if (hashType != HashSha256)
        {
            throw new InvalidOperationException($"Unsupported Tink hash type '{hashType}'. Only SHA256 is supported.");
        }

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = NormalizeCoordinate(x),
                Y = NormalizeCoordinate(y),
            },
        };

        // Imported here rather than at the first verification. The coordinates are the right length
        // by now, which is not the same as being a point on the curve, and the failure for one that
        // is not surfaces from ECDsa.Create — inside the verifier, after the provider has already
        // replaced a perfectly good cached keyset with this one. Failing during the parse means a
        // malformed keyset is a fetch that produced nothing, and the old keys keep working.
        using (ECDsa.Create(parameters))
        {
        }

        return new TinkEcdsaPublicKey
        {
            KeyId = keyId,
            HashAlgorithm = HashAlgorithmName.SHA256,
            Parameters = parameters,
            OutputPrefix = BuildOutputPrefix(keyId),
        };
    }

    /// <summary>
    /// Walks the <c>EcdsaPublicKey</c> message and picks out the four fields this needs.
    /// </summary>
    /// <remarks>
    /// Separated from the checking below because they fail differently: a field this does not
    /// recognise is skipped, as protobuf requires of any reader, while a field it does recognise
    /// and disagrees with stops the parse. Reading the two as one method made it easy to mistake
    /// which of those a given branch was doing.
    /// </remarks>
    private static (int? HashType, int? Curve, int? Encoding, byte[]? X, byte[]? Y) ReadEcdsaFields(byte[] material)
    {
        int? hashType = null;
        int? curve = null;
        int? encoding = null;
        byte[]? x = null;
        byte[]? y = null;

        foreach (var (field, value) in ProtobufReader.Read(material))
        {
            switch (field)
            {
                case 2 when value.Bytes is { } paramBytes:
                    (hashType, curve, encoding) = ReadEcdsaParams(paramBytes);
                    break;

                case 3: x = value.Bytes; break;
                case 4: y = value.Bytes; break;
                default: break;
            }
        }

        return (hashType, curve, encoding, x, y);
    }

    /// <summary>Reads the nested <c>EcdsaParams</c> message: hash, curve and signature encoding.</summary>
    private static (int? HashType, int? Curve, int? Encoding) ReadEcdsaParams(byte[] paramBytes)
    {
        int? hashType = null;
        int? curve = null;
        int? encoding = null;

        foreach (var (field, value) in ProtobufReader.Read(paramBytes))
        {
            switch (field)
            {
                case 1: hashType = (int)value.Varint; break;
                case 2: curve = (int)value.Varint; break;
                case 3: encoding = (int)value.Varint; break;
                default: break;
            }
        }

        return (hashType, curve, encoding);
    }

    /// <summary>
    /// Trims or pads a coordinate to the 32 bytes <see cref="ECPoint"/> expects.
    /// </summary>
    /// <remarks>
    /// Tink stores coordinates as protobuf <c>bytes</c> holding a big-endian integer, so a value
    /// whose high bit is set gets a leading <c>0x00</c> sign byte and arrives as 33 bytes.
    /// Passing that straight to <see cref="ECParameters"/> produces an invalid key. Every key in
    /// the live keyset is 33 bytes today.
    /// </remarks>
    private static byte[] NormalizeCoordinate(byte[] value)
    {
        const int P256CoordinateLength = 32;

        if (value.Length == P256CoordinateLength)
        {
            return value;
        }

        if (value.Length == P256CoordinateLength + 1 && value[0] == 0x00)
        {
            return value[1..];
        }

        if (value.Length < P256CoordinateLength)
        {
            // Left-pad a coordinate whose leading zero bytes were dropped.
            var padded = new byte[P256CoordinateLength];
            value.CopyTo(padded, P256CoordinateLength - value.Length);
            return padded;
        }

        throw new InvalidOperationException($"A P-256 coordinate cannot be {value.Length} bytes.");
    }

    /// <summary>
    /// Builds the 5-byte prefix Tink prepends to a signature: version <c>0x01</c> then the key id
    /// big-endian.
    /// </summary>
    private static byte[] BuildOutputPrefix(uint keyId)
    {
        var prefix = new byte[5];
        prefix[0] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1), keyId);
        return prefix;
    }
}
