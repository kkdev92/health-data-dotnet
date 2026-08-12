using System.Text;
using System.Text.Json;

namespace Kkdev92.HealthData.CodeGen.Specifications;

/// <summary>
/// Rewrites a JSON document into a canonical, byte-stable form.
/// </summary>
/// <remarks>
/// <para>
/// Verified on 2026-08-09: the Google Health API Discovery endpoint returns the <em>same</em>
/// document with <em>randomized object key order</em> on every request. Four consecutive fetches
/// produced four different SHA-256 values at an identical byte length, with roughly 250,000
/// differing byte positions and no semantic difference at all.
/// </para>
/// <para>
/// Storing the raw response would therefore make two things impossible:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Stable provenance. The recorded hash would change on every fetch, so it could never
///     distinguish "Google changed the contract" from "Google shuffled the keys".
///   </description></item>
///   <item><description>
///     Reviewable contract diffs. Every specification update would render as a whole-file
///     rewrite, which defeats the entire point of committing the snapshot.
///   </description></item>
/// </list>
/// <para>
/// Canonicalizing is not "hand-editing the snapshot": it is a
/// deterministic, lossless, machine-applied normalization. Object keys are ordered with
/// <see cref="StringComparer.Ordinal"/>; array order, numbers, and string contents are preserved
/// exactly.
/// </para>
/// </remarks>
internal static class JsonCanonicalizer
{
    /// <summary>Returns the canonical UTF-8 bytes for a JSON payload, ending with a newline.</summary>
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            // Utf8JsonWriter indents with Environment.NewLine by default, which would make the
            // canonical form CRLF on Windows and LF on Linux, i.e. a different hash per platform.
            NewLine = "\n",
            // Escaping is left at the strict default so output never depends on an encoder setting.
            SkipValidation = false,
        }))
        {
            Write(document.RootElement, writer);
        }

        // A trailing newline keeps the file POSIX-clean and diff-friendly.
        var bytes = buffer.ToArray();
        var result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    /// <summary>True when the payload is already in canonical form.</summary>
    public static bool IsCanonical(ReadOnlySpan<byte> utf8Json)
        => Canonicalize(utf8Json).AsSpan().SequenceEqual(utf8Json);

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                // Array order is semantic (parameterOrder, enum, scopes) and is never reordered.
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.Number:
                // Written raw so that the exact numeric representation survives the round trip.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            case JsonValueKind.Undefined:
            default:
                throw new InvalidOperationException($"Unexpected JSON value kind '{element.ValueKind}'.");
        }
    }

    /// <summary>UTF-8 without a byte order mark, matching the rest of the repository.</summary>
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);
}
