using System.Net.Http.Headers;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Authentication;

/// <summary>
/// Attaches the access token for each outgoing request.
/// </summary>
/// <remarks>
/// <para>
/// The token is chosen per request from the operation descriptor, not held on the client. That is
/// what lets one client serve many users without a token ever becoming shared state
/// (ADR-0007).
/// </para>
/// <para>
/// A request that carries no descriptor did not come from this SDK, and is left untouched rather
/// than being given a token it may not be entitled to.
/// </para>
/// </remarks>
public sealed class HealthDataAuthorizationHandler(IHealthDataAccessTokenProvider tokenProvider) : DelegatingHandler
{
    private readonly IHealthDataAccessTokenProvider _tokenProvider =
        tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    /// <summary>
    /// Origins a token may be sent to besides Google's, for a proxy or a test server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty by default, which confines every access token this handler attaches to
    /// <see cref="HealthDataApiMetadata.DefaultBaseAddress"/> or a loopback address. Requiring
    /// HTTPS alone was not enough: it accepts any host on the internet, so a mistyped or
    /// misconfigured base address turned a configuration error into a credential disclosure.
    /// </para>
    /// <para>
    /// Only the origin is compared — scheme, host and port. A path here has no effect, because the
    /// question is which server receives the token, not which route on it.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<Uri> AdditionalTrustedOrigins { get; init; } = [];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = request.GetHealthDataOperation();

        if (descriptor is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Checked here because here is the only place the destination is settled. HealthDataClientOptions
        // validates its BaseAddress, but that property is not what the request is sent to: HttpClient's own
        // BaseAddress wins over it, ConfigureHttpClient can set that after the SDK composed the client, and a
        // caller building their own HttpClient never goes through the options at all. Validating the option
        // was validating a suggestion.
        RequireSecureDestination(request.RequestUri);

        var token = await _tokenProvider
            .GetAccessTokenAsync(HealthDataTokenRequest.FromDescriptor(descriptor), cancellationToken)
            .ConfigureAwait(false);

        // Assigned either way. Null means "send this unauthorized", which has to include removing a
        // header that is already there — a default header on the client, or an outer handler, would
        // otherwise leave a credential on a request the application declined to authorize. Assigning
        // rather than appending also stops a stale header from a retried request winning.
        request.Headers.Authorization = token is null
            ? null
            : new AuthenticationHeaderValue("Bearer", token.Value);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses to put a bearer token on a request that is not going somewhere it can be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HTTPS, or plain HTTP to loopback for a local test server. Loopback alone is not enough:
    /// <see cref="Uri.IsLoopback"/> is true for <c>ftp://localhost</c> and <c>file://localhost</c>
    /// as well, and "the host is my machine" says nothing about whether the scheme will put the
    /// token on a wire in the clear.
    /// </para>
    /// <para>
    /// This throws rather than sending the request unauthorized. A missing header comes back as a
    /// 401 from somewhere far away from the mistake, whereas a configuration that would leak a
    /// credential should fail where it was configured.
    /// </para>
    /// </remarks>
    private void RequireSecureDestination(Uri? destination)
    {
        if (destination is not { IsAbsoluteUri: true }
            || (destination.Scheme != Uri.UriSchemeHttps
                && !(destination.Scheme == Uri.UriSchemeHttp && destination.IsLoopback)))
        {
            throw new InvalidOperationException(
                $"Refusing to send an access token to '{Describe(destination)}'. Requests carrying a token "
                + "must be HTTPS, or plain HTTP to a loopback address for a local test server. Check "
                + "HttpClient.BaseAddress, and any ConfigureHttpClient that sets it.");
        }

        // Loopback stays allowed without configuration: a token sent to this machine has not left
        // it, and every local test server would otherwise need registering.
        if (destination.IsLoopback ||
            IsSameOrigin(destination, HealthDataApiMetadata.DefaultBaseAddress) ||
            AdditionalTrustedOrigins.Any(origin => IsSameOrigin(destination, origin)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to send an access token to '{Describe(destination)}'. It is a valid HTTPS "
            + $"address but not one this handler trusts with a credential: only "
            + $"{Describe(HealthDataApiMetadata.DefaultBaseAddress)}, a loopback address, and "
            + "anything listed in AdditionalTrustedOrigins. If the address is deliberate — a "
            + "proxy, or a service emulator — add its origin to AdditionalTrustedOrigins.");
    }

    /// <summary>Whether two addresses name the same server.</summary>
    /// <remarks>
    /// Compared field by field rather than with <c>GetLeftPart(UriPartial.Authority)</c>, which
    /// includes userinfo: <c>https://attacker@health.googleapis.com</c> would otherwise fail to
    /// match while still resolving to Google, and the reverse trick is worse.
    /// <see cref="Uri.IdnHost"/> rather than <see cref="Uri.Host"/> so a Unicode spelling of the
    /// host cannot present as a different origin.
    /// </remarks>
    private static bool IsSameOrigin(Uri destination, Uri origin)
        => origin.IsAbsoluteUri
           && string.Equals(destination.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(destination.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
           && destination.Port == origin.Port;

    /// <summary>
    /// Names an address well enough to fix it, without repeating a credential someone put in it.
    /// </summary>
    /// <remarks>
    /// <c>GetLeftPart(UriPartial.Authority)</c> was the obvious choice and the wrong one: the
    /// authority includes the userinfo component, so
    /// <c>http://user:secret@example.test</c> came back whole. The misconfiguration this method
    /// exists to complain about is exactly the one where a credential ends up in a URI, and the
    /// complaint would have written it to a log.
    /// </remarks>
    private static string Describe(Uri? destination)
    {
        if (destination is not { IsAbsoluteUri: true })
        {
            return "(no address)";
        }

        var port = destination.IsDefaultPort ? string.Empty : $":{destination.Port}";

        return $"{destination.Scheme}://{destination.IdnHost}{port}";
    }
}
