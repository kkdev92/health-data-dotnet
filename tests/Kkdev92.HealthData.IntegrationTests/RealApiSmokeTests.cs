using Kkdev92.HealthData.DependencyInjection;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Requests;
using Microsoft.Extensions.DependencyInjection;
namespace Kkdev92.HealthData.IntegrationTests;

/// <summary>
/// Tests that talk to the real Google Health API.
/// </summary>
/// <remarks>
/// <para>
/// These never gate a pull request. CI runs
/// <c>--filter "Category!=Integration"</c>; this suite is exercised only by the manual or
/// scheduled <c>integration.yml</c> workflow.
/// </para>
/// <para>
/// The errors catalog documents <c>API_PRIVATE_PREVIEW_ACCESS_DENIED</c>, so access may require
/// Google-side allowlisting. Every test here must skip cleanly when credentials are absent
/// rather than fail.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RealApiSmokeTests
{
    private static bool HasCredentials
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLEHEALTH_ACCESS_TOKEN"));

    /// <summary>
    /// Reads one page of one data type, and asserts that the SDK understood the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to skip without credentials and, with them, run a body that called nothing — so a
    /// weekly green tick meant only that the job had started. A check that cannot fail is worse
    /// than no check, because it is mistaken for one.
    /// </para>
    /// <para>
    /// Read-only and one request: a smoke test on somebody's health record has no business
    /// writing, and paginating would spend their quota to learn nothing more.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReadsOnePageOfSteps()
    {
        Assert.SkipUnless(HasCredentials, "GOOGLEHEALTH_ACCESS_TOKEN is not set; skipping real API smoke test.");

        var services = new ServiceCollection();
        services.AddHealthData();
        services.AddHealthDataAccessToken(Environment.GetEnvironmentVariable("GOOGLEHEALTH_ACCESS_TOKEN")!);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<HealthDataClient>();

        var response = await client.Users.DataPoints.ListAsync(
            new ListDataPointsRequest
            {
                Parent = "users/me/dataTypes/steps",
                PageSize = 1,
            },
            TestContext.Current.CancellationToken);

        // Not "there is data" — an account may legitimately have none. What is being checked is
        // that the request was accepted and the response deserialized into the contract's shape.
        Assert.NotNull(response);

        foreach (var point in response.DataPoints ?? [])
        {
            // If a point came back, the union has to resolve to something this contract knows,
            // or to Unknown — never to a member that is set but unreadable.
            Assert.NotEqual(DataPointKind.None, point.GetKind());
        }
    }
}
