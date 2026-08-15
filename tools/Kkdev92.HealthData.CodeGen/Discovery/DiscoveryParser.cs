using System.Text.Json;
using Kkdev92.HealthData.CodeGen.IntermediateModel;
using Kkdev92.HealthData.CodeGen.Normalization;
using Kkdev92.HealthData.CodeGen.Specifications;

namespace Kkdev92.HealthData.CodeGen.Discovery;

/// <summary>
/// Converts a Discovery snapshot plus its companion specification files into the normalized IR.
/// </summary>
/// <remarks>
/// Ordering is fixed with <see cref="StringComparer.Ordinal"/> at every step. Dictionary
/// enumeration order must never influence the output.
/// </remarks>
internal static class DiscoveryParser
{
    public static ApiContract Parse(SpecSet spec)
    {
        var root = spec.Discovery.RootElement;
        var allowed = ReadAllowedOperations(spec);

        var operations = EnumerateOperations(root.GetProperty("resources"), [])
            .Where(op => allowed.Contains(op.Id))
            .Select(op => BuildOperation(op, spec))
            .OrderBy(op => op.Id, StringComparer.Ordinal)
            .ToArray();

        var schemas = root.GetProperty("schemas");
        var reachable = ComputeReachableSchemas(schemas, operations, spec);

        var schemaContracts = reachable
            .Select(name => BuildSchema(name, schemas.GetProperty(name)))
            .OrderBy(s => s.WireName, StringComparer.Ordinal)
            .ToArray();

        var openEnums = CollectOpenEnums(schemaContracts);

        return new ApiContract
        {
            Name = root.GetProperty("name").GetString()!,
            Title = root.GetProperty("title").GetString()!,
            ApiVersion = root.GetProperty("version").GetString()!,
            Revision = root.GetProperty("revision").GetString()!,
            RootUrl = new Uri(root.GetProperty("rootUrl").GetString()!),
            SpecSha256 = spec.DiscoverySha256,
            Scopes = BuildScopes(root, spec),
            Operations = operations,
            Schemas = schemaContracts,
            ErrorReasons = BuildErrorReasons(spec),
            OpenEnums = openEnums,

            // Derived from the operations rather than declared: every name parameter
            // already carries the pattern that defines the shape.
            ResourceNames = ResourceNameResolver.Resolve(operations),

            // Read rather than derived: none of this is in Discovery, which is the whole reason
            // the snapshot exists.
            DataTypes = BuildDataTypes(spec),
        };
    }

