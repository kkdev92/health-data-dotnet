using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Tests;

// The types below are handwritten replicas of generated output, declared at namespace level
// exactly as the emitter declares them. They exist so the serialization contract is verified
// independently of the generator: a change in System.Text.Json behaviour shows up here rather
// than as 138 broken models.

/// <summary>Replica of a generated open enum (ADR-0005).</summary>
[JsonConverter(typeof(OpenStringEnumConverter<SleepStageTypeReplica>))]
public readonly partial record struct SleepStageTypeReplica(string Value) : IOpenStringValue<SleepStageTypeReplica>
{
    /// <summary>Creates a value, including one not known at generation time.</summary>
    public static SleepStageTypeReplica FromValue(string value) => new(value);

    /// <summary>AWAKE.</summary>
    public static SleepStageTypeReplica Awake => new("AWAKE");

    /// <summary>DEEP.</summary>
    public static SleepStageTypeReplica Deep => new("DEEP");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Replica of a generated model.</summary>
public sealed partial class SleepStageReplica
{
    /// <summary>An open enum property.</summary>
    [JsonPropertyName("type")]
    public SleepStageTypeReplica? Type { get; init; }

    /// <summary>A google-datetime property.</summary>
    [JsonPropertyName("startTime")]
    [JsonConverter(typeof(GoogleTimestampConverter))]
    public GoogleTimestamp? StartTime { get; init; }

    /// <summary>A google-duration property.</summary>
    [JsonPropertyName("startUtcOffset")]
    [JsonConverter(typeof(GoogleDurationConverter))]
    public GoogleDuration? StartUtcOffset { get; init; }

    /// <summary>An int64 property, which travels as a JSON string.</summary>
    [JsonPropertyName("beatsPerMinute")]
    [JsonConverter(typeof(Int64StringConverter))]
    public long? BeatsPerMinute { get; init; }

    /// <summary>An array property.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>An open-ended object property.</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    /// <summary>Output only: readable, never transmitted (ADR-0006).</summary>
    [JsonPropertyName("createTime")]
    [JsonConverter(typeof(GoogleTimestampConverter))]
    public GoogleTimestamp? CreateTime { get; init; }
}

/// <summary>Replica of the generated serializer context.</summary>
[JsonSerializable(typeof(SleepStageReplica))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

public sealed class SerializationContractTests
{
    // Replica of the generated read-only table used to build the write contract.
    private static readonly Dictionary<Type, string[]> OutputOnlyProperties = new()
    {
        [typeof(SleepStageReplica)] = ["createTime"],
    };

    private static void StripOutputOnly(JsonTypeInfo typeInfo)
    {
        if (!OutputOnlyProperties.TryGetValue(typeInfo.Type, out var names))
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (Array.IndexOf(names, typeInfo.Properties[i].Name) >= 0)
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }

    private static readonly JsonSerializerOptions ReadOptions =
        new() { TypeInfoResolver = TestJsonContext.Default };

    private static readonly JsonSerializerOptions WriteOptions =
        new() { TypeInfoResolver = TestJsonContext.Default.WithAddedModifier(StripOutputOnly) };

    private static JsonTypeInfo<SleepStageReplica> ReadInfo
        => (JsonTypeInfo<SleepStageReplica>)ReadOptions.GetTypeInfo(typeof(SleepStageReplica));

    private static JsonTypeInfo<SleepStageReplica> WriteInfo
        => (JsonTypeInfo<SleepStageReplica>)WriteOptions.GetTypeInfo(typeof(SleepStageReplica));

    private const string Payload = """
        {
          "type": "DEEP",
          "startTime": "2026-08-09T12:34:56.789Z",
          "startUtcOffset": "-14400s",
          "beatsPerMinute": "9007199254740993",
          "tags": ["a", "b"],
          "metadata": {"nested": 1},
          "createTime": "2026-08-01T00:00:00Z"
        }
        """;

    /// <summary>
    /// The SDK's shared serializer options cannot be changed, not even before their first use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Options lock themselves the first time they serialize, so this is about the window before
    /// that. Code that reached <see cref="HealthDataJson.WriteOptions"/> first could change, say,
    /// how nulls are written, and every request the process sent would change with it — a patch
    /// that sends nulls asks the service to clear fields.
    /// </para>
    /// <para>
    /// Checked in a load context of its own. Any other test in this process that serializes locks
    /// the options as a side effect, which would let this pass without the change it pins.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSharedOptionsAreReadOnlyBeforeAnythingUsesThem()
    {
        var context = new AssemblyLoadContext(nameof(TheSharedOptionsAreReadOnlyBeforeAnythingUsesThem), isCollectible: true);

        try
        {
            var json = context
                .LoadFromAssemblyPath(typeof(HealthDataJson).Assembly.Location)
                .GetType(typeof(HealthDataJson).FullName!, throwOnError: true)!;

            foreach (var name in new[] { nameof(HealthDataJson.ReadOptions), nameof(HealthDataJson.WriteOptions) })
            {
                var options = (JsonSerializerOptions)json.GetProperty(name)!.GetValue(null)!;

                Assert.True(options.IsReadOnly, $"{name} can be changed before it is first used.");
            }
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// The tables the write contract is built from cannot be changed through a cast.
    /// </summary>
    /// <remarks>
    /// Both are declared read-only. A declaration is not a guarantee when the object behind it is a
    /// writable dictionary: removing a type from the output-only table stops its read-only fields
    /// being stripped from requests, and removing one from the union table stops two measurements
    /// in one data point being refused.
    /// </remarks>
    [Fact]
    public void TheWriteContractTablesCannotBeChanged()
    {
        Assert.False(HealthDataOutputOnlyProperties.ByType is IDictionary<Type, string[]> { IsReadOnly: false });
        Assert.False(HealthDataUnionMembers.ByType is IDictionary<Type, string[]> { IsReadOnly: false });
    }

    /// <summary>
    /// Rewriting the names in the public tables does not change what the write contract does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tables are frozen, but their values are arrays, and an array's elements can always be
    /// assigned. A caller that rewrote the output-only names for a type would stop those fields
    /// being stripped from requests; one that rewrote a union's members would let two measurements
    /// through in one data point.
    /// </para>
    /// <para>
    /// Checked in a load context of its own, because the rewrite is to shared static state and
    /// would otherwise reach every other test in this process.
    /// </para>
    /// </remarks>
    [Fact]
    public void RewritingThePublicTablesDoesNotChangeTheWriteContract()
    {
        var context = new AssemblyLoadContext(nameof(RewritingThePublicTablesDoesNotChangeTheWriteContract), isCollectible: true);

        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(HealthDataJson).Assembly.Location);
            Type Load(Type type) => assembly.GetType(type.FullName!, throwOnError: true)!;

            IReadOnlyDictionary<Type, string[]> Table(Type owner)
                => (IReadOnlyDictionary<Type, string[]>)Load(owner).GetField("ByType")!.GetValue(null)!;

            JsonTypeInfo WriteInfo(Type model)
                => (JsonTypeInfo)Load(typeof(HealthDataJson)).GetMethod(nameof(HealthDataJson.WriteInfo))!
                    .MakeGenericMethod(model).Invoke(null, null)!;

            var profile = Load(typeof(Profile));
            var dataPoint = Load(typeof(DataPoint));

            Array.Fill(Table(typeof(HealthDataOutputOnlyProperties))[profile], "rewritten");
            Array.Fill(Table(typeof(HealthDataUnionMembers))[dataPoint], "rewritten");

            Assert.DoesNotContain(WriteInfo(profile).Properties, property => property.Name == "membershipStartDate");

            var point = Activator.CreateInstance(dataPoint)!;
            dataPoint.GetProperty(nameof(DataPoint.HeartRate))!.SetValue(point, Activator.CreateInstance(Load(typeof(HeartRate))));
            dataPoint.GetProperty(nameof(DataPoint.Steps))!.SetValue(point, Activator.CreateInstance(Load(typeof(Steps))));

            Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(point, WriteInfo(dataPoint)));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void ReflectionIsDisabled()
    {
        // If this ever becomes true, a missing [JsonSerializable] would silently fall back to
        // reflection and break Native AOT only at run time.
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public void DeserializesEveryWireForm()
    {
        var stage = JsonSerializer.Deserialize(Payload, ReadInfo)!;

        Assert.Equal(SleepStageTypeReplica.Deep, stage.Type);
        Assert.Equal("DEEP", stage.Type!.Value.Value);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 12, 34, 56, 789, TimeSpan.Zero), stage.StartTime!.Value.Value);
        Assert.Equal(new GoogleDuration(-14400, 0), stage.StartUtcOffset);

        // Beyond 2^53: the exact reason int64 travels as a string.
        Assert.Equal(9007199254740993L, stage.BeatsPerMinute);

        Assert.Equal(["a", "b"], stage.Tags);
        Assert.Equal(1, stage.Metadata!.Value.GetProperty("nested").GetInt32());
        Assert.NotNull(stage.CreateTime);
    }

    [Fact]
    public void WriteContractOmitsReadOnlyProperties()
    {
        var stage = JsonSerializer.Deserialize(Payload, ReadInfo)!;
        var written = JsonSerializer.Serialize(stage, WriteInfo);

        // Readable...
        Assert.NotNull(stage.CreateTime);

        // ...but never transmitted (ADR-0006).
        Assert.DoesNotContain("createTime", written, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"DEEP\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Int64IsWrittenAsAString()
    {
        var written = JsonSerializer.Serialize(new SleepStageReplica { BeatsPerMinute = 42 }, WriteInfo);
        Assert.Contains("\"beatsPerMinute\":\"42\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEnumValuesRoundTripUnchanged()
    {
        // A value Google adds after this SDK was generated must not break deserialization.
        var stage = JsonSerializer.Deserialize("""{"type":"CORE_SLEEP_ADDED_IN_2027"}""", ReadInfo)!;

        Assert.Equal("CORE_SLEEP_ADDED_IN_2027", stage.Type!.Value.Value);
        Assert.Contains("\"CORE_SLEEP_ADDED_IN_2027\"", JsonSerializer.Serialize(stage, WriteInfo), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPropertiesAreIgnored()
    {
        // Additive schema changes must not break existing consumers.
        var stage = JsonSerializer.Deserialize(
            """{"type":"AWAKE","fieldAddedNextYear":{"deeply":["nested"]}}""", ReadInfo)!;

        Assert.Equal(SleepStageTypeReplica.Awake, stage.Type);
    }

    [Fact]
    public void AbsentPropertiesStayNull()
    {
        var stage = JsonSerializer.Deserialize("{}", ReadInfo)!;

        Assert.Null(stage.Type);
        Assert.Null(stage.StartTime);
        Assert.Null(stage.BeatsPerMinute);
        Assert.Null(stage.Tags);
    }

    [Fact]
    public void TimestampAndDurationRoundTripExactly()
    {
        var stage = JsonSerializer.Deserialize(Payload, ReadInfo)!;
        var written = JsonSerializer.Serialize(stage, WriteInfo);

        Assert.Contains("\"startTime\":\"2026-08-09T12:34:56.789Z\"", written, StringComparison.Ordinal);
        Assert.Contains("\"startUtcOffset\":\"-14400s\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ADoubleTheServiceCouldNotComputeArrivesAsNaN()
    {
        // The service sends "NaN" as a JSON string, which is what the protobuf JSON mapping
        // prescribes and what System.Text.Json rejects by default. Observed live on
        // daily-sleep-temperature-derivations: six points in 1,719 carried it, and one of them
        // failed the entire response — 1,713 good points lost to a strict number reader.
        const string Payload = """
            {"date":{"year":2024,"month":9,"day":25},
             "nightlyTemperatureCelsius":31.878450363196126,
             "baselineTemperatureCelsius":"NaN",
             "relativeNightlyStddev30dCelsius":"NaN"}
            """;

        var point = JsonSerializer.Deserialize(Payload, HealthDataJson.ReadInfo<DailySleepTemperatureDerivations>())!;

        Assert.Equal(31.878450363196126, point.NightlyTemperatureCelsius);
        Assert.True(double.IsNaN(point.BaselineTemperatureCelsius!.Value));
        Assert.True(double.IsNaN(point.RelativeNightlyStddev30dCelsius!.Value));
    }

    [Fact]
    public void ANaNThatWasReadCanBeSentBack()
    {
        // This API has no field mask, so an update is read, change, send whole. A value that is
        // readable but unsendable would make the point uneditable for reasons the caller cannot
        // see or fix.
        var point = new DailySleepTemperatureDerivations { BaselineTemperatureCelsius = double.NaN };

        var written = JsonSerializer.Serialize(
            point, HealthDataJson.WriteInfo<DailySleepTemperatureDerivations>());

        Assert.Contains("\"baselineTemperatureCelsius\":\"NaN\"", written, StringComparison.Ordinal);
    }
}
