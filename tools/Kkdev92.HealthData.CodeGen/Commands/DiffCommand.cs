using System.Text.Json;
using Kkdev92.HealthData.CodeGen.Discovery;
using Kkdev92.HealthData.CodeGen.Specifications;

namespace Kkdev92.HealthData.CodeGen.Commands;

/// <summary>
/// Reports what changed between the committed snapshot and another Discovery document.
/// </summary>
/// <remarks>
/// Changes are classified as additive or potentially breaking so
/// that a reviewer sees the risk before any code is regenerated. Semantic risk, such as a change
/// in retry guidance or date-range policy, is not visible in a Discovery diff at all and still
/// requires reading the release notes.
/// </remarks>
internal static class DiffCommand
{
    public static async Task<int> RunAsync(string version, string? againstPath, CancellationToken cancellationToken)
    {
        var repositoryRoot = SpecLoader.FindRepositoryRoot();
        var spec = SpecLoader.Load(repositoryRoot, version);

        byte[] candidateBytes;

        if (againstPath is not null)
        {
            candidateBytes = await File.ReadAllBytesAsync(againstPath, cancellationToken);
            Console.WriteLine($"comparing against: {againstPath}");
        }
        else
        {
            var source = spec.Metadata.RootElement.GetProperty("source").GetString()!;
            Console.WriteLine($"comparing against: {source}");
            using var client = new HttpClient();
            candidateBytes = await client.GetByteArrayAsync(new Uri(source), cancellationToken);
        }

        using var candidate = JsonDocument.Parse(candidateBytes);

        var oldRoot = spec.Discovery.RootElement;
        var newRoot = candidate.RootElement;

        var oldRevision = oldRoot.GetProperty("revision").GetString()!;
        var newRevision = newRoot.GetProperty("revision").GetString()!;

        Console.WriteLine($"revision: {oldRevision} -> {newRevision}");
        Console.WriteLine();

        var breaking = new List<string>();
        var additive = new List<string>();

        CompareOperations(oldRoot, newRoot, breaking, additive);
        CompareSchemas(oldRoot, newRoot, breaking, additive);
        CompareScopes(oldRoot, newRoot, breaking, additive);

        Report("Additive / safe", additive);
        Report("Potentially breaking", breaking);

        if (additive.Count == 0 && breaking.Count == 0)
        {
            Console.WriteLine(
                oldRevision == newRevision
                    ? "No contract changes."
                    : "Revision changed but no structural differences were detected.");
        }

        Console.WriteLine();
        Console.WriteLine(
            "Semantic risk is not visible here. Review the release notes for retry, page size, " +
            "date range, webhook and OAuth behaviour changes.");

        // A diff is a report, not a gate. Breaking changes are surfaced for a human to judge.
        return 0;
    }

