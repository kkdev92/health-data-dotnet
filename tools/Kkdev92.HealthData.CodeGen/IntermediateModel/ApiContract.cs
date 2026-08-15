namespace Kkdev92.HealthData.CodeGen.IntermediateModel;

/// <summary>
/// The normalized intermediate representation of a Google Health API contract.
/// </summary>
/// <remarks>
/// The emitter never reads the Discovery document directly. Everything
/// it needs is resolved into this shape first, so that Discovery's format and C# generation stay
/// decoupled and so that semantic overrides have a single place to apply.
/// </remarks>
internal sealed record ApiContract
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string ApiVersion { get; init; }
    public required string Revision { get; init; }
    public required Uri RootUrl { get; init; }
    public required string SpecSha256 { get; init; }
    public required IReadOnlyList<ScopeContract> Scopes { get; init; }
    public required IReadOnlyList<OperationContract> Operations { get; init; }
    public required IReadOnlyList<SchemaContract> Schemas { get; init; }
    public required IReadOnlyList<ErrorReasonContract> ErrorReasons { get; init; }
    public required IReadOnlyList<OpenEnumContract> OpenEnums { get; init; }

    /// <summary>The resource names the service accepts, one per distinct pattern.</summary>
    public required IReadOnlyList<ResourceNameContract> ResourceNames { get; init; }

    /// <summary>The data types, and which operations Google documents for each.</summary>
    public required IReadOnlyList<DataTypeContract> DataTypes { get; init; }
}

/// <summary>
/// One data type, as the Data Types page describes it.
/// </summary>
/// <remarks>
/// <para>
/// None of this is in Discovery. The REST path is the generic <c>dataTypes/{dataTypesId}</c>, so
/// per-type capability appears nowhere in the machine-readable contract — which is why an
/// application asking <c>steps</c> for a <c>get</c> receives
/// <c>400 UNSUPPORTED_DATA_TYPE_ACTION</c>, an answer about the type rather than about the id it
/// sent.
/// </para>
/// <para>
/// <strong>Metadata, never validation.</strong> The snapshot says so itself: capabilities are
/// published as metadata and must not become client-side hard validation, because the server
/// remains the authority. The generated table is emitted for a caller to read and is never
/// consulted before a request.
/// </para>
/// </remarks>
internal sealed record DataTypeContract
{
    /// <summary>The kebab-case id that goes in a resource name, for example <c>heart-rate</c>.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The snake_case name filters are written against, for example <c>heart_rate</c>.
    /// </summary>
    /// <remarks>
    /// A different string from <see cref="Id"/> and not derivable from it. Both are preserved
    /// verbatim; the spec forbids deriving one from the other by a naming rule.
    /// </remarks>
    public required string FilterName { get; init; }

    /// <summary>The operation short names Google documents, for example <c>list</c>.</summary>
    public required IReadOnlyList<string> Operations { get; init; }
}

/// <summary>
/// A generated resource name type, derived from the pattern Discovery puts on a name parameter.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>name</c> and <c>parent</c> parameter in this contract carries a <c>pattern</c> — a
/// regular expression the service applies before doing anything else. The generator used to put
/// that expression in a doc comment above a <c>string</c>. Here it becomes the type of the
/// parameter instead, so a name of the wrong shape stops being a 400 and starts being a compile
/// error (ADR-0010).
/// </para>
/// <para>
/// The pattern is also the definition of the type's structure: its segments say which ids the name
/// carries and which name it descends from. Nothing about the hierarchy is written down here — it
/// is read out of the expressions, so a resource Google adds arrives with the contract.
/// </para>
/// </remarks>
internal sealed record ResourceNameContract
{
    /// <summary>The generated type name, for example <c>PairedDeviceName</c>.</summary>
    public required string CSharpName { get; init; }

    /// <summary>The pattern exactly as Discovery states it, anchors included.</summary>
    public required string Pattern { get; init; }

    /// <summary>The segments the pattern is made of, in order.</summary>
    public required IReadOnlyList<ResourceNameSegment> Segments { get; init; }

