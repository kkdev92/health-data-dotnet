using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Kkdev92.HealthData.CodeGen.Commands;

/// <summary>
/// Removes documentation for members a consumer cannot see.
/// </summary>
/// <remarks>
/// <para>
/// The compiler documents whatever carries a <c>///</c> comment, private fields included, and this
/// repository comments private members on purpose — the reasoning belongs next to the code it
/// explains. That reasoning then ships inside the package. Microsoft's own guidance on the switch
/// says documenting private members "exposes the inner (potentially confidential) workings of your
/// library", and there is no compiler option for it: <c>-doc</c> takes a file, not a visibility.
/// </para>
/// <para>
/// Visibility comes from the compiled assembly rather than from a guess about the XML: a member id
/// says nothing about whether anyone outside can reach it. The assembly is read as metadata and
/// never loaded, so this cannot run its code or fail on a reference it has no copy of.
/// </para>
/// <para>
/// Whole <c>&lt;member&gt;</c> elements are removed and nothing is edited inside one.
/// <c>&lt;inheritdoc/&gt;</c> on a public member resolves against base types in this same file, so
/// trimming inside an element could break a reference the surviving documentation depends on.
/// </para>
/// </remarks>
internal static class FilterDocsCommand
{
    public static int Run(string documentationFile, string assemblyFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentationFile);
        ArgumentException.ThrowIfNullOrEmpty(assemblyFile);

        if (!File.Exists(documentationFile))
        {
            Console.Error.WriteLine($"codegen: no documentation file at '{documentationFile}'.");
            return 1;
        }

        if (!File.Exists(assemblyFile))
        {
            Console.Error.WriteLine($"codegen: no assembly at '{assemblyFile}'.");
            return 1;
        }

        var visible = ReadVisibleMembers(assemblyFile);
        var document = XDocument.Load(documentationFile);
        var members = document.Root?.Element("members");

        if (members is null)
        {
            return 0;
        }

        var removed = 0;

        foreach (var member in members.Elements("member").ToList())
        {
            if ((string?)member.Attribute("name") is not { } id)
            {
                continue;
            }

            if (IsVisible(id, visible))
            {
                continue;
            }

            member.Remove();
            removed++;
        }

        if (removed > 0)
        {
            document.Save(documentationFile);
        }

        Console.WriteLine(
            $"filter-docs        : {removed} of {removed + members.Elements("member").Count()} entries removed "
            + $"from {Path.GetFileName(documentationFile)}");

        return 0;
    }

    /// <summary>
    /// Whether a documentation id names something a consumer can reach.
    /// </summary>
    /// <remarks>
    /// The id carries a signature and arity that the member table does not — <c>M:T.M(System.String)</c>
    /// against <c>M:T.M</c>, and <c>T:T`1</c> against <c>T:T</c> — so the comparison is on the part
    /// before either. Overloads share a visibility in every case this contract produces, and a
    /// member the assembly does not admit to at all is removed whatever its signature says.
    /// </remarks>
    private static bool IsVisible(string id, IReadOnlySet<string> visible)
    {
        var withoutSignature = id.IndexOf('(', StringComparison.Ordinal) is var open and >= 0
            ? id[..open]
            : id;

        // An explicit interface implementation spells the interface with '#', and a generic
        // parameter list with '{' '}'.
        var normalized = withoutSignature.Replace('#', '.');

        if (normalized.IndexOf('{', StringComparison.Ordinal) is var brace and >= 0)
        {
            normalized = normalized[..brace];
        }

        if (visible.Contains(normalized))
        {
            return true;
        }

        // Arity: the id says List`1, the table says the same, but a conversion operator's id
        // carries a return type after '~'.
        var tilde = normalized.IndexOf('~', StringComparison.Ordinal);

        return tilde >= 0 && visible.Contains(normalized[..tilde]);
    }

    private static IReadOnlySet<string> ReadVisibleMembers(string assemblyFile)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);

        using var stream = File.OpenRead(assemblyFile);
        using var peReader = new PEReader(stream);

        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);

            if (!IsTypeVisible(metadata, definition))
            {
                continue;
            }

            var typeName = XmlTypeName(metadata, definition);
            visible.Add("T:" + typeName);

            foreach (var methodHandle in definition.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);

                if (IsAccessible(method.Attributes & MethodAttributes.MemberAccessMask))
                {
                    visible.Add("M:" + typeName + "." + metadata.GetString(method.Name).Replace('.', '#'));
                    visible.Add("M:" + typeName + "." + metadata.GetString(method.Name));
                }
            }

            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = metadata.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();

                if (IsAccessorVisible(metadata, accessors.Getter) || IsAccessorVisible(metadata, accessors.Setter))
                {
                    visible.Add("P:" + typeName + "." + metadata.GetString(property.Name));
                }
            }

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);

                if (IsAccessible(field.Attributes & FieldAttributes.FieldAccessMask))
                {
                    visible.Add("F:" + typeName + "." + metadata.GetString(field.Name));
                }
            }

            foreach (var eventHandle in definition.GetEvents())
            {
                var declaration = metadata.GetEventDefinition(eventHandle);

                if (IsAccessorVisible(metadata, declaration.GetAccessors().Adder))
                {
                    visible.Add("E:" + typeName + "." + metadata.GetString(declaration.Name));
                }
            }
        }

        return visible;
    }

    /// <summary>Protected counts as visible: a consumer can derive.</summary>
    private static bool IsAccessible(MethodAttributes access)
        => access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    private static bool IsAccessible(FieldAttributes access)
        => access is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;

    private static bool IsAccessorVisible(MetadataReader metadata, MethodDefinitionHandle handle)
        => !handle.IsNil
            && IsAccessible(metadata.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask);

    private static bool IsTypeVisible(MetadataReader metadata, TypeDefinition definition)
    {
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;

        if (visibility == TypeAttributes.Public)
        {
            return true;
        }

        if (visibility is not (TypeAttributes.NestedPublic or TypeAttributes.NestedFamily
            or TypeAttributes.NestedFamORAssem))
        {
            return false;
        }

        // A public type nested in an internal one is not reachable, so the whole chain is checked.
        var declaring = definition.GetDeclaringType();

        return !declaring.IsNil && IsTypeVisible(metadata, metadata.GetTypeDefinition(declaring));
    }

    /// <summary>The name a documentation id uses, where nesting is a dot rather than a plus.</summary>
    private static string XmlTypeName(MetadataReader metadata, TypeDefinition definition)
    {
        var name = metadata.GetString(definition.Name);
        var declaringHandle = definition.GetDeclaringType();

        while (!declaringHandle.IsNil)
        {
            var declaring = metadata.GetTypeDefinition(declaringHandle);
            name = metadata.GetString(declaring.Name) + "." + name;
            declaringHandle = declaring.GetDeclaringType();

            if (declaringHandle.IsNil)
            {
                var ns = metadata.GetString(declaring.Namespace);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }
        }

        var namespaceName = metadata.GetString(definition.Namespace);

        return string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name;
    }
}
