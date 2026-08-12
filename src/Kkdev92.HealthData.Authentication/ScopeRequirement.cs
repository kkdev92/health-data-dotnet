namespace Kkdev92.HealthData.Authentication;

/// <summary>
/// How a set of OAuth scopes satisfies an operation.
/// </summary>
public enum ScopeCombination
{
    /// <summary>Any one of the listed scopes is sufficient.</summary>
    AnyOf,

    /// <summary>Every listed scope is required.</summary>
    AllOf,
}

/// <summary>
/// The scopes an operation will accept.
/// </summary>
/// <remarks>
/// <para>
/// A plain <c>string[]</c> cannot express the difference between "any of these" and "all of
/// these", so the combination is explicit here.
/// </para>
/// <para>
/// A Discovery <c>scopes</c> array means any one of the listed values is accepted, so almost
/// everything is <see cref="ScopeCombination.AnyOf"/>. One operation is not:
/// <c>dataPoints.exportExerciseTcx</c>, whose per-method page states that an activity-and-fitness
/// scope and a location scope must both be present. Discovery cannot express that, which is why
/// the requirement is declared in <c>semantics.json</c> and carried on the operation descriptor
/// rather than derived from the scopes array. Verified against that page on 2026-08-12.
/// </para>
/// </remarks>
public sealed class ScopeRequirement
{
    private ScopeRequirement(ScopeCombination combination, IReadOnlyList<string> scopes)
    {
        Combination = combination;
        Scopes = scopes;
    }

    /// <summary>How <see cref="Scopes"/> combine.</summary>
    public ScopeCombination Combination { get; }

    /// <summary>The scopes involved.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>True when the operation declares no scopes at all.</summary>
    public bool IsEmpty => Scopes.Count == 0;

    /// <summary>Any one of these scopes suffices.</summary>
    public static ScopeRequirement AnyOf(params IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return new ScopeRequirement(ScopeCombination.AnyOf, [.. scopes]);
    }

    /// <summary>All of these scopes are required.</summary>
    public static ScopeRequirement AllOf(params IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return new ScopeRequirement(ScopeCombination.AllOf, [.. scopes]);
    }

    /// <summary>Whether a set of granted scopes satisfies this requirement.</summary>
    /// <remarks>
    /// An empty requirement is satisfied by anything. This is a client-side convenience for
    /// failing early with a clear message; the service remains the authority.
    /// </remarks>
    public bool IsSatisfiedBy(IEnumerable<string> grantedScopes)
    {
        ArgumentNullException.ThrowIfNull(grantedScopes);

        if (IsEmpty)
        {
            return true;
        }

        var granted = grantedScopes.ToHashSet(StringComparer.Ordinal);

        return Combination == ScopeCombination.AllOf
            ? Scopes.All(granted.Contains)
            : Scopes.Any(granted.Contains);
    }

    /// <inheritdoc />
    public override string ToString()
        => IsEmpty ? "(no scopes)" : $"{Combination}({string.Join(", ", Scopes)})";
}
