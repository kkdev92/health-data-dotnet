using Kkdev92.HealthData.CodeGen.Normalization;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// Golden tests for identifier normalization.
/// </summary>
/// <remarks>
/// The real specification exercises almost none of these rules: Discovery revision 20260805 has
/// no reserved-keyword property names, no digit-leading names and no PascalCase collisions. It
/// hits the member/type collision exactly three times. Synthetic cases are therefore the only way
/// to know the rules work before Google produces one.
/// </remarks>
public sealed class NamingNormalizerTests
{
    [Theory]
    [InlineData("nextPageToken", "NextPageToken")]
    [InlineData("pageSize", "PageSize")]
    [InlineData("heart-rate", "HeartRate")]
    [InlineData("heart_rate", "HeartRate")]
    [InlineData("dataSourceFamily", "DataSourceFamily")]
    [InlineData("name", "Name")]
    [InlineData("$.xgafv", "Xgafv")]
    [InlineData("upload_protocol", "UploadProtocol")]
    public void ConvertsWireNamesToPascalCase(string wireName, string expected)
        => Assert.Equal(expected, NamingNormalizer.ToPascalCase(wireName));

    [Fact]
    public void PrefixesIdentifiersThatWouldStartWithADigit()
        => Assert.Equal("_2faEnabled", NamingNormalizer.ToPascalCase("2faEnabled"));

    [Theory]
    // The three real collisions in Discovery revision 20260805.
    [InlineData("activeZoneMinutes", "ActiveZoneMinutes", "ActiveZoneMinutesValue")]
    [InlineData("moods", "Moods", "MoodsValue")]
    [InlineData("symptoms", "Symptoms", "SymptomsValue")]
    // A non-colliding member keeps its natural name.
    [InlineData("beatsPerMinute", "HeartRate", "BeatsPerMinute")]
    public void AvoidsMemberNamesThatCollideWithTheDeclaringType(string wireName, string typeName, string expected)
        => Assert.Equal(expected, NamingNormalizer.ToMemberName(wireName, typeName));

    [Theory]
    [InlineData("class", "@class")]
    [InlineData("string", "@string")]
    [InlineData("event", "@event")]
    [InlineData("Name", "Name")]
    public void EscapesReservedKeywords(string identifier, string expected)
        => Assert.Equal(expected, NamingNormalizer.EscapeIdentifier(identifier));

    [Theory]
    [InlineData("https://www.googleapis.com/auth/cloud-platform", "CloudPlatform")]
    [InlineData("https://www.googleapis.com/auth/googlehealth.profile.readonly", "ProfileReadonly")]
    [InlineData("https://www.googleapis.com/auth/googlehealth.activity_and_fitness.writeonly", "ActivityAndFitnessWriteonly")]
    public void DerivesScopeConstantNames(string url, string expected)
        => Assert.Equal(expected, NamingNormalizer.ScopeConstantName(url));

    [Theory]
    [InlineData("health.users.getProfile", "GetProfileAsync")]
    [InlineData("health.users.dataTypes.dataPoints.exportExerciseTcx", "ExportExerciseTcxAsync")]
    [InlineData("health.projects.subscribers.subscriptions.patch", "PatchAsync")]
    public void DerivesOperationMethodNames(string operationId, string expected)
        => Assert.Equal(expected, NamingNormalizer.OperationMethodName(operationId));

    [Theory]
    [InlineData("ACCOUNT_NOT_LINKED", "AccountNotLinked")]
    [InlineData("MISSING_OAUTH_SCOPE", "MissingOauthScope")]
    [InlineData("INTERNAL_ERROR", "InternalError")]
    public void DerivesErrorReasonConstantNames(string reason, string expected)
        => Assert.Equal(expected, NamingNormalizer.ErrorReasonConstantName(reason));

    [Fact]
    public void RejectsNamesWithNoIdentifierCharacters()
        => Assert.Throws<InvalidOperationException>(() => NamingNormalizer.ToPascalCase("---"));
}
