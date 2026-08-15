using Kkdev92.HealthData.CodeGen.IntermediateModel;
using Kkdev92.HealthData.CodeGen.Normalization;

namespace Kkdev92.HealthData.CodeGen.CSharp;

/// <summary>
/// A node in the C# resource tree, for example <c>client.Users.DataPoints</c>.
/// </summary>
internal sealed class ResourceNode(string path, string csharpName)
{
    public string Path { get; } = path;

    public string CSharpName { get; } = csharpName;

    public string TypeName { get; } = csharpName + "Resource";

    public SortedDictionary<string, ResourceNode> Children { get; } = new(StringComparer.Ordinal);

    public List<OperationContract> Operations { get; } = [];
}

/// <summary>
/// Emits request types and resource clients.
/// </summary>
/// <remarks>
/// Resource clients are sealed concrete classes, not interfaces. A generated interface would
/// break every implementer each time Google adds a method, and a fake
/// <see cref="System.Net.Http.HttpMessageHandler"/> already makes the wire contract testable.
/// </remarks>
internal sealed class ResourceEmitter(ApiContract contract, IReadOnlySet<string> flattenedPaths)
{
    private const string RootNamespace = "Kkdev92.HealthData";

    /// <summary>Request type names keyed by operation id.</summary>
    public Dictionary<string, string> RequestTypeNames { get; } = new(StringComparer.Ordinal);

    public ResourceNode BuildTree()
    {
        var root = new ResourceNode(string.Empty, string.Empty);

        foreach (var operation in contract.Operations)
        {
            var node = root;

            foreach (var segment in EffectiveSegments(operation.ResourcePath))
            {
                if (!node.Children.TryGetValue(segment.Path, out var child))
                {
                    child = new ResourceNode(segment.Path, segment.CSharpName);
                    node.Children[segment.Path] = child;
                }

                node = child;
            }

            node.Operations.Add(operation);
        }

        return root;
    }

    /// <summary>
    /// Splits a Discovery resource path into the segments that survive into the C# surface.
    /// </summary>
    private IEnumerable<(string Path, string CSharpName)> EffectiveSegments(string resourcePath)
    {
        var parts = resourcePath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var accumulated = new List<string>();

        foreach (var part in parts)
        {
            accumulated.Add(part);
            var path = string.Join('.', accumulated);

            if (flattenedPaths.Contains(path))
            {
                continue;
            }

            yield return (path, NamingNormalizer.ToPascalCase(part));
        }
    }

    /// <summary>
    /// Chooses request type names.
    /// </summary>
    /// <remarks>
    /// A bare method name is used when it is unambiguous across the whole surface, which gives
    /// <c>GetProfileRequest</c>. Where several resources share a method name, the resource is
    /// appended, which gives <c>ListDataPointsRequest</c>.
    /// <para>
    /// The disambiguation set only changes when Google adds an operation whose method name
    /// collides with an existing one, and a resulting rename shows up in the generated diff and
    /// in package validation rather than silently. A generator test pins the current names.
    /// </para>
    /// </remarks>
    public void ResolveRequestTypeNames()
    {
        var byMethodName = contract.Operations
            .GroupBy(op => MethodName(op.Id), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var operation in contract.Operations)
        {
            var method = MethodName(operation.Id);

            var name = byMethodName[method] == 1
                ? $"{method}Request"
                : $"{method}{ResourceName(operation.ResourcePath)}Request";

            RequestTypeNames[operation.Id] = name;
        }

        static string MethodName(string operationId)
            => NamingNormalizer.ToPascalCase(operationId[(operationId.LastIndexOf('.') + 1)..]);

        static string ResourceName(string resourcePath)
            => NamingNormalizer.ToPascalCase(resourcePath[(resourcePath.LastIndexOf('.') + 1)..]);
    }

