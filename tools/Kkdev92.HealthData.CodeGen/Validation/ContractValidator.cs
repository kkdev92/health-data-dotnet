using System.Text.Json;
using Kkdev92.HealthData.CodeGen.Discovery;
using Kkdev92.HealthData.CodeGen.IntermediateModel;
using Kkdev92.HealthData.CodeGen.Specifications;

namespace Kkdev92.HealthData.CodeGen.Validation;

internal sealed record ValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Enforces the specification rules and the generation invariants that keep output deterministic
/// and honest.
/// </summary>
internal static class ContractValidator
{
    public static ValidationResult Validate(SpecSet spec, ApiContract contract)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        ValidatePublicSurface(spec, errors, warnings);
        ValidateSemanticsTargets(spec, errors);
        ValidatePagination(spec, contract, errors, warnings);
        ValidateIdentifiers(contract, errors);
        ValidateSerializableShapes(contract, errors);

        return new ValidationResult(errors, warnings);
    }

    private static void ValidatePublicSurface(SpecSet spec, List<string> errors, List<string> warnings)
    {
        var discovered = DiscoveryParser
            .EnumerateOperations(spec.Discovery.RootElement.GetProperty("resources"), [])
            .Select(op => op.Id)
            .ToHashSet(StringComparer.Ordinal);

        var surface = spec.PublicSurface.RootElement;

        var allowed = surface.GetProperty("operations")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

        var excluded = surface.GetProperty("excluded")
            .EnumerateArray().Select(e => e.GetProperty("operation").GetString()!).ToHashSet(StringComparer.Ordinal);

        // An allowlisted operation that Discovery does not expose is a hard failure: we would
        // generate a method that cannot possibly work.
        foreach (var missing in allowed.Except(discovered, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            errors.Add($"public-surface.json allows '{missing}', which is absent from discovery.json.");
        }

        foreach (var missing in excluded.Except(discovered, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            errors.Add($"public-surface.json excludes '{missing}', which is absent from discovery.json.");
        }

        foreach (var duplicate in allowed.Intersect(excluded, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            errors.Add($"public-surface.json both allows and excludes '{duplicate}'.");
        }

        // A new Google operation is a warning, not a failure: generation still succeeds, but the
        // operation stays invisible until a human reviews it.
        foreach (var unclassified in discovered
                     .Except(allowed, StringComparer.Ordinal)
                     .Except(excluded, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            warnings.Add(
                $"discovery.json exposes '{unclassified}', which is neither allowed nor excluded. " +
                "Add it to public-surface.json (with review) or record why it is excluded.");
        }

        foreach (var exclusion in surface.GetProperty("excluded").EnumerateArray())
        {
            var operation = exclusion.GetProperty("operation").GetString();
            var reason = exclusion.TryGetProperty("reason", out var r) ? r.GetString() : null;

            if (string.IsNullOrWhiteSpace(reason))
            {
                errors.Add($"public-surface.json excludes '{operation}' without a reason.");
            }
        }
    }

    private static void ValidateSemanticsTargets(SpecSet spec, List<string> errors)
    {
        if (!spec.Semantics.RootElement.TryGetProperty("operations", out var operations))
        {
            return;
        }

        var discovered = DiscoveryParser
            .EnumerateOperations(spec.Discovery.RootElement.GetProperty("resources"), [])
            .Select(op => op.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in operations.EnumerateObject())
        {
            if (!discovered.Contains(entry.Name))
            {
                errors.Add($"semantics.json describes '{entry.Name}', which is absent from discovery.json.");
            }
        }
    }

    /// <summary>
    /// Cross-checks declared pagination against the actual response schema.
    /// </summary>
    /// <remarks>
    /// This is what catches the <c>dataPoints.dailyRollUp</c> asymmetry automatically: its request
    /// declares <c>pageToken</c> but its response has no <c>nextPageToken</c>, so it must be
    /// declared <c>requestOnly</c> and must not get an enumeration helper.
    /// </remarks>
    private static void ValidatePagination(SpecSet spec, ApiContract contract, List<string> errors, List<string> warnings)
    {
        var schemas = spec.Discovery.RootElement.GetProperty("schemas");

        foreach (var operation in contract.Operations)
        {
            var responseHasToken = operation.ResponseSchema is { } response &&
                                   schemas.TryGetProperty(response, out var schema) &&
                                   schema.TryGetProperty("properties", out var properties) &&
                                   properties.TryGetProperty("nextPageToken", out _);

            var requestHasToken = RequestDeclaresPageToken(schemas, operation) ||
                                  operation.Parameters.Any(p => p.WireName == "pageToken");

            switch (operation.Pagination?.Kind)
            {
                case PaginationKind.Query or PaginationKind.Body:
                    if (!responseHasToken)
                    {
                        errors.Add(
                            $"semantics.json declares paginated results for '{operation.Id}', but its response " +
                            $"schema '{operation.ResponseSchema}' has no nextPageToken. Use kind 'requestOnly'.");
                    }

                    break;

                case PaginationKind.RequestOnly:
                    if (responseHasToken)
                    {
                        errors.Add(
                            $"'{operation.Id}' is declared 'requestOnly', but its response schema " +
                            $"'{operation.ResponseSchema}' now has a nextPageToken. Promote it to a real " +
                            "pagination kind and remove the knownDocumentationConflict note.");
                    }

                    break;

                case null:
                    if (responseHasToken)
                    {
                        errors.Add(
                            $"'{operation.Id}' returns a nextPageToken but declares no pagination in " +
                            "semantics.json. Paginated operations must be described explicitly.");
                    }
                    else if (requestHasToken)
                    {
                        warnings.Add(
                            $"'{operation.Id}' accepts a pageToken but declares no pagination in semantics.json.");
                    }

                    break;

                default:
                    break;
            }
        }
    }

    private static bool RequestDeclaresPageToken(JsonElement schemas, OperationContract operation)
        => operation.RequestSchema is { } request &&
           schemas.TryGetProperty(request, out var schema) &&
           schema.TryGetProperty("properties", out var properties) &&
           properties.TryGetProperty("pageToken", out _);

    /// <summary>
    /// Rejects shapes the emitter cannot express correctly.
    /// </summary>
    /// <remarks>
    /// A <c>[JsonConverter]</c> attribute on a collection property applies to the collection, not
    /// its elements, so an array whose elements need a converter would serialize silently wrongly.
    /// Discovery revision 20260805 has no such case: the only formatted array element is
    /// <c>Electrocardiogram.waveformSamples</c> with <c>int32</c>, which needs no converter. This
    /// check exists so that stays true rather than being assumed.
    /// </remarks>
    private static void ValidateSerializableShapes(ApiContract contract, List<string> errors)
    {
        foreach (var schema in contract.Schemas)
        {
            foreach (var property in schema.Properties)
            {
                if (property.Type.Kind != IntermediateModel.TypeKind.Array)
                {
                    continue;
                }

                var element = property.Type.ElementType;

                if (element?.ConverterTypeName is { } converter)
                {
                    errors.Add(
                        $"'{schema.WireName}.{property.WireName}' is an array whose elements require " +
                        $"the converter '{converter}'. A property-level JsonConverter applies to the " +
                        "collection, not its elements, so the emitter needs a dedicated collection converter.");
                }
            }
        }
    }

    /// <summary>Catches generated names that would not compile or would silently collide.</summary>
    private static void ValidateIdentifiers(ApiContract contract, List<string> errors)
    {
        var typeNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var schema in contract.Schemas)
        {
            if (typeNames.TryGetValue(schema.CSharpName, out var existing))
            {
                errors.Add(
                    $"Schemas '{existing}' and '{schema.WireName}' both normalize to C# type '{schema.CSharpName}'.");
            }
            else
            {
                typeNames[schema.CSharpName] = schema.WireName;
            }

            var memberNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in schema.Properties)
            {
                // CS0542: a member may not share its name with the enclosing type.
                if (string.Equals(property.CSharpName, schema.CSharpName, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"'{schema.WireName}.{property.WireName}' normalizes to '{property.CSharpName}', " +
                        "which collides with its declaring type.");
                }

                if (memberNames.TryGetValue(property.CSharpName, out var existingMember))
                {
                    errors.Add(
                        $"'{schema.WireName}' properties '{existingMember}' and '{property.WireName}' both " +
                        $"normalize to member '{property.CSharpName}'.");
                }
                else
                {
                    memberNames[property.CSharpName] = property.WireName;
                }
            }
        }
    }
}
