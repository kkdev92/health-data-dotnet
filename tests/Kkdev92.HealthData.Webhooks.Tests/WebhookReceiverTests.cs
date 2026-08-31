using System.Net;
using System.Text;
using Kkdev92.HealthData.Http;
using Kkdev92.HealthData.TestSupport;
using Kkdev92.HealthData.Webhooks;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.HealthData.Webhooks.Tests;

/// <summary>
/// Endpoint behaviour: challenge handling, response codes, and verify-before-parse.
/// </summary>
public sealed class WebhookReceiverTests
{
    private const string Secret = "shared-endpoint-secret";

    private const string NotificationJson = """
        {"data":{"version":"1","clientProvidedSubscriptionName":"my-sub","healthUserId":"user-1",
        "operation":"UPSERT","dataType":"steps","intervals":[
        {"physicalTimeInterval":{"startTime":"2026-03-08T01:29:00Z","endTime":"2026-03-08T01:34:00Z"},
         "civilDateTimeInterval":{"startDateTime":{"date":{"year":2026,"month":3,"day":7},"time":{"hours":17,"minutes":29}},
                                  "endDateTime":{"date":{"year":2026,"month":3,"day":7},"time":{"hours":17,"minutes":34}}},
         "civilIso8601TimeInterval":{"startTime":"2026-03-07T17:29:00","endTime":"2026-03-07T17:34:00"}}]}}
        """;