    public IEnumerable<GeneratedFile> EmitRequests(Func<string, string[], CodeWriter> header)
    {
        foreach (var operation in contract.Operations)
        {
            var typeName = RequestTypeNames[operation.Id];

            // Only a request that carries a body names a model type. The using is conditional
            // because an unused one is an IDE0005 build error in this repository, not a nicety.
            var usings = operation.RequestSchema is null
                ? System.Array.Empty<string>()
                : [CSharpEmitter.ModelsNamespace];

            var writer = header(CSharpEmitter.RequestsNamespace, usings);

            writer.XmlDoc("summary", $"Request for {operation.Id}.");

            using (writer.Block($"public sealed class {typeName}"))
            {
                var first = true;

                foreach (var parameter in operation.Parameters.OrderBy(p => p.Location).ThenBy(p => p.WireName, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        writer.Line();
                    }

                    first = false;

                    writer.XmlDoc("summary", parameter.Description ?? parameter.WireName);

                    if (parameter.Pattern is { } pattern)
                    {
                        writer.XmlDoc("remarks", $"The service requires this to match the pattern {pattern}.");
                    }

                    // 'required' is used only for genuinely required path parameters. It is not
                    // sprayed across models; here it turns a
                    // guaranteed runtime failure into a compile error.
                    var modifier = parameter.IsRequired ? "required " : string.Empty;
                    var type = parameter.IsRequired ? StripNullable(parameter.Type.CSharpType) : parameter.Type.CSharpType;

                    writer.Line($"public {modifier}{type} {parameter.CSharpName} {{ get; init; }}");
                }

                if (operation.Pagination?.Kind == PaginationKind.Query)
                {
                    // Requests are init-only, so paging needs an explicit copy rather than a
                    // mutation. The generator knows every property, so the copy is exact.
                    writer.Line();
                    writer.XmlDoc("summary", "Returns a copy of this request positioned at the given page.");
                    using (writer.Block($"internal {typeName} WithPageToken(string? pageToken)"))
                    {
                        using (writer.Block("return new()", closing: "};"))
                        {
                            foreach (var parameter in operation.Parameters)
                            {
                                var value = parameter.WireName == "pageToken" ? "pageToken" : $"{parameter.CSharpName}";
                                writer.Line($"{parameter.CSharpName} = {value},");
                            }

                            if (operation.RequestSchema is not null)
                            {
                                writer.Line("Body = Body,");
                            }
                        }
                    }
                }

                if (operation.RequestSchema is { } bodySchema)
                {
                    if (!first)
                    {
                        writer.Line();
                    }

                    writer.XmlDoc("summary", "The request body.");
                    writer.Line(
                        $"public required {NamingNormalizer.ToPascalCase(bodySchema)} Body {{ get; init; }}");
                }
            }

            yield return new GeneratedFile($"Generated/Requests/{typeName}.g.cs", writer.ToString());
        }
    }

    public IEnumerable<GeneratedFile> EmitResources(ResourceNode root, Func<string, string[], CodeWriter> header)
    {
        foreach (var node in Flatten(root).Where(n => n.Path.Length > 0))
        {
            yield return EmitResource(node, header);
        }

        yield return EmitClientPartial(root, header);
    }

