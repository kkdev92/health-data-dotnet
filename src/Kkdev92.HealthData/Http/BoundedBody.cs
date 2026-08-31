using System.Buffers;

namespace Kkdev92.HealthData.Http;

/// <summary>
/// Reads a response body up to a limit, and no further.
/// </summary>
/// <remarks>
/// <para>
/// Three places needed this and wrote it three ways: a rented buffer here, and two hand-rolled
/// chunk loops over a <see cref="System.IO.MemoryStream"/> elsewhere. What differed between them
/// was only what to do when the limit is passed — an error body can be given up on, a token
/// response and a signing keyset cannot — so that decision stays with the caller and the reading
/// does not.
/// </para>
/// <para>
/// The rented buffer is returned cleared. What passes through here is a token endpoint's answer,
/// a signing keyset, or a health API error carrying user ids and data types; handing the array to
/// the next renter with those still in it would put them somewhere nobody thought to look.
/// </para>
/// </remarks>
internal static class BoundedBody
{
    /// <summary>
    /// Reads the whole body, or gives up if it is larger than <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="content">The response body.</param>
    /// <param name="maxBytes">The most that will be read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The bytes, or <see langword="null"/> when the body is larger than the limit — either
    /// because it said so, or because it turned out to be.
    /// </returns>
    public static async Task<byte[]?> ReadAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        // The declared length first, so an oversized body costs nothing to refuse. A server is
        // free to declare nothing at all, which is why the read is bounded as well.
        if (content.Headers.ContentLength is > int.MaxValue || content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            // One more than the limit: reading exactly the limit cannot tell a body that fits from
            // one that was cut off at it.
            var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);

            try
            {
                var read = await stream
                    .ReadAtLeastAsync(
                        buffer.AsMemory(0, maxBytes + 1),
                        maxBytes + 1,
                        throwOnEndOfStream: false,
                        cancellationToken)
                    .ConfigureAwait(false);

                return read > maxBytes ? null : buffer.AsSpan(0, read).ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }
}