    /// <summary>
    /// The name this one descends from, or <see langword="null"/> for a root such as
    /// <c>users/{user}</c>.
    /// </summary>
    public string? ParentCSharpName { get; init; }

    /// <summary>
    /// The member the parent offers to build this name: a method taking an id for a collection
    /// member, a property for a singleton such as <c>profile</c>.
    /// </summary>
    public required string MemberName { get; init; }

    /// <summary>The id parameter this name adds to its parent's, or null for a singleton.</summary>
    public string? IdParameterName { get; init; }

    /// <summary>Every id the name carries, outermost first — one per variable segment.</summary>
    public required IReadOnlyList<string> IdParameterNames { get; init; }

    /// <summary>An example of the wire form, for doc comments.</summary>
    public required string Example { get; init; }
}

/// <summary>One <c>/</c>-separated part of a resource name pattern.</summary>
/// <param name="Literal">The literal text, for example <c>pairedDevices</c> or <c>profile</c>.</param>
/// <param name="IsVariable">
/// True when the segment is <c>[^/]+</c> — an id supplied by the caller rather than fixed text.
/// </param>
internal sealed record ResourceNameSegment(string Literal, bool IsVariable);

/// <summary>
/// A generated open enum type.
/// </summary>
/// <remarks>
/// Discovery declares enums inline on a property rather than as a named schema, so the C# type
/// name is synthesized from the declaring schema and property. The values are those known at
/// generation time and are never treated as exhaustive (ADR-0005).
/// </remarks>
internal sealed record OpenEnumContract
{
    public required string CSharpName { get; init; }
    public required string DeclaringSchema { get; init; }
    public required string DeclaringProperty { get; init; }
    public required IReadOnlyList<OpenEnumValueContract> Values { get; init; }
}

internal sealed record OpenEnumValueContract(string WireValue, string CSharpName, string? Description);

internal sealed record ScopeContract(
    string Url,
    string CSharpName,
    string? Description,
    ScopeKind Kind);

/// <summary>
/// What a scope grants.
/// </summary>
/// <remarks>
/// <para>
/// Declared in <c>semantics.json</c>, not derived. Discovery says it in prose — "See your Google
/// Health sleep data" against "Add sleep data to Google Health, and edit or delete the data it
/// adds" — and a rule over that text would be a rule over Google's copywriting.
/// </para>
/// <para>
/// Deriving it from the operations does not work either: five <c>.readonly</c> scopes are declared
/// by POST operations, because <c>rollUp</c>, <c>dailyRollUp</c> and <c>reconcile</c> are POSTs
/// that read. Measured 2026-08-15 - the HTTP method disagrees with the scope in 5 of 19 cases.
/// </para>
/// </remarks>
internal enum ScopeKind
{
    /// <summary>Reads a person's data.</summary>
    Read,

    /// <summary>Writes, edits or deletes a person's data.</summary>
    Write,

    /// <summary>Authorizes as the project rather than as a person.</summary>
    Project,
}

internal sealed record ErrorReasonContract(string Reason, int HttpStatus);

internal sealed record OperationContract
{
    /// <summary>The Discovery operation id, for example <c>health.users.getProfile</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The dotted resource path, for example <c>users.dataTypes.dataPoints</c>.</summary>
    public required string ResourcePath { get; init; }

    public required string CSharpName { get; init; }
    public required string HttpMethod { get; init; }

    /// <summary>The URI template relative to the root URL, for example <c>v4/{+name}</c>.</summary>
    public required string PathTemplate { get; init; }

    public required IReadOnlyList<ParameterContract> Parameters { get; init; }
    public string? RequestSchema { get; init; }
    public string? ResponseSchema { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>
    /// How <see cref="Scopes"/> combine. Discovery cannot say, so only semantics.json can.
    /// </summary>
    public ScopeCombination ScopeCombination { get; init; } = ScopeCombination.AnyOf;

    public required ResponseKind ResponseKind { get; init; }
    public required RetryClassification RetryClassification { get; init; }
    public PaginationContract? Pagination { get; init; }
    public bool SupportsMediaDownload { get; init; }
    public string? Description { get; init; }
}

internal sealed record ParameterContract
{
    /// <summary>The name exactly as it appears on the wire. Never reshaped.</summary>
    public required string WireName { get; init; }

