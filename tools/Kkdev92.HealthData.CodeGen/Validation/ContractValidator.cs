using System.Text.Json;
using Kkdev92.HealthData.CodeGen.Discovery;
using Kkdev92.HealthData.CodeGen.IntermediateModel;
using Kkdev92.HealthData.CodeGen.Normalization;
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
        ValidateOpenEnums(contract, errors);
        ValidateResourceNames(contract, errors);
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
    /// <summary>
    /// Enforces what the nested-enum emission cannot express: names that would collide inside the
    /// generated container.
    /// </summary>
    /// <remarks>
    /// The enum struct is named after its declaring property and nested under
    /// <c>{Owner}.Types</c>, so three collisions become possible that the flat naming never had:
    /// a wire value whose normalized name equals the struct's own name (CS0542), a property whose
    /// name is one the struct already spends on its machinery (<c>Value</c>, <c>FromValue</c>,
    /// <c>ToString</c>), and a property literally called <c>types</c>, which would collide with
    /// the container itself. Discovery revision 20260805 has none of these; a later revision that
    /// introduces one must stop generation with the culprit named, not emit code that cannot
    /// compile.
    /// </remarks>
    internal static void ValidateOpenEnums(ApiContract contract, List<string> errors)
    {
        var emittedSchemas = contract.Schemas.Select(s => s.WireName).ToHashSet(StringComparer.Ordinal);

        foreach (var openEnum in contract.OpenEnums)
        {
            var leaf = NamingNormalizer.ToPascalCase(openEnum.DeclaringProperty);

            if (!emittedSchemas.Contains(openEnum.DeclaringSchema))
            {
                errors.Add(
                    $"Open enum '{openEnum.CSharpName}' is declared by '{openEnum.DeclaringSchema}', " +
                    "which is not among the emitted schemas, so its container has no owner.");
            }

            if (leaf is "Types" or "Value" or "FromValue" or "ToString")
            {
                errors.Add(
                    $"'{openEnum.DeclaringSchema}.{openEnum.DeclaringProperty}' normalizes to '{leaf}', " +
                    "which the nested enum container already uses for its own machinery.");
            }

            foreach (var value in openEnum.Values)
            {
                if (string.Equals(value.CSharpName, leaf, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"'{openEnum.DeclaringSchema}.{openEnum.DeclaringProperty}' value '{value.WireValue}' " +
                        $"normalizes to '{value.CSharpName}', which collides with its own enum type name (CS0542).");
                }
            }
        }
    }

    /// <summary>
    /// Checks the resource name types against what generation assumes about them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two of these can only be broken by a future revision, and both would produce code that
    /// compiles and is wrong rather than code that fails: a name parameter with no type would fall
    /// back to <c>string</c> and quietly lose its checking, and two patterns resolving to one type
    /// name would give a caller a type that accepts names of two different resources — the exact
    /// hole these types exist to close.
    /// </para>
    /// <para>
    /// The third is about the members: a name whose collection is called <c>parse</c> or
    /// <c>pattern</c> would emit a builder that collides with the type's own API (CS0102).
    /// </para>
    /// </remarks>
    internal static void ValidateResourceNames(ApiContract contract, List<string> errors)
    {
        var byPattern = contract.ResourceNames
            .GroupBy(name => name.Pattern, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var parameter in contract.Operations.SelectMany(operation => operation.Parameters))
        {
            if (parameter.Pattern is { } pattern && !byPattern.ContainsKey(pattern))
            {
                errors.Add(
                    $"Parameter '{parameter.WireName}' states the pattern '{pattern}', which resolved to no "
                    + "resource name type. It would be emitted as an unchecked string.");
            }
        }

        foreach (var duplicate in contract.ResourceNames
            .GroupBy(name => name.CSharpName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            errors.Add(
                $"Resource name type '{duplicate.Key}' would be generated for {duplicate.Count()} different "
                + $"patterns ({string.Join(", ", duplicate.Select(name => name.Pattern))}). One type accepting "
                + "two shapes of name would defeat the point of having it.");
        }

        var reserved = new[] { "Parse", "TryParse", "Pattern", "ToString", "From", "Me", "Equals", "GetHashCode" };

        foreach (var name in contract.ResourceNames)
        {
            foreach (var child in contract.ResourceNames.Where(c => c.ParentCSharpName == name.CSharpName))
            {
                if (reserved.Contains(child.MemberName, StringComparer.Ordinal))
                {
                    errors.Add(
                        $"'{name.CSharpName}' would declare a member '{child.MemberName}' for its child "
                        + $"'{child.CSharpName}', which collides with a member every name type has (CS0102).");
                }
            }

            if (name.IdParameterNames.Count != name.Segments.Count(segment => segment.IsVariable))
            {
                errors.Add(
                    $"'{name.CSharpName}' names {name.IdParameterNames.Count} ids but its pattern has "
                    + $"{name.Segments.Count(segment => segment.IsVariable)} variable segments.");
            }
        }
    }

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
