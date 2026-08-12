using System.Net;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.ContractTests;

/// <summary>
/// Asserts the exact wire contract of every generated operation.
/// </summary>
/// <remarks>
/// This is the test that matters most in the repository. Everything else can be re-derived; a
/// wrong URL or a reshaped query name is a silent, production-only failure.
/// </remarks>
public sealed class OperationContractTests
{
    private static (HealthDataClient Client, FakeHttpMessageHandler Handler) CreateClient(string responseBody = "{}")
    {
        var handler = FakeHttpMessageHandler.Responding(HttpStatusCode.OK, responseBody);
        var httpClient = new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };

        // System parameters are transport policy, not part of an operation's contract. Opting
        // out of the prettyPrint parameter here keeps each assertion below about the operation
        // itself; the parameter has its own tests in RuntimeBehaviourTests.
        var options = new HealthDataClientOptions { PrettyPrintResponses = true };

        return (new HealthDataClient(httpClient, options), handler);
    }

    private static void AssertRequest(
        FakeHttpMessageHandler handler,
        HttpMethod expectedMethod,
        string expectedRelativeUrl)
    {
        var recorded = handler.SingleRequest;
        Assert.Equal(expectedMethod, recorded.Method);
        Assert.Equal(expectedRelativeUrl, recorded.RelativeUrl);
    }

    // ---------- users ----------

    [Fact]
    public async Task GetProfile()
    {
        var (client, handler) = CreateClient();
        await client.Users.GetProfileAsync(new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken);
        AssertRequest(handler, HttpMethod.Get, "v4/users/me/profile");
    }

    [Fact]
    public async Task GetSettings()
    {
        var (client, handler) = CreateClient();
        await client.Users.GetSettingsAsync(new GetSettingsRequest { Name = "users/me/settings" }, TestContext.Current.CancellationToken);
        AssertRequest(handler, HttpMethod.Get, "v4/users/me/settings");
    }

    [Fact]
    public async Task GetIdentity()
    {
        var (client, handler) = CreateClient();
        await client.Users.GetIdentityAsync(new GetIdentityRequest { Name = "users/me/identity" }, TestContext.Current.CancellationToken);
        AssertRequest(handler, HttpMethod.Get, "v4/users/me/identity");
    }

    [Fact]
    public async Task GetIrnProfile()
    {
        var (client, handler) = CreateClient();
        await client.Users.GetIrnProfileAsync(new GetIrnProfileRequest { Name = "users/me/irnProfile" }, TestContext.Current.CancellationToken);
        AssertRequest(handler, HttpMethod.Get, "v4/users/me/irnProfile");
    }

    [Fact]
    public async Task UpdateProfile()
    {
        var (client, handler) = CreateClient();

        await client.Users.UpdateProfileAsync(
            new UpdateProfileRequest
            {
                Name = "users/me/profile",
                UpdateMask = new GoogleFieldMask("age"),
                Body = new Profile { Age = 41 },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Patch, "v4/users/me/profile?updateMask=age");
        Assert.Equal("""{"age":41}""", handler.SingleRequest.Body);
    }

    [Fact]
    public async Task UpdateSettings()
    {
        var (client, handler) = CreateClient();

        await client.Users.UpdateSettingsAsync(
            new UpdateSettingsRequest
            {
                Name = "users/me/settings",
                Body = new Settings { TimeZone = "America/New_York" },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Patch, "v4/users/me/settings");
    }

    // ---------- users.dataTypes.dataPoints ----------

    [Fact]
    public async Task ListDataPoints()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.ListAsync(
            new ListDataPointsRequest
            {
                Parent = "users/me/dataTypes/heart-rate",
                Filter = "start_time >= \"2026-08-01T00:00:00Z\"",
                PageSize = 1000,
                PageToken = "CBI",
            },
            TestContext.Current.CancellationToken);

        AssertRequest(
            handler,
            HttpMethod.Get,
            "v4/users/me/dataTypes/heart-rate/dataPoints" +
            "?filter=start_time%20%3E%3D%20%222026-08-01T00%3A00%3A00Z%22&pageSize=1000&pageToken=CBI");
    }

    [Fact]
    public async Task GetDataPoint()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.GetAsync(
            new GetDataPointsRequest { Name = "users/me/dataTypes/heart-rate/dataPoints/abc" },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Get, "v4/users/me/dataTypes/heart-rate/dataPoints/abc");
    }

    [Fact]
    public async Task CreateDataPoint()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.CreateAsync(
            new CreateDataPointsRequest
            {
                Parent = "users/me/dataTypes/weight",
                Body = new DataPoint { Name = "users/me/dataTypes/weight/dataPoints/1" },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Post, "v4/users/me/dataTypes/weight/dataPoints");
    }

    [Fact]
    public async Task PatchDataPoint()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.PatchAsync(
            new PatchDataPointsRequest
            {
                Name = "users/me/dataTypes/weight/dataPoints/1",
                Body = new DataPoint(),
            },
            TestContext.Current.CancellationToken);

        // Unlike every other PATCH in this API, dataPoints.patch declares no updateMask.
        AssertRequest(handler, HttpMethod.Patch, "v4/users/me/dataTypes/weight/dataPoints/1");
    }

    [Fact]
    public async Task BatchDeleteDataPoints()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.BatchDeleteAsync(
            new BatchDeleteRequest
            {
                Parent = "users/me/dataTypes/weight",
                Body = new BatchDeleteDataPointsRequest { Names = ["users/me/dataTypes/weight/dataPoints/1"] },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Post, "v4/users/me/dataTypes/weight/dataPoints:batchDelete");
    }

    [Fact]
    public async Task ReconcileDataPoints()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.ReconcileAsync(
            new ReconcileRequest
            {
                Parent = "users/me/dataTypes/steps",
                DataSourceFamily = "GOOGLE_FIT",
                PageSize = 50,
            },
            TestContext.Current.CancellationToken);

        // reconcile is a GET despite reading like a mutation.
        AssertRequest(
            handler,
            HttpMethod.Get,
            "v4/users/me/dataTypes/steps/dataPoints:reconcile?dataSourceFamily=GOOGLE_FIT&pageSize=50");
    }

    [Fact]
    public async Task RollUpDataPoints()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.RollUpAsync(
            new RollUpRequest
            {
                Parent = "users/me/dataTypes/steps",
                Body = new RollUpDataPointsRequest { PageSize = 10, WindowSize = new GoogleDuration(3600, 0) },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Post, "v4/users/me/dataTypes/steps/dataPoints:rollUp");

        // Pagination travels in the body for this operation, not the query string.
        Assert.Contains("\"pageSize\":10", handler.SingleRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"windowSize\":\"3600s\"", handler.SingleRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyRollUpDataPoints()
    {
        var (client, handler) = CreateClient();

        await client.Users.DataPoints.DailyRollUpAsync(
            new DailyRollUpRequest
            {
                Parent = "users/me/dataTypes/steps",
                Body = new DailyRollUpDataPointsRequest { WindowSizeDays = 7 },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Post, "v4/users/me/dataTypes/steps/dataPoints:dailyRollUp");
    }

    [Fact]
    public async Task ExportExerciseTcxAsJson()
    {
        var (client, handler) = CreateClient("""{"tcxData":"<TrainingCenterDatabase/>"}""");

        var response = await client.Users.DataPoints.ExportExerciseTcxAsync(
            new ExportExerciseTcxRequest
            {
                Name = "users/me/dataTypes/exercise/dataPoints/1",
                PartialData = true,
            },
            TestContext.Current.CancellationToken);

        AssertRequest(
            handler,
            HttpMethod.Get,
            "v4/users/me/dataTypes/exercise/dataPoints/1:exportExerciseTcx?partialData=true");

        Assert.Equal("<TrainingCenterDatabase/>", response.TcxData);
    }

    [Fact]
    public async Task ExportExerciseTcxAsMedia()
    {
        var (client, handler) = CreateClient("<TrainingCenterDatabase/>");
        using var destination = new MemoryStream();

        await client.Users.DataPoints.ExportExerciseTcxAsync(
            new ExportExerciseTcxRequest { Name = "users/me/dataTypes/exercise/dataPoints/1" },
            destination,
            TestContext.Current.CancellationToken);

        // Google's own description is explicit: without alt=media the server returns JSON.
        AssertRequest(
            handler,
            HttpMethod.Get,
            "v4/users/me/dataTypes/exercise/dataPoints/1:exportExerciseTcx?alt=media");

        Assert.Equal("<TrainingCenterDatabase/>", System.Text.Encoding.UTF8.GetString(destination.ToArray()));
    }

    // ---------- users.pairedDevices ----------

    [Fact]
    public async Task GetPairedDevice()
    {
        var (client, handler) = CreateClient();

        await client.Users.PairedDevices.GetAsync(
            new GetPairedDevicesRequest { Name = "users/me/pairedDevices/abc" },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Get, "v4/users/me/pairedDevices/abc");
    }

    [Fact]
    public async Task ListPairedDevices()
    {
        var (client, handler) = CreateClient();

        await client.Users.PairedDevices.ListAsync(
            new ListPairedDevicesRequest { Parent = "users/me", PageSize = 25 },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Get, "v4/users/me/pairedDevices?pageSize=25");
    }

    // ---------- projects.subscribers ----------

    [Fact]
    public async Task CreateSubscriber()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.CreateAsync(
            new CreateSubscribersRequest
            {
                Parent = "projects/my-project",
                SubscriberId = "primary",
                Body = new CreateSubscriberPayload { EndpointUri = "https://example.test/hook" },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Post, "v4/projects/my-project/subscribers?subscriberId=primary");
    }

    [Fact]
    public async Task DeleteSubscriber()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.DeleteAsync(
            new DeleteSubscribersRequest { Name = "projects/my-project/subscribers/primary", Force = true },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Delete, "v4/projects/my-project/subscribers/primary?force=true");
    }

    [Fact]
    public async Task ListSubscribers()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.ListAsync(
            new ListSubscribersRequest { Parent = "projects/my-project" },
            TestContext.Current.CancellationToken);

        AssertRequest(handler, HttpMethod.Get, "v4/projects/my-project/subscribers");
    }

    [Fact]
    public async Task PatchSubscriber()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.PatchAsync(
            new PatchSubscribersRequest
            {
                Name = "projects/my-project/subscribers/primary",
                UpdateMask = new GoogleFieldMask("endpointUri"),
                Body = new Subscriber { EndpointUri = "https://example.test/hook2" },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(
            handler,
            HttpMethod.Patch,
            "v4/projects/my-project/subscribers/primary?updateMask=endpointUri");

        // Subscriber.state, createTime and updateTime are output only and must not be echoed.
        Assert.DoesNotContain("state", handler.SingleRequest.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("createTime", handler.SingleRequest.Body!, StringComparison.Ordinal);
    }

    // ---------- projects.subscribers.subscriptions ----------

    [Fact]
    public async Task CreateSubscription()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.Subscriptions.CreateAsync(
            new CreateSubscriptionsRequest
            {
                Parent = "projects/my-project/subscribers/primary",
                SubscriptionId = "sub-1",
                Body = new CreateSubscriptionPayload { User = "users/1234" },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(
            handler,
            HttpMethod.Post,
            "v4/projects/my-project/subscribers/primary/subscriptions?subscriptionId=sub-1");
    }

    [Fact]
    public async Task DeleteSubscription()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.Subscriptions.DeleteAsync(
            new DeleteSubscriptionsRequest { Name = "projects/my-project/subscribers/primary/subscriptions/sub-1" },
            TestContext.Current.CancellationToken);

        AssertRequest(
            handler,
            HttpMethod.Delete,
            "v4/projects/my-project/subscribers/primary/subscriptions/sub-1");
    }

    [Fact]
    public async Task ListSubscriptions()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.Subscriptions.ListAsync(
            new ListSubscriptionsRequest
            {
                Parent = "projects/my-project/subscribers/primary",
                Filter = "user=users/1234",
            },
            TestContext.Current.CancellationToken);

        AssertRequest(
            handler,
            HttpMethod.Get,
            "v4/projects/my-project/subscribers/primary/subscriptions?filter=user%3Dusers%2F1234");
    }

    [Fact]
    public async Task PatchSubscription()
    {
        var (client, handler) = CreateClient();

        await client.Projects.Subscribers.Subscriptions.PatchAsync(
            new PatchSubscriptionsRequest
            {
                Name = "projects/my-project/subscribers/primary/subscriptions/sub-1",
                Body = new Subscription { DataTypes = ["steps"] },
            },
            TestContext.Current.CancellationToken);

        AssertRequest(
            handler,
            HttpMethod.Patch,
            "v4/projects/my-project/subscribers/primary/subscriptions/sub-1");
    }

    // ---------- cross-cutting ----------

    [Fact]
    public void EveryOperationIsReachableAndDescribed()
    {
        Assert.Equal(25, HealthDataGeneratedOperations.All.Count);

        foreach (var descriptor in HealthDataGeneratedOperations.All)
        {
            Assert.StartsWith("health.", descriptor.Id, StringComparison.Ordinal);
            Assert.StartsWith("v4/", descriptor.PathTemplate, StringComparison.Ordinal);
            Assert.NotEmpty(descriptor.Scopes);
        }

        // Only the project-administration surface uses cloud-platform credentials (ADR-0007).
        Assert.Equal(
            8,
            HealthDataGeneratedOperations.All.Count(d => d.RequiresProjectCredentials));
    }

    [Fact]
    public void RetryClassificationMatchesTheDocumentedPolicy()
    {
        var byId = HealthDataGeneratedOperations.All.ToDictionary(d => d.Id, StringComparer.Ordinal);

        // Writes are never resent automatically.
        Assert.Equal(RetryClassification.Never, byId["health.users.dataTypes.dataPoints.create"].RetryClassification);
        Assert.Equal(RetryClassification.Never, byId["health.users.updateProfile"].RetryClassification);

        // Reads are safe.
        Assert.Equal(RetryClassification.Safe, byId["health.users.getProfile"].RetryClassification);
        Assert.Equal(RetryClassification.Safe, byId["health.users.dataTypes.dataPoints.reconcile"].RetryClassification);

        // POST that only aggregates existing data.
        Assert.Equal(RetryClassification.SemanticallySafe, byId["health.users.dataTypes.dataPoints.rollUp"].RetryClassification);

        // DELETE converges on the same state.
        Assert.Equal(RetryClassification.Idempotent, byId["health.projects.subscribers.delete"].RetryClassification);
    }

    [Fact]
    public void PaginationKindsMatchTheContract()
    {
        var byId = HealthDataGeneratedOperations.All.ToDictionary(d => d.Id, StringComparer.Ordinal);

        Assert.Equal(PaginationKind.Query, byId["health.users.dataTypes.dataPoints.list"].Pagination);
        Assert.Equal(PaginationKind.Body, byId["health.users.dataTypes.dataPoints.rollUp"].Pagination);

        // Paginated request, no continuation token in the response: cannot be enumerated.
        Assert.Equal(PaginationKind.RequestOnly, byId["health.users.dataTypes.dataPoints.dailyRollUp"].Pagination);

        Assert.Equal(PaginationKind.None, byId["health.users.getProfile"].Pagination);
    }

    [Fact]
    public async Task OperationDescriptorTravelsWithTheRequest()
    {
        // A delegating handler must be able to choose a credential without re-parsing the URL.
        HealthDataOperationDescriptor? observed = null;

        using var handler = new FakeHttpMessageHandler((request, _) =>
        {
            observed = request.GetHealthDataOperation();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        var client = new HealthDataClient(httpClient);

        await client.Users.GetProfileAsync(new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal("health.users.getProfile", observed!.Id);
        Assert.False(observed.RequiresProjectCredentials);
    }

    [Fact]
    public async Task ErrorStatusBecomesATypedExceptionWithoutLeakingTheBody()
    {
        using var handler = FakeHttpMessageHandler.Responding(
            HttpStatusCode.Forbidden,
            """{"error":{"code":403,"status":"MISSING_OAUTH_SCOPE","message":"user 1234 lacks scope for heart rate"}}""");

        using var httpClient = new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        var client = new HealthDataClient(httpClient);

        var exception = await Assert.ThrowsAsync<HealthDataApiException>(() =>
            client.Users.GetProfileAsync(new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(HealthDataErrorReasons.MissingOauthScope, exception.Reason);
        Assert.Equal("health.users.getProfile", exception.OperationId);

        // The service message names a user and a data type. It must not reach the exception.
        Assert.DoesNotContain("1234", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("heart rate", exception.Message, StringComparison.Ordinal);
    }
}
