using System.Net;
using System.Text;
using Kkdev92.HealthData.TestSupport;
using Kkdev92.HealthData.Webhooks;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.HealthData.Webhooks.Tests;

/// <summary>
/// What <see cref="HealthDataWebhookKeyProvider"/> refuses before and during a fetch.
/// </summary>
/// <remarks>
/// The signature tests cover the cache, the throttle and the fallback. Left uncovered until here
/// were the two floors: where the keyset may come from, and how much of it will be read. Both
/// decide which key gets to verify a signature, which is the one thing this package exists to get
/// right.
/// </remarks>
public sealed class WebhookKeyProviderTests
{
    private const string NotificationJson = """
        {"data":{"version":"1","clientProvidedSubscriptionName":"sub","healthUserId":"user-1",
        "operation":"UPSERT","dataType":"steps","intervals":[]}}
        """;

    [Theory]
    [InlineData("http://keys.example.test/keyset.json")]   // plaintext: anything on the path can answer with its own key
    [InlineData("ftp://localhost/keyset.json")]            // IsLoopback is true, and the scheme is not HTTP
    [InlineData("file://localhost/keyset.json")]
    public void AKeysetUriThatIsNotHttpsOrLoopbackIsRefused(string uri)
    {
        using var client = new HttpClient(new KeysetHandler(() => "{}"));

        var exception = Assert.Throws<ArgumentException>(() =>
            new HealthDataWebhookKeyProvider(client, keysetUri: new Uri(uri)));

        Assert.Equal("keysetUri", exception.ParamName);
        Assert.Contains("not HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://keys.example.test/keyset.json")]
    [InlineData("http://localhost:8080/keyset.json")]
    [InlineData("http://127.0.0.1/keyset.json")]
    public void HttpsAnywhereAndPlainHttpToLoopbackAreAccepted(string uri)
    {
        using var client = new HttpClient(new KeysetHandler(() => "{}"));
        using var provider = new HealthDataWebhookKeyProvider(client, keysetUri: new Uri(uri));

        Assert.NotNull(provider);
    }

    [Fact]
    public void ARelativeKeysetUriIsRefused()
    {
        using var client = new HttpClient(new KeysetHandler(() => "{}"));

        var exception = Assert.Throws<ArgumentException>(() =>
            new HealthDataWebhookKeyProvider(client, keysetUri: new Uri("keyset.json", UriKind.Relative)));

        Assert.Contains("absolute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalNamesTheHostButNotACredentialPutInTheUri()
    {
        using var client = new HttpClient(new KeysetHandler(() => "{}"));

        var exception = Assert.Throws<ArgumentException>(() => new HealthDataWebhookKeyProvider(
            client, keysetUri: new Uri("http://user:hunter2@keys.example.test:8443/keyset.json?sig=abc")));

        Assert.Contains("http://keys.example.test:8443", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=abc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullHttpClientIsRefused()
        => Assert.Throws<ArgumentNullException>(() => new HealthDataWebhookKeyProvider(null!));

    [Theory]
    [InlineData(TimeSpan.TicksPerSecond * -1)]
    [InlineData(0)]
    public void ACacheDurationThatIsNotPositiveIsRefused(long ticks)
    {
        using var client = new HttpClient(new KeysetHandler(() => "{}"));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HealthDataWebhookKeyProvider(client, cacheDuration: TimeSpan.FromTicks(ticks)));

        Assert.Equal("cacheDuration", exception.ParamName);
    }

    /// <summary>
    /// A keyset that declares itself larger than the ceiling is refused before a byte is read.
    /// </summary>
    /// <remarks>
    /// With nothing cached there is no fallback, so the refusal surfaces as the fetch failing.
    /// The body here is tiny; only the declared length is oversized, which is what proves the
    /// check runs on the header rather than on what arrives.
    /// </remarks>
    [Fact]
    public async Task AKeysetThatDeclaresItselfOversizedIsRefusedBeforeItIsRead()
    {
        using var key = new TinkTestKey(1);
        var body = new DeclaredLengthContent(Encoding.UTF8.GetBytes(key.ToKeysetJson()), declaredLength: 256 * 1024 + 1);
        var handler = new ContentHandler(() => body);

        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);
        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));

        Assert.Contains("declares more than", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, body.BytesServed);
    }

    /// <summary>
    /// A keyset that declares no length and does not stop is cut off at the ceiling.
    /// </summary>
    /// <remarks>
    /// A server is free to send no Content-Length at all, so the declared-length check alone is
    /// not a bound. This is the read-side check: the body is a valid keyset padded past the limit
    /// with whitespace, so the only reason to refuse it is its size.
    /// </remarks>
    [Fact]
    public async Task AKeysetThatOverrunsTheCeilingWhileBeingReadIsRefused()
    {
        using var key = new TinkTestKey(1);
        var keyset = Encoding.UTF8.GetBytes(key.ToKeysetJson());
        var padded = new byte[256 * 1024 + 1];
        Array.Fill(padded, (byte)' ');
        keyset.CopyTo(padded, 0);

        var handler = new ContentHandler(() => new DeclaredLengthContent(padded, declaredLength: null));

        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);
        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken));

        Assert.Contains("exceeded", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The oversize refusal falls back like any other fetch failure once a keyset is cached.
    /// </summary>
    [Fact]
    public async Task AnOversizedKeysetFallsBackToTheCachedKeysLikeAnyOtherBadFetch()
    {
        using var key = new TinkTestKey(1);
        var good = Encoding.UTF8.GetBytes(key.ToKeysetJson());
        var serveOversized = false;
        var handler = new ContentHandler(() =>
            new DeclaredLengthContent(good, serveOversized ? 256 * 1024 + 1 : null));

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler), timeProvider: time);
        var verifier = new HealthDataWebhookSignatureVerifier(provider);
        var payload = Encoding.UTF8.GetBytes(NotificationJson);

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);

        serveOversized = true;
        time.Advance(TimeSpan.FromHours(7));

        Assert.True((await verifier.VerifyAsync(payload, key.Sign(payload), TestContext.Current.CancellationToken)).IsValid);
    }

}
