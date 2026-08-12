using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData;

/// <summary>
/// The entry point for the Google Health API.
/// </summary>
/// <remarks>
/// <para>
/// The client does not create or own an <see cref="HttpClient"/>: one is supplied, so its
/// lifetime, handler pipeline and timeouts stay under the application's control.
/// </para>
/// <para>
/// It also holds no credentials. Authorization is a pipeline concern, attached by a delegating
/// handler that reads the operation descriptor from the request (ADR-0007). This is what makes a
/// single client safe to share across users in a server.
/// </para>
/// <para>
/// The resource properties are declared in the generated part of this class, so the shape of the
/// API surface always follows the committed contract.
/// </para>
/// </remarks>
/// <example>
/// Every call needs a token, and this class does not obtain one. The handler below is what puts
/// it on the request; without it the API answers 401 and nothing here would work.
/// <code>
/// var authorization = new HealthDataAuthorizationHandler(tokenProvider)
/// {
///     InnerHandler = new HttpClientHandler(),
/// };
///
/// using var httpClient = new HttpClient(authorization)
/// {
///     BaseAddress = HealthDataApiMetadata.DefaultBaseAddress,
/// };
///
/// var client = new HealthDataClient(httpClient);
///
/// var profile = await client.Users.GetProfileAsync(
///     new GetProfileRequest { Name = "users/me/profile" },
///     cancellationToken);
/// </code>
/// In an application that already has dependency injection, <c>AddHealthData()</c> from
/// <c>Kkdev92.HealthData.DependencyInjection</c> composes the same thing.
/// </example>
public sealed partial class HealthDataClient
{
    /// <summary>Creates a client over the supplied <see cref="HttpClient"/>.</summary>
    public HealthDataClient(HttpClient httpClient)
        : this(httpClient, new HealthDataClientOptions())
    {
    }

    /// <summary>Creates a client with explicit options.</summary>
    public HealthDataClient(HttpClient httpClient, HealthDataClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Transport = new HealthDataTransport(httpClient, options);
        InitializeResources();
    }

    /// <summary>The options this client was created with.</summary>
    public HealthDataClientOptions Options { get; }

    /// <summary>The transport used by generated resources.</summary>
    internal HealthDataTransport Transport { get; }

    /// <summary>Implemented by the generated part of this class.</summary>
    private partial void InitializeResources();
}
