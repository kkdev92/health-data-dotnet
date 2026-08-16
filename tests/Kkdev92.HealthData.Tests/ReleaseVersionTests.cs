using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The version being built, the newest changelog entry and the package-validation baseline agree.
/// </summary>
/// <remarks>
/// <para>
/// Three numbers are kept by hand in three files, and each one is only correct relative to the
/// others. <c>VersionPrefix</c> says what is being built, the changelog says what shipped, and
/// <c>PackageValidationBaselineVersion</c> says what the assemblies are compared against — which
/// has to be the release immediately before this one, or the comparison is answering a question
/// nobody asked.
/// </para>
/// <para>
/// Nothing was checking any of it. <c>## [0.2.0-alpha] - unreleased</c> sat in the changelog after
/// that version had been on nuget.org for a day, and a stale baseline is worse because it is
/// invisible: the pack still succeeds, it just compares against an older surface than the one a
/// consumer is upgrading from, and re-reports differences that were deliberate two releases ago
/// until somebody writes a suppression file to quiet them.
/// </para>
/// <para>
/// Read from the changelog rather than from nuget.org on purpose. The authoritative answer is what
/// is published, but a test that asks the network is a test that fails on a train, and the restore
/// already refuses a baseline that is not published — <c>NU1102</c>, at pack time, loudly. What is
/// left uncovered is the baseline that exists but is not the latest, and the changelog knows that
/// offline.
/// </para>
/// </remarks>
public sealed partial class ReleaseVersionTests
{
    private static string PropsPath => Path.Combine(RepositoryRoot.Value, "src", "Directory.Build.props");

    private static string? Property(string name)
        => XDocument.Load(PropsPath).Descendants(name).FirstOrDefault()?.Value;

    /// <summary>The version this build produces, spelled the way a package file name spells it.</summary>
    private static string BuiltVersion
    {
        get
        {
            var prefix = Property("VersionPrefix");
            var suffix = Property("VersionSuffix");

            Assert.False(string.IsNullOrWhiteSpace(prefix), "VersionPrefix is not set.");

            return string.IsNullOrWhiteSpace(suffix) ? prefix! : $"{prefix}-{suffix}";
        }
    }

    /// <summary>
    /// The released versions the changelog names, newest first.
    /// </summary>
    /// <remarks>
    /// <c>[Unreleased]</c> is skipped: it is a heading for work that has not shipped, and treating
    /// it as a release would make the newest entry disagree with everything on the first commit
    /// after a release.
    /// </remarks>
    private static IReadOnlyList<(string Version, string Date)> Released()
    {
        var changelog = File.ReadAllText(Path.Combine(RepositoryRoot.Value, "CHANGELOG.md"));

        return
        [
            .. ReleaseHeading().Matches(changelog)
                .Select(match => (match.Groups["version"].Value, match.Groups["date"].Value))
                .Where(entry => !entry.Item1.Equals("Unreleased", StringComparison.OrdinalIgnoreCase))
        ];
    }

    [GeneratedRegex(@"^## \[(?<version>[^\]]+)\](?: - (?<date>\S+))?", RegexOptions.Multiline)]
    private static partial Regex ReleaseHeading();

    [Fact]
    public void TheChangelogsNewestEntryIsTheVersionBeingBuilt()
    {
        var released = Released();

        Assert.NotEmpty(released);
        Assert.Equal(BuiltVersion, released[0].Version);
    }

    /// <summary>
    /// The newest entry carries a date, because the version it names has shipped.
    /// </summary>
    /// <remarks>
    /// <c>VersionPrefix</c> is bumped in the release commit rather than ahead of one, so the entry
    /// it matches is always a release that went out — there is no window in which the newest entry
    /// is legitimately undated. Work in progress belongs under <c>[Unreleased]</c>, which this does
    /// not look at.
    /// </remarks>
    [Fact]
    public void TheNewestEntryIsDated()
    {
        var (version, date) = Released()[0];

        Assert.True(
            DateOnly.TryParseExact(date, "yyyy-MM-dd", out _),
            $"The changelog entry for {version} reads '{date}'. It is the version being built, so it "
            + "has shipped and the date it shipped is what belongs there — UTC, as the preamble says.");
    }

    /// <summary>
    /// The baseline names the release immediately before the one being built.
    /// </summary>
    /// <remarks>
    /// This is the one that will actually be got wrong. Bumping the version and writing the
    /// changelog entry are what a release feels like; moving the baseline is a third edit in a
    /// third file that changes nothing anybody can see, and leaving it behind costs nothing until
    /// the release after that.
    /// </remarks>
    [Fact]
    public void TheBaselineNamesThePreviousRelease()
    {
        var released = Released();

        Assert.True(released.Count >= 2, "There is only one release, so there is nothing to compare against.");

        var baseline = Property("PackageValidationBaselineVersion");
        var previous = released[1].Version;

        Assert.Equal(previous, baseline);
    }
}
