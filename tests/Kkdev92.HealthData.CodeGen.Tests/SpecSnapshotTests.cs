using System.Security.Cryptography;
using System.Text.Json;
using Kkdev92.HealthData.CodeGen.Specifications;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// Guards the integrity of the committed specification snapshot.
/// </summary>
/// <remarks>
/// These were the first tests written, because they protect provenance and everything the
/// generator produces rests on it. They deliberately fail whenever
/// the Discovery snapshot changes without <c>metadata.json</c> and <c>public-surface.json</c>
/// being reviewed in the same change.
/// </remarks>
public sealed class SpecSnapshotTests
{
    private static readonly string SpecDirectory = Path.Combine(RepositoryRoot.Value, "spec", "v4");

    private static JsonDocument LoadJson(string fileName)
        => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(SpecDirectory, fileName)));

    [Fact]
    public void DiscoverySnapshotMatchesRecordedHash()
    {
        var bytes = File.ReadAllBytes(Path.Combine(SpecDirectory, "discovery.json"));
        using var metadata = LoadJson("metadata.json");

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var expectedHash = metadata.RootElement.GetProperty("sha256").GetString();
        var expectedLength = metadata.RootElement.GetProperty("byteLength").GetInt32();

        // A mismatch here usually means one of three things:
        //   1. the snapshot was refreshed without updating metadata.json,
        //   2. git rewrote line endings (see.gitattributes, which pins spec/**/*.json to -text), or
        //   3. a raw endpoint response was pasted in instead of running 'codegen fetch'.
        Assert.Equal(expectedLength, bytes.Length);
        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public void DiscoverySnapshotIsStoredInCanonicalForm()
    {
        // The Discovery endpoint randomizes object key order per request: four consecutive
        // fetches on 2026-08-09 produced four different hashes at an identical byte length.
        // Canonical storage is what makes the recorded hash meaningful and keeps a contract
        // update readable as a diff instead of a whole-file rewrite.
        var bytes = File.ReadAllBytes(Path.Combine(SpecDirectory, "discovery.json"));

        Assert.True(
            JsonCanonicalizer.IsCanonical(bytes),
            "spec/v4/discovery.json is not canonical. Refresh it with 'codegen fetch' rather than saving a raw response.");

        using var metadata = LoadJson("metadata.json");
        Assert.True(metadata.RootElement.GetProperty("canonicalized").GetBoolean());
    }

    [Fact]
    public void MetadataRevisionMatchesDiscoveryRevision()
    {
        using var discovery = LoadJson("discovery.json");
        using var metadata = LoadJson("metadata.json");

        Assert.Equal(
            discovery.RootElement.GetProperty("revision").GetString(),
            metadata.RootElement.GetProperty("revision").GetString());

        Assert.Equal(
            discovery.RootElement.GetProperty("version").GetString(),
            metadata.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void EveryDiscoveryOperationIsEitherAllowedOrExplicitlyExcluded()
    {
        using var discovery = LoadJson("discovery.json");
        using var surface = LoadJson("public-surface.json");

        var discovered = EnumerateOperationIds(discovery.RootElement.GetProperty("resources")).ToHashSet(StringComparer.Ordinal);

        var allowed = surface.RootElement.GetProperty("operations")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

        var excluded = surface.RootElement.GetProperty("excluded")
            .EnumerateArray().Select(e => e.GetProperty("operation").GetString()!).ToHashSet(StringComparer.Ordinal);

        // An operation may not be both allowed and excluded.
        Assert.Empty(allowed.Intersect(excluded, StringComparer.Ordinal));

        // Nothing may be listed that the API does not actually expose.
        Assert.Empty(allowed.Except(discovered, StringComparer.Ordinal));
        Assert.Empty(excluded.Except(discovered, StringComparer.Ordinal));

        // The decisive rule: a new Google operation must not slip in unreviewed. A warning would
        // be easy to scroll past; a failing test is the review gate, and it can only trigger
        // inside a pull request that refreshes the snapshot.
        var unclassified = discovered.Except(allowed, StringComparer.Ordinal)
            .Except(excluded, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            unclassified.Length == 0,
            $"Discovery exposes operations that are neither allowed nor excluded in public-surface.json: {string.Join(", ", unclassified)}");
    }

    [Fact]
    public void SnapshotContainsTheOperationCountThisPlanWasBuiltOn()
    {
        using var discovery = LoadJson("discovery.json");
        using var surface = LoadJson("public-surface.json");

        Assert.Equal(27, EnumerateOperationIds(discovery.RootElement.GetProperty("resources")).Count());
        Assert.Equal(25, surface.RootElement.GetProperty("operations").GetArrayLength());
        Assert.Equal(2, surface.RootElement.GetProperty("excluded").GetArrayLength());
    }

    [Fact]
    public void ErrorAndDataTypeSpecsAreWellFormedAndAttributed()
    {
        using var errors = LoadJson("errors.json");
        Assert.Equal(
            errors.RootElement.GetProperty("count").GetInt32(),
            errors.RootElement.GetProperty("reasons").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(errors.RootElement.GetProperty("source").GetProperty("url").GetString()));

        using var dataTypes = LoadJson("data-types.json");
        var entries = dataTypes.RootElement.GetProperty("dataTypes").EnumerateArray().ToArray();
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            var id = entry.GetProperty("id").GetString()!;
            var filterName = entry.GetProperty("filterName").GetString()!;

            // Endpoint ids are kebab-case and filter names are snake_case. Both are carried
            // verbatim; neither may be derived from the other.
            Assert.DoesNotContain("_", id, StringComparison.Ordinal);
            Assert.DoesNotContain("-", filterName, StringComparison.Ordinal);
            Assert.NotEmpty(entry.GetProperty("operations").EnumerateArray());
        }
    }

    [Fact]
    public void SupportedVersionsIncludeTheCurrentVersion()
    {
        using var versions = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(RepositoryRoot.Value, "spec", "versions.json")));

        var current = versions.RootElement.GetProperty("current").GetString();
        var supported = versions.RootElement.GetProperty("supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Contains(current, supported);
        Assert.True(Directory.Exists(Path.Combine(RepositoryRoot.Value, "spec", current!)));
    }

    private static IEnumerable<string> EnumerateOperationIds(JsonElement resources)
    {
        foreach (var resource in resources.EnumerateObject())
        {
            if (resource.Value.TryGetProperty("methods", out var methods))
            {
                foreach (var method in methods.EnumerateObject())
                {
                    yield return method.Value.GetProperty("id").GetString()!;
                }
            }

            if (resource.Value.TryGetProperty("resources", out var nested))
            {
                foreach (var id in EnumerateOperationIds(nested))
                {
                    yield return id;
                }
            }
        }
    }
}
