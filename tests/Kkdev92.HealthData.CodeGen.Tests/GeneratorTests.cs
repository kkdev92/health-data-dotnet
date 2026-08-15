using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.HealthData.CodeGen.CSharp;
using Kkdev92.HealthData.CodeGen.Discovery;
using Kkdev92.HealthData.CodeGen.IntermediateModel;
using Kkdev92.HealthData.CodeGen.Specifications;
using Kkdev92.HealthData.CodeGen.Validation;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// Exercises the full pipeline against the committed specification.
/// </summary>
public sealed class GeneratorTests
{
    private static (SpecSet Spec, IReadOnlyList<GeneratedFile> Files, CSharpEmitter Emitter) Generate()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);
        var emitter = new CSharpEmitter(contract, unions: SpecLoader.ReadUnions(spec));
        return (spec, emitter.Emit(), emitter);
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        // The same input must produce byte-identical output. Running the whole pipeline twice in
        // one process also catches accidental dependence on dictionary enumeration order, which
        // varies between runs.
        var first = Generate().Files;
        var second = Generate().Files;

        Assert.Equal(first.Count, second.Count);

        foreach (var (a, b) in first.Zip(second))
        {
            Assert.Equal(a.RelativePath, b.RelativePath);
            Assert.Equal(a.Content, b.Content);
        }
    }

    [Fact]
    public void ContractPassesValidationWithoutErrorsOrWarnings()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);
        var result = ContractValidator.Validate(spec, contract);

        Assert.Empty(result.Errors);

        // A warning here means Discovery grew an operation that public-surface.json has not
        // classified yet. Generation is expected to produce no warnings at all.
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void OnlyAllowlistedOperationsAreParsed()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        Assert.Equal(25, contract.Operations.Count);

        // SMART Health Links are present in Discovery but deliberately not exposed.
        Assert.DoesNotContain(contract.Operations, op => op.Id.StartsWith("health.shl.", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryOperationAcceptsAtLeastWhatItsPerMethodPageLists()
    {
        // Discovery is not complete. Compared against all 25 per-method reference pages on
        // 2026-08-12, six operations accept scopes Discovery does not declare for them, and
        // reconcile is the mirror image: its page omits eight writeonly scopes Discovery does
        // list. The union is what gets generated, so this pins both directions at once — the
        // failure that matters is a scope quietly disappearing from an accepted list.
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["health.users.dataTypes.dataPoints.list"] = 7,
            ["health.users.dataTypes.dataPoints.get"] = 5,
            ["health.users.dataTypes.dataPoints.rollUp"] = 5,
            ["health.users.dataTypes.dataPoints.dailyRollUp"] = 5,
            ["health.users.dataTypes.dataPoints.reconcile"] = 13,
            ["health.users.getIdentity"] = 8,
        };

        foreach (var (id, count) in expected)
        {
            var operation = contract.Operations.Single(op => op.Id == id);

            Assert.Equal(count, operation.Scopes.Count);
            Assert.Contains(
                "https://www.googleapis.com/auth/googlehealth.nutrition.readonly",
                operation.Scopes);
        }

        // reconcile must keep the write scopes its page leaves out.
        var reconcile = contract.Operations
            .Single(op => op.Id == "health.users.dataTypes.dataPoints.reconcile");

        Assert.Equal(8, reconcile.Scopes.Count(s => s.EndsWith(".writeonly", StringComparison.Ordinal)));
    }

    [Fact]
    public void AScopeDiscoveryDoesNotDeclareStillGetsAConstant()
    {
        // nutrition.readonly is accepted by six operations and absent from Discovery's scope
        // list, so the constant can only come from semantics.json. Without it a caller has no
        // named way to ask for a scope the service documents.
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        var nutrition = contract.Scopes.Single(
            s => s.Url == "https://www.googleapis.com/auth/googlehealth.nutrition.readonly");

        Assert.Equal("NutritionReadonly", nutrition.CSharpName);
        Assert.Equal("See your Google Health nutrition data.", nutrition.Description);
        Assert.Equal(20, contract.Scopes.Count);
    }

    [Fact]
    public void TheRecordedFactsAgreeWithWhatIsGenerated()
    {
        // verified-facts.json is checked against Google's live pages by a scheduled workflow. This
        // checks the other half, offline: that the file still describes this contract. Without it
        // the manifest could drift from semantics.json and the weekly check would go on happily
        // comparing a stale record against the web.
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Value, "spec", "v4", "verified-facts.json")));

        var methods = manifest.RootElement.GetProperty("methods");
        Assert.NotEmpty(methods.EnumerateArray());

        foreach (var entry in methods.EnumerateArray())
        {
            var id = entry.GetProperty("operation").GetString()!;
            var operation = contract.Operations.SingleOrDefault(op => op.Id == id);

            Assert.True(operation is not null, $"verified-facts.json names '{id}', which this contract does not contain.");

            var recorded = entry.GetProperty("scopes")
                .EnumerateArray()
                .Select(s => s.GetString()!)
                .ToArray();

            // Every scope the page lists must be accepted. The reverse does not hold: the
            // generated set is the union, so it also carries scopes only Discovery declares.
            foreach (var scope in recorded)
            {
                Assert.True(
                    operation!.Scopes.Contains(scope),
                    $"'{id}' does not accept {scope}, which verified-facts.json records its page as listing.");
            }
        }
    }

    [Fact]
    public void AScopeOverrideThatDropsADiscoveryScopeIsRejected()
    {
        // The override replaces Discovery's list rather than merging with it, which is what makes
        // the recorded set readable in one place — and what would let a future revision's new
        // scope be silently discarded. Refreshing the snapshot has to fail loudly instead.
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");

        var mutated = JsonNode.Parse(spec.Semantics.RootElement.GetRawText())!;

        var recorded = mutated["operations"]!["health.users.dataTypes.dataPoints.list"]!
            ["scopeRequirement"]!["scopes"]!.AsArray();

        // Drop a scope Discovery declares for this operation, leaving the rest.
        var index = recorded
            .Select((node, i) => (node, i))
            .First(pair => pair.node!.GetValue<string>().EndsWith("sleep.readonly", StringComparison.Ordinal))
            .i;

        recorded.RemoveAt(index);

        using var damaged = JsonDocument.Parse(mutated.ToJsonString());

        var failure = Assert.Throws<InvalidOperationException>(() => DiscoveryParser.Parse(new SpecSet
        {
            Version = spec.Version,
            DiscoveryPath = spec.DiscoveryPath,
            DiscoveryBytes = spec.DiscoveryBytes,
            DiscoverySha256 = spec.DiscoverySha256,
            Discovery = spec.Discovery,
            Metadata = spec.Metadata,
            PublicSurface = spec.PublicSurface,
            Semantics = damaged,
            Errors = spec.Errors,
            DataTypes = spec.DataTypes,
        }));

        Assert.Contains("sleep.readonly", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Re-take the union", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableSchemasArePruned()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);
        var names = contract.Schemas.Select(s => s.WireName).ToHashSet(StringComparer.Ordinal);

        // 138 of the 147 declared schemas are reachable from the allowlisted operations.
        Assert.Equal(138, contract.Schemas.Count);

        // Reachable from users.getProfile.
        Assert.Contains("Profile", names);
        Assert.Contains("Date", names);

        // Reachable only from the excluded SMART Health Links operations.
        Assert.DoesNotContain("HttpBody", names);
        Assert.DoesNotContain("ManifestParams", names);

        // Orphans: declared by Discovery but referenced by nothing.
        Assert.DoesNotContain("DateTime", names);
        Assert.DoesNotContain("TimeZone", names);
        Assert.DoesNotContain("HttpResponse", names);
    }

    [Fact]
    public void GeneratedSourcesCarryProvenanceAndNoEnvironmentState()
    {
        var (spec, files, _) = Generate();

        foreach (var file in files)
        {
            Assert.StartsWith("// <auto-generated />", file.Content, StringComparison.Ordinal);
            Assert.Contains("Discovery revision: 20260805", file.Content, StringComparison.Ordinal);
            Assert.Contains(spec.DiscoverySha256, file.Content, StringComparison.Ordinal);

            // No machine path may leak into generated source.
            Assert.DoesNotContain("C:\\", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("/home/", file.Content, StringComparison.Ordinal);

            // The provenance header is the only place a generator would plausibly stamp a clock,
            // and it must not. The body is deliberately not scanned for date-like text: Google's
            // own descriptions contain example timestamps, for example TotalCaloriesRollupValue
            // which documents "2026-04-20T00:00:00Z".
            var header = string.Join('\n', file.Content.Split('\n').TakeWhile(l => l.StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", header);
            Assert.DoesNotMatch(@"\d{2}:\d{2}:\d{2}", header);
        }
    }

    [Fact]
    public void GeneratedSourcesUseLfAndNoByteOrderMark()
    {
        foreach (var file in Generate().Files)
        {
            Assert.DoesNotContain('\r', file.Content);
            Assert.DoesNotContain('\uFEFF', file.Content);

            var bytes = new UTF8Encoding(false).GetBytes(file.Content);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
    }

    [Fact]
    public void WireNamesSurviveGenerationUnchanged()
    {
        // Wire names are never reshaped. If a naming rule ever touches these, generation is wrong
        // regardless of how tidy the C# looks. The names live wherever the request is built, so
        // the whole generated surface is scanned rather than one file.
        var generated = string.Join('\n', Generate().Files.Select(f => f.Content));

        foreach (var wireName in new[] { "pageSize", "pageToken", "updateMask", "subscriberId", "partialData", "dataSourceFamily" })
        {
            Assert.Contains($"\"{wireName}\"", generated, StringComparison.Ordinal);
        }

        foreach (var reshaped in new[] { "page_size", "page_token", "update_mask", "subscriber_id", "partial_data", "data_source_family" })
        {
            Assert.DoesNotContain($"\"{reshaped}\"", generated, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PathTemplatesKeepTheVersionedSegmentAndReservedExpansion()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        var getProfile = contract.Operations.Single(op => op.Id == "health.users.getProfile");
        Assert.Equal("GET", getProfile.HttpMethod);
        Assert.Equal("v4/{+name}", getProfile.PathTemplate);

        var batchDelete = contract.Operations.Single(op => op.Id == "health.users.dataTypes.dataPoints.batchDelete");
        Assert.Equal("v4/{+parent}/dataPoints:batchDelete", batchDelete.PathTemplate);
    }

    [Fact]
    public void SemanticOverridesReachTheContract()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        // POST that only aggregates existing data is retryable; POST that writes is not.
        var rollUp = contract.Operations.Single(op => op.Id == "health.users.dataTypes.dataPoints.rollUp");
        Assert.Equal(IntermediateModel.RetryClassification.SemanticallySafe, rollUp.RetryClassification);
        Assert.Equal(IntermediateModel.PaginationKind.Body, rollUp.Pagination!.Kind);

        var create = contract.Operations.Single(op => op.Id == "health.users.dataTypes.dataPoints.create");
        Assert.Equal(IntermediateModel.RetryClassification.Never, create.RetryClassification);

        // The request accepts a page token but the response returns none, so results cannot be
        // enumerated.
        var dailyRollUp = contract.Operations.Single(op => op.Id == "health.users.dataTypes.dataPoints.dailyRollUp");
        Assert.Equal(IntermediateModel.PaginationKind.RequestOnly, dailyRollUp.Pagination!.Kind);

        // Subscriber writes are long-running; subscription writes are not.
        Assert.Equal(
            IntermediateModel.ResponseKind.Operation,
            contract.Operations.Single(op => op.Id == "health.projects.subscribers.create").ResponseKind);
        Assert.Equal(
            IntermediateModel.ResponseKind.Json,
            contract.Operations.Single(op => op.Id == "health.projects.subscribers.subscriptions.create").ResponseKind);

        // Dual-mode media response: JSON, or the TCX document itself.
        var tcx = contract.Operations.Single(op => op.Id == "health.users.dataTypes.dataPoints.exportExerciseTcx");
        Assert.Equal(IntermediateModel.ResponseKind.MediaOrJson, tcx.ResponseKind);
        Assert.True(tcx.SupportsMediaDownload);
    }

    [Fact]
    public void ReadOnlyPropertiesAreMarkedForWriteContractRemoval()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);
        var profile = contract.Schemas.Single(s => s.WireName == "Profile");

        Assert.True(profile.Properties.Single(p => p.WireName == "membershipStartDate").IsReadOnly);
        Assert.False(profile.Properties.Single(p => p.WireName == "age").IsReadOnly);
    }

    [Fact]
    public void EveryReachableSchemaIsEmitted()
    {
        // The generator once emitted a slice of the schemas rather than all of them. Coverage is
        // complete now, and the counter that reported the shortfall must read zero rather than
        // being quietly deleted.
        var (_, files, emitter) = Generate();

        Assert.Equal(0, emitter.SkippedSchemaCount);

        var models = files
            .Where(f => f.RelativePath.StartsWith("Generated/Models/", StringComparison.Ordinal))
            .ToArray();

        // The union accessor helpers and the nested-enum containers sit next to the models but
        // are not schemas, so they are counted separately rather than inflating the schema count.
        var helpers = models.Count(f => f.RelativePath.EndsWith("Extensions.g.cs", StringComparison.Ordinal));
        var containers = models.Count(f => f.RelativePath.EndsWith(".Types.g.cs", StringComparison.Ordinal));

        // DataPoint, RollupDataPoint and DailyRollupDataPoint. The last is the same union shape
        // as the second but a different schema, which is why it needs its own helpers rather than
        // sharing them.
        Assert.Equal(3, helpers);

        // One container per schema that declares at least one inline enum: 58 enums over 43
        // owners in Discovery revision 20260805.
        Assert.Equal(43, containers);
        Assert.Equal(138, models.Length - helpers - containers);

        // The flat top-level home the enums used to have. Nothing may come back here: a file in
        // this directory means the emitter has forgotten where enums live.
        Assert.DoesNotContain(files, f => f.RelativePath.StartsWith("Generated/Enums/", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenEnumsCoverEveryInlineEnumAndTolerateUnknownValues()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        Assert.Equal(58, contract.OpenEnums.Count);

        var sleepStageType = contract.OpenEnums.Single(e => e.CSharpName == "SleepStage.Types.Type");
        Assert.Equal("SleepStage", sleepStageType.DeclaringSchema);
        Assert.Contains(sleepStageType.Values, v => v.WireValue == "AWAKE" && v.CSharpName == "Awake");

        // The largest enum in the contract. A closed C# enum here would be unmaintainable and
        // would break on the next value Google adds.
        var exerciseType = contract.OpenEnums.Single(e => e.CSharpName == "Exercise.Types.ExerciseType");
        Assert.True(exerciseType.Values.Count > 150);
    }

    [Fact]
    public void OpenEnumsAreNestedUnderTheirOwnersProtobufStyle()
    {
        // The friction report asked for PairedDevice.DeviceType, which C# refuses: a nested type
        // may not share a name with a property of the enclosing class (CS0102). Google's own
        // protobuf codegen answers the same collision with a Types container - Person.Types.
        // PhoneType - so the enum is reachable by guesswork from the property a reader is looking
        // at, uniformly, for all 58 rather than for whichever names happened not to collide.
        var (_, files, _) = Generate();

        var container = files.Single(f => f.RelativePath == "Generated/Models/PairedDevice.Types.g.cs");

        Assert.Contains("public sealed partial class PairedDevice", container.Content, StringComparison.Ordinal);
        Assert.Contains("public static class Types", container.Content, StringComparison.Ordinal);
        Assert.Contains(
            "public readonly partial record struct DeviceType(string Value)",
            container.Content,
            StringComparison.Ordinal);

        // The owner's property refers to the nested type by its full dotted name, which works in
        // every scope and reads the same in the file and in IntelliSense.
        var owner = files.Single(f => f.RelativePath == "Generated/Models/PairedDevice.g.cs");
        Assert.Contains("public PairedDevice.Types.DeviceType? DeviceType", owner.Content, StringComparison.Ordinal);

        // The hardest case in the contract: Moods.moods, where the schema, the property and the
        // enum all want the same word. The property is already renamed MoodsValue by the CS0542
        // rule; the enum keeps the wire-derived name, nested where CS0542 does not reach.
        var moods = files.Single(f => f.RelativePath == "Generated/Models/Moods.Types.g.cs");
        Assert.Contains(
            "public readonly partial record struct Moods(string Value)",
            moods.Content,
            StringComparison.Ordinal);

        // Settings declares nine enums - every unit preference plus the two stride-length
        // sources; they share one container rather than nine partials of the same class.
        var settings = files.Single(f => f.RelativePath == "Generated/Models/Settings.Types.g.cs");
        Assert.Equal(9, CountOccurrences(settings.Content, "readonly partial record struct"));
    }

    [Fact]
    public void GeneratedTypesLiveInTheNamespaceOfTheirKind()
    {
        // 249 public types in one namespace was the report's tenth finding: typing
        // Kkdev92.HealthData. offered every measurement, request, response and enum at once. The
        // partition follows what a type is. A Discovery schema - something that crosses the wire -
        // lives in .Models. A request envelope - something this SDK invented to carry parameters -
        // lives in .Requests. The client, its resources and the operation table stay at the root,
        // which is what a consumer sees first.
        var (_, files, _) = Generate();

        foreach (var file in files)
        {
            var expected = file.RelativePath switch
            {
                var p when p.StartsWith("Generated/Models/", StringComparison.Ordinal)
                    => "namespace Kkdev92.HealthData.Models;",
                var p when p.StartsWith("Generated/Requests/", StringComparison.Ordinal)
                    => "namespace Kkdev92.HealthData.Requests;",
                var p when p.StartsWith("Generated/Serialization/", StringComparison.Ordinal)
                    => "namespace Kkdev92.HealthData.Serialization;",
                _ => "namespace Kkdev92.HealthData;",
            };

            Assert.Contains(expected, file.Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CrossNamespaceReferencesCarryTheirUsings()
    {
        var (_, files, _) = Generate();

        // A request with a body names a model type, so it needs the using; one without a body
        // names nothing outside its own namespace, and an unused using is an IDE0005 build error
        // in this repository.
        var withBody = files.Single(f => f.RelativePath == "Generated/Requests/UpdateProfileRequest.g.cs");
        var withoutBody = files.Single(f => f.RelativePath == "Generated/Requests/GetProfileRequest.g.cs");

        Assert.Contains("using Kkdev92.HealthData.Models;", withBody.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("using Kkdev92.HealthData.Models;", withoutBody.Content, StringComparison.Ordinal);

        // Resources call with request types and deserialize into model types, and the serializer
        // context registers every model by name.
        var resource = files.Single(f => f.RelativePath == "Generated/Resources/UsersResource.g.cs");
        Assert.Contains("using Kkdev92.HealthData.Models;", resource.Content, StringComparison.Ordinal);
        Assert.Contains("using Kkdev92.HealthData.Requests;", resource.Content, StringComparison.Ordinal);

        var context = files.Single(f => f.RelativePath == "Generated/Serialization/HealthDataJsonContext.g.cs");
        Assert.Contains("using Kkdev92.HealthData.Models;", context.Content, StringComparison.Ordinal);

        // The projects node exists only to hold its child resource: no operations, so no request
        // or model ever appears in it, and it must not import what it does not use.
        var projects = files.Single(f => f.RelativePath == "Generated/Resources/ProjectsResource.g.cs");
        Assert.DoesNotContain("using Kkdev92.HealthData.Models;", projects.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("using Kkdev92.HealthData.Requests;", projects.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnumValueThatWouldCollideWithItsNestedTypeNameIsRejected()
    {
        // Inside the container the struct is named after the property, so a wire value whose
        // normalized name equals it would be CS0542 in the emitted code. Discovery revision
        // 20260805 has no such value; if a later one introduces it, generation must stop with a
        // message that names the culprit rather than emitting code that does not compile.
        var contract = new ApiContract
        {
            Name = "health",
            Title = "Test",
            ApiVersion = "v4",
            Revision = "test",
            RootUrl = new Uri("https://health.googleapis.com/"),
            SpecSha256 = "test",
            Scopes = [],
            Operations = [],
            Schemas =
            [
                new SchemaContract
                {
                    WireName = "Sample",
                    CSharpName = "Sample",
                    Properties = [],
                    Description = null,
                },
            ],
            ErrorReasons = [],
            OpenEnums =
            [
                new OpenEnumContract
                {
                    CSharpName = "Sample.Types.Kind",
                    DeclaringSchema = "Sample",
                    DeclaringProperty = "kind",
                    Values = [new OpenEnumValueContract("KIND", "Kind", null)],
                },
            ],
        };

        var errors = new List<string>();
        ContractValidator.ValidateOpenEnums(contract, errors);

        Assert.Contains(errors, e => e.Contains("Sample.kind", StringComparison.Ordinal)
            && e.Contains("KIND", StringComparison.Ordinal));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;

        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void MemberTypeCollisionsAreResolved()
    {
        var spec = SpecLoader.Load(RepositoryRoot.Value, "v4");
        var contract = DiscoveryParser.Parse(spec);

        // The three real CS0542 collisions in Discovery revision 20260805.
        foreach (var (schemaName, wireName, expected) in new[]
                 {
                     ("ActiveZoneMinutes", "activeZoneMinutes", "ActiveZoneMinutesValue"),
                     ("Moods", "moods", "MoodsValue"),
                     ("Symptoms", "symptoms", "SymptomsValue"),
                 })
        {
            var schema = contract.Schemas.Single(s => s.WireName == schemaName);
            var property = schema.Properties.Single(p => p.WireName == wireName);

            Assert.Equal(expected, property.CSharpName);

            // The wire name is untouched by the collision rule.
            Assert.Equal(wireName, property.WireName);
        }
    }
}