    private static (HealthDataWebhookReceiver Receiver, TinkTestKey Key, HealthDataWebhookKeyProvider Provider)
        Create(string? secret = Secret)
    {
        var key = new TinkTestKey(1083906037);
        var handler = new KeysetHandler(() => key.ToKeysetJson());
        var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));
        var verifier = new HealthDataWebhookSignatureVerifier(provider);

        return (new HealthDataWebhookReceiver(verifier, secret), key, provider);
    }

    [Fact]
    public async Task AuthorizedChallengeReturns201()
    {
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes("""{"type": "verification"}""");

        var result = await receiver.HandleAsync(body, signatureHeader: null, Secret, TestContext.Current.CancellationToken);

        // The guide allows 200 or 201; the per-method reference requires 201. Returning the
        // stricter value satisfies both.
        Assert.Equal(WebhookRequestKind.AuthorizedChallenge, result.Kind);
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
    }

    [Fact]
    public async Task UnauthorizedChallengeReturns401()
    {
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes("""{"type": "verification"}""");

        // Google sends the second challenge without the credential. Answering it successfully
        // would mean the endpoint accepts anything.
        var result = await receiver.HandleAsync(body, signatureHeader: null, authorizationHeader: null, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.UnauthorizedChallenge, result.Kind);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task AChallengeWithTheWrongCredentialIsRejected()
    {
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes("""{"type": "verification"}""");

        var result = await receiver.HandleAsync(body, null, "wrong-secret", TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.UnauthorizedChallenge, result.Kind);
    }

    [Fact]
    public async Task AChallengeIsRejectedWhenNoSecretIsConfigured()
    {
        // Without a configured secret the two challenges are indistinguishable, so the endpoint
        // must not claim to have passed verification.
        var (receiver, key, provider) = Create(secret: null);
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes("""{"type": "verification"}""");

        var result = await receiver.HandleAsync(body, null, "anything", TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.UnauthorizedChallenge, result.Kind);
    }

    /// <summary>
    /// A notification Google really signed, arriving without this endpoint's secret, is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signature covers the body and says nothing about where the body was going, so a
    /// notification signed for one subscriber verifies just as well at another's endpoint. The
    /// secret is the only thing that binds a notification to the endpoint it was meant for, and
    /// Google sends it with every one: "a client-provided secret that will be sent with each
    /// notification to the subscriber endpoint using the Authorization header".
    /// </para>
    /// <para>
    /// The receiver used to check it on the verification challenge only. Every existing test
    /// happened to pass the right secret, so nothing noticed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    [InlineData("shared-endpoint-secre")]
    public async Task AValidlySignedNotificationWithoutTheEndpointSecretIsRefused(string? authorization)
    {
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await receiver.HandleAsync(body, key.Sign(body), authorization, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.UnauthorizedNotification, result.Kind);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);

        // Nothing parsed, for the same reason an unverified payload is never parsed.
        Assert.Null(result.Notification);
    }

    /// <summary>
    /// An unauthenticated request with a JSON type nobody expected does not become a 500.
    /// </summary>
    /// <remarks>
    /// The challenge check read <c>type</c> as a string without asking what kind it was, so a
    /// number, an object or an array threw out of a public endpoint that anyone can post to.
    /// </remarks>
    [Theory]
    [InlineData("{\"type\": 1}")]
    [InlineData("{\"type\": {}}")]
    [InlineData("{\"type\": []}")]
    [InlineData("{\"type\": null}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("not json at all")]
    public async Task AnyShapeOfBodyIsHandledRatherThanThrown(string json)
    {
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes(json);

        var result = await receiver.HandleAsync(body, signatureHeader: null, Secret, TestContext.Current.CancellationToken);

        // Refused one way or another; which way matters less than not throwing.
        Assert.NotEqual(WebhookRequestKind.Notification, result.Kind);
        Assert.NotEqual(HttpStatusCode.NoContent, result.StatusCode);
    }

    [Fact]
    public async Task AVerifiedNotificationReturns204AndParses()
    {
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await receiver.HandleAsync(body, key.Sign(body), Secret, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.Notification, result.Kind);
        Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);

        var data = result.Notification!.Data!;
        Assert.Equal("1", data.Version);
        Assert.Equal("my-sub", data.ClientProvidedSubscriptionName);
        Assert.Equal("steps", data.DataType);
        Assert.True(data.IsUpsert);
        Assert.False(data.IsDelete);

        var interval = Assert.Single(data.Intervals!);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 1, 29, 0, TimeSpan.Zero),
            interval.PhysicalTimeInterval!.StartTime!.Value.Value);
        Assert.Equal(2026, interval.CivilDateTimeInterval!.StartDateTime!.Date!.Year);
        Assert.Equal(17, interval.CivilDateTimeInterval.StartDateTime.Time!.Hours);
        Assert.Equal("2026-03-07T17:29:00", interval.CivilIso8601TimeInterval!.StartTime);
    }

    [Fact]
    public async Task AnUnverifiedNotificationIsNeverParsed()
    {
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await receiver.HandleAsync(body, signatureHeader: "AQIDBAUGBwgJ", Secret, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.Rejected, result.Kind);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);

        // The ordering made structural: there is no parsed notification to mistake for a
        // trustworthy one.
        Assert.Null(result.Notification);
    }

    [Fact]
    public async Task AnUnknownOperationValueStillParses()
    {
        // Additive change tolerance: a value added after this SDK shipped must not break a
        // receiver that only cares about UPSERT and DELETE.
        var (receiver, key, provider) = Create();
        using var _ = key;
        using var __ = provider;

        var body = Encoding.UTF8.GetBytes(
            """{"data":{"operation":"ARCHIVED","dataType":"sleep"}}""");

        var result = await receiver.HandleAsync(body, key.Sign(body), Secret, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.Notification, result.Kind);
        Assert.Equal("ARCHIVED", result.Notification!.Data!.Operation);
        Assert.False(result.Notification.Data.IsUpsert);
        Assert.False(result.Notification.Data.IsDelete);
    }

    /// <summary>
    /// Both secrets work while a rotation is in flight.
    /// </summary>
    /// <remarks>
    /// The secret changes at Google and in the application at two different moments, and Google
    /// keeps delivering in between. With one secret accepted, that window is an outage in which
    /// every notification is refused — and refused notifications are data the application never
    /// learns about.
    /// </remarks>
    [Theory]
    [InlineData("old-secret")]
    [InlineData("new-secret")]
    public async Task EitherSecretIsAcceptedDuringARotation(string presented)
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));

        var receiver = new HealthDataWebhookReceiver(
            new HealthDataWebhookSignatureVerifier(provider),
            new[] { "old-secret", "new-secret" });

        var body = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await receiver.HandleAsync(body, key.Sign(body), presented, TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.Notification, result.Kind);
    }

    /// <summary>A secret that is on neither side of the rotation is still refused.</summary>
    [Fact]
    public async Task ARotationDoesNotWidenWhatIsAccepted()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));

        var receiver = new HealthDataWebhookReceiver(
            new HealthDataWebhookSignatureVerifier(provider),
            new[] { "old-secret", "new-secret" });

        var body = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await receiver.HandleAsync(body, key.Sign(body), "third-secret", TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.UnauthorizedNotification, result.Kind);
    }

    /// <summary>An empty list is not "accept anything".</summary>
    [Fact]
    public async Task NoSecretsConfiguredRefusesEverything()
    {
        using var key = new TinkTestKey(1);
        var handler = new KeysetHandler(() => key.ToKeysetJson());
        using var provider = new HealthDataWebhookKeyProvider(new HttpClient(handler));

        var receiver = new HealthDataWebhookReceiver(
            new HealthDataWebhookSignatureVerifier(provider), Array.Empty<string>());

        var body = Encoding.UTF8.GetBytes(NotificationJson);

        var result = await receiver.HandleAsync(body, key.Sign(body), "anything", TestContext.Current.CancellationToken);

        Assert.Equal(WebhookRequestKind.UnauthorizedNotification, result.Kind);
    }

    [Theory]
    [InlineData("""{"type": "verification"}""", true)]
    [InlineData("""{"type":"verification"}""", true)]
    [InlineData("""{"type": "something-else"}""", false)]
    [InlineData("""{"data":{"dataType":"verification"}}""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]

    // Whatever comes before it, at whatever depth, is stepped over rather than searched.
    [InlineData("""{"other":{"a":1},"type":"verification"}""", true)]
    [InlineData("""{"other":[1,2],"type":"verification"}""", true)]
    [InlineData("""{"other":[{"type":"verification"}],"type":"nope"}""", false)]
    [InlineData("""{"data":1}""", false)]
    [InlineData("{}", false)]
    public void ChallengeDetectionIsStructural(string body, bool expected)
    {
        // Checked as JSON rather than by substring, so a notification that merely contains the
        // word is not mistaken for a challenge.
        Assert.Equal(expected, HealthDataWebhookReceiver.IsVerificationChallenge(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void TheVerificationUserAgentIsPublished()
        => Assert.Equal("Google-Health-API-Webhooks", HealthDataWebhookReceiver.VerificationUserAgent);

    [Fact]
    public void TheSignatureHeaderNameIsPublished()
        => Assert.Equal("GOOGLE-HEALTH-API-SIGNATURE", HealthDataWebhookSignatureVerifier.SignatureHeaderName);

    [Fact]
    public void TheKeysetUriMatchesGooglesPublishedLocation()
        => Assert.Equal(
            "https://www.gstatic.com/googlehealthapi/webhooks/webhooks_public_keyset.json",
            HealthDataWebhookKeyProvider.DefaultKeysetUri.ToString());
}

/// <summary>
/// Regression guard for a conflict between Google's Webhooks guide and its Discovery document.
/// </summary>
public sealed class SubscriberResponseKindTests
{
    [Fact]
    public void SubscriberWritesReturnAnOperation()
    {
        // The Webhooks guide reads as though a Subscriber comes back directly. Discovery and the
        // per-method reference both say Operation, and Discovery is the contract the service
        // enforces.
        Assert.Equal(ResponseKind.Operation, HealthDataGeneratedOperations.ProjectsSubscribersCreate.ResponseKind);
        Assert.Equal(ResponseKind.Operation, HealthDataGeneratedOperations.ProjectsSubscribersPatch.ResponseKind);
        Assert.Equal(ResponseKind.Operation, HealthDataGeneratedOperations.ProjectsSubscribersDelete.ResponseKind);
    }

    [Fact]
    public void SubscriptionWritesDoNot()
    {
        // The asymmetry is real and must not be normalised away.
        Assert.Equal(ResponseKind.Json, HealthDataGeneratedOperations.ProjectsSubscribersSubscriptionsCreate.ResponseKind);
        Assert.Equal(ResponseKind.Json, HealthDataGeneratedOperations.ProjectsSubscribersSubscriptionsPatch.ResponseKind);
        Assert.Equal(ResponseKind.Empty, HealthDataGeneratedOperations.ProjectsSubscribersSubscriptionsDelete.ResponseKind);
    }
}
