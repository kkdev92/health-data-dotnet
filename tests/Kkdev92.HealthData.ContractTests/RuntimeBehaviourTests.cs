using System.Diagnostics;
using System.Net;
using Kkdev92.HealthData.Diagnostics;
using Kkdev92.HealthData.Http;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Requests;
using Kkdev92.HealthData.Names;

namespace Kkdev92.HealthData.ContractTests;

/// <summary>
/// Exercises pagination, diagnostics, cancellation and error surfacing through the real client.
/// </summary>
public sealed class RuntimeBehaviourTests
{
    private static HealthDataClient CreateClient(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress });

    /// <summary>
    /// Captures the activities produced by the calls made inside this scope, and only those.
    /// </summary>
    /// <remarks>
    /// <see cref="ActivityListener"/> is process-wide, so a listener registered by one test also
    /// sees activities from test classes running in parallel. Correlating on the parent span
    /// isolates the capture without having to serialize the whole assembly:
    /// <see cref="Activity.Current"/> flows with the async context, so an SDK activity started by
    /// another test has a different parent.
    /// </remarks>
    private sealed class ActivityScope : IDisposable
    {
        // A literal, not ScopeSource.Name. Constructing an ActivitySource notifies every
        // registered listener, so a ShouldListenTo that reads ScopeSource would dereference the
        // field while its own static initializer is still running.
        private const string ScopeSourceName = "Kkdev92.HealthData.ContractTests.Scope";

        private static readonly ActivitySource ScopeSource = new(ScopeSourceName);

        private readonly ActivityListener _scopeListener;
        private readonly ActivityListener _sdkListener;
        private readonly Activity? _scope;

        public ActivityScope()
        {
            _scopeListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ScopeSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };

            ActivitySource.AddActivityListener(_scopeListener);
            _scope = ScopeSource.StartActivity("test-scope");

            _sdkListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == HealthDataDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Parent?.Id == _scope?.Id)
                    {
                        lock (Captured)
                        {
                            Captured.Add(activity);
                        }
                    }
                },
            };

            ActivitySource.AddActivityListener(_sdkListener);
        }

        public List<Activity> Captured { get; } = [];

        public void Dispose()
        {
            _scope?.Dispose();
            _sdkListener.Dispose();
            _scopeListener.Dispose();
        }
    }

    /// <summary>Serves a canned sequence of responses, one per request.</summary>
    private sealed class SequenceHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _index;

        public List<string> RequestedUrls { get; } = [];

        /// <summary>What was sent, for the operations whose cursor travels in the body.</summary>
        public List<string> RequestedBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.PathAndQuery);

            if (request.Content is { } content)
            {
                RequestedBodies.Add(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            var body = bodies[Math.Min(_index++, bodies.Length - 1)];

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task EnumerateFollowsPageTokensAndStops()
    {
        using var handler = new SequenceHandler(
            """{"dataPoints":[{"name":"a"},{"name":"b"}],"nextPageToken":"T2"}""",
            """{"dataPoints":[{"name":"c"}],"nextPageToken":"T3"}""",
            """{"dataPoints":[{"name":"d"}]}""");

        var client = CreateClient(handler);

        var names = new List<string>();

        await foreach (var point in client.Users.DataPoints.EnumerateAsync(
            new ListDataPointsRequest { Parent = UserName.Me.DataType("steps"), PageSize = 2 },
            TestContext.Current.CancellationToken))
        {
            names.Add(point.Name!);
        }

        Assert.Equal(["a", "b", "c", "d"], names);
        Assert.Equal(3, handler.RequestedUrls.Count);

        // The first request carries no token; later requests carry the one the service returned,
        // and every request keeps the caller's page size.
        Assert.DoesNotContain("pageToken", handler.RequestedUrls[0], StringComparison.Ordinal);
        Assert.Contains("pageToken=T2", handler.RequestedUrls[1], StringComparison.Ordinal);
        Assert.Contains("pageToken=T3", handler.RequestedUrls[2], StringComparison.Ordinal);
        Assert.All(handler.RequestedUrls, url => Assert.Contains("pageSize=2", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnumerateFollowsACursorThatTravelsInTheBody()
    {
        // rollUp is the one operation that pages this way. It had no streaming overload for that
        // reason alone — the generator only handled query cursors — so a caller who wanted every
        // rolled-up point wrote the loop, rebuilt the body each time, and had to know that the
        // token went inside it. Nothing about the response made that discoverable.
        using var handler = new SequenceHandler(
            """{"rollupDataPoints":[{"steps":{"countSum":"11"}}],"nextPageToken":"T2"}""",
            """{"rollupDataPoints":[{"steps":{"countSum":"22"}}]}""");

        var client = CreateClient(handler);

        var counts = new List<long?>();

        await foreach (var point in client.Users.DataPoints.EnumerateAsync(
            new RollUpRequest
            {
                Parent = UserName.Me.DataType("steps"),
                Body = new RollUpDataPointsRequest { PageSize = 1 },
            },
            TestContext.Current.CancellationToken))
        {
            counts.Add(point.Steps?.CountSum);
        }

        Assert.Equal([11L, 22L], counts);
        Assert.Equal(2, handler.RequestedBodies.Count);

        // The cursor is in the body, and everything else the caller set survives the copy.
        Assert.DoesNotContain("pageToken", handler.RequestedBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"pageToken\":\"T2\"", handler.RequestedBodies[1], StringComparison.Ordinal);
        Assert.All(handler.RequestedBodies, body => Assert.Contains("\"pageSize\":1", body, StringComparison.Ordinal));

        // And it is not smuggled into the query as well, which would be a second cursor the
        // service never asked for.
        Assert.All(handler.RequestedUrls, url => Assert.DoesNotContain("pageToken", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnumerateIsLazyAndStopsWhenTheCallerStops()
    {
        using var handler = new SequenceHandler(
            """{"dataPoints":[{"name":"a"}],"nextPageToken":"T2"}""",
            """{"dataPoints":[{"name":"b"}],"nextPageToken":"T3"}""");

        var client = CreateClient(handler);

        await foreach (var point in client.Users.DataPoints.EnumerateAsync(
            new ListDataPointsRequest { Parent = UserName.Me.DataType("steps") },
            TestContext.Current.CancellationToken))
        {
            Assert.Equal("a", point.Name);
            break;
        }

        // Breaking out must not have prefetched. A user's history is unbounded, so eager paging
        // would spend their quota for results nobody asked for.
        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task EnumerateStopsIfTheServiceEchoesTheSameToken()
    {
        // A service that returns the token it was given would otherwise loop forever.
        using var handler = new SequenceHandler(
            """{"dataPoints":[{"name":"a"}],"nextPageToken":"SAME"}""",
            """{"dataPoints":[{"name":"b"}],"nextPageToken":"SAME"}""");

        var client = CreateClient(handler);
        var count = 0;

        await foreach (var _ in client.Users.DataPoints.EnumerateAsync(
            new ListDataPointsRequest { Parent = UserName.Me.DataType("steps") },
            TestContext.Current.CancellationToken))
        {
            count++;
        }

        Assert.Equal(2, count);
        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public void DailyRollUpHasNoEnumerateOverload()
    {
        // Its request accepts a page token but its response returns none, so enumeration is
        // impossible. The generator must not have invented one.
        var methods = typeof(DataPointsResource).GetMethods().Select(m => m.Name).ToArray();

        Assert.Contains("EnumerateAsync", methods);
        Assert.Equal(PaginationKind.RequestOnly,
            HealthDataGeneratedOperations.UsersDataTypesDataPointsDailyRollUp.Pagination);

        // Six: the five that page by query parameter, and rollUp, which pages inside its body.
        // Where the cursor travels is not a reason to withhold enumeration — whether the response
        // returns one is, and dailyRollUp is the only operation where it does not.
        var enumerateCount = new[]
            {
                typeof(DataPointsResource), typeof(PairedDevicesResource),
                typeof(SubscribersResource), typeof(SubscriptionsResource),
            }
                .Sum(t => t.GetMethods().Count(m => m.Name == "EnumerateAsync"));

        Assert.Equal(6, enumerateCount);

        // The one that must not exist, named rather than counted: a count stays green if the
        // missing overload is replaced by an extra one somewhere else.
        var dailyRollUp = typeof(DataPointsResource)
            .GetMethods()
            .Where(m => m.Name == "EnumerateAsync")
            .Select(m => m.GetParameters()[0].ParameterType.Name)
            .ToArray();

        Assert.DoesNotContain("DailyRollUpRequest", dailyRollUp);
        Assert.Contains("RollUpRequest", dailyRollUp);
    }

    [Fact]
    public async Task SuppressesServerSidePrettyPrintingByDefault()
    {
        // Measured 2026-08-10 against the Google Health Discovery endpoint: prettyPrint=false
        // cut the payload from 282,943 to 207,058 bytes, a 26.8% reduction. The difference is
        // whitespace only.
        using var handler = new SequenceHandler("{}");
        var client = CreateClient(handler);

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.Me.Profile },
            TestContext.Current.CancellationToken);

        Assert.Equal("/v4/users/me/profile?prettyPrint=false", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task AppendsPrettyPrintAfterAnExistingQueryString()
    {
        using var handler = new SequenceHandler("{}");
        var client = CreateClient(handler);

        await client.Users.PairedDevices.ListAsync(
            new ListPairedDevicesRequest { Parent = UserName.Me, PageSize = 25 },
            TestContext.Current.CancellationToken);

        Assert.Equal("/v4/users/me/pairedDevices?pageSize=25&prettyPrint=false", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task SendsNothingWhenPrettyPrintingIsRequested()
    {
        using var handler = new SequenceHandler("{}");

        using var httpClient = new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        var client = new HealthDataClient(httpClient, new HealthDataClientOptions { PrettyPrintResponses = true });

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.Me.Profile },
            TestContext.Current.CancellationToken);

        // The service default is to indent, so opting in means sending no parameter at all.
        Assert.Equal("/v4/users/me/profile", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task ActivityCarriesOperationMetadataAndNeverTheUrl()
    {
        using var scope = new ActivityScope();
        using var handler = new SequenceHandler("""{"name":"users/1234/profile"}""");
        var client = CreateClient(handler);

        await client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.From("1234").Profile },
            TestContext.Current.CancellationToken);

        var activity = Assert.Single(scope.Captured);

        Assert.Equal("health.users.getProfile", activity.GetTagItem(HealthDataActivityTags.OperationId));
        Assert.Equal("v4", activity.GetTagItem(HealthDataActivityTags.ApiVersion));
        Assert.Equal("GET", activity.GetTagItem(HealthDataActivityTags.HttpRequestMethod));
        Assert.Equal("health.googleapis.com", activity.GetTagItem(HealthDataActivityTags.ServerAddress));
        Assert.Equal(200, activity.GetTagItem(HealthDataActivityTags.HttpResponseStatusCode));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);

        // A resource name embeds the user id and the data type. It must never become a tag.
        foreach (var (key, value) in activity.Tags)
        {
            Assert.DoesNotContain("1234", value ?? string.Empty, StringComparison.Ordinal);
            Assert.NotEqual("url.full", key);
            Assert.NotEqual("url.path", key);
        }
    }

    [Fact]
    public async Task ActivityRecordsFailuresWithoutTheServiceMessage()
    {
        using var scope = new ActivityScope();
        using var handler = FakeHttpMessageHandler.Responding(
            HttpStatusCode.TooManyRequests,
            """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"user 1234 exceeded quota"}}""");

        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HealthDataApiException>(() => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.Me.Profile },
            TestContext.Current.CancellationToken));

        var activity = Assert.Single(scope.Captured);

        Assert.Equal(429, activity.GetTagItem(HealthDataActivityTags.HttpResponseStatusCode));
        Assert.Equal("429", activity.GetTagItem(HealthDataActivityTags.ErrorType));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);

        foreach (var (_, value) in activity.Tags)
        {
            Assert.DoesNotContain("1234", value ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RateLimitExposesRetryAfterFromTheHeader()
    {
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };

            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return Task.FromResult(response);
        });

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HealthDataApiException>(() => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.Me.Profile },
            TestContext.Current.CancellationToken));

        Assert.True(exception.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(42), exception.RetryAfter);
    }

    [Fact]
    public async Task RateLimitFallsBackToRetryInfoWhenNoHeaderIsSent()
    {
        // The Google Health rate-limit documentation describes 429 but never promises a
        // Retry-After header, so the RetryInfo detail is the more likely source.
        using var handler = FakeHttpMessageHandler.Responding(
            HttpStatusCode.TooManyRequests,
            """
            {"error":{"code":429,"status":"RESOURCE_EXHAUSTED","details":[
              {"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"12.500s"}]}}
            """);

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HealthDataApiException>(() => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = UserName.Me.Profile },
            TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(12.5), exception.RetryAfter);
    }

    [Fact]
    public async Task CancellationPropagatesIntoTheBodyRead()
    {
        // ResponseHeadersRead means HttpClient.Timeout no longer bounds the body, so the caller's
        // token has to.
        using var cts = new CancellationTokenSource();

        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingStream(cts)),
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        });

        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.Users.GetProfileAsync(new GetProfileRequest { Name = UserName.Me.Profile }, cts.Token));
    }

    /// <summary>A body that never completes until the test cancels it.</summary>
    private sealed class BlockingStream(CancellationTokenSource cts) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
