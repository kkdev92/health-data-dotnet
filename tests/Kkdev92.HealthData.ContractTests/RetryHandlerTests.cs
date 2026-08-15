using System.Net;
using Kkdev92.HealthData.Http;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Requests;
using Kkdev92.HealthData.Resilience;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.HealthData.ContractTests;

/// <summary>
/// Verifies that retries follow the operation classification, not the HTTP method.
/// </summary>
/// <remarks>
/// Time is faked throughout, so the backoff is asserted exactly and no test waits for it.
/// </remarks>
public sealed class RetryHandlerTests
{
    /// <summary>Fails with the given status a fixed number of times, then succeeds.</summary>
    private sealed class FlakyHandler(HttpStatusCode failWith, int failures) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        public TimeSpan? RetryAfter { get; init; }

        /// <summary>The other form of Retry-After: an absolute HTTP-date rather than a delay.</summary>
        public DateTimeOffset? RetryAfterDate { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;

            if (Attempts <= failures)
            {
                var failure = new HttpResponseMessage(failWith)
                {
                    Content = new StringContent(
                        """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };

                if (RetryAfter is { } delta)
                {
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
                }
                else if (RetryAfterDate is { } date)
                {
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(date);
                }

                return Task.FromResult(failure);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (HealthDataClient Client, FakeTimeProvider Time) CreateClient(
        HttpMessageHandler inner,
        HealthDataRetryOptions? options = null)
    {
        var time = new FakeTimeProvider();

        var retry = new HealthDataRetryHandler(
            options ?? new HealthDataRetryOptions { BaseDelay = TimeSpan.FromSeconds(1), UseJitter = false },
            time)
        {
            InnerHandler = inner,
        };

        var httpClient = new HttpClient(retry) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        return (new HealthDataClient(httpClient), time);
    }

    /// <summary>Runs a call while advancing fake time so any pending backoff completes.</summary>
    private static async Task<T> RunAsync<T>(FakeTimeProvider time, Func<Task<T>> call)
    {
        var task = call();

        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Yield();
        }

        return await task;
    }

    [Fact]
    public async Task RetriesASafeReadUntilItSucceeds()
    {
        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 2);
        var (client, time) = CreateClient(inner);

        var profile = await RunAsync(time, () => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken));

        Assert.NotNull(profile);
        Assert.Equal(3, inner.Attempts);
    }

    [Fact]
    public async Task NeverRetriesAWrite()
    {
        // dataPoints.create is a POST that writes. Resending it could duplicate a health record.
        using var inner = new FlakyHandler(HttpStatusCode.ServiceUnavailable, failures: 1);
        var (client, time) = CreateClient(inner);

        await Assert.ThrowsAsync<HealthDataApiException>(() => RunAsync(time, () =>
            client.Users.DataPoints.CreateAsync(new CreateDataPointsRequest
            {
                Parent = "users/me/dataTypes/weight",
                Body = new DataPoint(),
            }, TestContext.Current.CancellationToken)));

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task RetriesASemanticallySafePost()
    {
        // rollUp is a POST, but it only aggregates existing data.
        Assert.Equal(
            RetryClassification.SemanticallySafe,
            HealthDataGeneratedOperations.UsersDataTypesDataPointsRollUp.RetryClassification);

        using var inner = new FlakyHandler(HttpStatusCode.GatewayTimeout, failures: 1);
        var (client, time) = CreateClient(inner);

        await RunAsync(time, () => client.Users.DataPoints.RollUpAsync(new RollUpRequest
        {
            Parent = "users/me/dataTypes/steps",
            Body = new RollUpDataPointsRequest(),
        }, TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task DoesNotRetryIdempotentOperationsUnlessAskedTo()
    {
        using var inner = new FlakyHandler(HttpStatusCode.ServiceUnavailable, failures: 1);
        var (client, time) = CreateClient(inner);

        await Assert.ThrowsAsync<HealthDataApiException>(() => RunAsync(time, () =>
            client.Projects.Subscribers.DeleteAsync(
                new DeleteSubscribersRequest { Name = "projects/p/subscribers/s" },
                TestContext.Current.CancellationToken)));

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task RetriesIdempotentOperationsWhenOptedIn()
    {
        using var inner = new FlakyHandler(HttpStatusCode.ServiceUnavailable, failures: 1);

        var (client, time) = CreateClient(inner, new HealthDataRetryOptions
        {
            BaseDelay = TimeSpan.FromSeconds(1),
            UseJitter = false,
            RetryIdempotentOperations = true,
        });

        await RunAsync(time, () => client.Projects.Subscribers.DeleteAsync(
            new DeleteSubscribersRequest { Name = "projects/p/subscribers/s" },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task StopsAtMaxAttemptsAndSurfacesTheLastFailure()
    {
        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 99);

        var (client, time) = CreateClient(inner, new HealthDataRetryOptions
        {
            MaxAttempts = 4,
            BaseDelay = TimeSpan.FromSeconds(1),
            UseJitter = false,
        });

        var exception = await Assert.ThrowsAsync<HealthDataApiException>(() => RunAsync(time, () =>
            client.Users.GetProfileAsync(
                new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken)));

        Assert.Equal(4, inner.Attempts);
        Assert.True(exception.IsRateLimited);
    }

    [Fact]
    public async Task DoesNotRetryClientErrors()
    {
        // A 403 will not become a 200 by asking again.
        using var inner = new FlakyHandler(HttpStatusCode.Forbidden, failures: 99);
        var (client, time) = CreateClient(inner);

        await Assert.ThrowsAsync<HealthDataApiException>(() => RunAsync(time, () =>
            client.Users.GetProfileAsync(
                new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken)));

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task BackoffIsExponentialAndUsesTheInjectedClock()
    {
        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 2);
        var time = new FakeTimeProvider();

        var retry = new HealthDataRetryHandler(
            new HealthDataRetryOptions { BaseDelay = TimeSpan.FromSeconds(2), UseJitter = false },
            time)
        {
            InnerHandler = inner,
        };

        using var httpClient = new HttpClient(retry) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        var client = new HealthDataClient(httpClient);

        var call = client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken);

        // Nothing has waited yet, so only the first attempt has been made.
        while (inner.Attempts < 1)
        {
            await Task.Yield();
        }

        Assert.Equal(1, inner.Attempts);

        // First backoff is exactly the base delay.
        time.Advance(TimeSpan.FromSeconds(2));
        while (inner.Attempts < 2)
        {
            await Task.Yield();
        }

        Assert.Equal(2, inner.Attempts);

        // Second backoff doubles. Advancing by the base delay alone is not enough.
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Yield();
        Assert.Equal(2, inner.Attempts);

        time.Advance(TimeSpan.FromSeconds(2));
        while (inner.Attempts < 3)
        {
            await Task.Yield();
        }

        await call;
        Assert.Equal(3, inner.Attempts);
    }

    /// <summary>
    /// The HTTP-date form of Retry-After is measured against the injected clock.
    /// </summary>
    /// <remarks>
    /// RFC 9110 allows either a delay in seconds or an absolute date, and they arrive on different
    /// properties. The date branch read <c>DateTimeOffset.UtcNow</c> directly, so it could not be
    /// tested at all and the class remark about delays going through the injected
    /// <c>TimeProvider</c> was true only of the other branch.
    /// </remarks>
    [Fact]
    public async Task AnHttpDateRetryAfterIsMeasuredAgainstTheInjectedClock()
    {
        var start = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 1)
        {
            RetryAfterDate = start.AddSeconds(30),
        };

        var (client, time) = CreateClient(inner);
        time.SetUtcNow(start);

        await RunAsync(time, () => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.Attempts);

        // Thirty seconds after the fake start, not thirty seconds after the wall clock — which is
        // what the assertion would be measuring if the handler still read DateTimeOffset.UtcNow.
        Assert.True(
            time.GetUtcNow() >= start.AddSeconds(30),
            $"the retry happened at {time.GetUtcNow():O}, before the date the server asked for.");
    }

    /// <summary>A date already in the past means "now", not a negative delay.</summary>
    [Fact]
    public async Task AnHttpDateInThePastRetriesImmediately()
    {
        var start = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 1)
        {
            RetryAfterDate = start.AddMinutes(-5),
        };

        var (client, time) = CreateClient(inner);
        time.SetUtcNow(start);

        await RunAsync(time, () => client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task ARetryAfterLongerThanMaxDelayIsNotShortened()
    {
        // The handler used to clamp Retry-After to MaxDelay, so a service asking for two minutes
        // was retried after thirty seconds — early, at a service that had just said it was not
        // ready. RFC 9110 §10.2.3 defines the field as how long the user agent ought to wait.
        // Declining to retry is the only remaining option that does not arrive too soon.
        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 1)
        {
            RetryAfter = TimeSpan.FromSeconds(120),
        };

        var time = new FakeTimeProvider();

        var retry = new HealthDataRetryHandler(
            new HealthDataRetryOptions
            {
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(30),
                UseJitter = false,
            },
            time)
        {
            InnerHandler = inner,
        };

        using var httpClient = new HttpClient(retry) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        var client = new HealthDataClient(httpClient);

        var failure = await Assert.ThrowsAsync<HealthDataApiException>(
            () => client.Users.GetProfileAsync(
                new GetProfileRequest { Name = "users/me/profile" },
                TestContext.Current.CancellationToken));

        // One attempt only: no second call was made, early or otherwise.
        Assert.Equal(1, inner.Attempts);

        // And the caller is told how long to wait, so it can schedule its own attempt.
        Assert.Equal(TimeSpan.FromSeconds(120), failure.RetryAfter);
    }

    [Fact]
    public async Task ARetryAfterWithinMaxDelayIsStillHonoured()
    {
        // The boundary the test above depends on: at or below MaxDelay the handler waits and
        // retries, so declining is confined to waits it genuinely will not sit through.
        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 1)
        {
            RetryAfter = TimeSpan.FromSeconds(30),
        };

        var time = new FakeTimeProvider();

        var retry = new HealthDataRetryHandler(
            new HealthDataRetryOptions
            {
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(30),
                UseJitter = false,
            },
            time)
        {
            InnerHandler = inner,
        };

        using var httpClient = new HttpClient(retry) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        var client = new HealthDataClient(httpClient);

        var call = client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken);

        while (inner.Attempts < 1)
        {
            await Task.Yield();
        }

        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Yield();
        Assert.Equal(1, inner.Attempts);

        time.Advance(TimeSpan.FromSeconds(1));
        while (inner.Attempts < 2)
        {
            await Task.Yield();
        }

        await call;
        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task RetryAfterHeaderOverridesTheComputedBackoff()
    {
        using var inner = new FlakyHandler(HttpStatusCode.TooManyRequests, failures: 1)
        {
            RetryAfter = TimeSpan.FromSeconds(9),
        };

        var time = new FakeTimeProvider();

        var retry = new HealthDataRetryHandler(
            new HealthDataRetryOptions { BaseDelay = TimeSpan.FromSeconds(1), UseJitter = false },
            time)
        {
            InnerHandler = inner,
        };

        using var httpClient = new HttpClient(retry) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        var client = new HealthDataClient(httpClient);

        var call = client.Users.GetProfileAsync(
            new GetProfileRequest { Name = "users/me/profile" }, TestContext.Current.CancellationToken);

        while (inner.Attempts < 1)
        {
            await Task.Yield();
        }

        // The computed backoff would have been one second; the service asked for nine.
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.Equal(1, inner.Attempts);

        time.Advance(TimeSpan.FromSeconds(8));
        while (inner.Attempts < 2)
        {
            await Task.Yield();
        }

        await call;
        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public void RejectsANonsensicalAttemptCount()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HealthDataRetryHandler(new HealthDataRetryOptions { MaxAttempts = 0 }));
}
