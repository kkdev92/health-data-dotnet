using System.Reflection;
using System.Text;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Snapshots the public API surface so a change to it has to be deliberate.
/// </summary>
/// <remarks>
/// <para>
/// Package validation compares against a published baseline, and there is not one yet, so this
/// stands in: a type or member that appears, disappears or is renamed shows up as a diff in
/// <c>tests/PublicApi/{assembly}.approved.txt</c> that a reviewer has to accept. Once a version is
/// on nuget.org, <c>PackageValidationBaselineVersion</c> is what actually decides binary
/// compatibility, and this becomes the cheaper first signal rather than the answer.
/// </para>
/// <para>
/// What it records is names and shapes: each type's kind, its members, property types and method
/// signatures. What it does not record is sealed-ness, nullability, <c>init</c> as against
/// <c>set</c> — both render as <c>set;</c> — base types and interfaces, generic constraints, enum
/// and constant values, and operators. A change to any of those is a change this file will not
/// show. "The surface has not moved" means the names and signatures have not moved, and nothing
/// stronger than that.
/// </para>
/// <para>
/// It also keeps the generated surface honest. 138 models and 25 operations are emitted from a
/// specification, and a naming-rule change could silently rename hundreds of members.
/// </para>
/// </remarks>
public sealed class PublicApiTests
{
    /// <summary>
    /// Set <c>APPROVE_PUBLIC_API=1</c> to rewrite the approved files instead of asserting.
    /// </summary>
    /// <remarks>
    /// An environment variable rather than a constant, so approving a deliberate API change does
    /// not require editing and reverting test code:
    /// <c>APPROVE_PUBLIC_API=1 dotnet test --filter PublicApi</c>, then review the diff.
    /// </remarks>
    private static bool OverwriteApprovedFile
        => Environment.GetEnvironmentVariable("APPROVE_PUBLIC_API") == "1";

    [Theory]
    [InlineData("Kkdev92.HealthData")]
    [InlineData("Kkdev92.HealthData.Authentication")]
    [InlineData("Kkdev92.HealthData.Webhooks")]
    [InlineData("Kkdev92.HealthData.DependencyInjection")]
    public void PublicApiMatchesTheApprovedSurface(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var actual = DescribePublicApi(assembly);

        var approvedPath = Path.Combine(RepositoryRoot.Value, "tests", "PublicApi", $"{assemblyName}.approved.txt");

        if (OverwriteApprovedFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(approvedPath)!);
            File.WriteAllText(approvedPath, actual, new UTF8Encoding(false));
        }

        Assert.True(File.Exists(approvedPath), $"No approved API file at '{approvedPath}'.");

        var approved = File.ReadAllText(approvedPath, new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);

        // A diff here is not automatically wrong. It means the public surface moved, and the
        // reviewer has to agree that it should have.
        Assert.Equal(approved, actual);
    }

    /// <summary>Renders the public surface in a stable, diff-friendly form.</summary>
    private static string DescribePublicApi(Assembly assembly)
    {
        var builder = new StringBuilder();

        var types = assembly.GetExportedTypes()
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            builder.Append(Describe(type)).Append('\n');

            var members = type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(IsInteresting)
                .Select(Describe)
                .OrderBy(m => m, StringComparer.Ordinal);

            foreach (var member in members)
            {
                builder.Append("    ").Append(member).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static bool IsInteresting(MemberInfo member)
    {
        // Property accessors and event add/remove appear separately as methods; the property or
        // event line already covers them.
        if (member is MethodInfo { IsSpecialName: true })
        {
            return false;
        }

        // Object's members are noise on every single type.
        return member.DeclaringType != typeof(object);
    }

    private static string Describe(Type type)
    {
        var kind = type switch
        {
            { IsEnum: true } => "enum",
            { IsInterface: true } => "interface",
            { IsValueType: true } => "struct",
            { IsAbstract: true, IsSealed: true } => "static class",
            _ => "class",
        };

        return $"{kind} {type.FullName}";
    }

    private static string Describe(MemberInfo member) => member switch
    {
        PropertyInfo property =>
            $"{TypeName(property.PropertyType)} {property.Name} {{ " +
            $"{(property.GetGetMethod() is not null ? "get; " : string.Empty)}" +
            $"{(property.GetSetMethod() is not null ? "set; " : string.Empty)}}}",

        FieldInfo field => field.IsLiteral
            ? $"const {TypeName(field.FieldType)} {field.Name}"
            : $"{TypeName(field.FieldType)} {field.Name}",

        ConstructorInfo constructor =>
            $".ctor({string.Join(", ", constructor.GetParameters().Select(DescribeParameter))})",

        MethodInfo method =>
            $"{TypeName(method.ReturnType)} {method.Name}" +
            $"({string.Join(", ", method.GetParameters().Select(DescribeParameter))})",

        Type nested => Describe(nested),

        _ => member.ToString() ?? member.Name,
    };

    private static string DescribeParameter(ParameterInfo parameter)
        => $"{TypeName(parameter.ParameterType)} {parameter.Name}{(parameter.HasDefaultValue ? " = default" : string.Empty)}";

    /// <summary>Renders a type name without assembly qualification, so the output is stable.</summary>
    private static string TypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            var arguments = string.Join(", ", type.GetGenericArguments().Select(TypeName));
            return $"{name}<{arguments}>";
        }

        if (type.IsArray)
        {
            return $"{TypeName(type.GetElementType()!)}[]";
        }

        return type.FullName ?? type.Name;
    }
}
