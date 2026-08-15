using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Authentication;

/// <summary>
/// What the SDK tells a token provider about the call it is about to make.
/// </summary>
/// <remarks>
/// Notably absent: any notion of "the current user". Which user a call is for is the
/// application's concept, not the SDK's, and it flows through whatever mechanism the application
/// already uses. A server would typically read it from the ambient request context inside its
/// provider implementation.
/// </remarks>
public sealed class HealthDataTokenRequest
{
    /// <summary>The Discovery operation id, for example <c>health.users.getProfile</c>.</summary>
    public required string OperationId { get; init; }

    /// <summary>The scopes the operation accepts.</summary>
    public required ScopeRequirement Scopes { get; init; }

    /// <summary>
    /// Whether the call needs project credentials rather than end-user consent.
    /// </summary>
    /// <remarks>
    /// <c>projects.subscribers.*</c> is administered with <c>cloud-platform</c>; everything under
    /// <c>users</c> runs on a user's OAuth grant. A provider that returns a user token for a
    /// subscriber call will get a 403 (ADR-0007).
    /// </remarks>
    public required bool RequiresProjectCredentials { get; init; }

    /// <summary>Builds a token request from an operation descriptor.</summary>
    public static HealthDataTokenRequest FromDescriptor(HealthDataOperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new HealthDataTokenRequest
        {
            OperationId = descriptor.Id,

            // The combination comes from the descriptor rather than from a rule applied here.
            // Assuming any-of for everything was this method's previous behaviour, and it
            // misreported the one operation Google documents as needing two scopes together.
            Scopes = ScopeRequirement.For(descriptor),
            RequiresProjectCredentials = descriptor.RequiresProjectCredentials,
        };
    }
}

/// <summary>
/// Supplies the access token for a call.
/// </summary>
/// <remarks>
/// <para>
/// The client owns no credentials. Authorization is attached by a delegating handler that asks
/// this provider per request, which is what makes one client safe to share across users in a
/// server (ADR-0007).
/// </para>
/// <para>
/// Implementations are responsible for caching and refreshing. This SDK deliberately ships no
/// production token store.
/// </para>
/// </remarks>
public interface IHealthDataAccessTokenProvider
{
    /// <summary>Returns the token to use, or <see langword="null"/> to send the request unauthorized.</summary>
    ValueTask<HealthDataAccessToken?> GetAccessTokenAsync(
        HealthDataTokenRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A provider that always returns the same token.
/// </summary>
/// <remarks>
/// For a console tool or a test. Not for a multi-user server: one token shared across every user
/// is exactly what the per-request provider model exists to prevent.
/// </remarks>
public sealed class StaticAccessTokenProvider(HealthDataAccessToken token) : IHealthDataAccessTokenProvider
{
    private readonly HealthDataAccessToken _token = token ?? throw new ArgumentNullException(nameof(token));

    /// <summary>Creates a provider from a raw token value.</summary>
    public StaticAccessTokenProvider(string token)
        : this(new HealthDataAccessToken(token))
    {
    }

    /// <inheritdoc />
    public ValueTask<HealthDataAccessToken?> GetAccessTokenAsync(
        HealthDataTokenRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<HealthDataAccessToken?>(_token);
}

/// <summary>
/// A provider that resolves a token from a delegate.
/// </summary>
/// <remarks>
/// The usual way to bridge to an application's own per-request user context.
/// </remarks>
public sealed class DelegateAccessTokenProvider(
    Func<HealthDataTokenRequest, CancellationToken, ValueTask<HealthDataAccessToken?>> resolve)
    : IHealthDataAccessTokenProvider
{
    private readonly Func<HealthDataTokenRequest, CancellationToken, ValueTask<HealthDataAccessToken?>> _resolve =
        resolve ?? throw new ArgumentNullException(nameof(resolve));

    /// <inheritdoc />
    public ValueTask<HealthDataAccessToken?> GetAccessTokenAsync(
        HealthDataTokenRequest request,
        CancellationToken cancellationToken = default)
        => _resolve(request, cancellationToken);
}
