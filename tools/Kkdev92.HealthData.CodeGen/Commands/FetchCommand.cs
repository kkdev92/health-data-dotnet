using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.HealthData.CodeGen.Specifications;

namespace Kkdev92.HealthData.CodeGen.Commands;

/// <summary>
/// Refreshes the Discovery snapshot and its provenance. The only command that uses the network.
/// </summary>
/// <remarks>
/// Fetching never generates code. The snapshot lands in the working tree so that a human reviews
/// the contract change as a diff before any C# is regenerated.
/// </remarks>
internal static class FetchCommand
{
    public static async Task<int> RunAsync(string version, string? timestampUtc, CancellationToken cancellationToken)
    {
        var repositoryRoot = SpecLoader.FindRepositoryRoot();
        var specDirectory = Path.Combine(repositoryRoot, "spec", version);
        var metadataPath = Path.Combine(specDirectory, "metadata.json");
        var discoveryPath = Path.Combine(specDirectory, "discovery.json");

        if (!File.Exists(metadataPath))
        {
            Console.Error.WriteLine($"codegen: no metadata.json for version '{version}' at '{metadataPath}'.");
            return 1;
        }

        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken))!.AsObject();
        var source = metadata["source"]?.GetValue<string>()
                     ?? $"https://health.googleapis.com/$discovery/rest?version={version}";

        Console.WriteLine($"fetching: {source}");

        using var client = new HttpClient();
        var rawPayload = await client.GetByteArrayAsync(new Uri(source), cancellationToken);

        // The endpoint randomizes object key order per request, so the raw bytes are not a stable
        // identity. Canonicalize before hashing or storing. See JsonCanonicalizer.
        var payload = JsonCanonicalizer.Canonicalize(rawPayload);
        var rawHash = Convert.ToHexStringLower(SHA256.HashData(rawPayload));
        var newHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var oldHash = metadata["sha256"]?.GetValue<string>();
        var oldRevision = metadata["revision"]?.GetValue<string>() ?? "(none)";

        if (string.Equals(newHash, oldHash, StringComparison.Ordinal))
        {
            Console.WriteLine($"unchanged: revision {oldRevision}, canonical sha256 {newHash}");
            Console.WriteLine($"           (raw response hash {rawHash} varies per request and is not recorded)");
            return 0;
        }

        using var fetched = JsonDocument.Parse(payload);
        var revision = fetched.RootElement.GetProperty("revision").GetString()!;
        var fetchedVersion = fetched.RootElement.GetProperty("version").GetString()!;

        if (!string.Equals(fetchedVersion, version, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"codegen: requested version '{version}' but the document reports '{fetchedVersion}'.");
            return 1;
        }

        // Written in canonical form so the committed snapshot is byte-stable and its diff is
        // reviewable. The content is identical to the response; only key order and whitespace
        // are normalized.
        await File.WriteAllBytesAsync(discoveryPath, payload, cancellationToken);

        metadata["revision"] = revision;
        metadata["sha256"] = newHash;
        metadata["byteLength"] = payload.Length;

        // The clock is an input, never something the tool invents, so that a fetch can be
        // replayed deterministically in tests.
        if (!string.IsNullOrWhiteSpace(timestampUtc))
        {
            metadata["retrievedAtUtc"] = timestampUtc;
        }

        // Deterministic and human-readable: LF regardless of platform (WriteIndented would
        // otherwise use Environment.NewLine) and no ' escaping of ordinary punctuation.
        var serialized = metadata.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        await File.WriteAllTextAsync(metadataPath, serialized + "\n", new UTF8Encoding(false), cancellationToken);

        Console.WriteLine($"updated: revision {oldRevision} -> {revision}");
        Console.WriteLine($"           sha256   {oldHash ?? "(none)"} -> {newHash}");
        Console.WriteLine($"           {discoveryPath}");
        Console.WriteLine();
        Console.WriteLine("Next: run 'codegen diff' to review the contract change, then 'codegen generate'.");
        return 0;
    }
}
