using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kkdev92.HealthData.TestSupport;

/// <summary>
/// Builds a Tink-shaped keyset and signatures so verification can be exercised end to end.
/// </summary>
/// <remarks>
/// Google's published keys can only verify Google's signatures, so a local key pair is the only
/// way to test the success path. The keyset JSON and the signature layout produced here are
/// byte-compatible with Google's, which is what makes the test meaningful.
/// </remarks>
public sealed class TinkTestKey : IDisposable
{
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TinkTestKey(uint keyId) => KeyId = keyId;

    public uint KeyId { get; }

    /// <summary>Renders this key as a Tink keyset JSON document.</summary>
    /// <param name="extraProtobufFields">
    /// Raw protobuf appended after the real fields, for proving that a reader skips what it does
    /// not know. Nothing checks these bytes; a caller supplies well-formed fields with numbers the
    /// key does not use.
    /// </param>
    /// <param name="paramsBytes">
    /// Replaces the serialized <c>EcdsaParams</c> message (field 2), for feeding the parser a params
    /// encoding it must reject while the public point stays genuine. Null keeps the real one.
    /// </param>
    public string ToKeysetJson(
        string status = "ENABLED",
        string outputPrefixType = "TINK",
        byte[]? extraProtobufFields = null,
        byte[]? paramsBytes = null)
        => ToKeysetJson([(this, status, outputPrefixType)], extraProtobufFields, paramsBytes);

    public static string ToKeysetJson(
        IReadOnlyList<(TinkTestKey Key, string Status, string Prefix)> keys,
        byte[]? extraProtobufFields = null,
        byte[]? paramsBytes = null)
    {
        var entries = keys.Select(k =>
            $$"""
              {"keyData":{"typeUrl":"type.googleapis.com/google.crypto.tink.EcdsaPublicKey",
              "value":"{{Convert.ToBase64String([.. k.Key.SerializeEcdsaPublicKey(paramsBytes), .. extraProtobufFields ?? []])}}",
              "keyMaterialType":"ASYMMETRIC_PUBLIC"},
              "status":"{{k.Status}}","keyId":{{k.Key.KeyId}},"outputPrefixType":"{{k.Prefix}}"}
              """);

        return $$"""{"primaryKeyId":{{keys[0].Key.KeyId}},"key":[{{string.Join(',', entries)}}]}""";
    }

    /// <summary>Signs a payload the way Tink does with a TINK output prefix.</summary>
    public string Sign(byte[] payload)
    {
        // DER, because the key declares DER encoding.
        var signature = _ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        // Prefix: 0x01 then the key id big-endian. The signed data is the payload unchanged; only
        // the LEGACY output prefix appends a 0x00 byte, per Tink's LegacyFullVerify.
        var prefixed = new byte[5 + signature.Length];
        prefixed[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(prefixed.AsSpan(1), KeyId);
        signature.CopyTo(prefixed, 5);

        return Convert.ToBase64String(prefixed);
    }

    /// <summary>Serializes the public key as a google.crypto.tink.EcdsaPublicKey protobuf.</summary>
    private byte[] SerializeEcdsaPublicKey(byte[]? paramsOverride = null)
    {
        var parameters = _ecdsa.ExportParameters(includePrivateParameters: false);

        var buffer = new List<byte>();

        // field 2 (params): hash_type=3 (SHA256), curve=2 (NIST_P256), encoding=2 (DER)
        var paramsBytes = paramsOverride ?? [0x08, 0x03, 0x10, 0x02, 0x18, 0x02];
        buffer.Add(0x12);
        buffer.Add((byte)paramsBytes.Length);
        buffer.AddRange(paramsBytes);

        // fields 3 and 4 (x, y). Written with the leading 0x00 sign byte Tink uses, so the test
        // exercises the coordinate normalization the parser has to do.
        AppendBytesField(buffer, 3, PrependSignByte(parameters.Q.X!));
        AppendBytesField(buffer, 4, PrependSignByte(parameters.Q.Y!));

        return [.. buffer];

        static byte[] PrependSignByte(byte[] coordinate) => [0x00, .. coordinate];

        static void AppendBytesField(List<byte> target, int field, byte[] value)
        {
            target.Add((byte)((field << 3) | 2));
            target.Add((byte)value.Length);
            target.AddRange(value);
        }
    }

    public void Dispose() => _ecdsa.Dispose();
}

/// <summary>Serves a fixed keyset and counts how often it was asked for.</summary>
public sealed class KeysetHandler(Func<string> keyset) : HttpMessageHandler
{
    public int Requests { get; private set; }

    public bool Fail { get; set; }

    /// <summary>Cancels this token from inside the request, the way a caller giving up mid-fetch does.</summary>
    /// <remarks>
    /// Cancelling before the call instead would be caught by the gate the provider waits on, which
    /// proves nothing about what the fetch does with a cancellation.
    /// </remarks>
    public CancellationTokenSource? CancelFrom { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;

        if (CancelFrom is { } source)
        {
            source.Cancel();
            throw new TaskCanceledException("the caller gave up", innerException: null, source.Token);
        }

        if (Fail)
        {
            throw new HttpRequestException("keyset unavailable");
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(keyset(), Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Answers every request with a body the caller supplies.</summary>
/// <remarks>
/// <see cref="KeysetHandler"/> covers the common case of serving JSON text. This one exists for
/// the checks that care about the response's declared length rather than its content.
/// </remarks>
public sealed class ContentHandler(Func<HttpContent> content) : HttpMessageHandler
{
    /// <summary>How many requests were served.</summary>
    public int Requests { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content() });
    }
}