    private static void CompareOperations(JsonElement oldRoot, JsonElement newRoot, List<string> breaking, List<string> additive)
    {
        var oldOps = Operations(oldRoot);
        var newOps = Operations(newRoot);

        foreach (var id in newOps.Keys.Except(oldOps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            additive.Add($"operation added: {id}");
        }

        foreach (var id in oldOps.Keys.Except(newOps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            breaking.Add($"operation removed: {id}");
        }

        foreach (var id in oldOps.Keys.Intersect(newOps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var before = oldOps[id];
            var after = newOps[id];

            CompareString(before, after, "httpMethod", $"{id}: HTTP method", breaking);
            CompareString(before, after, "path", $"{id}: path", breaking);

            var beforeParams = Parameters(before);
            var afterParams = Parameters(after);

            foreach (var name in afterParams.Keys.Except(beforeParams.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var required = afterParams[name].TryGetProperty("required", out var r) && r.GetBoolean();
                (required ? breaking : additive).Add(
                    $"{id}: parameter added{(required ? " (required)" : string.Empty)}: {name}");
            }

            foreach (var name in beforeParams.Keys.Except(afterParams.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                breaking.Add($"{id}: parameter removed: {name}");
            }

            foreach (var name in beforeParams.Keys.Intersect(afterParams.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var wasRequired = beforeParams[name].TryGetProperty("required", out var b) && b.GetBoolean();
                var isRequired = afterParams[name].TryGetProperty("required", out var a) && a.GetBoolean();

                if (!wasRequired && isRequired)
                {
                    breaking.Add($"{id}: parameter '{name}' became required");
                }
                else if (wasRequired && !isRequired)
                {
                    additive.Add($"{id}: parameter '{name}' became optional");
                }

                CompareString(beforeParams[name], afterParams[name], "type", $"{id}: parameter '{name}' type", breaking);
                CompareString(beforeParams[name], afterParams[name], "format", $"{id}: parameter '{name}' format", breaking);
            }

            CompareRef(before, after, "request", $"{id}: request schema", breaking);
            CompareRef(before, after, "response", $"{id}: response schema", breaking);

            var beforeScopes = Scopes(before);
            var afterScopes = Scopes(after);

            foreach (var scope in afterScopes.Except(beforeScopes, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                additive.Add($"{id}: scope accepted: {scope}");
            }

            foreach (var scope in beforeScopes.Except(afterScopes, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                breaking.Add($"{id}: scope no longer accepted: {scope}");
            }
        }
    }

    private static void CompareSchemas(JsonElement oldRoot, JsonElement newRoot, List<string> breaking, List<string> additive)
    {
        var oldSchemas = oldRoot.GetProperty("schemas");
        var newSchemas = newRoot.GetProperty("schemas");

        var oldNames = oldSchemas.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var newNames = newSchemas.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in newNames.Except(oldNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            additive.Add($"schema added: {name}");
        }

        foreach (var name in oldNames.Except(newNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            breaking.Add($"schema removed: {name}");
        }

        foreach (var name in oldNames.Intersect(newNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var beforeProps = Properties(oldSchemas.GetProperty(name));
            var afterProps = Properties(newSchemas.GetProperty(name));

            foreach (var property in afterProps.Keys.Except(beforeProps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                additive.Add($"{name}.{property}: property added");
            }

            foreach (var property in beforeProps.Keys.Except(afterProps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                breaking.Add($"{name}.{property}: property removed");
            }

            foreach (var property in beforeProps.Keys.Intersect(afterProps.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var before = beforeProps[property];
                var after = afterProps[property];

                CompareString(before, after, "type", $"{name}.{property}: type", breaking);
                CompareString(before, after, "format", $"{name}.{property}: format", breaking);
                CompareRef(before, after, "$ref", $"{name}.{property}: reference", breaking, direct: true);

                var wasReadOnly = before.TryGetProperty("readOnly", out var b) && b.GetBoolean();
                var isReadOnly = after.TryGetProperty("readOnly", out var a) && a.GetBoolean();

                if (!wasReadOnly && isReadOnly)
                {
                    breaking.Add($"{name}.{property}: became readOnly");
                }
                else if (wasReadOnly && !isReadOnly)
                {
                    additive.Add($"{name}.{property}: is no longer readOnly");
                }

                CompareEnums(before, after, $"{name}.{property}", breaking, additive);
            }
        }
    }

    private static void CompareEnums(JsonElement before, JsonElement after, string label, List<string> breaking, List<string> additive)
    {
        var beforeValues = EnumValues(before);
        var afterValues = EnumValues(after);

        if (beforeValues.Count == 0 && afterValues.Count == 0)
        {
            return;
        }

        foreach (var value in afterValues.Except(beforeValues, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // Additive because wire enums are open (ADR-0005): a new value cannot break parsing.
            additive.Add($"{label}: enum value added: {value}");
        }

        foreach (var value in beforeValues.Except(afterValues, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            breaking.Add($"{label}: enum value removed: {value}");
        }
    }

    private static void CompareScopes(JsonElement oldRoot, JsonElement newRoot, List<string> breaking, List<string> additive)
    {
        var before = AllScopes(oldRoot);
        var after = AllScopes(newRoot);

        foreach (var scope in after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            additive.Add($"scope declared: {scope}");
        }

        foreach (var scope in before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            breaking.Add($"scope withdrawn: {scope}");
        }
    }

    private static void Report(string title, List<string> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{title} ({entries.Count}):");

        foreach (var entry in entries)
        {
            Console.WriteLine($"  {entry}");
        }

        Console.WriteLine();
    }

    private static void CompareString(JsonElement before, JsonElement after, string property, string label, List<string> sink)
    {
        var b = before.TryGetProperty(property, out var bv) ? bv.GetString() : null;
        var a = after.TryGetProperty(property, out var av) ? av.GetString() : null;

        if (!string.Equals(b, a, StringComparison.Ordinal))
        {
            sink.Add($"{label}: {b ?? "(none)"} -> {a ?? "(none)"}");
        }
    }

    private static void CompareRef(JsonElement before, JsonElement after, string property, string label, List<string> sink, bool direct = false)
    {
        var b = ReadRef(before, property, direct);
        var a = ReadRef(after, property, direct);

        if (!string.Equals(b, a, StringComparison.Ordinal))
        {
            sink.Add($"{label}: {b ?? "(none)"} -> {a ?? "(none)"}");
        }

        static string? ReadRef(JsonElement element, string property, bool direct)
        {
            if (direct)
            {
                return element.TryGetProperty(property, out var value) ? value.GetString() : null;
            }

            return element.TryGetProperty(property, out var wrapper) && wrapper.TryGetProperty("$ref", out var reference)
                ? reference.GetString()
                : null;
        }
    }

    private static Dictionary<string, JsonElement> Operations(JsonElement root)
        => DiscoveryParser.EnumerateOperations(root.GetProperty("resources"), [])
            .ToDictionary(op => op.Id, op => op.Method, StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> Parameters(JsonElement method)
        => method.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> Properties(JsonElement schema)
        => schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static HashSet<string> Scopes(JsonElement method)
        => method.TryGetProperty("scopes", out var scopes)
            ? scopes.EnumerateArray().Select(s => s.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static HashSet<string> EnumValues(JsonElement element)
    {
        if (element.TryGetProperty("enum", out var values))
        {
            return values.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal);
        }

        if (element.TryGetProperty("items", out var items) && items.TryGetProperty("enum", out var itemValues))
        {
            return itemValues.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static HashSet<string> AllScopes(JsonElement root)
        => root.TryGetProperty("auth", out var auth) &&
           auth.TryGetProperty("oauth2", out var oauth2) &&
           oauth2.TryGetProperty("scopes", out var scopes)
            ? scopes.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
}
