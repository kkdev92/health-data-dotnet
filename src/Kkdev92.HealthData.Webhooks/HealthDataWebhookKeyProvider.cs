using System.Security.Cryptography;
using System.Text.Json;
using Kkdev92.HealthData.Http;
using Kkdev92.HealthData.Webhooks.Tink;

namespace Kkdev92.HealthData.Webhooks;

/// <summary>
/// Fetches and caches Google's published webhook keyset.
/// </summary>
/// <remarks>
/// <para>
/// Google rotates the signing keys every 30 days, so the keyset cannot be fetched once at startup
/// nor fetched on every request. This caches for a bounded period
/// and refreshes on demand when a signature names a key it has not seen.
/// </para>
/// <para>
/// A refresh that fails leaves the previous keys in place: a transient outage at
/// <c>gstatic.com</c> should not stop verifying notifications that the cached keys can still
/// verify. "Fails" covers every way the fetch can come back without a keyset in it, including a
/// CDN answering 200 with an error page — see <see cref="IsFetchFailure"/>. What it must never do
/// is accept an unverifiable payload, which the verifier enforces by failing closed.
/// </para>
/// <para>
/// That fallback ends. Past the stale limit the cached keys stop being "the last keyset Google
/// published" and become "keys nobody has been able to reconfirm since before one of them could
/// have been revoked", so the failure surfaces instead of being survived indefinitely. A caller
/// that cancels is never answered from the cache either: it asked to stop, not for an older
/// answer.
/// </para>
/// <para>
/// Concurrent callers share one in-flight fetch rather than starting a stampede.
/// </para>
/// </remarks>
public sealed class HealthDataWebhookKeyProvider : IDisposable
{
    /// <summary>Where Google publishes the keyset.</summary>
    public static Uri DefaultKeysetUri { get; } =
        new("https://www.gstatic.com/googlehealthapi/webhooks/webhooks_public_keyset.json");

    private readonly HttpClient _httpClient;
    private readonly Uri _keysetUri;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeSpan _maximumStaleAge;
    private readonly TimeSpan _minimumRefreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The keys and when they were fetched, as one value.
    /// </summary>
    /// <remarks>
    /// One reference rather than two fields, because the fast path in
    /// <see cref="GetKeysAsync"/> reads outside the gate that <see cref="FetchAsync"/> writes
    /// under. Two fields could be read half-updated — the new keys with the old timestamp, or the
    /// reverse — and <see cref="DateTimeOffset"/> is sixteen bytes on a sixty-four bit machine, so
    /// even reading one of them is not a single operation. Publishing an immutable pair through a
    /// single volatile reference makes the fast path see one state or the other and never a
    /// mixture.
    /// </remarks>
    private Snapshot? _snapshot;

    /// <summary>Only ever touched under the gate.</summary>
    private DateTimeOffset _lastAttemptAt;

