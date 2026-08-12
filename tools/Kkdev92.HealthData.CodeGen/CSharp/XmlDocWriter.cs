using System.Text;
using System.Text.RegularExpressions;

namespace Kkdev92.HealthData.CodeGen.CSharp;

/// <summary>
/// Prepares Discovery descriptions for embedding as XML documentation.
/// </summary>
/// <remarks>
/// <para>
/// Discovery text is prose written for a website, not for a C# doc comment. It contains raw angle
/// brackets, ampersands, Markdown, and occasional very long paragraphs. Embedding it unprocessed
/// produces invalid XML; embedding it with the Markdown left in produces IntelliSense full of
/// backticks and asterisks, which is what a reader sees on hover.
/// </para>
/// <para>
/// So the Markdown is translated rather than stripped: <c>`code`</c> becomes <c>&lt;c&gt;</c>,
/// <c>**bold**</c> becomes <c>&lt;b&gt;</c>, and a link keeps its address in a
/// <c>&lt;see href&gt;</c> instead of losing it. The order matters — escaping happens before the
/// tags go in, or the tags would be escaped too.
/// </para>
/// <para>
/// Lists are deliberately left alone, and it is worth saying why, because the reason is not the
/// obvious one. It is not that the text is Google's — converting <c>* item</c> into
/// <c>&lt;list&gt;</c> changes markup, not words. It is that a Discovery description is a single
/// line with no paragraph breaks, so nothing in it says where a list <em>ends</em>.
/// </para>
/// <para>
/// Measured, not assumed. A conservative converter — explicit <c>*</c> and numbered markers only,
/// never inferred from a hyphen, with a test proving no word was added, dropped or reordered —
/// produced this from the <c>Date</c> description: "…a credit card expiration date). Related
/// types:" became the last item of the list, and the three type names that follow joined the same
/// list rather than starting their own. Every word survived and the meaning did not. On
/// <c>subscribers.create</c> it flattened a two-step numbered procedure into one run of bullets.
/// </para>
/// <para>
/// So the markers stay. A reader sees an asterisk where a bullet was meant, which is a small cost
/// next to a list that says something the source did not. The same reasoning already applies to
/// the hyphen: an earlier attempt turned "sending it back - which is what a patch is - would
/// delete it" into a two-item list.
/// </para>
/// </remarks>
internal static partial class XmlDocWriter
{
    private const int MaxLineLength = 100;

    /// <summary>Placeholders that survive escaping, swapped for real tags at the end.</summary>
    private const char OpenMark = '';
    private const char CloseMark = '';

    public static string Normalize(string text)
    {
        // Whitespace first: the placeholders below are control characters, and collapsing turns
        // control characters into spaces.
        var collapsed = CollapseWhitespace(text);
        var linked = ConvertLinks(collapsed);
        var marked = ConvertInlineMarkdown(linked);
        var escaped = Escape(marked);

        return Wrap(RestoreTags(escaped));
    }

    /// <summary>
    /// Turns <c>[label](url)</c> into a link that keeps the address.
    /// </summary>
    /// <remarks>
    /// The address used to be discarded, which turned a reference into a dangling phrase — the
    /// reader was told to see something with no way to reach it.
    /// </remarks>
    private static string ConvertLinks(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf('[', index);

            if (open < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var close = text.IndexOf(']', open);

            if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
            {
                builder.Append(text, index, close < 0 ? text.Length - index : close - index + 1);
                index = close < 0 ? text.Length : close + 1;
                continue;
            }

            var urlEnd = text.IndexOf(')', close);

            if (urlEnd < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var label = text[(open + 1)..close];
            var url = text[(close + 2)..urlEnd].Trim();

            builder.Append(text, index, open - index);

            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(OpenMark).Append("see href=\"").Append(url).Append('"').Append(CloseMark);
                builder.Append(label);
                builder.Append(OpenMark).Append("/see").Append(CloseMark);
            }
            else
            {
                // A relative or in-page target means nothing outside the Discovery site.
                builder.Append(label);
            }

            index = urlEnd + 1;
        }

        return builder.ToString();
    }

    /// <summary>Marks up inline code and bold, using placeholders that escaping leaves alone.</summary>
    private static string ConvertInlineMarkdown(string text)
    {
        text = BoldPattern().Replace(
            text,
            m => $"{OpenMark}b{CloseMark}{m.Groups[1].Value}{OpenMark}/b{CloseMark}");

        text = CodePattern().Replace(
            text,
            m => $"{OpenMark}c{CloseMark}{m.Groups[1].Value}{OpenMark}/c{CloseMark}");

        // A backtick that was not part of a pair would render as itself.
        return text.Replace("`", string.Empty, StringComparison.Ordinal);
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;

        foreach (var c in text)
        {
            var isSpace = char.IsWhiteSpace(c);

            if (isSpace)
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                // Lone surrogates and control characters would produce invalid XML.
                builder.Append(char.IsControl(c) ? ' ' : c);
            }

            previousWasSpace = isSpace;
        }

        return builder.ToString().TrimEnd();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string RestoreTags(string text) => text
        .Replace(OpenMark, '<')
        .Replace(CloseMark, '>');

    /// <summary>Wraps on word boundaries so that generated diffs stay readable.</summary>
    /// <remarks>
    /// Never inside a tag: a line break between <c>&lt;see</c> and its <c>href</c> would produce
    /// something that is still valid XML and no longer a link.
    /// </remarks>
    private static string Wrap(string text)
    {
        if (text.Length <= MaxLineLength)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        var lineLength = 0;
        var depth = 0;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (depth == 0 && lineLength > 0 && lineLength + 1 + word.Length > MaxLineLength)
            {
                builder.Append('\n');
                lineLength = 0;
            }
            else if (lineLength > 0)
            {
                builder.Append(' ');
                lineLength++;
            }

            builder.Append(word);
            lineLength += word.Length;

            depth += word.Count(c => c == '<') - word.Count(c => c == '>');
            depth = Math.Max(depth, 0);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.Singleline)]
    private static partial Regex CodePattern();
}
