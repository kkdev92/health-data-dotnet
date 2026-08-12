using System.Text.Json;
using Kkdev92.HealthData.CodeGen.IntermediateModel;

namespace Kkdev92.HealthData.CodeGen.Normalization;

/// <summary>
/// Maps Discovery types and formats onto C# types (ADR-0008).
/// </summary>
internal static class TypeMapper
{
    /// <summary>Resolves a Discovery schema fragment into an IR type.</summary>
    /// <param name="element">A Discovery schema or property node.</param>
    /// <param name="nullable">Whether the rendered C# type should be nullable.</param>
    /// <param name="enumTypeName">
    /// The name to give a generated open enum. Discovery declares enums inline on a property,
    /// so the name has to be synthesized by the caller from the declaring schema and property.
    /// </param>
    public static TypeContract Map(JsonElement element, bool nullable, string? enumTypeName = null)
    {
        if (element.TryGetProperty("$ref", out var reference))
        {
            var schemaName = reference.GetString()!;
            return new TypeContract
            {
                Kind = TypeKind.Reference,
                CSharpType = Nullify(NamingNormalizer.ToPascalCase(schemaName), nullable),
                WireType = "object",
                SchemaRef = schemaName,
            };
        }

        var wireType = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString()! : "any";
        var wireFormat = element.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : null;

        if (wireType == "array")
        {
            var elementType = Map(element.GetProperty("items"), nullable: false, enumTypeName);
            return new TypeContract
            {
                Kind = TypeKind.Array,
                CSharpType = Nullify($"IReadOnlyList<{elementType.CSharpType}>", nullable),
                WireType = wireType,
                ElementType = elementType,
                // A collection cannot carry an element converter through [JsonConverter], so an
                // array whose elements need one is rejected by the validator rather than emitted
                // with a silently wrong contract.
                ConverterTypeName = null,
            };
        }

        if (wireType == "object")
        {
            // Discovery revision 20260805 uses additionalProperties in only four places, all
            // "type": "any": Operation.metadata, Operation.response, Status.details[] and
            // HttpBody.extensions[]. JsonElement keeps them lossless.
            return new TypeContract
            {
                Kind = TypeKind.Map,
                CSharpType = Nullify("JsonElement", nullable),
                WireType = wireType,
            };
        }

        if (element.TryGetProperty("enum", out var enumElement))
        {
            if (enumTypeName is null)
            {
                throw new InvalidOperationException("An enum-typed value requires a generated type name.");
            }

            return new TypeContract
            {
                Kind = TypeKind.Enum,
                CSharpType = Nullify(enumTypeName, nullable),
                WireType = wireType,
                WireFormat = wireFormat,
                EnumValues = enumElement.EnumerateArray().Select(v => v.GetString()!).ToArray(),
                EnumTypeName = enumTypeName,
                // The converter lives on the generated enum type, not on each property.
                ConverterTypeName = null,
            };
        }

        var (csharpType, converter) = (wireType, wireFormat) switch
        {
            // int64 is always transmitted as a JSON string.
            ("string", "int64") => ("long", "Int64StringConverter"),
            ("string", "uint64") => ("ulong", "Int64StringConverter"),
            ("string", "google-datetime") => ("GoogleTimestamp", "GoogleTimestampConverter"),
            ("string", "google-duration") => ("GoogleDuration", "GoogleDurationConverter"),
            ("string", "google-fieldmask") => ("GoogleFieldMask", null),
            // Google's byte format is base64url with padding, not the standard alphabet.
            ("string", "byte") => ("byte[]", "Base64UrlBytesConverter"),
            ("string", "date") => ("string", null),
            ("string", null) => ("string", null),
            ("string", _) => ("string", null),
            ("boolean", _) => ("bool", null),
            ("integer", "int64") => ("long", "Int64StringConverter"),
            ("integer", "uint32") => ("uint", null),
            ("integer", _) => ("int", null),
            ("number", "float") => ("float", null),
            ("number", _) => ("double", null),
            ("any", _) => ("JsonElement", null),
            _ => throw new InvalidOperationException(
                $"Unmapped Discovery type '{wireType}' with format '{wireFormat ?? "(none)"}'."),
        };

        return new TypeContract
        {
            Kind = wireType == "any" ? TypeKind.Any : TypeKind.Primitive,
            CSharpType = Nullify(csharpType, nullable),
            WireType = wireType,
            WireFormat = wireFormat,
            ConverterTypeName = converter,
        };
    }

    /// <summary>True when the C# type needs an explicit <c>?</c> suffix to express absence.</summary>
    private static string Nullify(string csharpType, bool nullable)
    {
        if (!nullable || csharpType.EndsWith('?'))
        {
            return csharpType;
        }

        return csharpType + "?";
    }
}
