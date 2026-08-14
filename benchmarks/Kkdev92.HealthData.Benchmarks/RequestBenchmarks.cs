using BenchmarkDotNet.Attributes;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Benchmarks;

/// <summary>
/// URI construction and open enum conversion.
/// </summary>
/// <remarks>
/// Both sit on the per-request hot path, and both are places where a naive implementation
/// allocates far more than it needs to.
/// </remarks>
[MemoryDiagnoser]
public class RequestBenchmarks
{
    private const string ResourceName = "users/1234567890/dataTypes/heart-rate/dataPoints/abcdef";
    private const string ParentName = "users/1234567890/dataTypes/heart-rate";

    [Benchmark(Description = "URI: path only")]
    public string BuildPathOnly()
        => new HealthDataRequestBuilder("v4/{+name}")
            .SetPath("name", ResourceName)
            .Build();

    [Benchmark(Description = "URI: path with custom-method suffix")]
    public string BuildCustomMethodPath()
        => new HealthDataRequestBuilder("v4/{+parent}/dataPoints:rollUp")
            .SetPath("parent", ParentName)
            .Build();

    [Benchmark(Description = "URI: path and four query parameters")]
    public string BuildPathAndQuery()
        => new HealthDataRequestBuilder("v4/{+parent}/dataPoints")
            .SetPath("parent", ParentName)
            .AddQuery("filter", "start_time >= \"2026-08-01T00:00:00Z\"")
            .AddQuery("pageSize", 1000)
            .AddQuery("pageToken", "CBIiCggBEgQIARAB")
            .AddQuery("dataSourceFamily", "GOOGLE_FIT")
            .Build();

    [Benchmark(Description = "URI: escaping a reserved character")]
    public string BuildWithReservedCharacter()
        => new HealthDataRequestBuilder("v4/{+name}")
            .SetPath("name", "users/me/dataTypes/exercise/dataPoints/a?b#c")
            .Build();

    [Benchmark(Description = "Open enum: known value")]
    public string OpenEnumKnown() => SleepStage.Types.Type.Deep.Value;

    [Benchmark(Description = "Open enum: construct from wire value")]
    public SleepStage.Types.Type OpenEnumFromValue() => SleepStage.Types.Type.FromValue("REM");

    [Benchmark(Description = "Open enum: compare")]
    public bool OpenEnumEquals() => SleepStage.Types.Type.FromValue("DEEP") == SleepStage.Types.Type.Deep;

    [Benchmark(Description = "GoogleTimestamp parse")]
    public GoogleTimestamp ParseTimestamp() => GoogleTimestamp.Parse("2026-08-10T12:34:56.789Z");

    [Benchmark(Description = "GoogleTimestamp format")]
    public string FormatTimestamp() => GoogleTimestamp.Parse("2026-08-10T12:34:56.789Z").ToString();

    [Benchmark(Description = "GoogleDuration parse")]
    public GoogleDuration ParseDuration() => GoogleDuration.Parse("-14400s");

    [Benchmark(Description = "GoogleDuration format (fractional)")]
    public string FormatDuration() => new GoogleDuration(12, 123_456_789).ToString();
}
