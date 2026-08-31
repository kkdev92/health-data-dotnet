using Kkdev92.HealthData.CodeGen.CSharp;
using Kkdev92.HealthData.CodeGen.IntermediateModel;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// The rules the emitter applies, each stated on its own.
/// </summary>
/// <remarks>
/// <para>
/// <c>codegen verify</c> already proves the committed sources are exactly what the generator
/// produces, and the golden tests prove the whole output does not move. Neither says which rule
/// produced which line, so a rewrite that happens to keep today's bytes identical could drop a
/// rule the next contract needs — and the failure would arrive as a diff across 235 files with no
/// statement of what was violated.
/// </para>
/// <para>
/// Each test here feeds a contract built for it alone. That is the point: a rule checked against
/// the real specification can pass because the value appears somewhere else, and cannot be checked
/// at all for a shape the current contract does not contain.
/// </para>
/// </remarks>
public sealed class EmitterRuleTests
{
    /// <summary>
    /// ADR-0005: a wire enum is a struct over the string, not a C# enum.
    /// </summary>
    /// <remarks>
    /// The decision is that a value Google adds later round-trips unchanged rather than failing to
    /// deserialize. A closed enum cannot do that, so the shape is the decision: were this ever to
    /// emit a C# enum, every future value would become a deserialization failure on somebody's
    /// health data.
    /// </remarks>
    [Fact]
    public void AWireEnumIsAStructOverTheStringValue()
    {
        var file = Single(EmitAll(Contract(openEnums: [Flavour])), "Models/Widget.Types.g.cs");

        Assert.Contains("readonly partial record struct Flavour(string Value)", file, StringComparison.Ordinal);
        Assert.Contains("FromValue(string value)", file, StringComparison.Ordinal);
        Assert.Contains("SALTY", file, StringComparison.Ordinal);
        Assert.DoesNotContain("public enum Flavour", file, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-0009: an enum nests under its owner's Types container.
    /// </summary>
    /// <remarks>
    /// Protobuf's own C# codegen answers the same CS0102 collision the same way. The rule being
    /// uniform is what makes the type one guess away from the property.
    /// </remarks>
    [Fact]
    public void AnEnumNestsUnderItsOwnersTypesContainer()
    {
        // The file name is part of the rule: the enum is emitted into its owner's partial, not
        // into one of its own.
        var file = Single(EmitAll(Contract(openEnums: [Flavour])), "Models/Widget.Types.g.cs");

        Assert.Contains("public sealed partial class Widget", file, StringComparison.Ordinal);
        Assert.Contains("public static class Types", file, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-0006: a read-only property stays readable and leaves the write contract.
    /// </summary>
    /// <remarks>
    /// One generated type per schema, and a table the serializer's write contract consults. If the
    /// table stops naming a property Discovery marks read-only, the SDK starts echoing a value the
    /// service owns back at it.
    /// </remarks>
    [Fact]
    public void AReadOnlyPropertyIsRecordedInTheOutputOnlyTable()
    {
        var emitted = EmitAll(Contract(schemas:
        [
            new SchemaContract
            {
                WireName = "Widget",
                CSharpName = "Widget",
                Properties =
                [
                    Property("createTime", "CreateTime", readOnly: true),
                    Property("label", "Label", readOnly: false),
                ],
            },
        ]));

        var table = Single(emitted, "HealthDataOutputOnlyProperties");

        Assert.Contains("typeof(Widget)", table, StringComparison.Ordinal);
        Assert.Contains("createTime", table, StringComparison.Ordinal);
        Assert.DoesNotContain("label", table, StringComparison.Ordinal);

        // Readable, though. The property is still on the model; only the write contract drops it.
        Assert.Contains("CreateTime", Single(emitted, "Models/Widget.g.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-0010: a parameter with a pattern takes the generated name type, not a string.
    /// </summary>
    /// <remarks>
    /// The pattern is the type. Passing the name of the wrong resource used to be a 400 from the
    /// service; it is a compile error while this rule holds, and silently a 400 again when it does
    /// not.
    /// </remarks>
    [Fact]
    public void AParameterWithAPatternTakesItsResourceNameType()
    {
        var emitted = EmitAll(Contract(
            operations: [Operation("health.widgets.get", "Get", "v4/{+name}", Parameter("name", "Name", pattern: WidgetPattern))],
            resourceNames: [WidgetName]));

        var request = Single(emitted, "Requests/");

        Assert.Contains("WidgetName Name { get; init; }", request, StringComparison.Ordinal);
        Assert.DoesNotContain("string Name { get; init; }", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// A parameter without a pattern stays a string.
    /// </summary>
    /// <remarks>
    /// The other half of the rule. Without it, the test above passes just as well against a
    /// generator that turned everything into a name type.
    /// </remarks>
    [Fact]
    public void AParameterWithoutAPatternStaysAString()
    {
        var emitted = EmitAll(Contract(
            operations: [Operation("health.widgets.list", "List", "v4/widgets", Parameter("filter", "Filter", pattern: null, location: ParameterLocation.Query))],
            resourceNames: [WidgetName]));

        var request = Single(emitted, "Requests/");

        Assert.Contains("Filter { get; init; }", request, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetName Filter", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// A query parameter keeps Google's wire name, whatever this SDK called the property.
    /// </summary>
    /// <remarks>
    /// The C# name is this SDK's to choose; the wire name is not. A generator that renamed the
    /// query key would produce requests the service does not understand, and the failure would be
    /// a 400 rather than anything a type could catch.
    /// </remarks>
    [Fact]
    public void AQueryParameterKeepsItsWireName()
    {
        var emitted = EmitAll(Contract(
            operations: [Operation("health.widgets.list", "List", "v4/widgets", Parameter("page_size", "PageSize", pattern: null, location: ParameterLocation.Query))],
            resourceNames: [WidgetName]));

        var resource = Single(emitted, "Resources/WidgetsResource");

        Assert.Contains("page_size", resource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every generated file carries its provenance and nothing about the machine that built it.
    /// </summary>
    /// <remarks>
    /// The golden test checks this against the real contract, where the revision is whatever the
    /// snapshot says. Here it is checked against a contract invented in this process, so a header
    /// that took its values from anywhere but the contract fails.
    /// </remarks>
    [Fact]
    public void EveryFileCarriesTheContractsRevisionAndHash()
    {
        var files = Emit(Contract(schemas: [new SchemaContract { WireName = "Widget", CSharpName = "Widget", Properties = [] }]));

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            Assert.StartsWith("// <auto-generated />", file.Content, StringComparison.Ordinal);
            Assert.Contains("Discovery revision: 19990101", file.Content, StringComparison.Ordinal);
            Assert.Contains(new string('0', 64), file.Content, StringComparison.Ordinal);

            // No machine path may reach a generated source.
            Assert.DoesNotContain("C:\\", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("/home/", file.Content, StringComparison.Ordinal);
        }
    }

    // ---- a contract small enough to reason about ----------------------------------------------

    private const string WidgetPattern = "^widgets/[^/]+$";

    private static readonly OpenEnumContract Flavour = new()
    {
        CSharpName = "Flavour",
        DeclaringSchema = "Widget",
        DeclaringProperty = "flavour",
        Values = [new OpenEnumValueContract("SALTY", "Salty", null)],
    };

    private static readonly ResourceNameContract WidgetName = new()
    {
        CSharpName = "WidgetName",
        Pattern = WidgetPattern,
        Segments = [new ResourceNameSegment("widgets", false), new ResourceNameSegment("widget", true)],
        MemberName = "Widget",
        IdParameterNames = ["widget"],
        Example = "widgets/1",
    };

    private static PropertyContract Property(string wire, string csharp, bool readOnly) => new()
    {
        WireName = wire,
        CSharpName = csharp,
        IsReadOnly = readOnly,
        Type = new TypeContract { Kind = TypeKind.Primitive, CSharpType = "string?", WireType = "string" },
    };

    private static ParameterContract Parameter(
        string wire,
        string csharp,
        string? pattern,
        ParameterLocation location = ParameterLocation.Path) => new()
        {
            WireName = wire,
            CSharpName = csharp,
            Location = location,
            IsRequired = location == ParameterLocation.Path,
            Pattern = pattern,
            Type = new TypeContract { Kind = TypeKind.Primitive, CSharpType = "string?", WireType = "string" },
        };

    private static OperationContract Operation(string id, string csharpName, string template, params ParameterContract[] parameters) => new()
    {
        Id = id,
        ResourcePath = "widgets",
        CSharpName = csharpName,
        HttpMethod = "GET",
        PathTemplate = template,
        Parameters = parameters,
        Scopes = [],
        ResponseKind = ResponseKind.Json,
        RetryClassification = RetryClassification.Safe,
    };

    private static ApiContract Contract(
        IReadOnlyList<SchemaContract>? schemas = null,
        IReadOnlyList<OperationContract>? operations = null,
        IReadOnlyList<OpenEnumContract>? openEnums = null,
        IReadOnlyList<ResourceNameContract>? resourceNames = null) => new()
        {
            Name = "health",
            Title = "Test",
            ApiVersion = "v4",
            Revision = "19990101",
            RootUrl = new Uri("https://example.test/"),
            SpecSha256 = new string('0', 64),
            Scopes = [],
            Operations = operations ?? [],
            Schemas = schemas ?? [],
            ErrorReasons = [],
            OpenEnums = openEnums ?? [],
            ResourceNames = resourceNames ?? [],
            DataTypes = [],
        };

    private static IReadOnlyList<GeneratedFile> Emit(ApiContract contract) => new CSharpEmitter(contract).Emit();

    private static IReadOnlyDictionary<string, string> EmitAll(ApiContract contract)
        => Emit(contract).ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

    /// <summary>The one emitted file whose path contains <paramref name="fragment"/>.</summary>
    private static string Single(IReadOnlyDictionary<string, string> files, string fragment)
    {
        var matches = files
            .Where(f => f.Key.Replace('\\', '/').Contains(fragment, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            matches.Length == 1,
            $"Expected one emitted file matching '{fragment}', found {matches.Length} of {files.Count}: "
            + string.Join(", ", files.Keys));

        return matches[0].Value;
    }
}
