using Kkdev92.HealthData.CodeGen.IntermediateModel;

namespace Kkdev92.HealthData.CodeGen.CSharp;

/// <summary>
/// Emits one type per resource name pattern.
/// </summary>
/// <remarks>
/// <para>
/// A generated name is a validated string with the shape the service demands. It is a class rather
/// than a struct on purpose: a struct has a <c>default</c> that no constructor ever saw, and the
/// one thing a name type must not have is an instance holding nothing that still compiles into a
/// request. Google's own generated .NET names are classes for the same reason.
/// </para>
/// <para>
/// The pattern from Discovery is compiled with <c>[GeneratedRegex]</c>, so validation is a
/// source-generated matcher rather than a runtime parse, and it is exactly the expression the
/// service applies — not a restatement of it.
/// </para>
/// </remarks>
internal sealed class ResourceNameEmitter(ApiContract contract)
{
    internal const string NamesNamespace = "Kkdev92.HealthData.Names";

    /// <summary>
    /// A backtick, which is how doc text asks for <c>&lt;c&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="CodeWriter.XmlDoc"/> escapes angle brackets — it has to, since most doc text
    /// comes from Discovery and arrives full of them — and converts the markdown it does
    /// understand. Writing the tag here would emit <c>&amp;lt;c&amp;gt;</c> into the comment.
    /// </remarks>
    private const string Tick = "`";

    public IEnumerable<GeneratedFile> Emit(Func<string, string[], CodeWriter> header)
    {
        foreach (var name in contract.ResourceNames)
        {
            yield return EmitName(name, header);
        }
    }

    private GeneratedFile EmitName(ResourceNameContract name, Func<string, string[], CodeWriter> header)
    {
        var writer = header(NamesNamespace, ["System.Diagnostics.CodeAnalysis", "System.Text.RegularExpressions"]);
        var children = contract.ResourceNames.Where(child => child.ParentCSharpName == name.CSharpName).ToArray();

        writer.XmlDoc("summary", $"The name of a {name.MemberName} resource: {Tick}{name.Example}{Tick}.");
        writer.XmlDoc(
            "remarks",
            $"The service requires this to match {Tick}{name.Pattern}{Tick}, and every instance of this type does. "
            + "Build one from its parts rather than from a string wherever you can — the parts cannot be "
            + "assembled into a name of the wrong shape.");

        using (writer.Block($"public sealed partial record {name.CSharpName}"))
        {
            writer.Line("private readonly string _value;");
            writer.Line();
            writer.Line($"private {name.CSharpName}(string value) => _value = value;");
            writer.Line();

            EmitPattern(writer, name);
            EmitParse(writer, name);
            EmitFactories(writer, name);
            EmitIds(writer, name);
            EmitParentAccessor(writer, name);
            EmitChildBuilders(writer, children);

            writer.Line();
            writer.XmlDoc("summary", "Returns the wire form of the name.");
            writer.Line("public override string ToString() => _value;");
        }

        return new GeneratedFile($"Generated/Names/{name.CSharpName}.g.cs", writer.ToString());
    }

    private static void EmitPattern(CodeWriter writer, ResourceNameContract name)
    {
        writer.XmlDoc("summary", "The pattern the service states for this name.");
        writer.Line($"public const string Pattern = {CodeWriter.Literal(name.Pattern)};");
        writer.Line();
        writer.Line($"[GeneratedRegex(Pattern)]");
        writer.Line("private static partial Regex Matcher();");
        writer.Line();
    }

    private static void EmitParse(CodeWriter writer, ResourceNameContract name)
    {
        writer.XmlDoc("summary", $"Parses the wire form of a {name.CSharpName}.");
        writer.XmlDoc("param name=\"value\"", $"A name of the form {Tick}{name.Example}{Tick}.");
        writer.XmlDoc(
            "exception cref=\"FormatException\"",
            "The value does not match the pattern the service requires.");

        using (writer.Block($"public static {name.CSharpName} Parse(string value)"))
        {
            writer.Line("ArgumentNullException.ThrowIfNull(value);");
            writer.Line();

            using (writer.Block("if (!Matcher().IsMatch(value))"))
            {
                writer.Line("throw new FormatException(");
                // The example carries {placeholders}; doubled so the emitted interpolated
                // string prints them rather than looking for variables of those names.
                writer.Line(
                    $"    $\"'{{value}}' is not a {name.CSharpName}. The service requires the form "
                    + $"{name.Example.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)}, "
                    + $"matching {{Pattern}}.\");");
            }

            writer.Line();
            writer.Line($"return new {name.CSharpName}(value);");
        }

        writer.Line();
        writer.XmlDoc("summary", "Parses the wire form, or reports that it does not match.");
        writer.XmlDoc("remarks", "For a name from somewhere unverified. A name this SDK built always parses.");

        using (writer.Block(
            $"public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out {name.CSharpName}? name)"))
        {
            using (writer.Block("if (value is null || !Matcher().IsMatch(value))"))
            {
                writer.Line("name = null;");
                writer.Line("return false;");
            }

            writer.Line();
            writer.Line($"name = new {name.CSharpName}(value);");
            writer.Line("return true;");
        }

        writer.Line();
    }