    /// <summary>The C# member name. A separate concept from <see cref="WireName"/>.</summary>
    public required string CSharpName { get; init; }

    public required ParameterLocation Location { get; init; }
    public required bool IsRequired { get; init; }
    public required TypeContract Type { get; init; }
    public string? Pattern { get; init; }
    public string? Description { get; init; }
}

internal sealed record SchemaContract
{
    public required string WireName { get; init; }
    public required string CSharpName { get; init; }
    public required IReadOnlyList<PropertyContract> Properties { get; init; }
    public string? Description { get; init; }
}

internal sealed record PropertyContract
{
    public required string WireName { get; init; }
    public required string CSharpName { get; init; }
    public required TypeContract Type { get; init; }

    /// <summary>
    /// True when Discovery marks the property <c>readOnly</c>.
    /// </summary>
    /// <remarks>
    /// Read-only properties are readable but are removed from the write contract rather than
    /// duplicated into a separate input type. See ADR-0006.
    /// </remarks>
    public required bool IsReadOnly { get; init; }

    public string? Description { get; init; }
}

internal sealed record TypeContract
{
    public required TypeKind Kind { get; init; }

    /// <summary>The rendered C# type, for example <c>string?</c> or <c>IReadOnlyList&lt;DataPoint&gt;</c>.</summary>
    public required string CSharpType { get; init; }

    /// <summary>The Discovery <c>type</c>, preserved for diagnostics and diffing.</summary>
    public required string WireType { get; init; }

    /// <summary>The Discovery <c>format</c>, for example <c>int64</c> or <c>google-datetime</c>.</summary>
    public string? WireFormat { get; init; }

    /// <summary>The referenced schema name, when <see cref="Kind"/> is <see cref="TypeKind.Reference"/>.</summary>
    public string? SchemaRef { get; init; }

    public TypeContract? ElementType { get; init; }

    /// <summary>Wire enum values known at generation time. Never treated as exhaustive (ADR-0005).</summary>
    public IReadOnlyList<string> EnumValues { get; init; } = [];

    /// <summary>The generated open enum type name, when <see cref="Kind"/> is <see cref="TypeKind.Enum"/>.</summary>
    public string? EnumTypeName { get; init; }

    /// <summary>
    /// The <c>JsonConverter</c> the property needs, or <see langword="null"/> when the default
    /// contract is correct.
    /// </summary>
    /// <remarks>
    /// Open enums carry their converter on the type itself, so those report no property-level
    /// converter here.
    /// </remarks>
    public string? ConverterTypeName { get; init; }
}

internal sealed record PaginationContract
{
    public required PaginationKind Kind { get; init; }
    public string? PageSize { get; init; }
    public string? PageToken { get; init; }
    public string? NextPageToken { get; init; }
    public string? Items { get; init; }
}

internal enum TypeKind
{
    Primitive,
    Reference,
    Array,
    Map,
    Enum,
    Any,
}

internal enum ParameterLocation
{
    Path,
    Query,
    Body,
}

internal enum ResponseKind
{
    Json,
    Empty,
    Operation,

    /// <summary>Both a typed JSON response and an <c>alt=media</c> stream are available.</summary>
    MediaOrJson,
}

internal enum RetryClassification
{
    Never,
    Safe,
    Idempotent,
    SemanticallySafe,
}

internal enum ScopeCombination
{
    /// <summary>Any one of the listed scopes is accepted, which is what Discovery implies.</summary>
    AnyOf,

    /// <summary>All of them are needed together. Only a per-method page can establish this.</summary>
    AllOf,
}

internal enum PaginationKind
{
    None,

    /// <summary>Page size and token travel as query parameters.</summary>
    Query,

    /// <summary>Page size and token travel inside the request body.</summary>
    Body,

    /// <summary>
    /// The request is paginated but the response carries no continuation token, so no
    /// enumeration helper may be generated. Currently only <c>dataPoints.dailyRollUp</c>.
    /// </summary>
    RequestOnly,
}
