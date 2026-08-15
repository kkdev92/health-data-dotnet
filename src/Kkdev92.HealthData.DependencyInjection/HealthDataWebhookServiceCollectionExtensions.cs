using Kkdev92.HealthData.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace Kkdev92.HealthData.DependencyInjection;

/// <summary>
/// Options for <see cref="HealthDataWebhookServiceCollectionExtensions.AddHealthDataWebhooks"/>.
/// </summary>
public sealed class HealthDataWebhookOptions
{
    /// <summary>
    /// Where the signing keys are fetched from. Defaults to Google's published keyset.
    /// </summary>
    /// <remarks>
    /// Overridden by a test that serves its own keyset. Anything but HTTPS or loopback is refused
    /// by the provider: this URI decides which public key is trusted to verify a signature.
    /// </remarks>
    public Uri? KeysetUri { get; set; }

    /// <summary>How long a fetched keyset is used before it is fetched again.</summary>
    public TimeSpan? CacheDuration { get; set; }

    /// <summary>The floor between refreshes prompted by an unrecognized key id.</summary>
    /// <remarks>
    /// Without one, a flood of forged signatures naming random key ids turns this application into
    /// a request amplifier against Google's keyset host.
    /// </remarks>
    public TimeSpan? MinimumRefreshInterval { get; set; }

    /// <summary>
    /// How stale a keyset may be before verification fails rather than continuing on it.
    /// </summary>
    /// <remarks>
    /// Google rotates the keys every thirty days. Past this age, a keyset nobody has been able to
    /// reconfirm stops being trusted — the alternative is verifying signatures against keys that
    /// may already have been withdrawn.
    /// </remarks>
    public TimeSpan? MaximumStaleAge { get; set; }

    /// <summary>
    /// The credentials to accept on an incoming notification.
    /// </summary>
    /// <remarks>
    /// More than one exists for rotation: the subscriber's secret changes at Google and in the
    /// application at two different moments, and notifications keep arriving in between. Leave
    /// empty and register the receiver yourself if the secret is resolved per request.
    /// </remarks>
    public IList<string> EndpointSecrets { get; } = [];
}

/// <summary>
/// Registers the webhook receiving side.
/// </summary>
/// <remarks>
/// <para>
/// The pieces have lifetimes that are not obvious from their constructors, and every application
/// receiving webhooks had to work them out: the key provider holds the cache so it is a singleton,
/// it needs an <see cref="HttpClient"/> that outlives a request, and it is
/// <see cref="IDisposable"/>. Four blocks of wiring in the one application built on this SDK, each
/// of which could be written a plausible wrong way — a scoped provider refetches the keyset per
/// request, a new <c>HttpClient</c> per provider exhausts sockets.
/// </para>
/// <para>
/// This lives in the dependency-injection package rather than in
/// <c>Kkdev92.HealthData.Webhooks</c> on purpose. That package has no third-party dependencies at
/// all, which is what lets it be referenced from anywhere; the cost is that referencing this one
/// brings the webhook assembly along even for an application that only reads.
/// </para>
/// </remarks>
public static class HealthDataWebhookServiceCollectionExtensions
{
    /// <summary>The name of the HTTP client the keyset is fetched with.</summary>
    /// <remarks>
    /// Named, so an application can configure it — a proxy, a timeout, a resilience handler —
    /// without replacing anything registered here.
    /// </remarks>
    public const string KeysetHttpClientName = "Kkdev92.HealthData.Webhooks.Keyset";

    /// <summary>
    /// Adds the keyset provider, the signature verifier, and a receiver.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Options, or null for the defaults.</param>
    /// <returns>The builder for the keyset client, so it can be configured further.</returns>
    /// <remarks>
    /// The receiver is registered only when a secret is configured. Without one it would refuse
    /// every notification, and a receiver that refuses everything is worse than no registration:
    /// it resolves, it runs, and it answers 401 to Google's verification challenge.
    /// </remarks>
    public static IHttpClientBuilder AddHealthDataWebhooks(
        this IServiceCollection services,
        Action<HealthDataWebhookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new HealthDataWebhookOptions();
        configure?.Invoke(options);

        var builder = services.AddHttpClient(KeysetHttpClientName);

        // Singleton because it is the cache. Resolved per scope, each request would fetch the
        // keyset again — thirty-day-old keys re-downloaded per notification.
        services.AddSingleton(provider => new HealthDataWebhookKeyProvider(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(KeysetHttpClientName),
            options.KeysetUri,
            options.CacheDuration,
            options.MinimumRefreshInterval,
            provider.GetService<TimeProvider>(),
            options.MaximumStaleAge));

        services.AddSingleton(provider => new HealthDataWebhookSignatureVerifier(
            provider.GetRequiredService<HealthDataWebhookKeyProvider>()));

        if (options.EndpointSecrets.Count > 0)
        {
            var secrets = options.EndpointSecrets.ToArray();

            services.AddSingleton(provider => new HealthDataWebhookReceiver(
                provider.GetRequiredService<HealthDataWebhookSignatureVerifier>(),
                secrets));
        }

        return builder;
    }
}
