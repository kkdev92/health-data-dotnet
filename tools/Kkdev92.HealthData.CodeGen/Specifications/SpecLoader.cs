using System.Security.Cryptography;
using System.Text.Json;

namespace Kkdev92.HealthData.CodeGen.Specifications;

/// <summary>
/// Loads the committed specification inputs for one API version.
/// </summary>
/// <remarks>
/// Everything the generator consumes lives under <c>spec/</c> and is committed. Nothing here
/// touches the network: that is exclusively the <c>fetch</c> command's job.
/// </remarks>
internal sealed class SpecSet
{
    public required string Version { get; init; }
    public required string DiscoveryPath { get; init; }
    public required byte[] DiscoveryBytes { get; init; }
    public required string DiscoverySha256 { get; init; }
    public required JsonDocument Discovery { get; init; }
    public required JsonDocument Metadata { get; init; }
    public required JsonDocument PublicSurface { get; init; }
    public required JsonDocument Semantics { get; init; }
    public required JsonDocument Errors { get; init; }
    public required JsonDocument DataTypes { get; init; }
}

/// <summary>
/// One union schema as <c>semantics.json</c> declares it.
/// </summary>
/// <param name="ExcludedMembers">
/// Message-typed members that are metadata rather than alternatives.
/// </param>
/// <param name="RoundTripNote">
/// What is actually lost if an unrecognised member is dropped, for this schema. It differs: a data
/// point is read and sent back, a roll-up is only ever read. Emitted into the doc comment on the
/// extension data property, so that the generated remark says something true of the type it is on
/// rather than of unions in general.
/// </param>
internal sealed record UnionContract(IReadOnlySet<string> ExcludedMembers, string? RoundTripNote);

internal static class SpecLoader
{
    /// <summary>Locates the repository root by walking up to the solution file.</summary>
    public static string FindRepositoryRoot(string? start = null)
    {
        var directory = new DirectoryInfo(start ?? Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HealthData.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate HealthData.slnx above '{start ?? Directory.GetCurrentDirectory()}'.");
    }

    public static SpecSet Load(string repositoryRoot, string version)
    {
        var specDirectory = Path.Combine(repositoryRoot, "spec", version);

        if (!Directory.Exists(specDirectory))
        {
            throw new InvalidOperationException($"No specification directory for version '{version}' at '{specDirectory}'.");
        }

        var discoveryPath = Path.Combine(specDirectory, "discovery.json");
        var discoveryBytes = File.ReadAllBytes(discoveryPath);

        var spec = new SpecSet
        {
            Version = version,
            DiscoveryPath = discoveryPath,
            DiscoveryBytes = discoveryBytes,
            DiscoverySha256 = Convert.ToHexStringLower(SHA256.HashData(discoveryBytes)),
            Discovery = JsonDocument.Parse(discoveryBytes),
            Metadata = Read(specDirectory, "metadata.json"),
            PublicSurface = Read(specDirectory, "public-surface.json"),
            Semantics = Read(specDirectory, "semantics.json"),
            Errors = Read(specDirectory, "errors.json"),
            DataTypes = Read(specDirectory, "data-types.json"),
        };

        VerifyProvenance(spec);
        return spec;
    }

    /// <summary>
    /// Fails when the snapshot no longer matches the hash recorded alongside it.
    /// </summary>
    /// <remarks>
    /// The usual cause is git rewriting line endings; <c>.gitattributes</c> pins
    /// <c>spec/**/*.json</c> to <c>-text</c> to prevent exactly that.
    /// </remarks>
    private static void VerifyProvenance(SpecSet spec)
    {
        var recordedHash = spec.Metadata.RootElement.GetProperty("sha256").GetString();

        if (!string.Equals(recordedHash, spec.DiscoverySha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"""
                 Specification snapshot does not match its recorded provenance.
                   file: {spec.DiscoveryPath}
                   recorded: {recordedHash}
                   actual: {spec.DiscoverySha256}
                 Re-run 'codegen fetch' if the snapshot was refreshed on purpose, and check that
                     .gitattributes still pins spec/**/*.json to -text.
                 """);
        }

        var metadataRevision = spec.Metadata.RootElement.GetProperty("revision").GetString();
        var discoveryRevision = spec.Discovery.RootElement.GetProperty("revision").GetString();

        if (!string.Equals(metadataRevision, discoveryRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"metadata.json revision '{metadataRevision}' does not match discovery.json revision '{discoveryRevision}'.");
        }

        // A raw response pasted in by hand would hash differently on every fetch, because the
        // endpoint shuffles object keys per request.
        if (!JsonCanonicalizer.IsCanonical(spec.DiscoveryBytes))
        {
            throw new InvalidOperationException(
                $"'{spec.DiscoveryPath}' is not in canonical form. Refresh it with 'codegen fetch' rather than " +
                "saving the raw endpoint response.");
        }
    }

    private static JsonDocument Read(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Missing specification input '{path}'.");
        }

        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    /// <summary>
    /// The union schemas declared in <c>semantics.json</c>, with the members to leave out.
    /// </summary>
    /// <remarks>
    /// Absent means no unions, which emits no helpers. Better than a hard-coded list that quietly
    /// disagrees with the spec, and better than inferring one from the schema — Discovery has no
    /// way to mark a property as metadata rather than an alternative, and the one place it matters
    /// is invisible until the service sends real data.
    /// </remarks>
    public static IReadOnlyDictionary<string, UnionContract> ReadUnions(SpecSet spec)
    {
        var unions = new Dictionary<string, UnionContract>(StringComparer.Ordinal);

        if (!spec.Semantics.RootElement.TryGetProperty("unions", out var declared))
        {
            return unions;
        }

        foreach (var schema in declared.EnumerateObject())
        {
            if (schema.Name.StartsWith('$') || schema.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var excluded = new HashSet<string>(StringComparer.Ordinal);

            if (schema.Value.TryGetProperty("excludeMembers", out var members))
            {
                foreach (var member in members.EnumerateArray())
                {
                    if (member.GetString() is { } name)
                    {
                        excluded.Add(name);
                    }
                }
            }

            unions[schema.Name] = new UnionContract(
                excluded,
                schema.Value.TryGetProperty("roundTripNote", out var note) && note.ValueKind == JsonValueKind.String
                    ? note.GetString()
                    : null);
        }

        return unions;
    }

}
