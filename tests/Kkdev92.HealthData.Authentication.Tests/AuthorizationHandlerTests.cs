using System.Net;
using Kkdev92.HealthData.Authentication;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Authentication.Tests;

/// <summary>
/// The token is chosen per request from the operation descriptor, never held on the client.
/// </summary>
public sealed class AuthorizationHandlerTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string?> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (HealthDataClient Client, CapturingHandler Handler) CreateClient(
        IHealthDataAccessTokenProvider provider)
    {
        var inner = new CapturingHandler();
        var auth = new HealthDataAuthorizationHandler(provider) { InnerHandler = inner };
        var httpClient = new HttpClient(auth) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };

        return (new HealthDataClient(httpClient), inner);
    }

    [Fact]
    public async Task AttachesTheBearerToken()
    {
        var (client, handler) = CreateClient(new StaticAccessTokenProvider("test-token"));

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken);

        Assert.Equal("Bearer test-token", handler.AuthorizationHeaders[0]);
    }

    [Fact]
    public async Task TheProviderSeesTheOperationAndItsScopes()
    {
        HealthDataTokenRequest? observed = null;

        var provider = new DelegateAccessTokenProvider((request, _) =>
        {
            observed = request;
            return ValueTask.FromResult<HealthDataAccessToken?>(new HealthDataAccessToken("t"));
        });

        var (client, _) = CreateClient(provider);

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal("health.users.getProfile", observed!.OperationId);
        Assert.False(observed.RequiresProjectCredentials);
        Assert.Contains(HealthDataScopes.ProfileReadonly, observed.Scopes.Scopes);

        // A Discovery scopes array means any one of them is accepted.
        Assert.Equal(ScopeCombination.AnyOf, observed.Scopes.Combination);
    }

    [Fact]
    public async Task DistinguishesProjectCredentialsFromUserConsent()
    {
        // The whole reason a single token field will not do (ADR-0007).
        var seen = new List<(string Operation, bool Project)>();

        var provider = new DelegateAccessTokenProvider((request, _) =>
        {
            seen.Add((request.OperationId, request.RequiresProjectCredentials));
            return ValueTask.FromResult<HealthDataAccessToken?>(new HealthDataAccessToken("t"));
        });

        var (client, _) = CreateClient(provider);

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken);

        await client.Projects.Subscribers.ListAsync(
            new ListSubscribersRequest { Parent = "projects/p" },
            TestContext.Current.CancellationToken);

        Assert.Equal(("health.users.getProfile", false), seen[0]);
        Assert.Equal(("health.projects.subscribers.list", true), seen[1]);
    }

    [Fact]
    public async Task ResolvesATokenPerRequestRatherThanCachingOne()
    {
        // A server serving many users must not reuse the first user's token.
        var counter = 0;

        var provider = new DelegateAccessTokenProvider((_, _) =>
            ValueTask.FromResult<HealthDataAccessToken?>(
                new HealthDataAccessToken($"token-{Interlocked.Increment(ref counter)}")));

        var (client, handler) = CreateClient(provider);

        for (var i = 0; i < 3; i++)
        {
            await client.Users.GetProfileAsync(
                new GetProfileRequest { Name = "users/me/profile" },
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Bearer token-1", "Bearer token-2", "Bearer token-3"], handler.AuthorizationHeaders);
    }

    [Fact]
    public async Task SendsUnauthorizedWhenTheProviderReturnsNothing()
    {
        var provider = new DelegateAccessTokenProvider((_, _) =>
            ValueTask.FromResult<HealthDataAccessToken?>(null));

        var (client, handler) = CreateClient(provider);

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken);

        Assert.Null(handler.AuthorizationHeaders[0]);
    }

    [Fact]
    public async Task LeavesForeignRequestsAlone()
    {
        // No descriptor means the request did not come from this SDK, so nothing is known about
        // what it is entitled to.
        var inner = new CapturingHandler();
        var auth = new HealthDataAuthorizationHandler(new StaticAccessTokenProvider("t")) { InnerHandler = inner };

        using var httpClient = new HttpClient(auth) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        using var response = await httpClient.GetAsync(new Uri("https://health.googleapis.com/other"), TestContext.Current.CancellationToken);

        Assert.Null(inner.AuthorizationHeaders[0]);
    }
}

