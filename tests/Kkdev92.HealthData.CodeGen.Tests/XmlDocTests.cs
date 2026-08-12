using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kkdev92.HealthData.CodeGen.CSharp;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// What Discovery prose has to become before it is a doc comment.
/// </summary>
/// <remarks>
/// The text is written for a web page: Markdown, links, bullets, angle brackets. Left alone it
/// either breaks the XML or reaches IntelliSense as backticks and asterisks, which is what a
/// reader sees on hover. These pin the translation rather than the prose.
/// </remarks>
public sealed class XmlDocTests
{
    [Theory]
    [InlineData("Values for `heart_rate`.", "Values for <c>heart_rate</c>.")]
    [InlineData("This is **required**.", "This is <b>required</b>.")]
    [InlineData("A `a` and `b` pair.", "A <c>a</c> and <c>b</c> pair.")]
    public void MarkdownBecomesTags(string input, string expected)
        => Assert.Equal(expected, XmlDocWriter.Normalize(input));

    /// <summary>A link keeps its address rather than becoming a phrase pointing nowhere.</summary>
    [Fact]
    public void LinksKeepTheirAddress()
    {
        var result = XmlDocWriter.Normalize("See [AIP-160](https://google.aip.dev/160) for syntax.");

        Assert.Contains("<see href=\"https://google.aip.dev/160\">AIP-160</see>", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dash in the middle of a sentence stays a dash.
    /// </summary>
    /// <remarks>
    /// Discovery descriptions arrive as one line with list items already inline, so nothing tells
    /// a bullet apart from an aside. Guessing from the dashes turned "sending it back - which is
    /// what a patch is - would delete it" into a two-item list, which says something the source
    /// did not. A hyphen a reader can ignore beats a sentence rearranged.
    /// </remarks>
    [Theory]
    [InlineData("Applies to: - `user` - `data_type` The rest follows.")]
    [InlineData("sending it back - which is what a patch is - would delete it")]
    public void DashesAreLeftAlone(string input)
    {
        var result = XmlDocWriter.Normalize(input);

        Assert.DoesNotContain("<list", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<item>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AngleBracketsAndAmpersandsAreEscaped()
    {
        var result = XmlDocWriter.Normalize("Use <T> & friends.");

        Assert.Equal("Use &lt;T&gt; &amp; friends.", result);
    }

    /// <summary>
    /// No generated doc comment carries an inline Markdown marker — a backtick or a bold pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit cases above cover the translation; this covers the corpus, which is where a shape
    /// nobody anticipated turns up. Bare URLs are allowed: several descriptions cite an AIP or a
    /// scope as plain text rather than as a link, and inventing a link around them would be
    /// asserting something the source did not say.
    /// </para>
    /// <para>
    /// <em>Inline</em> markers, which is narrower than the name this used to have. List markers —
    /// <c>*</c> and numbered items — do survive into the output, on purpose and by a decision
    /// recorded on <see cref="XmlDocWriter"/>. The name is narrow now so that it describes what is
    /// checked: a test called "carries no Markdown" that looks at two of the four kinds asserts
    /// less than its name promises.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoGeneratedDocCommentCarriesInlineMarkdown()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot.Value, "src"), "*.g.cs", SearchOption.AllDirectories))
        {
            var number = 0;

            foreach (var line in File.ReadLines(file))
            {
                number++;

                if (!line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains('`', StringComparison.Ordinal) || line.Contains("**", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number} {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(10)));
    }

    /// <summary>Every generated doc comment has to parse as XML, tags and all.</summary>
    [Fact]
    public void EveryGeneratedDocCommentIsWellFormed()
    {
        var broken = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot.Value, "src"), "*.g.cs", SearchOption.AllDirectories))
        {
            var block = new List<string>();

            foreach (var line in File.ReadLines(file).Append(string.Empty))
            {
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    block.Add(trimmed[3..].Trim());
                    continue;
                }

                if (block.Count > 0)
                {
                    try
                    {
                        XElement.Parse("<doc>" + string.Join(" ", block) + "</doc>");
                    }
                    catch (Exception exception)
                    {
                        broken.Add($"{Path.GetFileName(file)}: {exception.Message}");
                    }

                    block.Clear();
                }
            }
        }

        Assert.True(broken.Count == 0, string.Join("\n", broken.Take(5)));
    }
}
