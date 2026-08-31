namespace Kkdev92.HealthData;

/// <summary>
/// Options for <see cref="HealthDataClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately minimal. Credentials never appear here: the client does not own them
/// (ADR-0007), and a token on a shared client is a data-leak hazard in a multi-user server.
/// </para>
/// <para>
/// There is no timeout here either, and that is not an omission. The timeout is the
/// <see cref="HttpClient"/>'s — <c>httpClient.Timeout</c>, or a
/// <see cref="System.Threading.CancellationToken"/> per call, both of which this SDK passes
/// straight through. A second timeout on this object would be a second answer to the same
/// question, and the one that lost would be silent.
/// </para>
/// </remarks>
public sealed class HealthDataClientOptions
{
    /// <summary>The service endpoint. Defaults to <c>https://health.googleapis.com/</c>.</summary>
    /// <remarks>
    /// Used only when the supplied <see cref="HttpClient"/> has no base address of its own.
    /// </remarks>
    public Uri BaseAddress
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!value.IsAbsoluteUri)
            {
                throw new ArgumentException("The base address must be absolute.", nameof(BaseAddress));
            }

            // HTTPS, or plain HTTP to loopback for a local test server. Every request the SDK sends
            // here carries a bearer token for somebody's health record, and a plaintext host would
            // put it on the wire in the clear. This property exists to be overridden, which is
            // exactly why it needs a floor.
            //
            // The floor that matters is enforced again in HealthDataAuthorizationHandler, because
            // this property is not what a request is necessarily sent to.
            if (!Http.SecureUri.IsHttpsOrLoopback(value))
            {
                throw new ArgumentException(
                    $"'{Http.SecureUri.Describe(value)}' is not HTTPS. Requests to this address carry an "
                    + "access token; use HTTPS, or a loopback address for a local test server.",
                    nameof(BaseAddress));
            }

            field = value;
        }
    } = HealthDataApiMetadata.DefaultBaseAddress;

    /// <summary>
    /// The maximum number of bytes read from an error response body.
    /// </summary>
    /// <remarks>
    /// An error body is attacker-influenced in the general case and must not be buffered without
    /// a bound.
    /// </remarks>
    public int MaxErrorResponseBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// The clock. Defaults to <see cref="System.TimeProvider.System"/>.
    /// </summary>
    /// <remarks>
    /// Only one thing reads it: a <c>Retry-After</c> sent as an HTTP-date has to be turned into a
    /// duration, and that subtraction needs a now. It was <see cref="DateTimeOffset.UtcNow"/>,
    /// which left the arithmetic with no way to be tested — the same defect the retry handler had
    /// and already fixed by taking a <see cref="System.TimeProvider"/>.
    /// </remarks>
    public TimeProvider TimeProvider
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = TimeProvider.System;

    /// <summary>
    /// Whether to let the service pretty-print response JSON. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Google's system parameter <c>prettyPrint</c> defaults to <c>true</c> server-side, which
    /// indents every response. Measured against the Google Health Discovery endpoint on
    /// 2026-08-10, turning it off cut the payload from 282,943 to 207,058 bytes, a 26.8%
    /// reduction. A second Google endpoint showed 16.3%.
    /// </para>
    /// <para>
    /// The difference is whitespace only, so this SDK opts out by default: fewer bytes to
    /// transfer and fewer to skip while parsing. Set this to <see langword="true"/> to send
    /// nothing and accept the service default, which is useful when capturing traffic by hand.
    /// </para>
    /// </remarks>
    public bool PrettyPrintResponses { get; init; }
}
