using System.Text;
using System.Text.Json;
using Kkdev92.HealthData.TestSupport;
using Kkdev92.HealthData.Webhooks;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.HealthData.Webhooks.Tests;

/// <summary>
/// Malformed key material, as seen through the public path.
/// </summary>
/// <remarks>
/// <para>
/// The protobuf reader and the keyset parser are internal, and this project has no
/// <c>InternalsVisibleTo</c>. What can be tested is what the parser's failures turn into once a
/// keyset carrying them is fetched: every one of them has to be a fetch that produced nothing, so
/// that the previously cached keys keep working. <c>IsFetchFailure</c> lists
/// <c>OverflowException</c>, <c>ArgumentException</c> and <c>InvalidOperationException</c> on
/// the strength of the reader throwing them — these tests make it actually throw them.
/// </para>
/// <para>
/// The bytes are hand-assembled protobuf. Field numbers and wire types follow
/// <c>google.crypto.tink.EcdsaPublicKey</c>: field 2 is the params message, fields 3 and 4 are the
/// coordinates, all length-delimited (wire type 2).
/// </para>
/// </remarks>
public sealed class TinkKeysetParsingTests
{
    private const string NotificationJson = """
        {"data":{"version":"1","clientProvidedSubscriptionName":"sub","healthUserId":"user-1",
        "operation":"UPSERT","dataType":"steps","intervals":[]}}
        """;

    public static TheoryData<string, byte[]> MalformedKeyMaterial => new()
    {
        // A length prefix that runs past the end of the message.
        { "length-delimited field truncated", [0x12, 0x7F, 0x08, 0x03] },

        // A varint tag that never terminates: every byte has the continuation bit set.
        { "message ends inside a varint", [0x80, 0x80, 0x80] },

        // Wire type 3 is the removed start-group encoding.
        { "removed group wire type", [0x13, 0x00] },

        // A length that overflows int: 0xFF 0xFF 0xFF 0xFF 0x7F is 2^35 - 1.
        { "length overflows int", [0x12, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F] },

        // Well-formed protobuf with the coordinates missing altogether.
        { "no public point", [0x12, 0x06, 0x08, 0x03, 0x10, 0x02, 0x18, 0x02] },

        // A coordinate of 40 bytes: neither 32, nor 33 with a sign byte, nor short enough to pad.
        { "coordinate of impossible length", CoordinateOfLength(40) },
    };

    [Theory]
    [MemberData(nameof(MalformedKeyMaterial))]
    public async Task MalformedKeyMaterialIsAFetchThatProducedNothing(string reason, byte[] material)
    {
        using var key = new TinkTestKey(1);
        var serveBroken = false;
        var handler = new KeysetHandler(() => serveBroken ? KeysetWith(material) : key.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);
        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        serveBroken = true;
        time.Advance(TimeSpan.FromHours(7));

        // The bad keyset was fetched and rejected; the previous keys still verify.
        var result = await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, $"{reason}: the cached keys stopped verifying after a bad fetch.");
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [MemberData(nameof(MalformedKeyMaterial))]
    public async Task MalformedKeyMaterialWithNothingCachedSurfacesRatherThanVerifying(string reason, byte[] material)
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => KeysetWith(material));

        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);
        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        // No fallback exists, so the failure has to come out. What must not happen is a result
        // that says the signature is fine.
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));

        // The type is the point, not merely that something was thrown. IsFetchFailure decides
        // whether a bad keyset falls back to the cached keys or escapes as an unhandled failure,
        // and it recognises these types only. A NullReferenceException here would satisfy
        // "something threw" while meaning the parser has a bug.
        Assert.True(
            exception is InvalidOperationException or OverflowException or ArgumentException
                or FormatException or KeyNotFoundException or JsonException,
            $"{reason}: threw {exception.GetType().Name}, which IsFetchFailure does not list.");
    }

    /// <summary>
    /// A varint that runs past 64 bits is refused even when its low bits spell a valid value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first version of this test put an over-long varint in a field the parser ignores, and
    /// the key was then rejected for a different reason — a missing public point — so removing the
    /// 64-bit guard changed nothing and the test stayed green. A guard is only proven by an input
    /// that <em>passes</em> without it.
    /// </para>
    /// <para>
    /// This encoding puts <c>hash_type</c> in eleven bytes: <c>0x83</c> carries the value 3 with a
    /// continuation bit, nine <c>0x80</c> bytes carry nothing, and <c>0x00</c> terminates. A reader
    /// that keeps shifting past 64 bits decodes it as 3 — SHA-256 — and accepts a real key with a
    /// malformed encoding. The guard makes it a fetch that produced nothing instead.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AVarintLongerThan64BitsIsRefusedEvenWhenItsLowBitsAreValid()
    {
        using var key = new TinkTestKey(1);

        byte[] overlongHashType = [0x08, 0x83, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00];
        byte[] paramsBytes = [.. overlongHashType, 0x10, 0x02, 0x18, 0x02];

        var handler = new KeysetHandler(() => key.ToKeysetJson(paramsBytes: paramsBytes));

        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);
        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        // Nothing is cached, so the parse failure has to come out; had the parser accepted the
        // key, the signature — which is genuinely this key's — would have verified.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Unknown fields are skipped, not rejected.
    /// </summary>
    /// <remarks>
    /// Protobuf requires it of every reader, and it is what lets a future Tink field land without
    /// breaking verification. A real key with an extra fixed32 and an extra varint field spliced in
    /// still parses and still verifies.
    /// </remarks>
    [Fact]
    public async Task UnknownFieldsInTheKeyMaterialAreSkipped()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson(extraProtobufFields: [
            0x28, 0x2A,                         // field 5, varint 42
            0x35, 0x01, 0x02, 0x03, 0x04,       // field 6, fixed32
            0x39, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // field 7, fixed64
        ]));

        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);
        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
    }

    private static string KeysetWith(byte[] material)
        => $$"""{"primaryKeyId":1,"key":[{"keyData":{"typeUrl":"type.googleapis.com/google.crypto.tink.EcdsaPublicKey","value":"{{Convert.ToBase64String(material)}}","keyMaterialType":"ASYMMETRIC_PUBLIC"},"status":"ENABLED","keyId":1,"outputPrefixType":"TINK"}]}""";

    private static byte[] CoordinateOfLength(int length)
    {
        var coordinate = new byte[length];
        Array.Fill(coordinate, (byte)0x42);

        return
        [
            0x12, 0x06, 0x08, 0x03, 0x10, 0x02, 0x18, 0x02,   // params: SHA256, P-256, DER
            0x1A, (byte)length, .. coordinate,                // field 3: x
            0x22, (byte)length, .. coordinate,                // field 4: y
        ];
    }
}
