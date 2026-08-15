using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Names;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The generated helpers over the DataPoint union.
/// </summary>
public sealed class UnionHelperTests
{
    [Fact]
    public void GetKindIdentifiesThePopulatedMember()
    {
        var heartRate = new DataPoint { HeartRate = new HeartRate { BeatsPerMinute = 58 } };
        var steps = new DataPoint { Steps = new Steps { Count = 1200 } };

        Assert.Equal(DataPointKind.HeartRate, heartRate.GetKind());
        Assert.Equal(DataPointKind.Steps, steps.GetKind());
    }

    [Fact]
    public void GetKindReportsNoneForAnEmptyDataPoint()
        => Assert.Equal(DataPointKind.None, new DataPoint { Name = "users/me/dataTypes/steps/dataPoints/1" }.GetKind());

    [Fact]
    public void GetValueReturnsThePopulatedMember()
    {
        var point = new DataPoint { Sleep = new Sleep { Type = Sleep.Types.Type.Stages } };

        var value = Assert.IsType<Sleep>(point.GetValue());
        Assert.Equal(Sleep.Types.Type.Stages, value.Type);
    }

    [Fact]
    public void GetValueReturnsNullWhenNothingIsSet()
        => Assert.Null(new DataPoint().GetValue());

    [Fact]
    public void TheKindEnumCoversEveryUnionMember()
    {
        // 42 measurement members plus None and Unknown. If Google adds a member, this number
        // moves and the public API snapshot records it.
        //
        // Forty-two, not forty-three: DataPoint has a forty-third message-typed property and it is
        // not a measurement. See DataSourceIsNotOneOfTheThingsADataPointCanBe.
        Assert.Equal(44, Enum.GetValues<DataPointKind>().Length);

        Assert.Equal(0, (int)DataPointKind.None);
        Assert.Equal(1, (int)DataPointKind.Unknown);
    }

    /// <summary>
    /// The member that broke everything against the real service.
    /// </summary>
    /// <remarks>
    /// <c>dataSource</c> records which device or app produced a measurement, and Google says every
    /// data point carries one: "Each health data point, regardless of the complexity or data model
    /// ... must retain information about its source of origin." Counting it as a union member made
    /// <c>GetKind</c> answer <c>DataSource</c> for every real data point, because it sorts before
    /// heartRate, sleep and steps. Fixtures never set it, so nothing caught it until live data
    /// arrived. It is excluded in semantics.json; this pins the behaviour rather than the setting.
    /// </remarks>
    [Fact]
    public void DataSourceIsNotOneOfTheThingsADataPointCanBe()
    {
        var point = new DataPoint
        {
            DataSource = new DataSource { Platform = DataSource.Types.Platform.FromValue("ANDROID") },
            Steps = new Steps { Count = 4200 },
        };

        Assert.Equal(DataPointKind.Steps, point.GetKind());
        Assert.IsType<Steps>(point.GetValue());

        // Still reachable; it simply is not an alternative.
        Assert.Equal("ANDROID", point.DataSource.Platform?.Value);

        Assert.DoesNotContain("DataSource", Enum.GetNames<DataPointKind>());
    }

    [Fact]
    public void RollupDataPointsGetTheSameTreatment()
    {
        var rollup = new RollupDataPoint { Steps = new StepsRollupValue { CountSum = 8000 } };

        Assert.Equal(RollupDataPointKind.Steps, rollup.GetKind());
        Assert.IsType<StepsRollupValue>(rollup.GetValue());

        // 21 rollup members plus None and Unknown.
        Assert.Equal(23, Enum.GetValues<RollupDataPointKind>().Length);
    }

    /// <summary>
    /// The daily roll-up is its own schema, and it carries the window it aggregates over.
    /// </summary>
    /// <remarks>
    /// <c>civilStartTime</c> and <c>civilEndTime</c> are message-typed and both sort before every
    /// measurement, so counting them as members would answer <c>CivilEndTime</c> for every row —
    /// dataSource again, in a schema nobody had emitted helpers for yet. They stay properties,
    /// because a daily value is meaningless without knowing which day it is.
    /// </remarks>
    [Fact]
    public void ADailyRollUpKnowsWhichDayItIsWithoutThatBeingItsKind()
    {
        var day = new DailyRollupDataPoint
        {
            CivilStartTime = new CivilDateTime { Date = new Date { Year = 2026, Month = 8, Day = 10 } },
            CivilEndTime = new CivilDateTime { Date = new Date { Year = 2026, Month = 8, Day = 11 } },
            Steps = new StepsRollupValue { CountSum = 8432 },
        };

        Assert.Equal(DailyRollupDataPointKind.Steps, day.GetKind());
        Assert.IsType<StepsRollupValue>(day.GetValue());
        Assert.Equal(10, day.CivilStartTime.Date?.Day);

        // 23 measurement members plus None and Unknown.
        Assert.Equal(25, Enum.GetValues<DailyRollupDataPointKind>().Length);
        Assert.DoesNotContain("CivilStartTime", Enum.GetNames<DailyRollupDataPointKind>());
    }

    [Fact]
    public void AMemberAddedAfterGenerationDoesNotBreakDeserialization()
    {
        // GetKind reports Unknown rather than throwing. The payload still round-trips; there is
        // simply no typed accessor for a member this contract has never heard of.
        var point = System.Text.Json.JsonSerializer.Deserialize(
            """{"name":"users/me/dataTypes/x/dataPoints/1","brandNewMetric":{"value":1}}""",
            Serialization.HealthDataJson.ReadInfo<DataPoint>())!;

        // Unknown, not None: there is no typed accessor, but the member is there.
        Assert.Equal(DataPointKind.Unknown, point.GetKind());
        Assert.Null(point.GetValue());
        Assert.Equal("users/me/dataTypes/x/dataPoints/1", point.Name);

        // And it survives being written back out. That is the part that matters: dataPoints.patch
        // takes a DataPoint, so a member this contract has never heard of would otherwise be
        // deleted from a person's record by an application that only meant to read it.
        var round = System.Text.Json.JsonSerializer.Serialize(
            point, Serialization.HealthDataJson.WriteInfo<DataPoint>());

        Assert.Contains("brandNewMetric", round, StringComparison.Ordinal);
    }
}
