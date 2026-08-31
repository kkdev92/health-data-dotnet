using System.Net;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.TestSupport;

/// <summary>
/// A recording <see cref="HttpMessageHandler"/> used to assert the exact wire contract.
/// </summary>
/// <remarks>
/// No interface exists purely to be mocked: a fake handler is enough to assert HTTP method, exact
/// relative URL, query casing, headers and request body. Every contract test builds on it, and so
/// does the Native AOT smoke application.
/// </remarks>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly List<RecordedRequest> _requests = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => _responder = responder;

    /// <summary>
    /// Answers with whatever the caller builds for each request.
    /// </summary>
    /// <remarks>
    /// The synchronous shape is the useful one for a test that has to set a header or vary the
    /// body: an <see cref="HttpResponseMessage"/> built in a caller's lambda and handed to
    /// <c>Task.FromResult</c> is a disposable crossing into a task nobody awaits, which is the
    /// shape CA2025 exists to point at. Building it here keeps that in one place, where the
    /// response goes straight to the <see cref="HttpClient"/> that owns disposing it.
    /// </remarks>
    public static FakeHttpMessageHandler Responding(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);

        return new FakeHttpMessageHandler((request, _) => Task.FromResult(respond(request)));
    }

    /// <summary>Always answers with the given status and body.</summary>
    public static FakeHttpMessageHandler Responding(HttpStatusCode statusCode, string body, string mediaType = "application/json")
        => new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
        }));

    /// <summary>Every request observed, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>The only request observed. Throws if a different number was sent.</summary>
    /// <remarks>
    /// Spelled out rather than left to <c>Assert.Single</c>, because this type is shared with the
    /// Native AOT smoke application and a test framework cannot be published with it. The message
    /// says the same thing an assertion would.
    /// </remarks>
    public RecordedRequest SingleRequest => _requests.Count == 1
        ? _requests[0]
        : throw new InvalidOperationException(
            $"Expected exactly one request, but {_requests.Count} were sent.");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The body must be captured now: the caller may dispose the request before assertions run.
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        _requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
            body,
            request.GetHealthDataOperation()?.Id));

        return await _responder(request, cancellationToken);
    }

    /// <param name="OperationId">
    /// The Discovery operation id the descriptor carried, or null for a request that did not come
    /// from this SDK. Recorded here because it is part of what a request carries, and two places
    /// were capturing it with a handler written for that alone.
    /// </param>
    public sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        IReadOnlyDictionary<string, string> Headers,
        string? Body,
        string? OperationId = null)
    {
        /// <summary>The path and query relative to the base address, without a leading slash.</summary>
        public string RelativeUrl => RequestUri is null
            ? string.Empty
            : RequestUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimStart('/');
    }
}
