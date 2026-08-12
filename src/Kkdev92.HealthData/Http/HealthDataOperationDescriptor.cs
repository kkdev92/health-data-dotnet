namespace Kkdev92.HealthData.Http;

/// <summary>
/// How safe an operation is to send again after a transient failure.
/// </summary>
/// <remarks>
/// Retry is opt-in and writes are never resent automatically.
/// </remarks>
public enum RetryClassification
{
    /// <summary>Never resend automatically. All creates and updates.</summary>
    Never,

    /// <summary>A read with no side effects.</summary>
    Safe,

    /// <summary>Repeating the call converges on the same server state.</summary>
    Idempotent,

    /// <summary>
    /// Uses a method that implies a write, but only aggregates existing data. The roll-up
    /// operations use POST solely to carry a request body.
    /// </summary>
    SemanticallySafe,
}

/// <summary>What kind of response body an operation returns.</summary>
public enum ResponseKind
{
    /// <summary>A typed JSON resource.</summary>
    Json,

    /// <summary>No body.</summary>
    Empty,

    /// <summary>A long-running <c>Operation</c>.</summary>
    Operation,

    /// <summary>
    /// Either a typed JSON body or, with <c>alt=media</c>, a raw media stream.
    /// </summary>
    MediaOrJson,
}

/// <summary>How the scopes an operation lists combine.</summary>
/// <remarks>
/// <para>
/// A Discovery scopes array carries no combination: it is a flat list, and the convention is that
/// any one entry suffices. That convention is not always the contract. Google's per-method
/// reference for <c>dataPoints.exportExerciseTcx</c> states that this method needs an
/// activity-and-fitness scope <em>and</em> a location scope together, which a flat list cannot
/// express and Discovery does not record.
/// </para>
/// <para>
/// So the combination is carried here rather than inferred. It lives in the core package, with no
/// authentication types involved, because the descriptor is what generation produces and what a
/// token provider reads; deriving it later would put the guess back.
/// </para>
/// </remarks>
public enum HealthDataScopeCombination
{
    /// <summary>Any one of the listed scopes is accepted. The Discovery default.</summary>
    AnyOf,

    /// <summary>Every listed scope must be present together.</summary>
    AllOf,
}

/// <summary>How an operation paginates, if at all.</summary>
public enum PaginationKind
{
    /// <summary>Not paginated.</summary>
    None,

    /// <summary>Page size and token travel as query parameters.</summary>
    Query,

    /// <summary>Page size and token travel inside the request body.</summary>
    Body,

    /// <summary>
    /// The request accepts a page token but the response returns none, so results cannot be
    /// enumerated. Currently only <c>dataPoints.dailyRollUp</c>.
    /// </summary>
    RequestOnly,
}

/// <summary>
/// The generated metadata describing one Google Health API operation.
/// </summary>
/// <remarks>
/// Attached to every outgoing request via <see cref="HttpRequestMessageExtensions"/> so that
/// delegating handlers can make decisions without re-parsing the URL: which credential to
/// attach, whether a retry is permissible, and what to report in diagnostics.
/// </remarks>
public sealed class HealthDataOperationDescriptor
{
    /// <summary>The Discovery operation id, for example <c>health.users.getProfile</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The Google Health API version this operation belongs to.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>The HTTP method.</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>The URI path template, relative to the service root.</summary>
    public required string PathTemplate { get; init; }

    /// <summary>OAuth scopes the service accepts for this operation.</summary>
    /// <remarks>
    /// Read together with <see cref="ScopeCombination"/>, which says whether one of them is enough
    /// or all of them are needed. Reading this list alone and assuming the former is what this
    /// SDK used to do, and it was wrong for one operation.
    /// </remarks>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>How <see cref="Scopes"/> combine. Defaults to <c>AnyOf</c>, as Discovery implies.</summary>
    public HealthDataScopeCombination ScopeCombination { get; init; }
        = HealthDataScopeCombination.AnyOf;

    /// <summary>Whether the operation is authorized with project credentials rather than user OAuth.</summary>
    /// <remarks>
    /// <c>projects.subscribers.*</c> uses <c>cloud-platform</c>; everything under <c>users</c>
    /// uses end-user consent. A single token field cannot serve both (ADR-0007).
    /// </remarks>
    public required bool RequiresProjectCredentials { get; init; }

    /// <summary>How safe the operation is to resend.</summary>
    public required RetryClassification RetryClassification { get; init; }

    /// <summary>The kind of response body.</summary>
    public required ResponseKind ResponseKind { get; init; }

    /// <summary>How the operation paginates.</summary>
    public required PaginationKind Pagination { get; init; }
}
