using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Kkdev92.HealthData.TestSupport;

/// <summary>JSON request and response bodies, without repeating the encoding and media type.</summary>
public static class JsonContent
{
    /// <summary>A UTF-8 <c>application/json</c> body.</summary>
    public static StringContent Of(string json) => new(json, Encoding.UTF8, "application/json");
}

/// <summary>
/// A body that declares whatever length it is told to.
/// </summary>
/// <remarks>
/// <see cref="StringContent"/> always declares the true length, so it cannot exercise a check that
/// reads <c>Content-Length</c> before reading the body — the kind that refuses an oversized
/// response before a byte of it is buffered. <see cref="BytesServed"/> is what proves the refusal
/// happened first.
/// </remarks>
public sealed class DeclaredLengthContent : HttpContent
{
    private readonly byte[] _body;
    private readonly long? _declaredLength;

    /// <summary>Creates a body of <paramref name="body"/> that declares <paramref name="declaredLength"/>.</summary>
    /// <param name="body">The bytes actually written.</param>
    /// <param name="declaredLength">What to put in <c>Content-Length</c>, or null to declare nothing.</param>
    public DeclaredLengthContent(byte[] body, long? declaredLength)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _declaredLength = declaredLength;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    /// <summary>How many bytes were written. Zero means the body was never read.</summary>
    public long BytesServed { get; private set; }

    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        ArgumentNullException.ThrowIfNull(stream);

        await stream.WriteAsync(_body).ConfigureAwait(false);
        BytesServed += _body.Length;
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = _declaredLength ?? 0;
        return _declaredLength is not null;
    }
}
