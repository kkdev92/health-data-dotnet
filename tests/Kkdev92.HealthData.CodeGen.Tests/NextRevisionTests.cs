using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.HealthData.CodeGen.CSharp;
using Kkdev92.HealthData.CodeGen.Discovery;
using Kkdev92.HealthData.CodeGen.Specifications;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// What the next Discovery revision does to this generator.
/// </summary>
/// <remarks>
/// <para>
/// The contract is a snapshot, and Google changes theirs. Each test here edits the snapshot the way
/// a revision plausibly would and asserts what happens — because the failure worth preventing is
/// not "generation breaks", which is loud and fixable in an afternoon. It is generation
/// <em>succeeding</em> and producing something quietly wrong: a scope in no set, a name type
/// accepting two shapes, an operation with no page cursor that looks like it has one.
/// </para>
/// <para>
/// So each one says which of the two it is. A test that asserts a throw is asserting that the
/// generator refuses to guess.
/// </para>
/// </remarks>
public sealed class NextRevisionTests
{
    private static SpecSet Spec => SpecLoader.Load(RepositoryRoot.Value, "v4");

    /// <summary>The spec with one of its documents replaced.</summary>
    private static SpecSet With(SpecSet spec, JsonDocument? discovery = null, JsonDocument? semantics = null)
        => new()
        {
            Version = spec.Version,
            DiscoveryPath = spec.DiscoveryPath,
            DiscoveryBytes = spec.DiscoveryBytes,
            DiscoverySha256 = spec.DiscoverySha256,
            Discovery = discovery ?? spec.Discovery,
            Metadata = spec.Metadata,
            PublicSurface = spec.PublicSurface,
            Semantics = semantics ?? spec.Semantics,
            Errors = spec.Errors,
            DataTypes = spec.DataTypes,
        };

    private static JsonDocument Edit(JsonDocument document, Action<JsonNode> edit)
    {
        var mutable = JsonNode.Parse(document.RootElement.GetRawText())!;
        edit(mutable);

        return JsonDocument.Parse(mutable.ToJsonString());
    }

