using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Requests;
using Kkdev92.HealthData.Serialization;
using Kkdev92.HealthData.Names;

namespace Kkdev92.HealthData.Benchmarks;

/// <summary>
/// Deserialization and serialization cost.
/// </summary>
/// <remarks>
/// The target is client-side overhead, not the network. What these
/// measure is whether the source-generated contract and the custom converters stay allocation-lean
/// as the contract grows.
/// </remarks>
[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private const string ProfileJson = """
        {"name":"users/me/profile","age":41,"autoRunningStrideLengthMm":1150,
         "autoWalkingStrideLengthMm":700,"membershipStartDate":{"year":2019,"month":3,"day":14},
         "userConfiguredRunningStrideLengthMm":1160,"userConfiguredWalkingStrideLengthMm":710}
        """;

    private byte[] _profileUtf8 = [];
    private byte[] _dataPointPageUtf8 = [];
    private byte[] _largeHeartRatePageUtf8 = [];
    private DataPoint _dataPointToWrite = new();
    private Profile _profileToWrite = new();

    /// <summary>Data points in a page, matching a realistic list call.</summary>
    [Params(1000)]
    public int PageSize { get; set; }

    /// <summary>Samples in a large historical heart-rate response.</summary>
    [Params(10_000)]
    public int LargePageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _profileUtf8 = Encoding.UTF8.GetBytes(ProfileJson);
        _dataPointPageUtf8 = Encoding.UTF8.GetBytes(BuildDataPointPage(PageSize));
        _largeHeartRatePageUtf8 = Encoding.UTF8.GetBytes(BuildHeartRatePage(LargePageSize));

        _profileToWrite = JsonSerializer.Deserialize(_profileUtf8, HealthDataJson.ReadInfo<Profile>())!;

        _dataPointToWrite = new DataPoint
        {
            Name = "users/me/dataTypes/heart-rate/dataPoints/1",
            HeartRate = new HeartRate
            {
                BeatsPerMinute = 58,
                SampleTime = new ObservationSampleTime { PhysicalTime = GoogleTimestamp.Parse("2026-08-10T12:00:00Z") },
            },
        };
    }

    [Benchmark(Description = "Profile deserialize")]
    public Profile DeserializeProfile()
        => JsonSerializer.Deserialize(_profileUtf8, HealthDataJson.ReadInfo<Profile>())!;

    [Benchmark(Description = "DataPoint page deserialize")]
    public ListDataPointsResponse DeserializeDataPointPage()
        => JsonSerializer.Deserialize(_dataPointPageUtf8, HealthDataJson.ReadInfo<ListDataPointsResponse>())!;

    [Benchmark(Description = "Large heart-rate response deserialize")]
    public ListDataPointsResponse DeserializeLargeHeartRatePage()
        => JsonSerializer.Deserialize(_largeHeartRatePageUtf8, HealthDataJson.ReadInfo<ListDataPointsResponse>())!;

    [Benchmark(Description = "Request serialize (write contract)")]
    public string SerializeDataPoint()
        => JsonSerializer.Serialize(_dataPointToWrite, HealthDataJson.WriteInfo<DataPoint>());

    [Benchmark(Description = "Request serialize with output-only stripping")]
    public string SerializeProfile()
        => JsonSerializer.Serialize(_profileToWrite, HealthDataJson.WriteInfo<Profile>());

    private static string BuildDataPointPage(int count)
    {
        var builder = new StringBuilder(count * 128);
        builder.Append("""{"dataPoints":[""");

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            // Built with Append rather than an interpolated raw string: JSON's closing braces
            // fight the raw-string delimiters and the result is unreadable either way.
            builder.Append(Invariant, $"{{\"name\":\"users/me/dataTypes/steps/dataPoints/{i}\",")
                .Append(Invariant, $"\"steps\":{{\"count\":\"{i * 7}\"}}}}");
        }

        return builder.Append("""],"nextPageToken":"CBI"}""").ToString();
    }

    private static string BuildHeartRatePage(int count)
    {
        var builder = new StringBuilder(count * 192);
        builder.Append("""{"dataPoints":[""");

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            // The realistic shape: an int64-as-string plus the physical/offset time pair that
            // every health record carries.
            builder.Append(Invariant, $"{{\"name\":\"users/me/dataTypes/heart-rate/dataPoints/{i}\",")
                .Append(Invariant, $"\"heartRate\":{{\"beatsPerMinute\":\"{55 + (i % 40)}\",")
                .Append(Invariant, $"\"sampleTime\":{{\"physicalTime\":\"2026-08-10T12:00:{i % 60:D2}Z\",")
                .Append("\"utcOffset\":\"-14400s\"}}}");
        }

        return builder.Append("""],"nextPageToken":"CBI"}""").ToString();
    }

    private static System.Globalization.CultureInfo Invariant => System.Globalization.CultureInfo.InvariantCulture;
}
