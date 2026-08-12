using System.Runtime.CompilerServices;

namespace Kkdev92.HealthData.Pagination;

/// <summary>
/// Turns a token-paginated operation into a stream of items.
/// </summary>
/// <remarks>
/// <para>
/// The raw list call stays the primary API and returns one page. This helper is the convenience
/// layer on top. No API here materializes an entire history into a
/// <c>List&lt;T&gt;</c>: a user's heart-rate history is unbounded, so the only safe shape is a
/// stream the caller can stop consuming.
/// </para>
/// <para>
/// Not every paginated request can be enumerated. <c>dataPoints.dailyRollUp</c> accepts a page
/// token but returns none, so no enumeration helper is generated for it; that asymmetry is
/// enforced by the generator's validator rather than discovered at run time.
/// </para>
/// </remarks>
public static class AsyncPageEnumerable
{
    /// <summary>
    /// Enumerates every item across pages, requesting the next page only as needed.
    /// </summary>
    /// <typeparam name="TResponse">The list response type.</typeparam>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <param name="fetchPage">Fetches one page for the given continuation token.</param>
    /// <param name="selectItems">Reads the items from a page.</param>
    /// <param name="selectNextPageToken">Reads the continuation token from a page.</param>
    /// <param name="cancellationToken">Stops enumeration, including an in-flight page fetch.</param>
    public static async IAsyncEnumerable<TItem> CreateAsync<TResponse, TItem>(
        Func<string?, CancellationToken, Task<TResponse>> fetchPage,
        Func<TResponse, IReadOnlyList<TItem>?> selectItems,
        Func<TResponse, string?> selectNextPageToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        ArgumentNullException.ThrowIfNull(selectItems);
        ArgumentNullException.ThrowIfNull(selectNextPageToken);

        string? pageToken = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await fetchPage(pageToken, cancellationToken).ConfigureAwait(false);

            foreach (var item in selectItems(page) ?? [])
            {
                yield return item;
            }

            var nextPageToken = selectNextPageToken(page);

            // An empty token means the same thing as no token; treating "" as a cursor would
            // request the first page forever.
            if (string.IsNullOrEmpty(nextPageToken))
            {
                yield break;
            }

            // A service that hands back the token it was just given would also loop forever.
            // Stopping is better than spending a caller's quota on it.
            if (string.Equals(nextPageToken, pageToken, StringComparison.Ordinal))
            {
                yield break;
            }

            pageToken = nextPageToken;
        }
    }
}
