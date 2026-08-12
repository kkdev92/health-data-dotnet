using System.Text.Json;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Exercises the actual generated contract, not a replica.
/// </summary>
/// <remarks>
/// <see cref="SerializationContractTests"/> pins the serialization design against handwritten
/// replicas. These tests confirm the generator really produced that design across the whole
/// contract.
/// </remarks>
public sealed class GeneratedModelTests
{
    [Fact]
    public void ProfileRoundTripsAndOmitsOutputOnlyProperties()
    {
        const string payload = """
            {
              "name": "users/me/profile",
              "age": 41,
              "autoRunningStrideLengthMm": 1150,
              "membershipStartDate": { "year": 2019, "month": 3, "day": 14 },
              "userConfiguredWalkingStrideLengthMm": 700
            }
            """;

        var profile = JsonSerializer.Deserialize(payload, HealthDataJson.ReadInfo<Profile>())!;

        Assert.Equal("users/me/profile", profile.Name);
        Assert.Equal(41, profile.Age);
        Assert.Equal(1150, profile.AutoRunningStrideLengthMm);
        Assert.Equal(2019, profile.MembershipStartDate!.Year);

        var written = JsonSerializer.Serialize(profile, HealthDataJson.WriteInfo<Profile>());

        // Output only in Discovery, so never sent back.
        Assert.DoesNotContain("autoRunningStrideLengthMm", written, StringComparison.Ordinal);
        Assert.DoesNotContain("membershipStartDate", written, StringComparison.Ordinal);

        // Writable fields survive.
        Assert.Contains("\"age\":41", written, StringComparison.Ordinal);
        Assert.Contains("\"userConfiguredWalkingStrideLengthMm\":700", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Int64ValuesTravelAsStrings()
    {
        var heartRate = JsonSerializer.Deserialize(
            """{"beatsPerMinute":"72"}""", HealthDataJson.ReadInfo<HeartRate>())!;

        Assert.Equal(72L, heartRate.BeatsPerMinute);

        var written = JsonSerializer.Serialize(heartRate, HealthDataJson.WriteInfo<HeartRate>());
        Assert.Contains("\"beatsPerMinute\":\"72\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void SleepStageUsesOpenEnumsAndTimestampPrimitives()
    {
        const string payload = """
            {
              "type": "DEEP",
              "startTime": "2026-08-09T22:15:00Z",
              "endTime": "2026-08-09T23:05:00Z",
              "startUtcOffset": "-14400s",
              "createTime": "2026-08-10T06:00:00Z"
            }
            """;

        var stage = JsonSerializer.Deserialize(payload, HealthDataJson.ReadInfo<SleepStage>())!;

        Assert.Equal(SleepStageType.Deep, stage.Type);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 22, 15, 0, TimeSpan.Zero), stage.StartTime!.Value.Value);
        Assert.Equal(new GoogleDuration(-14400, 0), stage.StartUtcOffset);

        var written = JsonSerializer.Serialize(stage, HealthDataJson.WriteInfo<SleepStage>());

        Assert.Contains("\"type\":\"DEEP\"", written, StringComparison.Ordinal);
        Assert.Contains("\"startUtcOffset\":\"-14400s\"", written, StringComparison.Ordinal);

        // createTime and updateTime are output only on SleepStage.
        Assert.DoesNotContain("createTime", written, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEnumValueIsPreserved()
    {
        // Google adds enum values additively; a new one must not break an existing client.
        var stage = JsonSerializer.Deserialize(
            """{"type":"MICRO_AROUSAL"}""", HealthDataJson.ReadInfo<SleepStage>())!;

        Assert.Equal("MICRO_AROUSAL", stage.Type!.Value.Value);
        Assert.NotEqual(SleepStageType.Deep, stage.Type!.Value);
    }

    [Fact]
    public void UnknownDataPointMemberDoesNotBreakDeserialization()
    {
        // DataPoint is a union of 42 measurement members — 43 message-typed properties less
        // dataSource, which is metadata rather than an alternative. A member added after
        // generation must be tolerated, not fatal.
        const string payload = """
            {
              "name": "users/me/dataTypes/heart-rate/dataPoints/1",
              "heartRate": { "beatsPerMinute": "58" },
              "brandNewMetricFrom2027": { "value": 1 }
            }
            """;

        var dataPoint = JsonSerializer.Deserialize(payload, HealthDataJson.ReadInfo<DataPoint>())!;

        Assert.Equal(58L, dataPoint.HeartRate!.BeatsPerMinute);
        Assert.Null(dataPoint.Steps);
    }

    [Fact]
    public void OperationKeepsResponseAndMetadataLossless()
    {
        // No public polling resource exists, so Operation is surfaced as-is and its payload is
        // preserved rather than interpreted.
        const string payload = """
            {
              "name": "operations/abc",
              "done": true,
              "response": { "@type": "type.googleapis.com/x", "nested": [1, 2, 3] }
            }
            """;

        var operation = JsonSerializer.Deserialize(payload, HealthDataJson.ReadInfo<Operation>())!;

        Assert.True(operation.Done);
        Assert.Equal(3, operation.Response!.Value.GetProperty("nested").GetArrayLength());
    }

    [Fact]
    public void EveryReachableSchemaIsRegisteredWithTheSerializer()
    {
        // Reflection is disabled, so a model missing from the generated context would only fail
        // at run time, on the one call that happens to use it.
        // The root namespace also holds request types, resource clients and the client itself,
        // none of which cross the wire. The invariant that matters is that every type carrying
        // wire-mapped properties has a contract in both directions.
        var candidates = typeof(HealthDataApiMetadata).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsPublic: true, IsAbstract: false }
                        && t.Namespace == "Kkdev92.HealthData")
                            .ToArray();

        var registered = candidates.Where(IsRegistered).ToArray();

        Assert.Equal(138, registered.Length);

        foreach (var type in candidates.Where(HasWireMappedProperties))
        {
            Assert.Contains(type, registered);
            Assert.NotNull(HealthDataJson.WriteOptions.GetTypeInfo(type));
        }

        static bool HasWireMappedProperties(Type type)
            => type.GetProperties().Any(p =>
                p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).Length > 0);

        static bool IsRegistered(Type type)
        {
            try
            {
                // Reflection is disabled, so an unregistered type throws rather than resolving.
                return HealthDataJson.ReadOptions.GetTypeInfo(type) is not null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }
    }

    [Fact]
    public void OutputOnlyTableCoversTheSchemasDiscoveryMarksReadOnly()
    {
        // 27 of the 147 declared schemas carry readOnly properties, and all 27 are reachable from
        // the allowlisted operations: none of them belong to the excluded SMART Health Links
        // surface.
        Assert.Equal(27, HealthDataOutputOnlyProperties.ByType.Count);
        Assert.Equal(["createTime", "state", "updateTime"], HealthDataOutputOnlyProperties.ByType[typeof(Subscriber)]);
    }
}
