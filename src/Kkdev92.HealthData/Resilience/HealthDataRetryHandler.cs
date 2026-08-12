using System.Diagnostics;
using System.Net;
using Kkdev92.HealthData.Diagnostics;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Resilience;

/// <summary>
/// Options for <see cref="HealthDataRetryHandler"/>.
/// </summary>
public sealed class HealthDataRetryOptions
{
    /// <summary>Total attempts, including the first. Defaults to 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The base of the exponential backoff. Defaults to one second.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest this handler will wait between attempts. Defaults to thirty seconds.</summary>
    /// <remarks>
    /// Caps the exponential backoff. It also bounds a <c>Retry-After</c>, but by declining to
    /// retry rather than by shortening the wait: if the server asks for longer than this, the
    /// response is returned to the caller with its <c>Retry-After</c> intact.
    /// </remarks>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to also retry operations classified as idempotent, such as DELETE.
    /// </summary>
    /// <remarks>
    /// Off by default. An idempotent operation converges on the same server state, but a delete
    /// that actually succeeded and lost its response would report "not found" on the retry, which
    /// most callers would rather see as the original failure.
    /// </remarks>
    public bool RetryIdempotentOperations { get; init; }

    /// <summary>
    /// Whether to randomize each wait across the interval.
    /// </summary>
    /// <remarks>
    /// On by default. Without it, every client that hit the same rate limit retries in lockstep.
    /// Tests turn it off to make delays exact.
    /// </remarks>
    public bool UseJitter { get; init; } = true;
}

/// <summary>
/// Retries selected transient HTTP responses, using the operation descriptor to decide what is
/// safe to resend.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in. Nothing in the core client installs this: a client that silently re-sends requests is
/// a liability on an API that writes health data.
/// </para>
/// <para>
/// The decision comes from the descriptor attached to the request, not from the HTTP method.
/// <c>dataPoints.rollUp</c> is a POST, but it only aggregates existing data and is classified
/// <see cref="RetryClassification.SemanticallySafe"/>; <c>dataPoints.create</c> is also a POST
/// and is never retried.
/// </para>
/// <para>
/// Only responses are retried. A transport failure — a reset connection, a DNS error, a timeout —
/// surfaces as an exception and is left to the caller, because at that point there is no response
/// to say whether the request reached the service or a write already took effect.
/// </para>
/// <para>
/// Delays go through <see cref="TimeProvider"/> so a test can prove the backoff without
/// waiting for it.
/// </para>
/// </remarks>
public sealed class HealthDataRetryHandler : DelegatingHandler
{
    private readonly HealthDataRetryOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the handler.</summary>
    public HealthDataRetryHandler(HealthDataRetryOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new HealthDataRetryOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxAttempts, 1);

        // The delays too, not only the attempt count. A negative base or maximum produces a
        // negative Task.Delay at the moment of the first failure — a long way from the line that
        // configured it — and an unbounded attempt count overflows the exponential into a wait
        // nobody meant. Startup is where a configuration mistake should surface.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxAttempts, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.BaseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxDelay, _options.BaseDelay);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = request.GetHealthDataOperation();

        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (attempt >= _options.MaxAttempts ||
                !IsRetryableStatus(response.StatusCode) ||
                !IsRetryableOperation(descriptor))
            {
                return response;
            }

            if (ComputeDelay(attempt, response) is not { } delay)
            {
                // The server asked for longer than this handler is willing to wait. Retrying
                // sooner is the one thing that must not happen: RFC 9110 defines Retry-After as
                // how long the user agent ought to wait, and a shortened wait hits a service that
                // has just said it is not ready. Hand the response back instead, with its
                // Retry-After intact, and let the caller schedule its own attempt.
                return response;
            }

            // The response is not returned, so its body would otherwise leak.
            response.Dispose();

            Activity.Current?.SetTag(HealthDataActivityTags.RetryAttempt, attempt);

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the operation may be resent at all.
    /// </summary>
    /// <remarks>
    /// A request with no descriptor did not come from this SDK, so nothing is known about its
    /// safety and it is not retried.
    /// </remarks>
    private bool IsRetryableOperation(HealthDataOperationDescriptor? descriptor)
        => descriptor?.RetryClassification switch
        {
            RetryClassification.Safe or RetryClassification.SemanticallySafe => true,
            RetryClassification.Idempotent => _options.RetryIdempotentOperations,
            _ => false,
        };

    /// <summary>
    /// Whether the status indicates a transient failure.
    /// </summary>
    /// <remarks>
    /// The rate-limit documentation names 429 explicitly. The 5xx values are the usual transient
    /// set; 500 is included because Google's error catalog defines <c>INTERNAL_ERROR</c> as a
    /// server-side condition rather than a client mistake.
    /// </remarks>
    private static bool IsRetryableStatus(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// How long to wait before the next attempt, or <c>null</c> to stop retrying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A server-supplied wait is taken as given: it knows when the quota window resets. It used to
    /// be clamped by <see cref="HealthDataRetryOptions.MaxDelay"/>, which turned
    /// <c>Retry-After: 120</c> into a retry after thirty seconds — the comment here said the
    /// header wins and the code made it lose. RFC 9110 §10.2.3 defines the field as how long the
    /// user agent ought to wait, so retrying early is worse than not retrying at all: it arrives
    /// while the service is still shedding load and spends an attempt to do it.
    /// </para>
    /// <para>
    /// <c>MaxDelay</c> now bounds willingness to wait rather than the wait itself. Beyond it the
    /// handler stops and returns the response, so a server asking for an hour cannot park a
    /// request for an hour, and cannot be retried early either.
    /// </para>
    /// </remarks>
    private TimeSpan? ComputeDelay(int attempt, HttpResponseMessage response)
    {
        // Both forms of the header, because RFC 9110 allows either and only one of them lands in
        // Delta. Reading just that one meant an HTTP-date was ignored and the exponential guess
        // used instead.
        if (ServerRequestedDelay(response) is { } requested)
        {
            return requested > _options.MaxDelay ? null : requested;
        }

        // Exponential: base, base*2, base*4...
        var exponential = _options.BaseDelay * Math.Pow(2, attempt - 1);
        var capped = Clamp(exponential);

        if (!_options.UseJitter)
        {
            return capped;
        }

        // Full jitter: a uniform draw over the whole interval, which spreads a thundering herd
        // better than jittering around the target.
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * capped.TotalMilliseconds);
    }

    /// <summary>
    /// The wait the server asked for, in whichever form it sent it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Retry-After</c> is either a delay in seconds or an HTTP-date; <c>RetryConditionHeaderValue</c>
    /// puts them in different properties, so a handler that reads one silently ignores the other.
    /// A date already in the past means "now" rather than a negative delay.
    /// </para>
    /// <para>
    /// An instance method for the clock. It was static and read <c>DateTimeOffset.UtcNow</c>, which
    /// made the class remark about delays going through <see cref="TimeProvider"/> true of the
    /// delay-seconds form and not of this one — and left the HTTP-date arithmetic with no way to be
    /// tested at all.
    /// </para>
    /// </remarks>
    private TimeSpan? ServerRequestedDelay(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private TimeSpan Clamp(TimeSpan delay)
        => delay < TimeSpan.Zero ? TimeSpan.Zero
            : delay > _options.MaxDelay ? _options.MaxDelay
            : delay;
}
