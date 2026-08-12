using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kkdev92.HealthData.Serialization;

/// <summary>
/// The serialization entry points used by this SDK.
/// </summary>
/// <remarks>
/// <para>
/// Two contracts exist for the same models. Reading keeps every property. Writing removes the
/// properties Discovery marks <c>readOnly</c>, so a value the service owns is never echoed back
/// to it (ADR-0006).
/// </para>
/// <para>
/// Both resolve through the source-generated <see cref="HealthDataJsonContext"/>. Reflection is
/// disabled for this assembly, so a type missing from that context fails loudly rather than
/// silently falling back and then breaking only under Native AOT.
/// </para>
/// </remarks>
public static class HealthDataJson
{
    /// <summary>Options for deserializing service responses.</summary>
    public static JsonSerializerOptions ReadOptions { get; } = new()
    {
        TypeInfoResolver = HealthDataJsonContext.Default,
    };

    /// <summary>Options for serializing request payloads, excluding output-only properties.</summary>
    public static JsonSerializerOptions WriteOptions { get; } = new()
    {
        TypeInfoResolver = HealthDataJsonContext.Default.WithAddedModifier(RemoveOutputOnlyProperties),

        // Absent and null are the same thing on this wire contract, and omitting nulls keeps
        // PATCH payloads to what the caller actually set.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Returns the read contract for <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The type is not part of the generated contract.</exception>
    public static JsonTypeInfo<T> ReadInfo<T>()
        => (JsonTypeInfo<T>)ReadOptions.GetTypeInfo(typeof(T));

    /// <summary>Returns the write contract for <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The type is not part of the generated contract.</exception>
    public static JsonTypeInfo<T> WriteInfo<T>()
        => (JsonTypeInfo<T>)WriteOptions.GetTypeInfo(typeof(T));

    /// <summary>
    /// Drops output-only properties from a type's write contract.
    /// </summary>
    /// <remarks>
    /// A contract modifier rather than a second set of generated types: applying this to the 23
    /// affected schemas transitively would otherwise have required 55 additional generated types
    /// (ADR-0006).
    /// </remarks>
    private static void RemoveOutputOnlyProperties(JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (!HealthDataOutputOnlyProperties.ByType.TryGetValue(typeInfo.Type, out var outputOnly))
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (Array.IndexOf(outputOnly, typeInfo.Properties[i].Name) >= 0)
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}
