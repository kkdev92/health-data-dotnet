using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Kkdev92.HealthData.DependencyInjection;
using Kkdev92.HealthData.Http;
using Kkdev92.HealthData.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Kkdev92.HealthData.Authentication.Tests;

/// <summary>
/// The pipeline as it actually composes, exercised end to end rather than by inspection.
/// </summary>
/// <remarks>
/// These exist because the tests that were meant to cover this could not fail. One resolved the
/// token provider straight out of a scope without going through <see cref="IHttpClientFactory"/>
/// at all; the other asserted a retried request still carried a token, using a provider that
/// returned the same string every time. Both passed while the pipeline sent the wrong user's token
/// and resolved it once for three attempts.
/// </remarks>
public sealed class PipelineCompositionTests
{
    /// <summary>Records what the server would have seen.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.ServiceUnavailable;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorizations.Add(request.Headers.Authorization?.Parameter);

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("{}"),
                RequestMessage = request,
            });
        }
    }

    private sealed class CountingProvider : IHealthDataAccessTokenProvider
    {
        public int Calls { get; private set; }

        public ValueTask<HealthDataAccessToken?> GetAccessTokenAsync(
            HealthDataTokenRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<HealthDataAccessToken?>(new HealthDataAccessToken($"token-{Calls}"));
        }
    }

    private sealed class CurrentUser
    {
        public string Name { get; set; } = "unset";
    }

    /// <summary>
    /// Two scopes, two users, and neither may see the other's token.
    /// </summary>
    /// <remarks>
    /// The failure this pins is not subtle once seen: handlers registered into
    /// <see cref="IHttpClientFactory"/> are built in a scope of the factory's own and reused for
    /// the handler's lifetime, so a scoped provider resolved inside one belongs to that scope
    /// forever. Before authorization was composed outside the factory, both requests here carried
    /// the token of a user that never existed.
    /// </remarks>
    [Fact]
    public async Task EachScopeSendsItsOwnUsersToken()
    {
        var recorder = new Recorder();
        var services = new ServiceCollection();

        services.AddHealthData().ConfigurePrimaryHttpMessageHandler(() => recorder);
        services.AddScoped<CurrentUser>();
        services.AddHealthDataAccessToken((sp, _, _) =>
            ValueTask.FromResult<HealthDataAccessToken?>(
                new HealthDataAccessToken(sp.GetRequiredService<CurrentUser>().Name)));

        using var root = services.BuildServiceProvider();

        foreach (var name in new[] { "alice", "bob" })
        {
            using var scope = root.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Name = name;

            await Assert.ThrowsAsync<HealthDataApiException>(() =>
                scope.ServiceProvider.GetRequiredService<HealthDataClient>().Users.GetProfileAsync(
                    new GetProfileRequest { Name = "users/me/profile" },
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(["alice", "bob"], recorder.Authorizations);
    }

    /// <summary>
    /// Every attempt resolves a token again, rather than replaying the first one.
    /// </summary>
    /// <remarks>
    /// Registration order decides which handler wraps which, and the token provider returning a
    /// different value each call is what makes the difference visible — with a fixed token the
    /// wrong order looks exactly like the right one.
    /// </remarks>
    [Fact]
    public async Task EveryRetryAttemptResolvesAFreshToken()
    {
        var recorder = new Recorder();
        var counting = new CountingProvider();
        var services = new ServiceCollection();

        services.AddHealthData(o => o.Retry = new HealthDataRetryOptions
        {
            MaxAttempts = 3,
            BaseDelay = TimeSpan.Zero,
            UseJitter = false,
        }).ConfigurePrimaryHttpMessageHandler(() => recorder);

        services.AddSingleton<IHealthDataAccessTokenProvider>(counting);

        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<HealthDataApiException>(() =>
            provider.GetRequiredService<HealthDataClient>().Users.GetProfileAsync(
                new GetProfileRequest { Name = "users/me/profile" },
                TestContext.Current.CancellationToken));

        Assert.Equal(3, recorder.Authorizations.Count);
        Assert.Equal(3, counting.Calls);
        Assert.Equal(["token-1", "token-2", "token-3"], recorder.Authorizations);
    }

    /// <summary>
    /// A null token means unauthorized, which has to include removing a header already there.
    /// </summary>
    /// <remarks>
    /// The provider's contract says null sends the request unauthorized. Only setting the header
    /// when a token exists is not the same thing: a default header on the client, or an outer
    /// handler, would leave a credential on a request the application declined to authorize.
    /// </remarks>
    [Fact]
    public async Task ANullTokenRemovesAnAuthorizationAlreadyOnTheRequest()
    {
        var recorder = new Recorder { Status = HttpStatusCode.Unauthorized };

        var handler = new HealthDataAuthorizationHandler(new NullProvider()) { InnerHandler = recorder };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://health.googleapis.com") };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v4/users/me/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "left-over-from-somewhere-else");
        request.SetHealthDataOperation(HealthDataGeneratedOperations.UsersGetProfile);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(recorder.Authorizations));
    }

    /// <summary>
    /// Whatever the builder was told to do to the client actually happens to it.
    /// </summary>
    /// <remarks>
    /// Composing the pipeline here instead of asking the factory for a client is what makes the
    /// scope correct, and it also skips the step where the factory applies everything registered
    /// on the builder. Timeout, default headers and the rest were silently dropped, and the type
    /// still compiled and still sent requests — which is the kind of break nothing notices.
    /// </remarks>
    [Fact]
    public void ConfigureHttpClientReachesTheClientTheCallerGets()
    {
        var recorder = new Recorder();
        var services = new ServiceCollection();

        services.AddHealthData()
            .ConfigurePrimaryHttpMessageHandler(() => recorder)
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(7);
                client.DefaultRequestHeaders.Add("X-Configured", "yes");
            });

        services.AddHealthDataAccessToken("t");

        using var provider = services.BuildServiceProvider();
        var http = HttpClientOf(provider.GetRequiredService<HealthDataClient>());

        Assert.Equal(TimeSpan.FromSeconds(7), http.Timeout);
        Assert.True(http.DefaultRequestHeaders.Contains("X-Configured"));
        Assert.Equal(HealthDataApiMetadata.DefaultBaseAddress, http.BaseAddress);
    }

    /// <summary>The client the transport will actually send on.</summary>
    private static HttpClient HttpClientOf(HealthDataClient client)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

        var transport = typeof(HealthDataClient).GetProperty("Transport", Any)!.GetValue(client)!;

        return (HttpClient)transport.GetType()
            .GetFields(Any)
            .First(f => f.FieldType == typeof(HttpClient))
            .GetValue(transport)!;
    }

    private sealed class NullProvider : IHealthDataAccessTokenProvider
    {
        public ValueTask<HealthDataAccessToken?> GetAccessTokenAsync(
            HealthDataTokenRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<HealthDataAccessToken?>(null);
    }
}

