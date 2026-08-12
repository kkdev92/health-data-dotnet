using System.Globalization;
using System.Text;

namespace Kkdev92.HealthData.CodeGen.CSharp;

/// <summary>
/// Builds generated source text under the generator's determinism rules.
/// </summary>
/// <remarks>
/// Line endings are always LF, encoding is always UTF-8 without BOM, and no value that varies by
/// machine, clock, or locale may be written. Everything numeric goes through invariant culture.
/// </remarks>
internal sealed class CodeWriter
{
    private const string IndentUnit = "    ";
    private readonly StringBuilder _builder = new();
    private int _indent;

    /// <summary>UTF-8 without a byte order mark.</summary>
    public static readonly UTF8Encoding OutputEncoding = new(encoderShouldEmitUTF8Identifier: false);

    public CodeWriter Line()
    {
        _builder.Append('\n');
        return this;
    }

    public CodeWriter Line(string text)
    {
        if (text.Length > 0)
        {
            for (var i = 0; i < _indent; i++)
            {
                _builder.Append(IndentUnit);
            }

            _builder.Append(text);
        }

        _builder.Append('\n');
        return this;
    }

    public CodeWriter Lines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            Line(line);
        }

        return this;
    }

    /// <summary>Opens a braced block, closed with <paramref name="closing"/> when disposed.</summary>
    public IDisposable Block(string header, string closing = "}")
    {
        Line(header);
        Line("{");
        _indent++;
        return new BlockScope(this, closing);
    }

    /// <summary>Writes an XML documentation comment, escaped and normalized.</summary>
    public CodeWriter XmlDoc(string tag, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return this;
        }

        var normalized = XmlDocWriter.Normalize(content);
        var lines = normalized.Split('\n');

        // The opening tag may carry attributes, as in `param name="request"`. The closing tag
        // must use the element name alone or the XML is malformed.
        var space = tag.IndexOf(' ', StringComparison.Ordinal);
        var elementName = space < 0 ? tag : tag[..space];

        if (lines.Length == 1)
        {
            return Line($"/// <{tag}>{lines[0]}</{elementName}>");
        }

        Line($"/// <{tag}>");
        foreach (var line in lines)
        {
            Line($"/// {line}");
        }

        return Line($"/// </{elementName}>");
    }

    public static string Literal(string value)
        => "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => _builder.ToString();

    /// <summary>Writes the file only when its content actually changes, keeping timestamps stable.</summary>
    public static bool WriteIfChanged(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path, OutputEncoding);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return false;
            }
        }

        File.WriteAllText(path, content, OutputEncoding);
        return true;
    }

    private sealed class BlockScope(CodeWriter writer, string closing) : IDisposable
    {
        public void Dispose()
        {
            writer._indent--;
            writer.Line(closing);
        }
    }
}