    /// <summary>
    /// The factory that builds a root name from its ids, and <c>Me</c> where the API has one.
    /// </summary>
    /// <remarks>
    /// Only roots get a <c>From</c>. A child is built from its parent — <c>user.PairedDevice(id)</c>
    /// — because that is the relationship the pattern describes, and offering both ways of writing
    /// the same name would leave a caller choosing between them for no reason.
    /// </remarks>
    private static void EmitFactories(CodeWriter writer, ResourceNameContract name)
    {
        if (name.ParentCSharpName is not null)
        {
            return;
        }

        var id = name.IdParameterNames[0];
        var collection = name.Segments[0].Literal;

        writer.XmlDoc("summary", $"Builds the name of one item in the {Tick}{name.Segments[0].Literal}{Tick} collection.");
        writer.XmlDoc("param name=\"" + id + "\"", $"The id, which must not contain a slash.");

        using (writer.Block($"public static {name.CSharpName} From(string {id})"))
        {
            writer.Line($"ArgumentException.ThrowIfNullOrEmpty({id});");
            writer.Line();
            writer.Line($"return Parse($\"{collection}/{{{id}}}\");");
        }

        writer.Line();

        // users/me is the only alias the API defines, and the pattern accepts it like any other
        // id. It is worth a member of its own because every call in a user-facing app uses it.
        if (string.Equals(collection, "users", StringComparison.Ordinal))
        {
            writer.XmlDoc("summary", "The signed-in user: users/me.");
            writer.XmlDoc(
                "remarks",
                "The alias Google documents for whoever the credential belongs to. It is an id like any "
                + "other as far as the pattern is concerned.");
            writer.Line($"public static {name.CSharpName} Me {{ get; }} = Parse(\"users/me\");");
            writer.Line();
        }
    }

    /// <summary>The ids the name carries, each read back out of the validated string.</summary>
    private static void EmitIds(CodeWriter writer, ResourceNameContract name)
    {
        var index = 0;

        for (var position = 0; position < name.Segments.Count; position++)
        {
            if (!name.Segments[position].IsVariable)
            {
                continue;
            }

            var id = name.IdParameterNames[index++];
            var property = char.ToUpperInvariant(id[0]) + id[1..];

            writer.XmlDoc("summary", $"The {Tick}{id}{Tick} segment of this name.");
            writer.Line($"public string {property} => Segment({Normalization.NamingNormalizer.Invariant(position)});");
            writer.Line();
        }

        // Split on demand rather than held: a name is usually passed straight to a request, and
        // the ids are read only when a caller wants one of them back out.
        writer.Line("private string Segment(int index) => _value.Split('/')[index];");
        writer.Line();
    }

    private static void EmitParentAccessor(CodeWriter writer, ResourceNameContract name)
    {
        if (name.ParentCSharpName is not { } parent)
        {
            return;
        }

        writer.XmlDoc("summary", $"The {parent} this name belongs to.");
        writer.Line($"public {parent} {ParentMember(parent)} => {parent}.Parse(");
        writer.Line($"    string.Join('/', _value.Split('/')[..{Normalization.NamingNormalizer.Invariant(ParentSegmentCount(name))}]));");
        writer.Line();
    }

    private static void EmitChildBuilders(CodeWriter writer, IReadOnlyList<ResourceNameContract> children)
    {
        foreach (var child in children)
        {
            if (child.IdParameterName is { } id)
            {
                writer.XmlDoc("summary", $"The name of one item in this resource's {Tick}{child.Segments[^2].Literal}{Tick} collection.");
                writer.XmlDoc("param name=\"" + id + "\"", "The id, which must not contain a slash.");

                using (writer.Block($"public {child.CSharpName} {child.MemberName}(string {id})"))
                {
                    writer.Line($"ArgumentException.ThrowIfNullOrEmpty({id});");
                    writer.Line();
                    writer.Line($"return {child.CSharpName}.Parse($\"{{_value}}/{child.Segments[^2].Literal}/{{{id}}}\");");
                }

                writer.Line();
                continue;
            }

            // A singleton: there is exactly one, so it is a property rather than a lookup.
            writer.XmlDoc("summary", $"The {Tick}{child.Segments[^1].Literal}{Tick} of this resource.");
            writer.Line(
                $"public {child.CSharpName} {child.MemberName} => "
                + $"{child.CSharpName}.Parse($\"{{_value}}/{child.Segments[^1].Literal}\");");
            writer.Line();
        }
    }

    /// <summary>How many segments the parent's own pattern has.</summary>
    private static int ParentSegmentCount(ResourceNameContract name)
        => name.Segments.Count - (name.IdParameterName is null ? 1 : 2);

    /// <summary>The member name a child uses for its parent.</summary>
    private static string ParentMember(string parentTypeName)
        => parentTypeName.EndsWith("Name", StringComparison.Ordinal)
            ? parentTypeName[..^"Name".Length]
            : parentTypeName;
}