    [Fact]
    public void AScopeGoogleAddsStopsGenerationRatherThanBelongingToNoSet()
    {
        // The failure this prevents: HealthDataScopes.ReadOnly silently not containing a scope that
        // reads, so an application asking for "everything that reads" quietly stops asking for it —
        // which looks like a permissions bug in the application long before anyone suspects the
        // package.
        var spec = Spec;

        using var discovery = Edit(spec.Discovery, root =>
            root["auth"]!["oauth2"]!["scopes"]!.AsObject().Add(
                "https://www.googleapis.com/auth/googlehealth.mobility.readonly",
                new JsonObject { ["description"] = "See your Google Health mobility data" }));

        var failure = Assert.Throws<InvalidOperationException>(
            () => DiscoveryParser.Parse(With(spec, discovery: discovery)));

        Assert.Contains("mobility.readonly", failure.Message, StringComparison.Ordinal);
        Assert.Contains("scopeKinds", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameShapeThisGeneratorCannotReadStopsGeneration()
    {
        // A pattern with alternation, which the resolver has no way to turn into segments. Emitting
        // a type from a guess would give a caller a compile error about the wrong thing.
        var spec = Spec;

        using var discovery = Edit(spec.Discovery, root =>
            root["resources"]!["users"]!["methods"]!["getProfile"]!["parameters"]!["name"]!["pattern"] =
                "^users/(me|[0-9]+)/profile$");

        var failure = Assert.Throws<InvalidOperationException>(
            () => DiscoveryParser.Parse(With(spec, discovery: discovery)));

        Assert.Contains("^users/(me|[0-9]+)/profile$", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewResourceArrivesAsItsOwnNameType()
    {
        // The other direction: a shape the resolver can read produces a type without anyone
        // writing one. This is what "the hierarchy is read out of the expressions" has to mean.
        var spec = Spec;

        using var discovery = Edit(spec.Discovery, root =>
            root["resources"]!["users"]!["resources"]!["pairedDevices"]!["methods"]!["get"]!
                ["parameters"]!["name"]!["pattern"] = "^users/[^/]+/wearables/[^/]+$");

        var contract = DiscoveryParser.Parse(With(spec, discovery: discovery));
        var wearable = Assert.Single(contract.ResourceNames, name => name.CSharpName == "WearableName");

        Assert.Equal("UserName", wearable.ParentCSharpName);
        Assert.Equal("wearableId", wearable.IdParameterName);
        Assert.Equal(["userId", "wearableId"], wearable.IdParameterNames);

        // And the type it replaced is gone rather than left behind as an orphan.
        Assert.DoesNotContain(contract.ResourceNames, name => name.CSharpName == "PairedDeviceName");
    }

    [Fact]
    public void ADataTypeGoogleAddsIsAbsentFromTheTableAndNotRefused()
    {
        // The data type table is a capture of prose. A type added after it is simply not in it,
        // and the SDK must not turn "not in the table" into "not supported" — the table says so
        // itself: capabilities are metadata, the server is the authority.
        var spec = Spec;
        var contract = DiscoveryParser.Parse(spec);

        Assert.DoesNotContain(contract.DataTypes, dataType => dataType.Id == "mobility");

        // Nothing in generation consults the table, so a name for it is still built.
        var emitter = new CSharpEmitter(contract, unions: SpecLoader.ReadUnions(spec));
        var files = emitter.Emit();

        Assert.Contains(files, file => file.RelativePath == "Generated/Names/DataTypeName.g.cs");
    }

    [Fact]
    public void PaginationThatNobodyDeclaresProducesNoEnumerationRatherThanAGuess()
    {
        // Discovery cannot say an operation pages; semantics.json does. A revision that adds
        // pageToken to an operation's parameters and nothing else must not produce a streaming
        // helper, because whether the response returns a cursor is exactly what is unstated.
        var spec = Spec;

        using var discovery = Edit(spec.Discovery, root =>
            root["resources"]!["users"]!["resources"]!["dataTypes"]!["resources"]!["dataPoints"]!
                ["methods"]!["get"]!["parameters"]!.AsObject().Add(
                    "pageToken",
                    new JsonObject
                    {
                        ["type"] = "string",
                        ["location"] = "query",
                        ["description"] = "A page token.",
                    }));

        var contract = DiscoveryParser.Parse(With(spec, discovery: discovery));
        var get = contract.Operations.Single(operation => operation.Id == "health.users.dataTypes.dataPoints.get");

        Assert.Contains(get.Parameters, parameter => parameter.WireName == "pageToken");
        Assert.Null(get.Pagination);

        var emitter = new CSharpEmitter(contract, unions: SpecLoader.ReadUnions(spec));
        var resource = emitter.Emit().Single(file => file.RelativePath == "Generated/Resources/DataPointsResource.g.cs");

        Assert.Equal(3, CountOccurrences(resource.Content, "public IAsyncEnumerable<"));
    }

    [Fact]
    public void AnOperationGoogleAddsIsReportedRatherThanGenerated()
    {
        // public-surface.json decides what this SDK exposes. An operation that appears in a new
        // revision and is not classified there is a decision for a person, so it is a warning
        // rather than a silent addition or a silent omission.
        var spec = Spec;

        using var discovery = Edit(spec.Discovery, root =>
            root["resources"]!["users"]!["methods"]!.AsObject().Add(
                "getMobilityProfile",
                new JsonObject
                {
                    ["id"] = "health.users.getMobilityProfile",
                    ["path"] = "v4/{+name}",
                    ["httpMethod"] = "GET",
                    ["parameters"] = new JsonObject
                    {
                        ["name"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["required"] = true,
                            ["location"] = "path",
                            ["pattern"] = "^users/[^/]+/mobilityProfile$",
                        },
                    },
                    ["parameterOrder"] = new JsonArray("name"),
                    ["scopes"] = new JsonArray("https://www.googleapis.com/auth/googlehealth.profile.readonly"),
                }));

        var mutated = With(spec, discovery: discovery);
        var contract = DiscoveryParser.Parse(mutated);

        Assert.DoesNotContain(contract.Operations, operation => operation.Id == "health.users.getMobilityProfile");

        var result = Validation.ContractValidator.Validate(mutated, contract);

        Assert.Contains(
            result.Warnings.Concat(result.Errors),
            message => message.Contains("getMobilityProfile", StringComparison.Ordinal));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
