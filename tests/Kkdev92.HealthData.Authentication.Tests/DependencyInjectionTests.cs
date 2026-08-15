using Kkdev92.HealthData.Authentication;
using Kkdev92.HealthData.DependencyInjection;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Names;
using Kkdev92.HealthData.Requests;
using Kkdev92.HealthData.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Kkdev92.HealthData.Authentication.Tests;

/// <summary>
/// Wiring tests for the DependencyInjection package.
/// </summary>
/// <remarks>
/// They live in this assembly rather than a project of their own because everything the DI
/// package composes is authentication: the token provider and the authorization handler.
/// </remarks>
public sealed class DependencyInjectionTests
{
    [Fact]
    public void ResolvesAClient()
    {
        var services = new ServiceCollection();
        services.AddHealthData();
        services.AddHealthDataAccessToken("test-token");

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<HealthDataClient>());
    }

    [Fact]
    public void AppliesClientOptions()
    {
        var services = new ServiceCollection();

        services.AddHealthData(options => options.Client = new HealthDataClientOptions
        {
            MaxErrorResponseBytes = 4096,
            PrettyPrintResponses = true,
        });

        services.AddHealthDataAccessToken("t");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HealthDataClient>();

        Assert.Equal(4096, client.Options.MaxErrorResponseBytes);
        Assert.True(client.Options.PrettyPrintResponses);
    }

    [Fact]
    public void FailsLoudlyWhenNoTokenProviderIsRegistered()
    {
        // No token provider is defaulted on purpose. The only "safe" default would be to send no
        // credentials, which surfaces as a 401 from the service on the first real call. Failing
        // while the client is being composed points at the actual mistake instead.
        var services = new ServiceCollection();
        services.AddHealthData();

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            provider.GetRequiredService<HealthDataClient>);

        Assert.Contains(nameof(IHealthDataAccessTokenProvider), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Counts attempts and always fails with a retryable status.</summary>
    private sealed class CountingFailureHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            LastAuthorization = request.Headers.Authorization?.ToString();

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (HealthDataClient Client, CountingFailureHandler Primary) BuildClient(
        Action<HealthDataBuilderOptions>? configure)
    {
        var primary = new CountingFailureHandler();
        var services = new ServiceCollection();

        services.AddHealthData(configure).ConfigurePrimaryHttpMessageHandler(() => primary);
        services.AddHealthDataAccessToken("test-token");

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<HealthDataClient>(), primary);
    }

    [Fact]
    public async Task InstallsNoRetryHandlerByDefault()
    {
        // Retry is opt-in: a client that silently re-sends writes is a liability on an API that
        // stores health data.
        var (client, primary) = BuildClient(configure: null);

        await Assert.ThrowsAsync<HealthDataApiException>(() => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.Me.Profile },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, primary.Attempts);
    }

    [Fact]
    public async Task ComposesRetryAroundAuthorizationWhenEnabled()
    {
        var (client, primary) = BuildClient(options => options.Retry = new HealthDataRetryOptions
        {
            MaxAttempts = 3,
            BaseDelay = TimeSpan.Zero,
            UseJitter = false,
        });

        await Assert.ThrowsAsync<HealthDataApiException>(() => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.Me.Profile },
            TestContext.Current.CancellationToken));

        Assert.Equal(3, primary.Attempts);

        // Authorization sits inside retry, so every attempt carries a freshly resolved token
        // rather than reusing one that may have expired between attempts.
        Assert.Equal("Bearer test-token", primary.LastAuthorization);
    }

    [Fact]
    public async Task ADelegateProviderSeesPerRequestState()
    {
        // The shape a multi-user server needs: the token is resolved per call from whatever
        // context the application already has.
        var services = new ServiceCollection();
        services.AddHealthData();
        services.AddScoped<CurrentUser>();

        services.AddHealthDataAccessToken((sp, _, _) =>
            ValueTask.FromResult<HealthDataAccessToken?>(
                new HealthDataAccessToken($"token-for-{sp.GetRequiredService<CurrentUser>().Id}")));

        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Id = "alice";

        var tokenProvider = scope.ServiceProvider.GetRequiredService<IHealthDataAccessTokenProvider>();

        var token = await tokenProvider.GetAccessTokenAsync(
            HealthDataTokenRequest.FromDescriptor(HealthDataGeneratedOperations.UsersGetProfile),
            TestContext.Current.CancellationToken);

        Assert.Equal("token-for-alice", token!.Value);
    }

    [Fact]
    public async Task ATrustedOriginConfiguredHereReachesTheHandler()
    {
        // The handler refuses to send a token anywhere but Google by default. This package is
        // what builds it, so an application wiring the SDK through DI can only declare a proxy
        // if the option is carried across — otherwise the hardening forces it to stop using
        // AddHealthData at all, which is worse than not having the hardening.
        var services = new ServiceCollection();

        services.AddHealthDataAccessToken("ya29.token");
        services.AddHealthData(options =>
        {
            options.AdditionalTrustedOrigins = [new Uri("https://proxy.example.test/")];
            options.Client = new HealthDataClientOptions
            {
                BaseAddress = new Uri("https://proxy.example.test/"),
            };
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<HealthDataClient>();

        // The request reaches the network rather than being refused by the destination check.
        // Any transport outcome is fine; an InvalidOperationException from the check is not.
        var failure = await Record.ExceptionAsync(
            () => client.Users.GetProfileAsync(
                new GetProfileRequest { Name = UserName.Me.Profile },
                TestContext.Current.CancellationToken));

        Assert.False(
            failure is InvalidOperationException { Message: var message }
            && message.Contains("Refusing to send an access token", StringComparison.Ordinal),
            $"The configured origin did not reach the handler: {failure?.Message}");
    }

    private sealed class CurrentUser
    {
        public string Id { get; set; } = string.Empty;
    }
}
