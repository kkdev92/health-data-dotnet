using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kkdev92.HealthData.Webhooks;

/// <summary>What an endpoint should do with an incoming request.</summary>
public enum WebhookRequestKind
{
    /// <summary>A data-change notification.</summary>
    Notification,

    /// <summary>An endpoint verification challenge carrying the configured credential.</summary>
    AuthorizedChallenge,

    /// <summary>An endpoint verification challenge sent without the credential.</summary>
    UnauthorizedChallenge,

    /// <summary>
    /// A notification that did not carry the endpoint's credential.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Rejected"/>: the signature may well be Google's. What is missing
    /// is the secret that says the notification was meant for this endpoint.
    /// </remarks>
    UnauthorizedNotification,

    /// <summary>The signature did not verify, so the request is not from Google.</summary>
    Rejected,
}

/// <summary>
/// The result of handling a webhook request.
/// </summary>
/// <remarks>
/// The parsed notifications are reachable only through <see cref="Notifications"/>, which is
/// populated only when the signature verified. That is the receive-verify-parse ordering made
/// structural rather than a rule to remember.
/// </remarks>
public sealed class WebhookRequestResult
{
    private WebhookRequestResult(
        WebhookRequestKind kind,
        HttpStatusCode statusCode,
        IReadOnlyList<HealthDataNotification> notifications,
        WebhookSignatureResult? signature)
    {
        Kind = kind;
        StatusCode = statusCode;
        Notifications = notifications;
        Signature = signature;
    }

    /// <summary>What the request was.</summary>
    public WebhookRequestKind Kind { get; }

    /// <summary>The status the endpoint should return.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The parsed notifications, in the order they arrived. Empty unless the request was a verified
    /// notification.
    /// </summary>
    /// <remarks>
    /// One request carries a batch: the Webhooks guide delivers every body as a JSON array of 1 to
    /// 99 notifications, for any mix of users and data types, and the status acknowledges the whole
    /// batch at once. Capture every one of them before returning the status; a notification left
    /// behind here is not delivered again.
    /// </remarks>
    public IReadOnlyList<HealthDataNotification> Notifications { get; }

    /// <summary>The signature outcome, when a signature was checked.</summary>
    public WebhookSignatureResult? Signature { get; }

    internal static WebhookRequestResult ForNotifications(IReadOnlyList<HealthDataNotification> notifications, WebhookSignatureResult signature)
        => new(WebhookRequestKind.Notification, HttpStatusCode.NoContent, notifications, signature);

    internal static WebhookRequestResult AuthorizedChallenge()
        => new(WebhookRequestKind.AuthorizedChallenge, HttpStatusCode.Created, [], null);

    internal static WebhookRequestResult UnauthorizedChallenge()
        => new(WebhookRequestKind.UnauthorizedChallenge, HttpStatusCode.Unauthorized, [], null);

    internal static WebhookRequestResult UnauthorizedNotification()
        => new(WebhookRequestKind.UnauthorizedNotification, HttpStatusCode.Unauthorized, [], null);

    internal static WebhookRequestResult Rejected(WebhookSignatureResult signature)
        => new(WebhookRequestKind.Rejected, HttpStatusCode.Unauthorized, [], signature);
}

/// <summary>
/// Handles requests delivered to a subscriber endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Google verifies an endpoint by sending two challenges: one carrying the configured
/// <c>Authorization</c> credential and one without it. The endpoint must accept the first and
/// reject the second, which proves it is not an open relay.
/// </para>
/// <para>
/// Expected responses, from the Webhooks guide:
/// </para>
/// <code>
/// authorized challenge     200 OK or 201 Created   -> this returns 201
/// unauthorized challenge   401 or 403              -> this returns 401
/// notification             204 No Content          -> immediately
/// </code>
/// <para>
/// 201 is returned for the authorized challenge because the per-method reference requires
/// exactly that while the guide allows either. Returning the stricter value satisfies both.
/// </para>
/// <para>
/// Respond first, work later. The guide says to answer a notification immediately; queue the
/// parsed notifications and return, rather than doing the processing inline.
/// </para>
/// </remarks>
public sealed class HealthDataWebhookReceiver
{
    /// <summary>The longest presented credential, in UTF-8 bytes, compared without allocating.</summary>
    private const int StackBytes = 256;

    private readonly HealthDataWebhookSignatureVerifier _verifier;
    private readonly byte[][] _endpointSecrets;

