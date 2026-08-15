using System.Net;
using BenchmarkDotNet.Attributes;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Requests;

namespace Kkdev92.HealthData.Benchmarks;

/// <summary>
/// The overhead the pagination helper adds on top of raw list calls.
/// </summary>
/// <remarks>
/// A stub handler stands in for the network so the measurement is the SDK's own cost: request
/// building, the page loop, and deserialization. The comparison that matters is enumeration
/// against calling the raw list method in a loop.
/// </remarks>
[MemoryDiagnoser]
public class PaginationBenchmarks
{
    private HealthDataClient _client = null!;

    /// <summary>Pages served before the token runs out.</summary>
    [Params(10)]
    public int Pages { get; set; }

    /// <summary>Items per page.</summary>
    [Params(100)]
    public int ItemsPerPage { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var handler = new PagedStubHandler(Pages, ItemsPerPage);
        var httpClient = new HttpClient(handler) { BaseAddress = HealthDataApiMetadata.DefaultBaseAddress };
        _client = new HealthDataClient(httpClient);
    }

    [Benchmark(Description = "Enumerate every item across pages", Baseline = true)]
    public async Task<int> EnumerateAllPages()
    {
        var count = 0;

        await foreach (var _ in _client.Users.DataPoints.EnumerateAsync(
            new ListDataPointsRequest { Parent = "users/me/dataTypes/steps" }))
        {
            count++;
        }

        return count;
    }

    [Benchmark(Description = "Raw list loop, driving the token by hand")]
    public async Task<int> ListPagesManually()
    {
        var count = 0;
        string? pageToken = null;

        do
        {
            var page = await _client.Users.DataPoints.ListAsync(new ListDataPointsRequest
            {
                Parent = "users/me/dataTypes/steps",
                PageToken = pageToken,
            });

            count += page.DataPoints?.Count ?? 0;
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (pageToken is not null);

        return count;
    }

    [Benchmark(Description = "Single page, no enumeration")]
    public async Task<int> SinglePage()
    {
        var page = await _client.Users.DataPoints.ListAsync(
            new ListDataPointsRequest { Parent = "users/me/dataTypes/steps" });

        return page.DataPoints?.Count ?? 0;
    }

    /// <summary>Serves a fixed number of pages, then one without a token.</summary>
    private sealed class PagedStubHandler(int pages, int itemsPerPage) : HttpMessageHandler
    {
        private readonly string[] _bodies = BuildBodies(pages, itemsPerPage);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            var index = 0;

            var tokenAt = query.IndexOf("pageToken=P", StringComparison.Ordinal);

            if (tokenAt >= 0)
            {
                var start = tokenAt + "pageToken=P".Length;
                var end = start;

                while (end < query.Length && char.IsAsciiDigit(query[end]))
                {
                    end++;
                }

                index = int.Parse(query.AsSpan(start, end - start), provider: System.Globalization.CultureInfo.InvariantCulture);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies[index], System.Text.Encoding.UTF8, "application/json"),
            });
        }

        private static string[] BuildBodies(int pages, int itemsPerPage)
        {
            var bodies = new string[pages];

            for (var page = 0; page < pages; page++)
            {
                var builder = new System.Text.StringBuilder(itemsPerPage * 96);
                builder.Append("""{"dataPoints":[""");

                for (var i = 0; i < itemsPerPage; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    var invariant = System.Globalization.CultureInfo.InvariantCulture;

                    builder.Append(invariant, $"{{\"name\":\"users/me/dataTypes/steps/dataPoints/{page}-{i}\",")
                           .Append(invariant, $"\"steps\":{{\"count\":\"{i}\"}}}}");
                }

                builder.Append(']');

                if (page < pages - 1)
                {
                    builder.Append(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $",\"nextPageToken\":\"P{page + 1}\"");
                }

                bodies[page] = builder.Append('}').ToString();
            }

            return bodies;
        }
    }
}
