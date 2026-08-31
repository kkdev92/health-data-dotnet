using System.Net;

using Kkdev92.HealthData.TestSupport;

namespace Kkdev92.HealthData.ContractTests;

/// <summary>
/// Verifies the test double itself, so that the contract assertions built on it can be trusted.
/// </summary>
public sealed class FakeHttpMessageHandlerTests
{
    [Fact]
    public async Task RecordsMethodRelativeUrlAndBody()
    {
        using var handler = FakeHttpMessageHandler.Responding(HttpStatusCode.OK, """{"ok":true}""");
        using var client = new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };

        using var content = JsonContent.Of("""{"age":42}""");
        using var response = await client.PatchAsync("v4/users/me/profile?updateMask=age", content, TestContext.Current.CancellationToken);

        var recorded = handler.SingleRequest;
        Assert.Equal(HttpMethod.Patch, recorded.Method);
        Assert.Equal("v4/users/me/profile?updateMask=age", recorded.RelativeUrl);
        Assert.Equal("""{"age":42}""", recorded.Body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PreservesQueryCasingExactly()
    {
        // Wire query names must never be reshaped by a naming rule. If this ever reports
        // page_size, the URI builder has rewritten a wire name.
        using var handler = FakeHttpMessageHandler.Responding(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };

        using var response = await client.GetAsync(
            "v4/users/me/dataTypes/heart-rate/dataPoints?pageSize=1000&pageToken=abc&dataSourceFamily=x",
            TestContext.Current.CancellationToken);

        Assert.Contains("pageSize=1000", handler.SingleRequest.RelativeUrl, StringComparison.Ordinal);
        Assert.Contains("pageToken=abc", handler.SingleRequest.RelativeUrl, StringComparison.Ordinal);
        Assert.Contains("dataSourceFamily=x", handler.SingleRequest.RelativeUrl, StringComparison.Ordinal);
    }
}
