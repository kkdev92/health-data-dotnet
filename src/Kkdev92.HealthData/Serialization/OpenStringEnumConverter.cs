using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kkdev92.HealthData.Serialization;

/// <summary>
/// Serializes a generated open enum as its bare wire string.
/// </summary>
/// <typeparam name="T">The generated open enum type.</typeparam>
/// <remarks>
/// Reflection-free and safe under Native AOT: the closed generic instantiation is referenced
/// directly by the generated <c>[JsonConverter]</c> attribute, so the trimmer keeps it.
/// </remarks>
public sealed class OpenStringEnumConverter<T> : JsonConverter<T>
    where T : struct, IOpenStringValue<T>
{
    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string value for {typeToConvert.Name}.");
        }

        // Unknown values are preserved verbatim rather than rejected: that is the whole point of
        // an open enum.
        return T.FromValue(reader.GetString()!);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}
