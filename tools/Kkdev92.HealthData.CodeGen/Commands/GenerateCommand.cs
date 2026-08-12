using Kkdev92.HealthData.CodeGen.CSharp;
using Kkdev92.HealthData.CodeGen.Discovery;
using Kkdev92.HealthData.CodeGen.Specifications;
using Kkdev92.HealthData.CodeGen.Validation;

namespace Kkdev92.HealthData.CodeGen.Commands;

/// <summary>
/// Generates the C# contract from the committed specification snapshot. Never touches the network.
/// </summary>
internal static class GenerateCommand
{
    /// <summary>Where generated sources live, relative to the repository root.</summary>
    public const string OutputProject = "src/Kkdev92.HealthData";

    private const string GeneratedDirectory = "Generated";

    public static int Run(string version, bool verifyOnly)
    {
        var repositoryRoot = SpecLoader.FindRepositoryRoot();
        var spec = SpecLoader.Load(repositoryRoot, version);
        var contract = DiscoveryParser.Parse(spec);
        var validation = ContractValidator.Validate(spec, contract);

        foreach (var warning in validation.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        foreach (var error in validation.Errors)
        {
            Console.Error.WriteLine($"error: {error}");
        }

        if (validation.HasErrors)
        {
            Console.Error.WriteLine($"codegen: generation aborted, {validation.Errors.Count} validation error(s).");
            return 1;
        }

        var emitter = new CSharpEmitter(contract, ReadFlattenedResourcePaths(spec), SpecLoader.ReadUnions(spec));
        var files = emitter.Emit();
        var outputRoot = Path.Combine(repositoryRoot, OutputProject);

        Console.WriteLine($"api version       : {contract.ApiVersion}");
        Console.WriteLine($"discovery revision: {contract.Revision}");
        Console.WriteLine($"spec sha256       : {contract.SpecSha256}");
        Console.WriteLine($"operations        : {contract.Operations.Count}");
        Console.WriteLine($"schemas reachable : {contract.Schemas.Count}");
        Console.WriteLine($"scopes            : {contract.Scopes.Count}");
        Console.WriteLine($"error reasons     : {contract.ErrorReasons.Count}");

        // Partial coverage is always reported. A generator that silently emits a subset reads as
        // if it covered everything.
        if (emitter.SkippedSchemaCount > 0)
        {
            Console.WriteLine(
                $"note              : {emitter.SkippedSchemaCount} reachable schema(s) not emitted.");
        }

        return verifyOnly
            ? Verify(outputRoot, files)
            : Write(outputRoot, files);
    }

    /// <summary>
    /// Resource path segments collapsed away in the C# surface, for example
    /// <c>users.dataTypes</c> so that callers write <c>client.Users.DataPoints</c>.
    /// </summary>
    /// <remarks>
    /// An explicit list in <c>semantics.json</c> rather than a heuristic. Both
    /// <c>users.dataTypes</c> and <c>projects</c> declare no methods, but only one of them
    /// should disappear from the public surface.
    /// </remarks>
    public static IReadOnlySet<string> ReadFlattenedResourcePaths(SpecSet spec)
    {
        if (spec.Semantics.RootElement.TryGetProperty("resourceNaming", out var naming) &&
            naming.TryGetProperty("flatten", out var flatten))
        {
            return flatten.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static int Write(string outputRoot, IReadOnlyList<GeneratedFile> files)
    {
        var expected = files.Select(f => Normalize(f.RelativePath)).ToHashSet(StringComparer.Ordinal);
        var changed = 0;

        foreach (var file in files)
        {
            if (CodeWriter.WriteIfChanged(Path.Combine(outputRoot, file.RelativePath), file.Content))
            {
                changed++;
            }
        }

        // Remove sources that the generator no longer produces, so that a renamed or deleted
        // operation cannot leave a stale file behind.
        var removed = 0;

        foreach (var stale in EnumerateGeneratedFiles(outputRoot).Where(p => !expected.Contains(p)))
        {
            File.Delete(Path.Combine(outputRoot, stale));
            Console.WriteLine($"removed: {stale}");
            removed++;
        }

        Console.WriteLine($"generated         : {files.Count} file(s), {changed} changed, {removed} removed");
        return 0;
    }

    private static int Verify(string outputRoot, IReadOnlyList<GeneratedFile> files)
    {
        var problems = new List<string>();
        var expected = files.ToDictionary(f => Normalize(f.RelativePath), f => f.Content, StringComparer.Ordinal);
        var onDisk = EnumerateGeneratedFiles(outputRoot).ToHashSet(StringComparer.Ordinal);

        foreach (var (relativePath, content) in expected.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var absolute = Path.Combine(outputRoot, relativePath);

            if (!File.Exists(absolute))
            {
                problems.Add($"missing: {relativePath}");
                continue;
            }

            var actual = File.ReadAllText(absolute, CodeWriter.OutputEncoding);

            if (!string.Equals(actual, content, StringComparison.Ordinal))
            {
                problems.Add($"stale: {relativePath}");
            }
        }

        foreach (var orphan in onDisk.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            problems.Add($"orphaned: {orphan}");
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"error: {problem}");
            }

            Console.Error.WriteLine(
                "codegen: checked-in generated sources are not up to date. Run 'codegen generate'.");
            return 1;
        }

        Console.WriteLine($"verified          : {files.Count} file(s) match the committed sources");
        return 0;
    }

    private static IEnumerable<string> EnumerateGeneratedFiles(string outputRoot)
    {
        var directory = Path.Combine(outputRoot, GeneratedDirectory);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, "*.g.cs", SearchOption.AllDirectories)
            .Select(path => Normalize(Path.GetRelativePath(outputRoot, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Uses forward slashes on every platform so comparisons are stable.</summary>
    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/');
}
