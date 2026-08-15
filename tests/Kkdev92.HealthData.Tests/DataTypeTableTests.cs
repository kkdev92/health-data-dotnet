namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The data type table, and the line it does not cross.
/// </summary>
/// <remarks>
/// <para>
/// Which operations a data type supports appears nowhere in Discovery: the REST path is the
/// generic <c>dataTypes/{dataTypesId}</c>, so asking <c>steps</c> for a <c>get</c> compiles, is
/// sent, and answers <c>400 UNSUPPORTED_DATA_TYPE_ACTION</c> — about the type, not about the id.
/// The generator has always read the table and shipped none of it, so the one application built on
/// this SDK copied it out of this repository, with a comment saying there was nowhere else to get
/// it.
/// </para>
/// <para>
/// It is published as metadata. The snapshot's own wording is that capabilities "must not become
/// client-side hard validation; the server remains the authority", and these tests hold that as
/// well as the contents.
/// </para>
/// </remarks>
public sealed class DataTypeTableTests
{
    [Fact]
    public void EveryDocumentedDataTypeIsThere()
    {
        // 43 in the snapshot verified 2026-08-09. Asserted rather than derived, so a type arriving
        // with a new capture is something to look at.
        Assert.Equal(43, HealthDataGeneratedDataTypes.All.Count);

        Assert.Equal(
            [.. HealthDataGeneratedDataTypes.All.Select(t => t.Id).Order(StringComparer.Ordinal)],
            [.. HealthDataGeneratedDataTypes.All.Select(t => t.Id)]);
    }

    [Fact]
    public void TheFilterNameIsNotTheId()
    {
        // The pair that made an application send heart-rate.interval.start_time and read a 400.
        var heartRate = HealthDataGeneratedDataTypes.Find("heart-rate");

        Assert.NotNull(heartRate);
        Assert.Equal("heart_rate", heartRate.FilterName);
        Assert.NotEqual(heartRate.Id, heartRate.FilterName);

        // And the type where they happen to match, which is why steps is the one everybody tries
        // first and the one that works.
        var steps = HealthDataGeneratedDataTypes.Find("steps");

        Assert.NotNull(steps);
        Assert.Equal(steps.Id, steps.FilterName);
    }

    [Fact]
    public void SupportsAnswersWhatTheDocumentationLists()
    {
        var steps = HealthDataGeneratedDataTypes.Find("steps")!;

        Assert.True(steps.Supports("list"));
        Assert.True(steps.Supports("dailyRollUp"));

        // The one that produced UNSUPPORTED_DATA_TYPE_ACTION: steps documents no get, no create,
        // no patch and no batchDelete.
        Assert.False(steps.Supports("get"));
        Assert.False(steps.Supports("create"));

        var hydration = HealthDataGeneratedDataTypes.Find("hydration-log")!;

        Assert.True(hydration.Supports("get"));
        Assert.True(hydration.Supports("create"));
    }

    [Fact]
    public void AnUnknownIdIsNullRatherThanAnError()
    {
        // Null is not "unsupported". A type Google adds after this capture is absent here and
        // works perfectly well at the service, which is why nothing in the SDK consults this
        // before sending.
        Assert.Null(HealthDataGeneratedDataTypes.Find("something-google-added-later"));
    }

    [Fact]
    public void NothingInTheSdkValidatesAgainstTheTable()
    {
        // A data type name is built from the pattern, not from the table: a type this snapshot has
        // never heard of still produces a name and still gets sent.
        var name = Names.UserName.Me.DataType("something-google-added-later");

        Assert.Equal("users/me/dataTypes/something-google-added-later", name.ToString());
    }
}
