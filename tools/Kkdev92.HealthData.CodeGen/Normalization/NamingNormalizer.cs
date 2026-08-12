using System.Globalization;
using System.Text;

namespace Kkdev92.HealthData.CodeGen.Normalization;

/// <summary>
/// Turns wire names into legal, stable C# identifiers.
/// </summary>
/// <remarks>
/// <para>
/// This type only ever produces <em>C# side</em> names. Wire names are never passed through it:
/// a query parameter called <c>pageSize</c> stays <c>pageSize</c> on the wire no matter what the
/// C# member is called.
/// </para>
/// <para>
/// Every rule here is deterministic and culture-invariant, because the same specification must
/// produce byte-identical output on every machine.
/// </para>
/// </remarks>
internal static class NamingNormalizer
{
    // C# keywords that cannot be used bare as identifiers. Contextual keywords are legal and
    // deliberately absent.
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    /// <summary>Converts a wire name such as <c>nextPageToken</c> or <c>heart-rate</c> to PascalCase.</summary>
    public static string ToPascalCase(string wireName)
    {
        ArgumentException.ThrowIfNullOrEmpty(wireName);

        var builder = new StringBuilder(wireName.Length);
        var capitalizeNext = true;

        foreach (var c in wireName)
        {
            if (c is '_' or '-' or '.' or ' ' or '/')
            {
                capitalizeNext = true;
                continue;
            }

            if (!char.IsLetterOrDigit(c))
            {
                // Anything else is dropped rather than transliterated, so the rule stays total.
                continue;
            }

            builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
            capitalizeNext = false;
        }

        if (builder.Length == 0)
        {
            throw new InvalidOperationException($"Wire name '{wireName}' contains no identifier characters.");
        }

        // An identifier may not start with a digit. Discovery revision 20260805 has no such case,
        // but the rule must be total rather than lucky.
        if (char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Produces a legal member name for a property, avoiding the CS0542 collision where a member
    /// would have the same name as its enclosing type.
    /// </summary>
    /// <remarks>
    /// Discovery revision 20260805 hits this exactly three times:
    /// <c>ActiveZoneMinutes.activeZoneMinutes</c>, <c>Moods.moods</c> and <c>Symptoms.symptoms</c>.
    /// </remarks>
    public static string ToMemberName(string wireName, string declaringTypeName)
    {
        var name = ToPascalCase(wireName);
        return string.Equals(name, declaringTypeName, StringComparison.Ordinal) ? name + "Value" : name;
    }

    /// <summary>Escapes a C# keyword so it can be used as an identifier.</summary>
    public static string EscapeIdentifier(string identifier)
        => ReservedKeywords.Contains(identifier) ? "@" + identifier : identifier;

    /// <summary>Converts a scope URL to a constant name, e.g. <c>ActivityAndFitnessReadonly</c>.</summary>
    public static string ScopeConstantName(string scopeUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeUrl);

        var lastSegment = scopeUrl[(scopeUrl.LastIndexOf('/') + 1)..];

        // "googlehealth.activity_and_fitness.readonly" -> "ActivityAndFitnessReadonly"
        // "cloud-platform"                             -> "CloudPlatform"
        if (lastSegment.StartsWith("googlehealth.", StringComparison.Ordinal))
        {
            lastSegment = lastSegment["googlehealth.".Length..];
        }

        return ToPascalCase(lastSegment.Replace('.', '_'));
    }

    /// <summary>
    /// Converts an operation id to a C# method name, e.g. <c>health.users.getProfile</c> to
    /// <c>GetProfileAsync</c>.
    /// </summary>
    public static string OperationMethodName(string operationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        var lastSegment = operationId[(operationId.LastIndexOf('.') + 1)..];
        return ToPascalCase(lastSegment) + "Async";
    }

    /// <summary>Normalizes an error reason such as <c>ACCOUNT_NOT_LINKED</c> to <c>AccountNotLinked</c>.</summary>
    public static string ErrorReasonConstantName(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return ToPascalCase(reason.ToLowerInvariant());
    }

    /// <summary>
    /// Normalizes a wire enum value such as <c>SLEEP_STAGE_TYPE_UNSPECIFIED</c> to
    /// <c>SleepStageTypeUnspecified</c>.
    /// </summary>
    public static string EnumValueName(string wireValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(wireValue);
        return ToPascalCase(wireValue.ToLowerInvariant());
    }

    /// <summary>Formats an integer using invariant culture, for deterministic output.</summary>
    public static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