    /// <summary>
    /// Collects every inline enum into a generated open enum type.
    /// </summary>
    /// <remarks>
    /// Discovery has no named enum types, so the C# name is derived from the declaring schema and
    /// the <em>wire</em> property name. The wire name is used deliberately: deriving from the
    /// collision-adjusted member name would make the type name depend on an unrelated rule.
    /// The name is dotted — <c>Settings.Types.DistanceUnit</c> — because the enum is emitted
    /// nested inside its owner, protobuf style, where it can be found from the property that uses
    /// it. A flat sibling named after the property would collide with the property itself
    /// (CS0102), which is the same reason protobuf's own C# codegen nests under <c>Types</c>.
    /// </remarks>
    private static IReadOnlyList<OpenEnumContract> CollectOpenEnums(IReadOnlyList<SchemaContract> schemas)
    {
        var enums = new List<OpenEnumContract>();

        foreach (var schema in schemas)
        {
            foreach (var property in schema.Properties)
            {
                var type = property.Type.Kind == TypeKind.Array ? property.Type.ElementType! : property.Type;

                if (type.Kind != TypeKind.Enum || type.EnumTypeName is null)
                {
                    continue;
                }

                enums.Add(new OpenEnumContract
                {
                    CSharpName = type.EnumTypeName,
                    DeclaringSchema = schema.WireName,
                    DeclaringProperty = property.WireName,
                    Values = type.EnumValues
                        .Select(v => new OpenEnumValueContract(v, NamingNormalizer.EnumValueName(v), null))
                        .ToArray(),
                });
            }
        }

        return enums.OrderBy(e => e.CSharpName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every operation id present in the Discovery document, in document order.</summary>
    public static IEnumerable<(string Id, string ResourcePath, JsonElement Method)> EnumerateOperations(
        JsonElement resources,
        IReadOnlyList<string> path)
    {
        foreach (var resource in resources.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var resourcePath = path.Append(resource.Name).ToArray();

            if (resource.Value.TryGetProperty("methods", out var methods))
            {
                foreach (var method in methods.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    yield return (method.Value.GetProperty("id").GetString()!, string.Join('.', resourcePath), method.Value);
                }
            }

            if (resource.Value.TryGetProperty("resources", out var nested))
            {
                foreach (var item in EnumerateOperations(nested, resourcePath))
                {
                    yield return item;
                }
            }
        }
    }

    private static HashSet<string> ReadAllowedOperations(SpecSet spec)
        => spec.PublicSurface.RootElement.GetProperty("operations")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The scope constants to generate: Discovery's list plus anything semantics.json adds.
    /// </summary>
    /// <remarks>
    /// Discovery's <c>auth.oauth2.scopes</c> is not the complete set. It omits
    /// <c>nutrition.readonly</c>, which the Scopes guide and the per-method reference of every
    /// data-point read operation both list. The snapshot is a verbatim copy checked against a
    /// recorded hash, so an addition cannot be made there; semantics.json declares it instead,
    /// with its provenance.
    /// </remarks>
    private static IReadOnlyList<ScopeContract> BuildScopes(JsonElement root, SpecSet spec)
    {
        var declared = new List<ScopeContract>();
        var kinds = ReadScopeKinds(spec);

        if (root.TryGetProperty("auth", out var auth) &&
            auth.TryGetProperty("oauth2", out var oauth2) &&
            oauth2.TryGetProperty("scopes", out var scopes))
        {
            declared.AddRange(scopes.EnumerateObject()
                .Select(scope => new ScopeContract(
                    scope.Name,
                    NamingNormalizer.ScopeConstantName(scope.Name),
                    scope.Value.TryGetProperty("description", out var d) ? d.GetString() : null,
                    KindOf(scope.Name, kinds))));
        }

        if (spec.Semantics.RootElement.TryGetProperty("scopeCrossCheck", out var crossCheck) &&
            crossCheck.TryGetProperty("additionalScopes", out var additional))
        {
            foreach (var scope in additional.EnumerateArray())
            {
                var url = scope.GetProperty("url").GetString()!;

                if (declared.Any(s => string.Equals(s.Url, url, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"semantics.json adds scope '{url}', but Discovery already declares it. "
                        + "Remove the addition: a Discovery revision has caught up.");
                }

                declared.Add(new ScopeContract(
                    url,
                    NamingNormalizer.ScopeConstantName(url),
                    scope.TryGetProperty("description", out var d) ? d.GetString() : null,
                    KindOf(url, kinds)));
            }
        }

        return [.. declared.OrderBy(s => s.Url, StringComparer.Ordinal)];
    }

    /// <summary>Reads the declared scope classification, keyed by scope url.</summary>
    private static IReadOnlyDictionary<string, ScopeKind> ReadScopeKinds(SpecSet spec)
    {
        var kinds = new Dictionary<string, ScopeKind>(StringComparer.Ordinal);

        if (!spec.Semantics.RootElement.TryGetProperty("authentication", out var authentication) ||
            !authentication.TryGetProperty("scopeKinds", out var declared))
        {
            return kinds;
        }

        foreach (var (property, kind) in
            new[] { ("read", ScopeKind.Read), ("write", ScopeKind.Write), ("project", ScopeKind.Project) })
        {
            if (!declared.TryGetProperty(property, out var list))
            {
                continue;
            }

            foreach (var scope in list.EnumerateArray())
            {
                var url = scope.GetString()!;

                if (kinds.TryGetValue(url, out var already))
                {
                    throw new InvalidOperationException(
                        $"semantics.json lists scope '{url}' as both {already} and {kind}. A scope grants "
                        + "one of the three; two lists would make the generated sets overlap.");
                }

                kinds[url] = kind;
            }
        }

        return kinds;
    }

    /// <summary>
    /// The declared kind of a scope.
    /// </summary>
    /// <remarks>
    /// Missing is a failure rather than a default. A scope Google adds would otherwise belong to no
    /// kind, and an application asking for "everything that reads" would silently stop asking for
    /// it — which looks like a permissions bug in the application long before anybody suspects the
    /// package.
    /// </remarks>
    private static ScopeKind KindOf(string url, IReadOnlyDictionary<string, ScopeKind> kinds)
        => kinds.TryGetValue(url, out var kind)
            ? kind
            : throw new InvalidOperationException(
                $"Scope '{url}' is not classified in semantics.json under authentication.scopeKinds. "
                + "Add it to read, write or project - Discovery states which in the scope's own "
                + "description - so that the generated scope sets stay complete.");

    /// <summary>
    /// Reads the data type table, which Discovery does not carry.
    /// </summary>
    /// <remarks>
    /// The generator loaded this file and used none of it. That is how an application ended up
    /// asking <c>steps</c> for a <c>get</c>: the information was in the repository and not in the
    /// package.
    /// </remarks>
    private static IReadOnlyList<DataTypeContract> BuildDataTypes(SpecSet spec)
    {
        if (!spec.DataTypes.RootElement.TryGetProperty("dataTypes", out var dataTypes))
        {
            return [];
        }

        return
        [
            .. dataTypes.EnumerateArray()
                .Select(dataType => new DataTypeContract
                {
                    Id = dataType.GetProperty("id").GetString()!,
                    FilterName = dataType.GetProperty("filterName").GetString()!,
                    Operations =
                    [
                        .. dataType.GetProperty("operations")
                            .EnumerateArray()
                            .Select(operation => operation.GetString()!)
                    ],
                })
                .OrderBy(dataType => dataType.Id, StringComparer.Ordinal)
        ];
    }

    private static IReadOnlyList<ErrorReasonContract> BuildErrorReasons(SpecSet spec)
        => spec.Errors.RootElement.GetProperty("reasons")
            .EnumerateArray()
            .Select(e => new ErrorReasonContract(
                e.GetProperty("reason").GetString()!,
                e.GetProperty("httpStatus").GetInt32()))
                    .OrderBy(e => e.Reason, StringComparer.Ordinal)
                    .ToArray();

    private static OperationContract BuildOperation((string Id, string ResourcePath, JsonElement Method) source, SpecSet spec)
    {
        var (id, resourcePath, method) = source;
        var overrides = FindOperationOverrides(spec, id);

        var parameters = new List<ParameterContract>();

        if (method.TryGetProperty("parameters", out var parameterElement))
        {
            foreach (var parameter in parameterElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var required = parameter.Value.TryGetProperty("required", out var r) && r.GetBoolean();
                var location = parameter.Value.GetProperty("location").GetString() switch
                {
                    "path" => ParameterLocation.Path,
                    "query" => ParameterLocation.Query,
                    var other => throw new InvalidOperationException(
                        $"Operation '{id}' parameter '{parameter.Name}' has unsupported location '{other}'."),
                };

                parameters.Add(new ParameterContract
                {
                    WireName = parameter.Name,
                    CSharpName = NamingNormalizer.ToPascalCase(parameter.Name),
                    Location = location,
                    IsRequired = required,
                    Type = TypeMapper.Map(parameter.Value, nullable: !required),
                    Pattern = parameter.Value.TryGetProperty("pattern", out var p) ? p.GetString() : null,
                    Description = parameter.Value.TryGetProperty("description", out var d) ? d.GetString() : null,
                });
            }
        }

        var httpMethod = method.GetProperty("httpMethod").GetString()!;
        var responseSchema = method.TryGetProperty("response", out var response)
            ? response.GetProperty("$ref").GetString()
            : null;

        return new OperationContract
        {
            Id = id,
            ResourcePath = resourcePath,
            CSharpName = NamingNormalizer.OperationMethodName(id),
            HttpMethod = httpMethod,
            PathTemplate = method.GetProperty("path").GetString()!,
            Parameters = parameters,
            RequestSchema = method.TryGetProperty("request", out var request) ? request.GetProperty("$ref").GetString() : null,
            ResponseSchema = responseSchema,
            Scopes = ResolveScopes(method, overrides),
            ScopeCombination = ResolveScopeCombination(overrides),
            ResponseKind = ResolveResponseKind(responseSchema, overrides),
            RetryClassification = ResolveRetry(httpMethod, overrides),
            Pagination = ResolvePagination(overrides),
            SupportsMediaDownload = method.TryGetProperty("supportsMediaDownload", out var media) && media.GetBoolean(),
            Description = method.TryGetProperty("description", out var description) ? description.GetString() : null,
        };
    }

    private static JsonElement? FindOperationOverrides(SpecSet spec, string operationId)
    {
        if (spec.Semantics.RootElement.TryGetProperty("operations", out var operations) &&
            operations.TryGetProperty(operationId, out var element))
        {
            return element;
        }

        return null;
    }

    private static ResponseKind ResolveResponseKind(string? responseSchema, JsonElement? overrides)
    {
        if (overrides?.TryGetProperty("responseKind", out var kind) == true)
        {
            return kind.GetString() switch
            {
                "mediaOrJson" => ResponseKind.MediaOrJson,
                "operation" => ResponseKind.Operation,
                "resource" or "json" => ResponseKind.Json,
                var other => throw new InvalidOperationException($"Unknown responseKind '{other}' in semantics.json."),
            };
        }

        return responseSchema switch
        {
            null or "Empty" => ResponseKind.Empty,
            "Operation" => ResponseKind.Operation,
            _ => ResponseKind.Json,
        };
    }

    /// <summary>
    /// The scopes an operation accepts, from Discovery unless semantics.json overrides them.
    /// </summary>
    /// <remarks>
    /// An override replaces the list rather than adding to it, so the accepted set is always
    /// readable in one place. Discovery is not always complete: Google's per-method pages list
    /// scopes for some operations that the Discovery entry omits, and that page is the higher
    /// authority under the source precedence in docs/architecture.md.
    /// </remarks>
    private static IReadOnlyList<string> ResolveScopes(JsonElement method, JsonElement? overrides)
    {
        var fromDiscovery = method.TryGetProperty("scopes", out var scopes)
            ? scopes.EnumerateArray().Select(s => s.GetString()!).ToArray()
            : [];

        if (overrides?.TryGetProperty("scopeRequirement", out var requirement) != true ||
            !requirement.TryGetProperty("scopes", out var declared))
        {
            return [.. fromDiscovery.OrderBy(s => s, StringComparer.Ordinal)];
        }

        var recorded = declared.EnumerateArray().Select(s => s.GetString()!).ToArray();

        // The override must contain everything Discovery does. It exists to add scopes the
        // per-method page lists and Discovery omits, so a scope only Discovery has means the
        // union was taken against an older revision and this entry has gone stale. Left
        // unchecked, refreshing the snapshot would quietly shorten an accepted-scope list —
        // the one direction that stops a token provider selecting a token that would work.
        var missing = fromDiscovery
            .Except(recorded, StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"semantics.json records scopes for '{method.GetProperty("id").GetString()}' that omit "
                + $"{string.Join(", ", missing)}, which Discovery declares for it. Re-take the union of "
                + "the Discovery entry and the per-method reference page, then update verifiedOn.");
        }

        return [.. recorded.OrderBy(s => s, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether one of the scopes suffices or all of them are needed.
    /// </summary>
    /// <remarks>
    /// Discovery has no field for this: its scopes array is flat, and the convention is that any
    /// one entry is enough. Only a per-method page can establish otherwise, so only semantics.json
    /// can say so here.
    /// </remarks>
    private static ScopeCombination ResolveScopeCombination(JsonElement? overrides)
    {
        if (overrides?.TryGetProperty("scopeRequirement", out var requirement) == true &&
            requirement.TryGetProperty("combination", out var combination))
        {
            return combination.GetString() switch
            {
                "anyOf" => ScopeCombination.AnyOf,
                "allOf" => ScopeCombination.AllOf,
                var other => throw new InvalidOperationException(
                    $"Unknown scopeRequirement combination '{other}' in semantics.json."),
            };
        }

        return ScopeCombination.AnyOf;
    }

    private static RetryClassification ResolveRetry(string httpMethod, JsonElement? overrides)
    {
        if (overrides?.TryGetProperty("retryClassification", out var classification) == true)
        {
            return classification.GetString() switch
            {
                "never" => RetryClassification.Never,
                "safe" => RetryClassification.Safe,
                "idempotent" => RetryClassification.Idempotent,
                "semanticallySafe" => RetryClassification.SemanticallySafe,
                var other => throw new InvalidOperationException($"Unknown retryClassification '{other}' in semantics.json."),
            };
        }

        // Defaults mirror semantics.json "defaults". Writes are never retried automatically.
        return httpMethod switch
        {
            "GET" => RetryClassification.Safe,
            "DELETE" => RetryClassification.Idempotent,
            _ => RetryClassification.Never,
        };
    }

    private static PaginationContract? ResolvePagination(JsonElement? overrides)
    {
        if (overrides?.TryGetProperty("pagination", out var pagination) != true)
        {
            return null;
        }

        var kind = pagination.GetProperty("kind").GetString() switch
        {
            "query" => PaginationKind.Query,
            "body" => PaginationKind.Body,
            "requestOnly" => PaginationKind.RequestOnly,
            var other => throw new InvalidOperationException($"Unknown pagination kind '{other}' in semantics.json."),
        };

        return new PaginationContract
        {
            Kind = kind,
            PageSize = GetOptionalString(pagination, "pageSize"),
            PageToken = GetOptionalString(pagination, "pageToken"),
            NextPageToken = GetOptionalString(pagination, "nextPageToken"),
            Items = GetOptionalString(pagination, "items"),
        };
    }

    private static string? GetOptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static SchemaContract BuildSchema(string name, JsonElement schema)
    {
        var typeName = NamingNormalizer.ToPascalCase(name);
        var properties = new List<PropertyContract>();

        if (schema.TryGetProperty("properties", out var propertyElement))
        {
            foreach (var property in propertyElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                properties.Add(new PropertyContract
                {
                    WireName = property.Name,
                    CSharpName = NamingNormalizer.ToMemberName(property.Name, typeName),
                    // Every response field is optional in practice: Google omits absent fields
                    // rather than sending null, and additive changes must not break consumers.
                    // Nested protobuf style: the enum for Settings.distanceUnit is
                    // Settings.Types.DistanceUnit. The dotted name is what property types render
                    // as, so it resolves identically inside the owner and everywhere else.
                    Type = TypeMapper.Map(
                        property.Value,
                        nullable: true,
                        enumTypeName: $"{typeName}.Types.{NamingNormalizer.ToPascalCase(property.Name)}"),
                    IsReadOnly = property.Value.TryGetProperty("readOnly", out var ro) && ro.GetBoolean(),
                    Description = property.Value.TryGetProperty("description", out var d) ? d.GetString() : null,
                });
            }
        }

        return new SchemaContract
        {
            WireName = name,
            CSharpName = typeName,
            Properties = properties,
            Description = schema.TryGetProperty("description", out var desc) ? desc.GetString() : null,
        };
    }

    /// <summary>
    /// Computes the transitive closure of schemas reachable from the allowlisted operations.
    /// </summary>
    /// <remarks>
    /// Without this, generation emits dead types. Discovery revision 20260805 declares 147
    /// schemas, of which 138 are reachable once the SMART Health Links operations are excluded.
    /// </remarks>
    private static IReadOnlyList<string> ComputeReachableSchemas(
        JsonElement schemas,
        IReadOnlyList<OperationContract> operations,
        SpecSet spec)
    {
        var seeds = new List<string>();

        foreach (var operation in operations)
        {
            if (operation.RequestSchema is { } request)
            {
                seeds.Add(request);
            }

            if (operation.ResponseSchema is { } response)
            {
                seeds.Add(response);
            }
        }

        // Schemas that are unreachable from any operation but deliberately retained.
        if (spec.PublicSurface.RootElement.TryGetProperty("additionalSchemas", out var additional))
        {
            seeds.AddRange(additional.EnumerateArray().Select(e => e.GetString()!));
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(seeds);

        while (pending.Count > 0)
        {
            var name = pending.Pop();

            if (!visited.Add(name))
            {
                continue;
            }

            if (!schemas.TryGetProperty(name, out var schema))
            {
                throw new InvalidOperationException($"Schema '{name}' is referenced but not defined in discovery.json.");
            }

            foreach (var reference in EnumerateSchemaReferences(schema))
            {
                pending.Push(reference);
            }
        }

        return visited.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> EnumerateSchemaReferences(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties))
        {
            yield break;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.TryGetProperty("$ref", out var direct))
            {
                yield return direct.GetString()!;
            }
            else if (property.Value.TryGetProperty("items", out var items) &&
                     items.TryGetProperty("$ref", out var itemRef))
            {
                yield return itemRef.GetString()!;
            }
        }
    }
}
