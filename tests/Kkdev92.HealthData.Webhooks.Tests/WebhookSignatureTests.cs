using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kkdev92.HealthData.TestSupport;
using Kkdev92.HealthData.Webhooks;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.HealthData.Webhooks.Tests;

public sealed class WebhookSignatureTests
{
    private const string NotificationJson = """
        {"data":{"version":"1","clientProvidedSubscriptionName":"sub","healthUserId":"user-1",
        "operation":"UPSERT","dataType":"steps","intervals":[]}}
        """;

    private static (HealthDataWebhookSignatureVerifier Verifier, KeysetHandler Handler, HealthDataWebhookKeyProvider Provider)
        Create(TinkTestKey key, TimeProvider? time = null)
    {
        var handler = new KeysetHandler(() => key.ToKeysetJson());
        var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        return (new HealthDataWebhookSignatureVerifier(provider), handler, provider);
    }

    [Fact]
    public async Task AcceptsAGenuineSignature()
    {
        using var key = new TinkTestKey(1083906037);
        var (verifier, _, provider) = Create(key);
        using var _ = provider;

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(WebhookSignatureFailure.None, result.Failure);
        Assert.Equal(1083906037u, result.KeyId);
    }

    [Fact]
    public async Task RejectsATamperedPayload()
    {
        using var key = new TinkTestKey(7);
        var (verifier, _, provider) = Create(key);
        using var _ = provider;

        var payload = Encoding.UTF8.GetBytes(NotificationJson);
        var signature = key.Sign(payload);

        // One byte different: "steps" becomes "sleep".
        var tampered = Encoding.UTF8.GetBytes(NotificationJson.Replace("steps", "sleep", StringComparison.Ordinal));

        var result = await verifier.VerifyAsync(tampered, signature, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailure.Invalid, result.Failure);
    }

    [Fact]
    public async Task RejectsASignatureFromADifferentKey()
    {
        using var trusted = new TinkTestKey(1);
        using var attacker = new TinkTestKey(1); // same key id, different key pair
        var (verifier, _, provider) = Create(trusted);
        using var _ = provider;

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await verifier.VerifyAsync(payload, attacker.Sign(payload), TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailure.Invalid, result.Failure);
    }

