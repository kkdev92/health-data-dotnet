using Kkdev92.HealthData.Names;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// What a resource name type is worth at run time.
/// </summary>
/// <remarks>
/// <para>
/// The compile-time half of the claim cannot be written as a test — a request that takes the wrong
/// name no longer builds, which is the point — so it is proved once in the code generator's own
/// tests and in <c>docs/adr/0010-resource-names.md</c>. What is left here is the half that survives
/// into a running program: a string from outside is checked before it is sent, and a name built
/// from parts cannot come out malformed.
/// </para>
/// <para>
/// Every expectation below is the contract's own pattern, not a restatement of it: the same
/// expression the service applies, compiled into the type.
/// </para>
/// </remarks>
public sealed class ResourceNameTests
{
    [Fact]
    public void MeIsTheAliasGoogleDocuments()
    {
        Assert.Equal("users/me", UserName.Me.ToString());
        Assert.Equal("me", UserName.Me.UserId);
    }

    [Fact]
    public void ANameBuiltFromPartsRendersTheWireForm()
    {
        var point = UserName.Me.DataType("steps").DataPoint("abc");

        Assert.Equal("users/me/dataTypes/steps/dataPoints/abc", point.ToString());
        Assert.Equal("steps", point.DataTypeId);
        Assert.Equal("abc", point.DataPointId);
        Assert.Equal("me", point.UserId);
    }

    [Fact]
    public void AChildKnowsWhatItBelongsTo()
    {
        var point = UserName.From("1234567890").DataType("heart-rate").DataPoint("abc");

        Assert.Equal("users/1234567890/dataTypes/heart-rate", point.DataType.ToString());
        Assert.Equal("users/1234567890", point.DataType.User.ToString());
        Assert.Equal("users/1234567890/profile", point.DataType.User.Profile.ToString());
    }

    [Theory]
    [InlineData("this is not a resource name")]
    [InlineData("")]
    [InlineData("users/")]
    [InlineData("users/me/")]
    [InlineData("users/me/pairedDevices/abc")]
    [InlineData("projects/p/subscribers/s")]
    [InlineData("users/me/dataTypes/steps/dataPoints/abc")]
    public void ParseRejectsAnythingThatIsNotThisKindOfName(string value)
    {
        // The first four are not names at all. The last three are perfectly good names of other
        // resources, which is the failure the report described: a data point name assigned to a
        // paired device request went out and came back 400. It cannot be assigned now, and if it
        // arrives as a string it does not get past here.
        Assert.Throws<FormatException>(() => DataTypeName.Parse(value));
        Assert.False(DataTypeName.TryParse(value, out _));
    }

    [Fact]
    public void TheMessageSaysWhatWasWantedAndWhatArrived()
    {
        var thrown = Assert.Throws<FormatException>(
            () => PairedDeviceName.Parse("users/me/dataTypes/steps/dataPoints/abc"));

        Assert.Contains("users/me/dataTypes/steps/dataPoints/abc", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("users/{userId}/pairedDevices/{pairedDeviceId}", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(PairedDeviceName.Pattern, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdWithASlashInItIsRejectedRatherThanSilentlyExtendingTheName()
    {
        // "steps/dataPoints/x" as a data type id would render a name that reads like a data point.
        // The pattern's [^/]+ says no, and the builder runs the pattern, so this is caught where it
        // is written rather than where it is sent.
        Assert.Throws<FormatException>(() => UserName.Me.DataType("steps/dataPoints/x"));
        Assert.Throws<FormatException>(() => UserName.From("a/b"));
    }

    [Fact]
    public void AnEmptyIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => UserName.Me.DataType(""));
        Assert.Throws<ArgumentException>(() => UserName.From(""));
        Assert.Throws<ArgumentNullException>(() => UserName.From(null!));
    }

    [Fact]
    public void ANameParsedFromTheServiceRoundTrips()
    {
        // The other direction from building: a list response carries names as strings, and the
        // only way to call get with one is to parse it.
        const string Wire = "users/me/pairedDevices/2424238460";

        Assert.True(PairedDeviceName.TryParse(Wire, out var name));
        Assert.Equal(Wire, name.ToString());
        Assert.Equal("2424238460", name.PairedDeviceId);
    }

    [Fact]
    public void TwoNamesOfTheSameResourceAreEqual()
    {
        Assert.Equal(UserName.Me.DataType("steps"), UserName.From("me").DataType("steps"));
        Assert.NotEqual(UserName.Me.DataType("steps"), UserName.Me.DataType("distance"));
    }

    [Fact]
    public void TheDocumentedFormatAndThePatternDisagreeAndThePatternWins()
    {
        // Google's own description of pairedDevices.get says
        // "Format: users/{user}/devices/{device}" while the pattern beside it says pairedDevices.
        // Sending the documented form answers 404. The type is built from the pattern, so the
        // documented form does not compile into a request and does not parse into a name.
        Assert.False(PairedDeviceName.TryParse("users/me/devices/abc", out _));
        Assert.True(PairedDeviceName.TryParse("users/me/pairedDevices/abc", out _));
    }
}
