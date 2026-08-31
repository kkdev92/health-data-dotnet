using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kkdev92.HealthData.Diagnostics;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Http;

/// <summary>
/// Sends generated requests and materializes their responses.
/// </summary>
/// <remarks>
/// <para>
/// Responses are read with <see cref="HttpCompletionOption.ResponseHeadersRead"/> so a large
/// history page is not buffered twice. That has a consequence
/// worth stating plainly: <see cref="HttpClient.Timeout"/> then covers only the headers, so the
/// caller's <see cref="CancellationToken"/> is propagated into the body read and the
/// deserialization. Nothing here relies on the client timeout to bound the whole exchange.
/// </para>
/// <para>
/// Failures are surfaced as <see cref="HealthDataApiException"/> with the parsed error
/// envelope attached. The error body is read under a byte bound, and the service's
/// human-readable message never reaches the exception message.
/// </para>
/// </remarks>
public sealed class HealthDataTransport(HttpClient httpClient, HealthDataClientOptions options)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly HealthDataClientOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Sends a request and deserializes a typed JSON response.</summary>
    public async Task<TResponse> SendAsync<TResponse>(
        HealthDataOperationDescriptor descriptor,
        string relativeUri,
        HttpContent? content,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(responseTypeInfo);

        using var response = await SendCoreAsync(descriptor, relativeUri, content, cancellationToken)
            .ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        TResponse? value;

        await using (stream.ConfigureAwait(false))
        {
            value = await JsonSerializer
                .DeserializeAsync(stream, responseTypeInfo, cancellationToken)
                .ConfigureAwait(false);
        }

        // A success status with a null body means the service sent literal `null`, which no
        // operation in this contract does. Surfacing it as an API exception is more useful than
        // returning null from a non-nullable signature.
        return value ?? throw new HealthDataApiException(response.StatusCode, descriptor.Id);
    }

    /// <summary>Sends a request that returns no meaningful body.</summary>
    public async Task SendAsync(
        HealthDataOperationDescriptor descriptor,
        string relativeUri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(descriptor, relativeUri, content, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Sends a request and copies a media response into <paramref name="destination"/>.</summary>
    /// <remarks>
    /// Streaming rather than buffering: an exported TCX can be large, and materializing it as a
    /// string would defeat the point.
    /// </remarks>
    public async Task DownloadAsync(
        HealthDataOperationDescriptor descriptor,
        string relativeUri,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var response = await SendCoreAsync(descriptor, relativeUri, content: null, cancellationToken)
            .ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            await stream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Serializes a request body using the write contract, which omits output-only fields.</summary>
    public static HttpContent CreateJsonContent<TRequest>(TRequest value, JsonTypeInfo<TRequest> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        var json = JsonSerializer.Serialize(value, typeInfo);
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HealthDataOperationDescriptor descriptor,
        string relativeUri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrEmpty(relativeUri);

        var baseAddress = _httpClient.BaseAddress ?? _options.BaseAddress;

        using var request = new HttpRequestMessage(descriptor.Method, new Uri(baseAddress, ApplySystemParameters(relativeUri)))
        {
            Content = content,
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.SetHealthDataOperation(descriptor);

        using var activity = HealthDataDiagnostics.StartOperation(descriptor, baseAddress);

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HealthDataDiagnostics.RecordException(activity, ex);
            throw;
        }

        HealthDataDiagnostics.RecordResponse(activity, response.StatusCode);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            HealthDataError? error;

            await using (stream.ConfigureAwait(false))
            {
                error = await HealthDataErrorParser
                    .ParseAsync(stream, _options.MaxErrorResponseBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new HealthDataApiException(
                response.StatusCode,
                descriptor.Id,
                error,
                ResolveRetryAfter(response, error));
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Appends Google system parameters that apply to every operation.
    /// </summary>
    /// <remarks>
    /// These are transport policy rather than part of any operation's contract, so they are added
    /// here instead of by the generated request builders.
    /// </remarks>
    private string ApplySystemParameters(string relativeUri)
    {
        if (_options.PrettyPrintResponses)
        {
            // Send nothing and accept the service default, which is to indent.
            return relativeUri;
        }

        var separator = relativeUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{relativeUri}{separator}prettyPrint=false";
    }

    /// <summary>
    /// Determines how long to wait before retrying, if the service said anything about it.
    /// </summary>
    /// <remarks>
    /// The <c>Retry-After</c> header takes precedence, then a <c>google.rpc.RetryInfo</c> detail.
    /// The Google Health rate-limit documentation describes 429 responses but never promises a
    /// <c>Retry-After</c> header, so this is often null.
    /// </remarks>
    private TimeSpan? ResolveRetryAfter(HttpResponseMessage response, HealthDataError? error)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _options.TimeProvider.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        var retryInfo = error?.Details.FirstOrDefault(d => d.IsRetryInfo)?.RetryDelay;
        return retryInfo?.ToTimeSpan();
    }

    /// <summary>Returns the read contract for a generated type.</summary>
    public static JsonTypeInfo<T> ReadInfo<T>() => HealthDataJson.ReadInfo<T>();

    /// <summary>Returns the write contract for a generated type.</summary>
    public static JsonTypeInfo<T> WriteInfo<T>() => HealthDataJson.WriteInfo<T>();

    /// <summary>Exposed so generated resources can report the configured status handling.</summary>
    internal static bool IsSuccess(HttpStatusCode statusCode) => (int)statusCode is >= 200 and < 300;
}
