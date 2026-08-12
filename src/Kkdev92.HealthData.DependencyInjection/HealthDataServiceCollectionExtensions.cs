using Kkdev92.HealthData.Authentication;
using Kkdev92.HealthData.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Kkdev92.HealthData.DependencyInjection;

/// <summary>
/// Options for <see cref="HealthDataServiceCollectionExtensions.AddHealthData"/>.
/// </summary>
public sealed class HealthDataBuilderOptions
{
    /// <summary>Options for the client itself.</summary>
    public HealthDataClientOptions Client { get; set; } = new();

    /// <summary>
    /// Retry options, or <see langword="null"/> to install no retry handler.
    /// </summary>
    /// <remarks>
    /// Null by default. Retry is opt-in because writes must never be resent silently.
    /// </remarks>
    public HealthDataRetryOptions? Retry { get; set; }

    /// <summary>
    /// Origins an access token may be sent to besides Google's, for a proxy or an emulator.
    /// </summary>
    /// <remarks>
    /// Empty by default, matching <see cref="HealthDataAuthorizationHandler"/>. Registered here
    /// as well because this package builds the handler, and a hardening the composition root
    /// cannot reach is one an application has to abandon the composition root to use.
    /// </remarks>
    public IReadOnlyCollection<Uri> AdditionalTrustedOrigins { get; set; } = [];
}

/// <summary>
/// Registers <see cref="HealthDataClient"/> with dependency injection.
/// </summary>
/// <remarks>
/// <para>
/// This package exists so the core library does not have to reference
/// <c>Microsoft.Extensions.*</c>. Core keeps zero third-party
/// runtime dependencies; the integration lives here.
/// </para>
/// <para>
/// Retry and authorization are composed around the factory's pipeline rather than registered
/// into it, and both parts of that matter.
/// </para>
/// <code>
/// HealthDataClient (scoped)
///    -> HealthDataRetryHandler        (optional, outermost)
///       -> HealthDataAuthorizationHandler
///          -> IHttpMessageHandlerFactory pipeline  (pooled, shared)
///             -> HttpClientHandler
/// </code>
/// <para>
/// Authorization is built here, in the caller's scope, because <see cref="IHttpClientFactory"/>
/// creates message handlers in a scope of its own and reuses them across requests for the
/// handler's lifetime. A scoped <see cref="IHealthDataAccessTokenProvider"/> resolved inside that
/// pipeline therefore belongs to the factory's scope and not to the request's — on a multi-user
/// server, the token sent is not the caller's. Composing outside the factory is the arrangement
/// Microsoft documents for exactly this case.
/// </para>
/// <para>
/// Retry wraps authorization so that each attempt asks for a token again, rather than replaying
/// one that may have expired between attempts. The connection pooling, handler lifetime and
/// rotation all remain the factory's; only these two handlers sit outside it.
/// </para>
/// </remarks>
public static class HealthDataServiceCollectionExtensions
{
    /// <summary>The named <see cref="HttpClient"/> the SDK uses.</summary>
    public const string HttpClientName = "Kkdev92.HealthData";

    /// <summary>
    /// Registers the client, its named <see cref="HttpClient"/> and the authorization handler.
    /// </summary>
    /// <remarks>
    /// An <see cref="IHealthDataAccessTokenProvider"/> must be registered separately. It is not
    /// defaulted, because the only safe default would be "send no credentials", which fails at
    /// the service rather than at startup.
    /// </remarks>
    public static IHttpClientBuilder AddHealthData(
        this IServiceCollection services,
        Action<HealthDataBuilderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new HealthDataBuilderOptions();
        configure?.Invoke(options);

        services.AddSingleton(options.Client);

        var builder = services.AddHttpClient(HttpClientName);

        // Scoped, so the token provider below is resolved from the caller's scope.
        services.AddScoped(provider =>
        {
            // The factory keeps owning this: pooling, lifetime and rotation are unchanged. It is
            // only wrapped, never replaced, and never disposed from here.
            HttpMessageHandler pipeline = provider
                .GetRequiredService<IHttpMessageHandlerFactory>()
                .CreateHandler(HttpClientName);

            pipeline = new HealthDataAuthorizationHandler(
                provider.GetRequiredService<IHealthDataAccessTokenProvider>())
            {
                AdditionalTrustedOrigins = options.AdditionalTrustedOrigins,
                InnerHandler = pipeline,
            };

            if (options.Retry is { } retry)
            {
                // Outermost, so a retried attempt runs authorization again instead of replaying
                // the token the first attempt happened to get.
                pipeline = new HealthDataRetryHandler(retry, provider.GetService<TimeProvider>())
                {
                    InnerHandler = pipeline,
                };
            }

            // disposeHandler: false — the factory's handler outlives this client, and disposing it
            // would tear down a pipeline other scopes are still using.
            var client = new HttpClient(pipeline, disposeHandler: false)
            {
                BaseAddress = options.Client.BaseAddress,
            };

            // What IHttpClientFactory.CreateClient would have done to it. Building the client here
            // rather than asking the factory for one is what makes the scope correct, and it also
            // skips this step — so ConfigureHttpClient, and anything else registered on the
            // builder, silently did nothing. Applied after BaseAddress so a caller can override it.
            foreach (var configure in provider
                .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get(HttpClientName)
                .HttpClientActions)
            {
                configure(client);
            }

            return new HealthDataClient(client, provider.GetRequiredService<HealthDataClientOptions>());
        });

        return builder;
    }

    /// <summary>Registers a token provider resolved from a delegate.</summary>
    /// <remarks>
    /// Scoped, so the delegate can read whatever per-request context the application already has,
    /// such as the signed-in user.
    /// </remarks>
    public static IServiceCollection AddHealthDataAccessToken(
        this IServiceCollection services,
        Func<IServiceProvider, HealthDataTokenRequest, CancellationToken, ValueTask<HealthDataAccessToken?>> resolve)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolve);

        services.AddScoped<IHealthDataAccessTokenProvider>(provider =>
            new DelegateAccessTokenProvider((request, cancellationToken) =>
                resolve(provider, request, cancellationToken)));

        return services;
    }

    /// <summary>
    /// Registers a single fixed access token.
    /// </summary>
    /// <remarks>
    /// For a console tool or a test. A multi-user server must not share one token across users.
    /// </remarks>
    public static IServiceCollection AddHealthDataAccessToken(this IServiceCollection services, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        services.AddSingleton<IHealthDataAccessTokenProvider>(new StaticAccessTokenProvider(accessToken));
        return services;
    }
}
