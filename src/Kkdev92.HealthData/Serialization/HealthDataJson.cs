using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <para>
/// Both also accept the named floating-point literals. The service sends <c>"NaN"</c> as a JSON
/// string for a double it could not compute, which is what the protobuf JSON mapping prescribes
/// and what System.Text.Json rejects by default. Observed on
/// <c>daily-sleep-temperature-derivations</c>: six points in 1,719 carried
/// <c>"baselineTemperatureCelsius": "NaN"</c>, and one of them failed the whole response.
/// </para>
/// </remarks>
public static class HealthDataJson
{
    /// <summary>Options for deserializing service responses.</summary>
    /// <remarks>Read-only: a change here would change how every response in the process is read.</remarks>
    public static JsonSerializerOptions ReadOptions { get; } = Locked(new()
    {
        TypeInfoResolver = HealthDataJsonContext.Default,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

    /// <summary>Options for serializing request payloads, excluding output-only properties.</summary>
    /// <remarks>Read-only: a change here would change every request the process sends.</remarks>
    public static JsonSerializerOptions WriteOptions { get; } = Locked(new()
    {
        TypeInfoResolver = HealthDataJsonContext.Default
            .WithAddedModifier(RemoveOutputOnlyProperties)
            .WithAddedModifier(RejectAmbiguousUnions),

        // Absent and null are the same thing on this wire contract, and omitting nulls keeps
        // PATCH payloads to what the caller actually set.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Writing needs it as well as reading. This API has no field mask, so an update is a
        // read, a change and a send of the whole point. A point that came back carrying NaN
        // would otherwise be readable and then unsendable.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

    /// <summary>
    /// Makes options read-only before anything outside this type can reach them.
    /// </summary>
    /// <remarks>
    /// Options lock themselves the first time they serialize, which left a window: code that
    /// reached them before the SDK had used them could change, say, how nulls are written, and
    /// every request from the process would change with it. A patch that sends nulls asks the
    /// service to clear fields.
    /// </remarks>
    private static JsonSerializerOptions Locked(JsonSerializerOptions options)
    {
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Returns the read contract for <typeparamref name="T"/>.</summary>
    /// <exception cref="NotSupportedException">The type is not part of the generated contract.</exception>
    public static JsonTypeInfo<T> ReadInfo<T>()
        => (JsonTypeInfo<T>)ReadOptions.GetTypeInfo(typeof(T));

    /// <summary>Returns the write contract for <typeparamref name="T"/>.</summary>
    /// <exception cref="NotSupportedException">The type is not part of the generated contract.</exception>
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

        // The copy, not the public table: an element of the public table's arrays can be assigned.
        if (!HealthDataOutputOnlyProperties.ForWriteContract.TryGetValue(typeInfo.Type, out var outputOnly))
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (outputOnly.Contains(typeInfo.Properties[i].Name))
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Refuses to write a union that carries more than one of its alternatives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>DataPoint</c> is a name plus forty-two mutually exclusive measurements. Discovery has
    /// no way to say "exactly one of these", so every member is a settable property and an object
    /// initializer can set two. The service refuses the request; what it says is about the call,
    /// not about which pair of members made it wrong.
    /// </para>
    /// <para>
    /// Checked here rather than in the setters because <c>dataPoints.patch</c> is read, modify,
    /// send: a point that arrived carrying a measurement has to be able to carry it back. This is
    /// the last moment where the object is finished and the request has not gone out.
    /// </para>
    /// <para>
    /// On the write contract only. A response that carries two must still deserialize — refusing
    /// it would drop a person's data over a rule about a shape the service chose to send.
    /// </para>
    /// </remarks>
    private static void RejectAmbiguousUnions(JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        // The copy, for the same reason as the output-only table.
        if (!HealthDataUnionMembers.ForWriteContract.TryGetValue(typeInfo.Type, out var alternatives))
        {
            return;
        }

        // Resolved once, when the contract is built, rather than per serialization. The getters
        // are the source-generated ones: reflection is disabled for this assembly.
        List<(string Name, Func<object, object?> Get)> members = [];

        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is { } get && alternatives.Contains(property.Name))
            {
                members.Add((property.Name, get));
            }
        }

        var typeName = typeInfo.Type.Name;

        typeInfo.OnSerializing = value =>
        {
            var carried = 0;

            foreach (var (_, get) in members)
            {
                if (get(value) is not null)
                {
                    carried++;
                }
            }

            if (carried > 1)
            {
                throw CarriesMoreThanOne(typeName, members, value);
            }
        };
    }

    /// <summary>
    /// The refusal for a union that carries more than one alternative, naming each one it carries.
    /// </summary>
    /// <remarks>
    /// Kept out of the check that throws it. The check runs for every point written, and a valid
    /// point has no use for the names. Gathered inline, they also made the optimized check slower
    /// on the valid path, which never reaches them.
    /// </remarks>
    private static InvalidOperationException CarriesMoreThanOne(
        string typeName, List<(string Name, Func<object, object?> Get)> members, object value)
    {
        List<string> names = [];

        foreach (var (name, get) in members)
        {
            if (get(value) is not null)
            {
                names.Add(name);
            }
        }

        return new InvalidOperationException(
            $"A {typeName} carries one measurement, and this one has {names.Count}: "
            + $"{string.Join(", ", names)}. The service accepts exactly one, so this request "
            + "would be refused. Send one measurement per data point.");
    }
}