    /// <summary>Creates a provider.</summary>
    /// <param name="httpClient">Used to fetch the keyset.</param>
    /// <param name="keysetUri">Override the keyset location. Defaults to Google's published URL.</param>
    /// <param name="cacheDuration">
    /// How long a fetched keyset is considered current. Defaults to six hours, comfortably inside
    /// the 30-day rotation while still picking up an early rotation the same day.
    /// </param>
    /// <param name="maximumStaleAge">
    /// How long a keyset may go unconfirmed before a failing refresh stops being survivable.
    /// Defaults to twenty-four hours. Inside the window a failed fetch keeps serving the cached
    /// keys; past it the failure is raised instead, so that a compromised key cannot be trusted
    /// for as long as the network happens to stay broken. Must be at least
    /// <paramref name="cacheDuration"/>.
    /// </param>
    /// <param name="minimumRefreshInterval">
    /// The floor between on-demand refreshes. Defaults to one minute. Without it, a flood of
    /// forged signatures naming random key ids would become a request amplifier against
    /// <c>gstatic.com</c>.
    /// </param>
    /// <param name="timeProvider">The clock, so expiry is testable.</param>
    public HealthDataWebhookKeyProvider(
        HttpClient httpClient,
        Uri? keysetUri = null,
        TimeSpan? cacheDuration = null,
        TimeSpan? minimumRefreshInterval = null,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumStaleAge = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _keysetUri = keysetUri ?? DefaultKeysetUri;

        // HTTPS, or loopback for a test server. This URI decides which public key is trusted to
        // verify a webhook signature; over plaintext, anything on the path can answer with a key
        // of its own and sign whatever it likes.
        if (!_keysetUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The keyset URI must be absolute.", nameof(keysetUri));
        }

        if (!SecureUri.IsHttpsOrLoopback(_keysetUri))
        {
            throw new ArgumentException(
                $"'{SecureUri.Describe(_keysetUri)}' is not HTTPS. This URI decides which key verifies a "
                + "signature; use HTTPS, or a loopback address for a local test server.",
                nameof(keysetUri));
        }
        _cacheDuration = cacheDuration ?? TimeSpan.FromHours(6);
        _minimumRefreshInterval = minimumRefreshInterval ?? TimeSpan.FromMinutes(1);
        _maximumStaleAge = maximumStaleAge ?? TimeSpan.FromHours(24);
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Positive, because every one of these is compared against elapsed time. A negative cache
        // duration expires the keyset the instant it is fetched, which turns the throttle into the
        // only thing standing between a forged key id and gstatic.com; a negative stale limit fails
        // every verification. Neither is a configuration anybody means.
        if (_cacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheDuration), _cacheDuration, "The cache duration must be positive.");
        }

        // Positive, not merely non-negative. Zero switches the throttle off, and the throttle is
        // what stops a flood of forged signatures naming random key ids from becoming a request
        // amplifier against gstatic.com — which is the reason given for it a few lines above.
        if (_minimumRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumRefreshInterval),
                _minimumRefreshInterval,
                "The refresh interval must be positive; zero would disable the throttle.");
        }

        if (_maximumStaleAge < _cacheDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumStaleAge),
                _maximumStaleAge,
                $"A keyset cannot go stale ({_maximumStaleAge}) before it expires ({_cacheDuration}).");
        }
    }

    internal async Task<IReadOnlyList<TinkEcdsaPublicKey>> GetKeysAsync(CancellationToken cancellationToken)
    {
        // Before the cache, not only before the network. The remark on this class says a caller
        // that cancels is never answered from the cache; a fast path that returns without looking
        // at the token made that true only when the cache happened to be stale.
        cancellationToken.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _snapshot) is { } cached
            && _timeProvider.GetUtcNow() - cached.FetchedAt < _cacheDuration)
        {
            return cached.Keys;
        }

        return await FetchAsync(force: false, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<TinkEcdsaPublicKey>> RefreshAsync(CancellationToken cancellationToken)
        => await FetchAsync(force: true, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<TinkEcdsaPublicKey>> FetchAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = _timeProvider.GetUtcNow();
            var current = _snapshot;

            // Another caller may have refreshed while this one waited. Same rule as the fast path:
            // whichever branch returns the cached keys, it does so only for a caller still asking.
            cancellationToken.ThrowIfCancellationRequested();

            if (current is not null)
            {
                if (!force && now - current.FetchedAt < _cacheDuration)
                {
                    return current.Keys;
                }

                // Whether or not this is a forced refresh. During an outage the cache is past
                // its lifetime and every inbound request would otherwise go to gstatic.com in turn,
                // which is the amplification the throttle exists to prevent — the same shape as the
                // forged-key-id flood, arriving through the ordinary path instead.
                if (now - _lastAttemptAt < _minimumRefreshInterval)
                {
                    return current.Keys;
                }
            }

            _lastAttemptAt = now;

            try
            {
                var payload = await ReadKeysetAsync(cancellationToken).ConfigureAwait(false);
                current = new Snapshot(TinkKeysetParser.Parse(payload), now);
                Volatile.Write(ref _snapshot, current);
            }
            catch (Exception ex) when (IsFetchFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                // Keep serving the keys already held. Verification still fails closed for any
                // signature they cannot verify.
                //
                // Not forever, though. Google rotates every 30 days and revokes sooner than that
                // if a key is compromised, and a provider that survives every failure would go on
                // verifying against a withdrawn key for as long as the fetch kept failing —
                // availability bought with the one property this class exists to protect. Past
                // the stale limit the failure surfaces and the endpoint fails closed.
                //
                // The cancellation clause is on the filter rather than in here: a caller that
                // cancelled asked for the work to stop, and handing it stale keys instead would
                // be answering a different question. Only the transport giving up — which arrives
                // as the same exception type with the caller's token untouched — falls back.
                if (current is null || now - current.FetchedAt >= _maximumStaleAge)
                {
                    throw;
                }
            }

            return current.Keys;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The failures that mean the fetch produced no keyset, as opposed to a bug in the parser.
    /// </summary>
    /// <remarks>
    /// The list used to be the three exception types the happy-path outage throws, which made the
    /// remark on this class true only for a refused connection. A CDN having a bad day is at least
    /// as likely to answer 200 with an HTML error page, or to cut the response short — and those
    /// arrive as <see cref="JsonException"/> and <see cref="IOException"/>. Everything here means
    /// the same thing to a caller: what came back was not a keyset.
    /// </remarks>
    private static bool IsFetchFailure(Exception exception) => exception
        // The request did not complete, including HttpClient's own timeout.
        is HttpRequestException or TaskCanceledException or IOException

        // Something came back and it was not a keyset: not JSON, JSON of the wrong shape, key
        // material that is not Base64, or a key entry missing a property the parser needs.
        or JsonException or InvalidOperationException or FormatException or KeyNotFoundException

        // ... or protobuf that decodes to a length no buffer has, or a key entry whose Base64
        // value is JSON null, or a public point that is not on the curve. These come out of the
        // arithmetic and the BCL rather than from a throw the parser wrote, which is exactly why a
        // list assembled by reading its throws missed them. ArgumentOutOfRangeException derives
        // from ArgumentException, so one entry covers both.
        or OverflowException or ArgumentException or CryptographicException;

    /// <summary>The largest keyset this will read, in bytes.</summary>
    /// <remarks>
    /// Google's published keyset is a few kilobytes. This is the ceiling rather than the
    /// expectation: the fetch is over the network, and a client that buffers whatever arrives has
    /// no answer to a response that does not stop.
    /// </remarks>
    private const int MaximumKeysetBytes = 256 * 1024;

    /// <summary>Reads the keyset, refusing one that is implausibly large.</summary>
    private async Task<byte[]> ReadKeysetAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(_keysetUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Read before the body is. Content-Length is what the server declared, but once the body
        // has been buffered the property answers with its real size instead — so asking afterwards
        // would report every oversized keyset as one that declared itself.
        var declared = response.Content.Headers.ContentLength;

        var body = await BoundedBody
            .ReadAsync(response.Content, MaximumKeysetBytes, cancellationToken)
            .ConfigureAwait(false);

        // Which of the two limits was hit is worth saying. A keyset that declares more than this
        // was refused before a byte of it arrived; one that merely turned out to be larger came
        // from a server that declared nothing, or declared something untrue.
        return body ?? throw new InvalidOperationException(
            declared > MaximumKeysetBytes
                ? $"The keyset at {SecureUri.Describe(_keysetUri)} declares more than {MaximumKeysetBytes} bytes."
                : $"The keyset at {SecureUri.Describe(_keysetUri)} exceeded {MaximumKeysetBytes} bytes.");
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private sealed record Snapshot(IReadOnlyList<TinkEcdsaPublicKey> Keys, DateTimeOffset FetchedAt);
}
