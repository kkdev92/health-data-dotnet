using System.Text.RegularExpressions;
using Kkdev92.HealthData.CodeGen.IntermediateModel;

namespace Kkdev92.HealthData.CodeGen.Normalization;

/// <summary>
/// Turns the patterns Discovery puts on name parameters into the resource name types.
/// </summary>
/// <remarks>
/// <para>
/// The pattern is the whole input. <c>^users/[^/]+/pairedDevices/[^/]+$</c> says: a name of four
/// segments, two of them ids, whose last collection is <c>pairedDevices</c> — which is enough to
/// name the type <c>PairedDeviceName</c>, to know it carries a user id and a device id, and to know
/// it descends from <c>^users/[^/]+$</c>. Nothing here is a list somebody keeps up to date.
/// </para>
/// <para>
/// The parent is found by looking for the pattern that is a strict prefix of this one, so the
/// hierarchy is discovered rather than declared. In the current contract every non-root pattern has
/// its parent present; one that did not would be a root with a longer path, which is a shape worth
/// noticing rather than papering over, so <see cref="Kkdev92.HealthData.CodeGen.Validation.ContractValidator"/>
/// rejects it.
/// </para>
/// </remarks>
internal static partial class ResourceNameResolver
{
    /// <summary>The only two forms a pattern segment takes in this contract.</summary>
    private const string Variable = "[^/]+";