    /// <summary>Creates a receiver that accepts one endpoint secret.</summary>
    /// <param name="verifier">Verifies the signature Google sends.</param>
    /// <param name="endpointSecret">
    /// The credential configured on the subscriber. Without it every notification and every
    /// challenge is refused, because there is nothing to tell one endpoint's traffic from
    /// another's.
    /// </param>
    public HealthDataWebhookReceiver(
        HealthDataWebhookSignatureVerifier verifier,
        string? endpointSecret = null)
        : this(verifier, endpointSecret is null ? [] : [endpointSecret])
    {
    }

    /// <summary>
    /// Creates a receiver that accepts any of several endpoint secrets.
    /// </summary>
    /// <param name="verifier">Verifies the signature Google sends.</param>
    /// <param name="endpointSecrets">
    /// Every credential to accept. More than one exists for rotation: a subscriber's secret is
    /// changed at Google and in the application at two different moments, and notifications keep
    /// arriving in between. Accepting both for the length of that window is the difference between
    /// a rotation and an outage. Remove the old one once nothing is arriving with it.
    /// </param>
    /// <remarks>
    /// Order does not matter and every candidate is compared, so the time taken does not depend on
    /// which one matched or on how far down the list it was.
    /// </remarks>
    public HealthDataWebhookReceiver(
        HealthDataWebhookSignatureVerifier verifier,
        IEnumerable<string> endpointSecrets)
    {
        ArgumentNullException.ThrowIfNull(endpointSecrets);

        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));

        _endpointSecrets = [.. endpointSecrets.Where(s => !string.IsNullOrEmpty(s)).Select(Encoding.UTF8.GetBytes)];
    }

    /// <summary>The user agent Google sends verification challenges with.</summary>
    public const string VerificationUserAgent = "Google-Health-API-Webhooks";

    /// <summary>
    /// The body Google sends to verify an endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a test that posts a challenge at its own endpoint, which is the only way to find out
    /// whether the receiver is wired up before asking Google to try. The value was documented in
    /// prose here and nowhere a caller could reach, so every such test wrote the literal out again
    /// and would keep working while quietly testing the wrong thing if Google changed it.
    /// </para>
    /// <para>
    /// A challenge is recognized structurally rather than by comparing against this string: a
    /// notification whose payload happens to contain the same text is not a challenge, and
    /// whitespace is not part of the meaning. This is what to send, not what is matched.
    /// </para>
    /// </remarks>
    public const string VerificationChallengeBody = """{"type": "verification"}""";

    /// <summary>
    /// Handles an incoming request.
    /// </summary>
    /// <param name="rawBody">
    /// The body exactly as received. Reading it into a string and back, or binding it to a model
    /// and re-serializing, changes the bytes and the signature will never verify.
    /// </param>
    /// <param name="signatureHeader">The <c>GOOGLE-HEALTH-API-SIGNATURE</c> header value.</param>
    /// <param name="authorizationHeader">The <c>Authorization</c> header value, if any.</param>
    /// <param name="cancellationToken">Cancels a keyset refresh.</param>
    public async Task<WebhookRequestResult> HandleAsync(
        ReadOnlyMemory<byte> rawBody,
        string? signatureHeader,
        string? authorizationHeader = null,
        CancellationToken cancellationToken = default)
    {
        if (IsVerificationChallenge(rawBody.Span))
        {
            // A challenge proves the endpoint checks credentials, so the credential decides.
            return IsAuthorized(authorizationHeader)
                ? WebhookRequestResult.AuthorizedChallenge()
                : WebhookRequestResult.UnauthorizedChallenge();
        }

        // Every notification carries it, not only the challenge. Google: the secret "will be sent
        // with each notification to the subscriber endpoint using the Authorization header".
        //
        // Checking it is not belt and braces on top of the signature, because the signature covers
        // the body and nothing about where the body was going. A notification Google signed for
        // one subscriber verifies just as well at another subscriber's endpoint, and the secret is
        // the only thing that says which endpoint it was meant for.
        if (!IsAuthorized(authorizationHeader))
        {
            return WebhookRequestResult.UnauthorizedNotification();
        }

        var signature = await _verifier.VerifyAsync(rawBody, signatureHeader, cancellationToken).ConfigureAwait(false);

        if (!signature.IsValid)
        {
            // Nothing is parsed. An unverified payload is never turned into a model, so it cannot
            // be mistaken for a trustworthy one downstream.
            return WebhookRequestResult.Rejected(signature);
        }

        return WebhookRequestResult.ForNotifications(ReadNotifications(rawBody.Span), signature);
    }

    /// <summary>Reads a verified body as the batch of notifications it carries.</summary>
    /// <remarks>
    /// <para>
    /// The Webhooks guide delivers every request body as a JSON array of 1 to 99 notification
    /// objects, "even when a delivery contains a single notification". Earlier versions of the
    /// guide showed a lone notification as a bare object and only a batch as an array, so a bare
    /// object is still read, as a batch of one.
    /// </para>
    /// <para>
    /// An empty array is a batch with nothing in it, and a null where a notification should be is
    /// read as an empty notification, as a null body always was. Anything else that does not read
    /// as notifications throws. That is deliberate: the signature says Google sent it, and an
    /// endpoint that lets the exception escape answers 500, which Google retries. The batch then
    /// waits at Google for a receiver that can read it, where a 4xx or a 204 would lose it.
    /// </para>
    /// </remarks>
    private static HealthDataNotification[] ReadNotifications(ReadOnlySpan<byte> body)
    {
        if (!StartsWithArray(body))
        {
            return [JsonSerializer.Deserialize(body, WebhookJsonContext.Default.HealthDataNotification) ?? new HealthDataNotification()];
        }

        var batch = JsonSerializer.Deserialize(body, WebhookJsonContext.Default.HealthDataNotificationArray) ?? [];

        // The declared element type is not nullable, but nothing on the wire enforces that.
        for (var i = 0; i < batch.Length; i++)
        {
            batch[i] ??= new HealthDataNotification();
        }

        return batch;
    }

    /// <summary>Whether the first JSON token of the body opens an array.</summary>
    /// <remarks>
    /// Anything that is not JSON at all goes down the object path, which reports it exactly as it
    /// did before arrays were read.
    /// </remarks>
    private static bool StartsWithArray(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            return reader.Read() && reader.TokenType == JsonTokenType.StartArray;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether the request carries the configured endpoint credential.</summary>
    /// <remarks>
    /// Compared in fixed time. The credential is a shared secret, and a comparison that returns
    /// early leaks it a byte at a time.
    /// </remarks>
    private bool IsAuthorized(string? authorizationHeader)
    {
        if (_endpointSecrets.Length == 0 || string.IsNullOrEmpty(authorizationHeader))
        {
            // No secret configured means nothing can be distinguished, so everything is treated as
            // unauthorized rather than waved through.
            return false;
        }

        // On the stack when it fits. This runs on every request before anything is authenticated,
        // so an allocation here is one any caller can ask for; a header longer than any real
        // credential is still compared, from the heap.
        var length = Encoding.UTF8.GetByteCount(authorizationHeader);
        Span<byte> presented = length <= StackBytes ? stackalloc byte[StackBytes] : new byte[length];
        presented = presented[..Encoding.UTF8.GetBytes(authorizationHeader, presented)];

        var matched = false;

        foreach (var secret in _endpointSecrets)
        {
            // Every candidate, even after one matched: returning early would make the time taken
            // depend on which secret it was, which is the thing FixedTimeEquals exists to avoid.
            matched |= CryptographicOperations.FixedTimeEquals(presented, secret);
        }

        return matched;
    }

    /// <summary>
    /// Whether the body is a verification challenge rather than a notification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The challenge body is <c>{"type": "verification"}</c>. It is checked structurally rather
    /// than by substring so that a notification containing that text is not mistaken for one.
    /// </para>
    /// <para>
    /// Over a reader rather than a <see cref="JsonDocument"/>, which has no span overload and so
    /// needed a copy of the whole body. This runs first, before anything has been authenticated,
    /// on an endpoint anyone can post to — so it doubled the memory cost of every request,
    /// including the ones that were never going to be accepted.
    /// </para>
    /// </remarks>
    public static bool IsVerificationChallenge(ReadOnlySpan<byte> rawBody)
    {
        if (rawBody.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(rawBody);

            // Anything that is not an object — a number, an array, a bare string — is not a
            // challenge, and saying so is not the same as throwing on it. This is the path an
            // unauthenticated request takes, so a throw here is a 500 anyone can ask for.
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isType = reader.ValueTextEquals("type"u8);

                if (!reader.Read())
                {
                    return false;
                }

                if (isType)
                {
                    return reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("verification"u8);
                }

                // Past a nested object or array in one step; a primitive is already behind us.
                // TrySkip rather than Skip: Skip throws InvalidOperationException on a partial
                // buffer, and the only exception this method catches is JsonException. False here
                // means "not a challenge", which sends the body down the path that fails closed.
                if (!reader.TrySkip())
                {
                    return false;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