    private static IEnumerable<ResourceNode> Flatten(ResourceNode node)
    {
        yield return node;

        foreach (var child in node.Children.Values)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private GeneratedFile EmitResource(ResourceNode node, Func<string, string[], CodeWriter> header)
    {
        // Computed from what the operations actually mention, because an unused using is an
        // IDE0005 build error. The projects node, for example, exists only to hold its child
        // resource and names neither a request nor a model.
        var usings = new List<string> { "System.Text.Json", "Kkdev92.HealthData.Http", "Kkdev92.HealthData.Pagination" };

        if (node.Operations.Count > 0)
        {
            usings.Add(CSharpEmitter.RequestsNamespace);
        }

        if (node.Operations.Any(op => op.RequestSchema is not null
            || (op.ResponseSchema is not null && op.ResponseKind != ResponseKind.Empty)))
        {
            usings.Add(CSharpEmitter.ModelsNamespace);
        }

        var writer = header(RootNamespace, [.. usings]);

        writer.XmlDoc("summary", $"Operations on the {node.Path} resource.");

        using (writer.Block($"public sealed class {node.TypeName}"))
        {
            writer.Line("private readonly HealthDataTransport _transport;");
            writer.Line();

            using (writer.Block($"internal {node.TypeName}(HealthDataTransport transport)"))
            {
                writer.Line("_transport = transport;");

                foreach (var child in node.Children.Values)
                {
                    writer.Line($"{child.CSharpName} = new {child.TypeName}(transport);");
                }
            }

            foreach (var child in node.Children.Values)
            {
                writer.Line();
                writer.XmlDoc("summary", $"The {child.Path} sub-resource.");
                writer.Line($"public {child.TypeName} {child.CSharpName} {{ get; }}");
            }

            foreach (var operation in node.Operations)
            {
                writer.Line();
                EmitOperationMethod(writer, operation);
            }
        }

        return new GeneratedFile($"Generated/Resources/{node.TypeName}.g.cs", writer.ToString());
    }

    private void EmitOperationMethod(CodeWriter writer, OperationContract operation)
    {
        var requestType = RequestTypeNames[operation.Id];
        var descriptor = $"HealthDataGeneratedOperations.{DescriptorPropertyName(operation.Id)}";

        writer.XmlDoc("summary", operation.Description ?? operation.Id);
        writer.XmlDoc("param name=\"request\"", "The request parameters.");
        writer.XmlDoc("param name=\"cancellationToken\"", "Cancels the call, including the response body read.");

        var returnType = operation.ResponseSchema is { } schema && operation.ResponseKind != ResponseKind.Empty
            ? NamingNormalizer.ToPascalCase(schema)
            : null;

        var signature = returnType is null
            ? $"public async Task {operation.CSharpName}({requestType} request, CancellationToken cancellationToken = default)"
            : $"public async Task<{returnType}> {operation.CSharpName}({requestType} request, CancellationToken cancellationToken = default)";

        using (writer.Block(signature))
        {
            writer.Line("ArgumentNullException.ThrowIfNull(request);");
            writer.Line();
            writer.Line($"var builder = new HealthDataRequestBuilder({CodeWriter.Literal(operation.PathTemplate)})");

            foreach (var parameter in operation.Parameters.Where(p => p.Location == ParameterLocation.Path))
            {
                writer.Line($"    .SetPath({CodeWriter.Literal(parameter.WireName)}, request.{parameter.CSharpName})");
            }

            foreach (var parameter in operation.Parameters.Where(p => p.Location == ParameterLocation.Query))
            {
                writer.Line($"    .AddQuery({CodeWriter.Literal(parameter.WireName)}, request.{parameter.CSharpName})");
            }

            writer.Line("    ;");
            writer.Line();

            if (operation.RequestSchema is { } bodySchema)
            {
                var bodyType = NamingNormalizer.ToPascalCase(bodySchema);
                writer.Line(
                    $"using var content = HealthDataTransport.CreateJsonContent(request.Body, HealthDataTransport.WriteInfo<{bodyType}>());");
            }

            var contentArgument = operation.RequestSchema is null ? "null" : "content";

            if (returnType is null)
            {
                writer.Line(
                    $"await _transport.SendAsync({descriptor}, builder.Build(), {contentArgument}, cancellationToken).ConfigureAwait(false);");
            }
            else
            {
                writer.Line(
                    $"return await _transport.SendAsync({descriptor}, builder.Build(), {contentArgument}, HealthDataTransport.ReadInfo<{returnType}>(), cancellationToken).ConfigureAwait(false);");
            }
        }

        // Query-paginated operations get a streaming convenience overload. Body-paginated and
        // request-only operations deliberately do not: rollUp carries its cursor inside the body,
        // and dailyRollUp returns no cursor at all.
        if (operation.Pagination?.Kind == PaginationKind.Query &&
            operation.Pagination.Items is { } itemsProperty &&
            operation.ResponseSchema is { } listSchema)
        {
            var responseType = NamingNormalizer.ToPascalCase(listSchema);
            var itemsMember = NamingNormalizer.ToMemberName(itemsProperty, responseType);
            var itemType = ItemTypeOf(listSchema, itemsProperty);

            writer.Line();
            writer.XmlDoc("summary", $"Enumerates every item returned by {operation.Id}, fetching pages as needed.");
            writer.XmlDoc(
                "remarks",
                "Pages are requested lazily. Nothing accumulates the whole result set in memory, " +
                "so a caller may stop at any point.");

            using (writer.Block(
                $"public IAsyncEnumerable<{itemType}> EnumerateAsync({requestType} request, CancellationToken cancellationToken = default)"))
            {
                writer.Line("ArgumentNullException.ThrowIfNull(request);");
                writer.Line();
                writer.Line("return AsyncPageEnumerable.CreateAsync(");
                writer.Line($"    (pageToken, ct) => {operation.CSharpName}(request.WithPageToken(pageToken), ct),");
                writer.Line($"    page => page.{itemsMember},");
                writer.Line("    page => page.NextPageToken,");
                writer.Line("    cancellationToken);");
            }
        }

        // A media-capable operation also gets a stream-first overload. Buffering an exported TCX
        // into a string would defeat the point.
        if (operation.ResponseKind == ResponseKind.MediaOrJson)
        {
            writer.Line();
            writer.XmlDoc("summary", $"Downloads the media form of {operation.Id} into the supplied stream.");
            writer.XmlDoc("remarks", "Sends alt=media and streams the response rather than buffering it.");

            using (writer.Block(
                $"public async Task {operation.CSharpName}({requestType} request, Stream destination, CancellationToken cancellationToken = default)"))
            {
                writer.Line("ArgumentNullException.ThrowIfNull(request);");
                writer.Line("ArgumentNullException.ThrowIfNull(destination);");
                writer.Line();
                writer.Line($"var builder = new HealthDataRequestBuilder({CodeWriter.Literal(operation.PathTemplate)})");

                foreach (var parameter in operation.Parameters.Where(p => p.Location == ParameterLocation.Path))
                {
                    writer.Line($"    .SetPath({CodeWriter.Literal(parameter.WireName)}, request.{parameter.CSharpName})");
                }

                foreach (var parameter in operation.Parameters.Where(p => p.Location == ParameterLocation.Query))
                {
                    writer.Line($"    .AddQuery({CodeWriter.Literal(parameter.WireName)}, request.{parameter.CSharpName})");
                }

                writer.Line("    .AddQuery(\"alt\", \"media\")");
                writer.Line("    ;");
                writer.Line();
                writer.Line(
                    $"await _transport.DownloadAsync({descriptor}, builder.Build(), destination, cancellationToken).ConfigureAwait(false);");
            }
        }
    }

    private GeneratedFile EmitClientPartial(ResourceNode root, Func<string, string[], CodeWriter> header)
    {
        var writer = header(RootNamespace, []);

        writer.XmlDoc("summary", "The generated resource surface of the client.");

        using (writer.Block("public sealed partial class HealthDataClient"))
        {
            foreach (var child in root.Children.Values)
            {
                writer.XmlDoc("summary", $"The {child.Path} resource.");
                writer.Line($"public {child.TypeName} {child.CSharpName} {{ get; private set; }} = null!;");
                writer.Line();
            }

            using (writer.Block("private partial void InitializeResources()"))
            {
                foreach (var child in root.Children.Values)
                {
                    writer.Line($"{child.CSharpName} = new {child.TypeName}(Transport);");
                }
            }
        }

        return new GeneratedFile("Generated/Resources/HealthDataClient.Resources.g.cs", writer.ToString());
    }

    /// <summary>Resolves the element type of a list response's items property.</summary>
    private string ItemTypeOf(string schemaName, string itemsProperty)
    {
        var schema = contract.Schemas.SingleOrDefault(s => s.WireName == schemaName)
            ?? throw new InvalidOperationException($"Response schema '{schemaName}' is not part of the contract.");

        var property = schema.Properties.SingleOrDefault(p => p.WireName == itemsProperty)
            ?? throw new InvalidOperationException(
                $"semantics.json names '{itemsProperty}' as the items property of '{schemaName}', but no such property exists.");

        return property.Type.ElementType?.CSharpType
            ?? throw new InvalidOperationException($"'{schemaName}.{itemsProperty}' is not an array.");
    }

    public static string DescriptorPropertyName(string operationId)
        => NamingNormalizer.ToPascalCase(operationId["health.".Length..].Replace('.', '_'));

    private static string StripNullable(string type) => type.EndsWith('?') ? type[..^1] : type;
}