    [Fact]
    public async Task FailsClosedForAnUnknownKeyId()
    {
        using var known = new TinkTestKey(1);
        using var unknown = new TinkTestKey(999);
        var (verifier, handler, provider) = Create(known);
        using var _ = provider;

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await verifier.VerifyAsync(payload, unknown.Sign(payload), TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailure.UnknownKey, result.Failure);
        Assert.Equal(999u, result.KeyId);

        // Only the initial fetch. Refetching a keyset obtained microseconds ago cannot produce
        // the missing key, and doing so on every forged signature would turn this endpoint into
        // a request amplifier against gstatic.com.
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PicksUpARotatedKeyOnRefresh()
    {
        // The case the refresh exists for: Google rotates every 30 days, and a notification
        // signed with the new key arrives before the cache expires.
        using var oldKey = new TinkTestKey(1);
        using var newKey = new TinkTestKey(2);

        var rotated = false;
        var handler = new KeysetHandler(() => rotated ? newKey.ToKeysetJson() : oldKey.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        // Prime the cache with the old keyset.
        Assert.True((await verifier.VerifyAsync(payload, oldKey.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
        Assert.Equal(1, handler.Requests);

        rotated = true;

        // Past the refresh throttle, but still inside the cache window, so only the unknown key
        // id triggers the refetch.
        time.Advance(TimeSpan.FromMinutes(2));

        var result = await verifier.VerifyAsync(payload, newKey.Sign(payload), TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(2u, result.KeyId);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task KeepsVerifyingWithCachedKeysWhenTheKeysetIsUnreachable()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        // gstatic.com goes down and the cache expires.
        handler.Fail = true;
        time.Advance(TimeSpan.FromHours(7));

        // A transient outage must not stop verifying notifications the held keys can still
        // verify. Anything they cannot verify still fails closed.
        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
    }

    /// <summary>
    /// A keyset nobody has been able to reconfirm eventually stops being trusted.
    /// </summary>
    /// <remarks>
    /// The fallback above is for an outage, and an outage ends. Without a limit it also covers a
    /// host that is permanently gone, and the endpoint would go on verifying against keys Google
    /// may have revoked for as long as the fetch kept failing — availability paid for with the one
    /// property the provider exists to protect.
    /// </remarks>
    [Fact]
    public async Task KeysThatCannotBeReconfirmedStopBeingTrusted()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        handler.Fail = true;

        // Inside the day, the outage is survived.
        time.Advance(TimeSpan.FromHours(23));
        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        // Past it, a signature these keys can verify is refused all the same, because what is in
        // doubt is the keys rather than the signature.
        time.Advance(TimeSpan.FromHours(2));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A caller that cancels is not answered with cached keys.
    /// </summary>
    /// <remarks>
    /// The transport giving up and the caller giving up arrive as the same exception type, and
    /// treating them alike meant work a caller had explicitly stopped carried on to a result. Only
    /// the first is an outage.
    /// </remarks>
    [Fact]
    public async Task CancellingIsNotAnsweredFromTheCache()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        // Prime the cache, then let it expire so the next call has to go to the network.
        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
        time.Advance(TimeSpan.FromHours(7));

        using var cancellation = new CancellationTokenSource();
        handler.CancelFrom = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), cancellation.Token));
    }

    /// <summary>
    /// A response that is not a keyset falls back the same way an unreachable host does.
    /// </summary>
    /// <remarks>
    /// A CDN having a bad day is at least as likely to answer 200 with an error page as to refuse
    /// the connection. The fallback used to cover only the second, which made the documented
    /// behaviour true for one kind of outage and not the other.
    /// </remarks>
    [Theory]
    [InlineData("<html><body>503 Service Unavailable</body></html>")]
    [InlineData("""{"key":[]}""")]
    [InlineData("""{"primaryKeyId":1,"key":[{"keyData":{"typeUrl":"type.googleapis.com/google.crypto.tink.EcdsaPublicKey","value":"not base64!"},"status":"ENABLED","keyId":1,"outputPrefixType":"TINK"}]}""")]
    public async Task AResponseThatIsNotAKeysetFallsBackToTheCachedKeys(string broken)
    {
        using var key = new TinkTestKey(1);
        var serveBroken = false;
        var handler = new KeysetHandler(() => serveBroken ? broken : key.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        serveBroken = true;
        time.Advance(TimeSpan.FromHours(7));

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
    }

    /// <summary>
    /// A cached keyset is not handed to a caller that has already cancelled.
    /// </summary>
    /// <remarks>
    /// The rule was enforced where the network is, so it held only when the cache happened to be
    /// stale. A fresh cache returned before anything looked at the token.
    /// </remarks>
    [Fact]
    public async Task ACancelledCallerIsNotServedFromAFreshCache()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        // Prime it, so the next call would be served without going anywhere.
        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), cancellation.Token));
    }

    /// <summary>
    /// An outage does not turn every inbound request into a fetch.
    /// </summary>
    /// <remarks>
    /// The throttle applied only to a forced refresh, so once the cache went stale and the fetch
    /// started failing, every notification that arrived went to gstatic.com in turn — the same
    /// amplification the throttle exists to prevent, reached through the ordinary path instead of
    /// through forged key ids.
    /// </remarks>
    [Fact]
    public async Task AnOutageDoesNotRefetchOnEveryRequest()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
        Assert.Equal(1, handler.Requests);

        // gstatic goes down and the cache expires.
        handler.Fail = true;
        time.Advance(TimeSpan.FromHours(7));

        // Five notifications arrive inside the one-minute throttle window.
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
            time.Advance(TimeSpan.FromSeconds(5));
        }

        // One attempt for the whole burst, not one each.
        Assert.Equal(2, handler.Requests);
    }

    /// <summary>A refresh throttle of zero is not a throttle.</summary>
    /// <remarks>
    /// It exists so that a flood of forged signatures naming random key ids cannot become a
    /// request amplifier against gstatic.com. Zero switches that off.
    /// </remarks>
    [Fact]
    public void ARefreshIntervalOfZeroIsRefused()
    {
        using var client = new HttpClient(new KeysetHandler(() => "{}"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthDataWebhookKeyProvider(
            client, minimumRefreshInterval: TimeSpan.Zero));
    }

    /// <summary>A key entry whose Base64 value is JSON null falls back like any other bad keyset.</summary>
    [Fact]
    public async Task AKeysetWithANullKeyValueFallsBackToTheCachedKeys()
    {
        using var key = new TinkTestKey(1);
        var serveBroken = false;

        var handler = new KeysetHandler(() => serveBroken
            ? """{"primaryKeyId":1,"key":[{"keyData":{"typeUrl":"type.googleapis.com/google.crypto.tink.EcdsaPublicKey","value":null},"status":"ENABLED","keyId":1,"outputPrefixType":"TINK"}]}"""
            : key.ToKeysetJson());

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        serveBroken = true;
        time.Advance(TimeSpan.FromHours(7));

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
    }

    /// <summary>A keyset cannot go stale before it expires.</summary>
    [Fact]
    public void AStaleLimitShorterThanTheCacheIsRefused()
    {
        using var client = new HttpClient(new KeysetHandler(() => "{}"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthDataWebhookKeyProvider(
            client,
            cacheDuration: TimeSpan.FromHours(6),
            maximumStaleAge: TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task AFirstFetchThatFailsIsNotSwallowed()
    {
        // With nothing cached there is no safe fallback, so the failure has to surface.
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson()) { Fail = true };
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsAMissingSignature(string? header)
    {
        using var key = new TinkTestKey(1);
        var (verifier, _, provider) = Create(key);
        using var _ = provider;

        var result = await verifier.VerifyAsync(
            Encoding.UTF8.GetBytes(NotificationJson), header, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookSignatureFailure.Missing, result.Failure);
    }

    [Theory]
    [InlineData("not base64 at all !!!")]
    [InlineData("AQID")]                    // valid base64 but shorter than the 5-byte prefix
    [InlineData("AAAAAAAAAAAAAAAAAAAA")]    // version byte 0x00, not the TINK 0x01
    public async Task RejectsAMalformedSignature(string header)
    {
        using var key = new TinkTestKey(1);
        var (verifier, _, provider) = Create(key);
        using var _ = provider;

        var result = await verifier.VerifyAsync(
            Encoding.UTF8.GetBytes(NotificationJson), header, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookSignatureFailure.Malformed, result.Failure);
    }

    [Fact]
    public async Task WhitespaceDifferencesBreakVerification()
    {
        // Why the raw bytes must be verified: re-serializing changes whitespace, and the
        // signature covers the bytes as sent.
        using var key = new TinkTestKey(1);
        var (verifier, _, provider) = Create(key);
        using var _ = provider;

        var original = Encoding.UTF8.GetBytes(NotificationJson);
        var signature = key.Sign(original);

        var reserialized = JsonSerializer.SerializeToUtf8Bytes(JsonDocument.Parse(original).RootElement);

        Assert.False((await verifier.VerifyAsync(reserialized, signature, TestContext.Current.CancellationToken)).IsValid);
        Assert.True((await verifier.VerifyAsync(original, signature, TestContext.Current.CancellationToken)).IsValid);
    }

    [Fact]
    public async Task DisabledKeysDoNotVerify()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson(status: "DISABLED"));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        // A keyset with no usable key is an error, not a silent pass.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonTinkOutputPrefixesAreRejected()
    {
        // RAW carries no prefix and LEGACY appends 0x00 to the signed message. Guessing which one
        // a keyset meant would be a security decision made by accident.
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson(outputPrefixType: "RAW"));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));

        Assert.Contains("RAW", exception.Message, StringComparison.Ordinal);
    }
}
