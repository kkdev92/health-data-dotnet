using System.Text.Json;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// A union carries one measurement, and sending two is caught before the request leaves.
/// </summary>
/// <remarks>
/// <para>
/// <c>DataPoint</c> is a name plus forty-two mutually exclusive measurements, and Discovery has no
/// way to say "exactly one of these" — so the properties are all settable and nothing stopped a
/// caller writing two. The service does not accept it, and what came back said nothing about which
/// pair was the problem.
/// </para>
/// <para>
/// Setters cannot be the answer: <c>dataPoints.patch</c> is read-modify-send, so a point that
/// arrived with a measurement has to be able to carry it back. The check therefore lives where the
/// request is written, which is the last place both facts are true — the object is finished and it
/// has not been sent.
/// </para>
/// </remarks>
public sealed class UnionValidationTests
{
    private static string Write<T>(T value) => JsonSerializer.Serialize(value, HealthDataJson.WriteInfo<T>());

    [Fact]
    public void OneMeasurementSerializes()
    {
        var json = Write(new DataPoint { Steps = new Steps { Count = 100 } });

        Assert.Contains("\"steps\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoMeasurementsAreRefusedWithBothNamed()
    {
        var point = new DataPoint
        {
            Steps = new Steps { Count = 100 },
            Weight = new Weight { WeightGrams = 70000 },
        };

        var thrown = Assert.Throws<InvalidOperationException>(() => Write(point));

        // Both, not just a count: the point of the message is to say which two.
        Assert.Contains("steps", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("weight", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DataPoint), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasurementBesideItsMetadataIsFine()
    {
        // dataSource is on every data point and is not an alternative. Counting it as one would
        // reject every real measurement — which is the same mistake that made GetKind answer
        // DataSource for everything before semantics.json excluded it.
        var json = Write(new DataPoint
        {
            Steps = new Steps { Count = 100 },
            DataSource = new DataSource { RecordingMethod = DataSource.Types.RecordingMethod.PassivelyMeasured },
        });

        Assert.Contains("\"steps\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dataSource\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMeasurementIsFine()
    {
        // batchDelete sends names and nothing else, and a patch that changes only the interval is
        // a real request. "Not more than one" is the rule, not "exactly one".
        var json = Write(new DataPoint { Name = "users/me/dataTypes/steps/dataPoints/1" });

        Assert.Contains("\"name\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingBackTwoMeasurementsStillWorks()
    {
        // The check is on the write contract only. A response that carries something this contract
        // did not expect must still deserialize — refusing it would lose a person's data to a
        // client-side rule about a shape the service chose.
        const string Json = """{"steps":{"count":"100"},"weight":{"weight":{"grams":"70000"}}}""";

        var point = JsonSerializer.Deserialize(Json, HealthDataJson.ReadInfo<DataPoint>());

        Assert.NotNull(point?.Steps);
        Assert.NotNull(point?.Weight);
    }

    [Fact]
    public void TheRollUpUnionsAreCheckedToo()
    {
        // Three schemas are unions. Roll-ups are computed by the service and not sent back, so
        // this one is about a caller building a request out of one it received.
        var thrown = Assert.Throws<InvalidOperationException>(() => Write(new RollupDataPoint
        {
            Steps = new StepsRollupValue { CountSum = 1 },
            Distance = new DistanceRollupValue { MillimetersSum = 1000 },
        }));

        Assert.Contains(nameof(RollupDataPoint), thrown.Message, StringComparison.Ordinal);
    }
}