/// <summary>
/// Where a bearer token is allowed to go.
/// </summary>
/// <remarks>
/// <para>
/// <c>HealthDataClientOptions.BaseAddress</c> has had an HTTPS floor for a while, and it was not
/// the thing being checked. The transport sends to <c>HttpClient.BaseAddress ?? options.BaseAddress</c>,
/// so a caller who built their own client, or a <c>ConfigureHttpClient</c> that ran after the SDK
/// composed one, decided the destination and never went past that floor. The token went anyway,
/// because the handler attached it on seeing an operation descriptor and looked at nothing else.
/// </para>
/// <para>
/// These pin the destination rather than the option, and they go through a real
/// <see cref="HttpClient"/> so that the URI they assert on is the one that would be sent.
/// </para>
/// </remarks>
public sealed class TokenDestinationTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task<(Exception? Failure, RecordingHandler Handler)> CallAsync(string baseAddress)
    {
        var recording = new RecordingHandler();

        using var httpClient = new HttpClient(
            new HealthDataAuthorizationHandler(new StaticAccessTokenProvider("ya29.token"))
            {
                InnerHandler = recording,
            })
        {
            BaseAddress = new Uri(baseAddress),
        };

        var client = new HealthDataClient(httpClient);

        try
        {
            await client.Users.GetProfileAsync(
                new GetProfileRequest { Name = "users/me/profile" },
                TestContext.Current.CancellationToken);

            return (null, recording);
        }
        catch (Exception exception)
        {
            return (exception, recording);
        }
    }

    /// <summary>A destination the token cannot be sent to is refused, and nothing is sent.</summary>
    [Theory]
    [InlineData("http://example.test/")]              // plaintext, someone else's host
    [InlineData("http://192.0.2.1/")]                 // plaintext, an address rather than a name
    [InlineData("ftp://localhost/")]                  // IsLoopback is true here, which is the trap
    public async Task ATokenIsNotSentToAnUnsafeDestination(string baseAddress)
    {
        var (failure, recording) = await CallAsync(baseAddress);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("Refusing to send an access token", failure!.Message, StringComparison.Ordinal);

        // The point of failing rather than stripping the header: the request never happened.
        Assert.Null(recording.LastUri);
        Assert.Null(recording.LastAuthorization);
    }

    [Theory]
    [InlineData("https://health.googleapis.com/")]
    [InlineData("http://localhost:5001/")]            // a local test server
    [InlineData("http://127.0.0.1:5001/")]
    public async Task ATokenIsSentToASafeDestination(string baseAddress)
    {
        var (failure, recording) = await CallAsync(baseAddress);

        Assert.Null(failure);
        Assert.Equal("Bearer", recording.LastAuthorization?.Scheme);
        Assert.Equal("ya29.token", recording.LastAuthorization?.Parameter);
    }

    /// <summary>
    /// HTTPS is not on its own a reason to hand over a credential.
    /// </summary>
    /// <remarks>
    /// This case used to pass the token: the check asked only whether the destination was HTTPS,
    /// so any host on the internet qualified, and a mistyped or misconfigured base address became
    /// a credential disclosure rather than a failed request. The README's claim that the packages
    /// reach Google and nowhere else was not true while this was allowed.
    /// </remarks>
    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("https://health.googleapis.com.example.test/")]   // a suffix, not the real host
    [InlineData("https://health.googleapis.com:8443/")]           // right host, another port
    public async Task ATokenIsNotSentToAnUntrustedHttpsHost(string baseAddress)
    {
        var (failure, recording) = await CallAsync(baseAddress);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("AdditionalTrustedOrigins", failure!.Message, StringComparison.Ordinal);

        Assert.Null(recording.LastUri);
        Assert.Null(recording.LastAuthorization);
    }

    [Fact]
    public async Task AProxyWorksOnceItsOriginIsDeclared()
    {
        // Custom destinations stay supported — a proxy, an emulator, a recording test server —
        // they just have to be named, so that trusting them is a decision somebody made.
        var recording = new RecordingHandler();

        using var httpClient = new HttpClient(
            new HealthDataAuthorizationHandler(new StaticAccessTokenProvider("ya29.token"))
            {
                AdditionalTrustedOrigins = [new Uri("https://proxy.example.test/")],
                InnerHandler = recording,
            })
        {
            BaseAddress = new Uri("https://proxy.example.test/"),
        };

        var client = new HealthDataClient(httpClient);

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken);

        Assert.Equal("ya29.token", recording.LastAuthorization?.Parameter);
    }

    [Fact]
    public async Task DeclaringOneProxyDoesNotTrustAnother()
    {
        var recording = new RecordingHandler();

        using var httpClient = new HttpClient(
            new HealthDataAuthorizationHandler(new StaticAccessTokenProvider("ya29.token"))
            {
                AdditionalTrustedOrigins = [new Uri("https://proxy.example.test/")],
                InnerHandler = recording,
            })
        {
            BaseAddress = new Uri("https://other.example.test/"),
        };

        var client = new HealthDataClient(httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Users.GetProfileAsync(
                new GetProfileRequest { Name = "users/me/profile" },
                TestContext.Current.CancellationToken));

        Assert.Null(recording.LastAuthorization);
    }

    /// <summary>
    /// The check sees the resolved address, not the one the options were given.
    /// </summary>
    /// <remarks>
    /// This is the case the old floor missed entirely: options say HTTPS, the client says plaintext,
    /// and the client wins.
    /// </remarks>
    [Fact]
    public async Task TheClientsBaseAddressBeatsTheValidatedOption()
    {
        var recording = new RecordingHandler();

        using var httpClient = new HttpClient(
            new HealthDataAuthorizationHandler(new StaticAccessTokenProvider("ya29.token"))
            {
                InnerHandler = recording,
            })
        {
            BaseAddress = new Uri("http://example.test/"),
        };

        // Options that pass their own validation, which is exactly why validating them was not enough.
        var options = new HealthDataClientOptions { BaseAddress = new Uri("https://health.googleapis.com/") };
        var client = new HealthDataClient(httpClient, options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken));

        Assert.Null(recording.LastUri);
    }

    /// <summary>The same, through the container, where ConfigureHttpClient runs after the SDK.</summary>
    [Fact]
    public async Task ConfigureHttpClientCannotRedirectTheTokenToPlaintext()
    {
        var recording = new RecordingHandler();

        var services = new ServiceCollection();
        services.AddHealthDataAccessToken("ya29.token");
        services.AddHealthData()
            .ConfigureHttpClient(c => c.BaseAddress = new Uri("http://example.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => recording);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<HealthDataClient>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken));

        Assert.Null(recording.LastUri);
    }
}