public sealed class AccessTokenTests
{
    [Fact]
    public void ToStringNeverContainsTheToken()
    {
        var token = new HealthDataAccessToken("ya29.super-secret", DateTimeOffset.UnixEpoch);

        // Tokens reach logs by accident far more often than on purpose.
        Assert.DoesNotContain("super-secret", token.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiryAccountsForClockSkew()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var token = new HealthDataAccessToken("t", now.AddSeconds(30));

        // Still 30 seconds of life left, but not enough to survive the round trip.
        Assert.True(token.IsExpired(now));
        Assert.False(token.IsExpired(now, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ATokenWithNoExpiryNeverExpires()
        => Assert.False(new HealthDataAccessToken("t").IsExpired(DateTimeOffset.MaxValue));

    [Fact]
    public void RejectsAnEmptyToken()
        => Assert.Throws<ArgumentException>(() => new HealthDataAccessToken("  "));
}

public sealed class ScopeRequirementTests
{
    [Fact]
    public void AnyOfIsSatisfiedByASingleMatch()
    {
        var requirement = ScopeRequirement.AnyOf("a", "b", "c");

        Assert.True(requirement.IsSatisfiedBy(["b"]));
        Assert.False(requirement.IsSatisfiedBy(["d"]));
    }

    [Fact]
    public void AllOfNeedsEveryScope()
    {
        var requirement = ScopeRequirement.AllOf("a", "b");

        Assert.True(requirement.IsSatisfiedBy(["a", "b", "c"]));
        Assert.False(requirement.IsSatisfiedBy(["a"]));
    }

    [Fact]
    public void AnEmptyRequirementIsAlwaysSatisfied()
        => Assert.True(ScopeRequirement.AnyOf().IsSatisfiedBy([]));

    [Fact]
    public void ExportingTcxNeedsActivityAndLocationTogether()
    {
        // Google's per-method page for exportExerciseTcx says outright that the any-one-of rule
        // does not apply to it: an activity-and-fitness scope AND a location scope must both be
        // in the token. A token carrying only one of them is rejected by the service, so the SDK
        // must not report it as sufficient. Verified against that page on 2026-08-12.
        var request = HealthDataTokenRequest.FromDescriptor(
            HealthDataGeneratedOperations.UsersDataTypesDataPointsExportExerciseTcx);

        Assert.Equal(ScopeCombination.AllOf, request.Scopes.Combination);

        Assert.False(request.Scopes.IsSatisfiedBy([HealthDataScopes.ActivityAndFitnessReadonly]));
        Assert.False(request.Scopes.IsSatisfiedBy([HealthDataScopes.LocationReadonly]));

        Assert.True(request.Scopes.IsSatisfiedBy(
            [HealthDataScopes.ActivityAndFitnessReadonly, HealthDataScopes.LocationReadonly]));
    }

    [Fact]
    public void ExportingTcxIsTheOnlyOperationThatNeedsACombination()
    {
        // The exception is documented for exactly one method. Keeping the rest at any-of is what
        // stops a fix for one operation from silently demanding extra consent everywhere else.
        // An earlier version of this test asserted that *no* operation needed a combination, and
        // it passed for as long as the contract was wrong.
        var combined = HealthDataGeneratedOperations.All
            .Where(d => HealthDataTokenRequest.FromDescriptor(d).Scopes.Combination
                        == ScopeCombination.AllOf)
            .Select(d => d.Id)
            .ToArray();

        Assert.Equal(["health.users.dataTypes.dataPoints.exportExerciseTcx"], combined);
    }

    [Fact]
    public void ADescriptorThatSaysNothingStillMeansAnyOf()
    {
        // The combination is a property with a default rather than a required member, so a
        // hand-built descriptor keeps the Discovery convention instead of failing to compile or
        // silently demanding every scope at once.
        var descriptor = new HealthDataOperationDescriptor
        {
            Id = "test.operation",
            ApiVersion = "v4",
            Method = HttpMethod.Get,
            PathTemplate = "v4/test",
            Scopes = ["a", "b"],
            RequiresProjectCredentials = false,
            RetryClassification = RetryClassification.Safe,
            ResponseKind = ResponseKind.Json,
            Pagination = PaginationKind.None,
        };

        var request = HealthDataTokenRequest.FromDescriptor(descriptor);

        Assert.Equal(ScopeCombination.AnyOf, request.Scopes.Combination);
        Assert.True(request.Scopes.IsSatisfiedBy(["a"]));
    }
}
