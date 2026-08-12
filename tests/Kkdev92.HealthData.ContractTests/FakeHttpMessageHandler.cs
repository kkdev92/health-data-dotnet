using System.Net;

namespace Kkdev92.HealthData.ContractTests;

/// <summary>
/// A recording <see cref="HttpMessageHandler"/> used to assert the exact wire contract.
/// </summary>
/// <remarks>
/// No interface exists purely to be mocked: a fake handler is enough to assert HTTP method, exact
/// relative URL, query casing, headers and request body. Every contract test in this project
/// builds on it.
/// </remarks>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly List<RecordedRequest> _requests = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => _responder = responder;

    /// <summary>Always answers with the given status and body.</summary>
    public static FakeHttpMessageHandler Responding(HttpStatusCode statusCode, string body, string mediaType = "application/json")
        => new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
        }));

    /// <summary>Every request observed, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>The only request observed. Fails if a different number was sent.</summary>
    public RecordedRequest SingleRequest => Assert.Single(_requests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The body must be captured now: the caller may dispose the request before assertions run.
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        _requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
            body));

        return await _responder(request, cancellationToken);
    }

    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        IReadOnlyDictionary<string, string> Headers,
        string? Body)
    {
        /// <summary>The path and query relative to the base address, without a leading slash.</summary>
        public string RelativeUrl => RequestUri is null
            ? string.Empty
            : RequestUri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimStart('/');
    }
}