    [GeneratedRegex(@"^\^(?<body>[A-Za-z0-9/\[\]^+_-]+)\$$")]
    private static partial Regex Anchored();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9]*$")]
    private static partial Regex Literal();

    /// <summary>
    /// Builds one contract per distinct pattern, ordered so a parent precedes its children.
    /// </summary>
    public static IReadOnlyList<ResourceNameContract> Resolve(IEnumerable<OperationContract> operations)
    {
        var patterns = operations
            .SelectMany(operation => operation.Parameters)
            .Where(parameter => parameter.Pattern is not null)
            .Select(parameter => parameter.Pattern!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(pattern => pattern, StringComparer.Ordinal)
            .ToArray();

        var segments = patterns.ToDictionary(
            pattern => pattern,
            Parse,
            StringComparer.Ordinal);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pattern in patterns)
        {
            names[pattern] = TypeName(segments[pattern]);
        }

        var contracts = new List<ResourceNameContract>();

        foreach (var pattern in patterns)
        {
            var own = segments[pattern];

            var parent = patterns
                .Where(candidate => !string.Equals(candidate, pattern, StringComparison.Ordinal))
                .Where(candidate => IsPrefix(segments[candidate], own))

                // The nearest ancestor, so users/{u}/dataTypes/{t}/dataPoints/{p} descends from the
                // data type rather than jumping straight to the user.
                .OrderByDescending(candidate => segments[candidate].Count)
                .FirstOrDefault();

            var last = own[^1];

            contracts.Add(new ResourceNameContract
            {
                CSharpName = names[pattern],
                Pattern = pattern,
                Segments = own,
                ParentCSharpName = parent is null ? null : names[parent],

                // A collection member is reached with an id — pairedDevice("abc") — and a singleton
                // is just there, so it reads as a property: profile, settings.
                MemberName = last.IsVariable
                    ? Singular(own[^2].Literal)
                    : NamingNormalizer.ToPascalCase(last.Literal),
                IdParameterName = last.IsVariable ? IdParameter(own[^2].Literal) : null,
                IdParameterNames = [.. IdParameters(own)],
                Example = Example(own),
            });
        }

        // Parents first, so a generated file can refer to a type declared earlier without the
        // reader having to jump forward. Ties keep the ordinal order of the patterns themselves,
        // which keeps generation deterministic.
        return [.. contracts.OrderBy(contract => contract.Segments.Count).ThenBy(contract => contract.CSharpName, StringComparer.Ordinal)];
    }

    /// <summary>Splits an anchored pattern into its segments.</summary>
    /// <exception cref="InvalidOperationException">
    /// The pattern is not of the anchored <c>literal</c>/<c>[^/]+</c> form every name in this
    /// contract uses. Guessing at a richer expression would produce a type that claims a structure
    /// the service never agreed to.
    /// </exception>
    public static IReadOnlyList<ResourceNameSegment> Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        if (Anchored().Match(pattern) is not { Success: true } anchored)
        {
            throw new InvalidOperationException(
                $"The resource name pattern '{pattern}' is not anchored with ^ and $, so its segments cannot be read.");
        }

        // The separator appears inside the id token as well as between segments — [^/]+ is a
        // slash. Splitting first would cut it into '[^' and ']+', so the token is lifted out
        // before the split and put back after. The placeholder is a control character, which no
        // pattern in this contract contains and no literal segment could.
        const string Placeholder = "\u0001";

        var parts = anchored.Groups["body"].Value
            .Replace(Variable, Placeholder, StringComparison.Ordinal)
            .Split('/');

        var segments = new List<ResourceNameSegment>(parts.Length);

        foreach (var part in parts)
        {
            if (string.Equals(part, Placeholder, StringComparison.Ordinal))
            {
                segments.Add(new ResourceNameSegment(Variable, IsVariable: true));
                continue;
            }

            if (!Literal().IsMatch(part))
            {
                throw new InvalidOperationException(
                    $"The resource name pattern '{pattern}' contains the segment '{part}', which is neither a "
                    + $"literal nor '{Variable}'. Only those two forms can be turned into a type.");
            }

            segments.Add(new ResourceNameSegment(part, IsVariable: false));
        }

        if (segments[0].IsVariable)
        {
            throw new InvalidOperationException(
                $"The resource name pattern '{pattern}' starts with an id rather than a collection, so there is "
                + "nothing to name the type after.");
        }

        return segments;
    }

    /// <summary>The type name a pattern earns: its last collection, singular, plus <c>Name</c>.</summary>
    /// <remarks>
    /// A name ending in an id is named for the collection that id belongs to —
    /// <c>pairedDevices/{x}</c> is a <c>PairedDeviceName</c>. A name ending in a literal is a
    /// singleton and is named for the literal itself, unchanged: <c>settings</c> stays
    /// <c>SettingsName</c> rather than being singularized into something the service never says.
    /// </remarks>
    private static string TypeName(IReadOnlyList<ResourceNameSegment> segments)
    {
        var last = segments[^1];

        var stem = last.IsVariable
            ? Singular(segments[^2].Literal)
            : NamingNormalizer.ToPascalCase(last.Literal);

        return stem + "Name";
    }

    /// <summary>
    /// The singular of a collection segment.
    /// </summary>
    /// <remarks>
    /// Deliberately the plainest rule that covers this contract — every collection here is a simple
    /// <c>-s</c> plural: users, projects, subscribers, subscriptions, dataTypes, dataPoints,
    /// pairedDevices. An English inflector would be a dependency and a source of surprises for a
    /// vocabulary of seven words. A collection that did not end in <c>s</c> keeps its own name,
    /// which reads oddly but never invents a word.
    /// </remarks>
    private static string Singular(string collection)
    {
        var singular = collection.EndsWith('s') ? collection[..^1] : collection;

        return NamingNormalizer.ToPascalCase(singular);
    }

    /// <summary>The camelCase parameter name for the id a collection segment introduces.</summary>
    private static string IdParameter(string collection)
    {
        var pascal = Singular(collection) + "Id";

        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    private static IEnumerable<string> IdParameters(IReadOnlyList<ResourceNameSegment> segments)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].IsVariable)
            {
                yield return IdParameter(segments[index - 1].Literal);
            }
        }
    }

    /// <summary>Whether one segment list is a strict prefix of another.</summary>
    private static bool IsPrefix(IReadOnlyList<ResourceNameSegment> candidate, IReadOnlyList<ResourceNameSegment> full)
    {
        if (candidate.Count >= full.Count)
        {
            return false;
        }

        for (var index = 0; index < candidate.Count; index++)
        {
            if (candidate[index] != full[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A readable instance of the pattern, for the type's doc comment.</summary>
    private static string Example(IReadOnlyList<ResourceNameSegment> segments)
    {
        var parts = new List<string>(segments.Count);

        for (var index = 0; index < segments.Count; index++)
        {
            parts.Add(segments[index].IsVariable
                ? "{" + IdParameter(segments[index - 1].Literal) + "}"
                : segments[index].Literal);
        }

        return string.Join('/', parts);
    }
}
