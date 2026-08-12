using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Asserts that a packed package contains only what it is supposed to contain.
/// </summary>
/// <remarks>
/// <para>
/// <c>.gitignore</c> does not participate in packing. NuGet packs MSBuild items, so a file kept
/// out of git is not thereby kept out of the package: <c>Content</c> items are packed
/// automatically because <c>IncludeContentInPack</c> defaults to true, and any item can be packed
/// with <c>Pack="true"</c>. Working notes, agent configuration and anything else deliberately
/// untracked would be published without a word of warning.
/// </para>
/// <para>
/// So the guarantee is asserted here rather than assumed. The allowlist is the real protection:
/// anything not on it fails, whatever it happens to be. Scanning the contents for a build
/// machine's absolute path is defence in depth, for the case where a permitted file grows a
/// reference it should not have — a pdb path recorded in a debug directory, most often.
/// </para>
/// <para>
/// This runs against the output of <c>dotnet pack</c>, so it is excluded from the ordinary test
/// run and executed as its own step after packing. It skips rather than fails when no package is
/// present, so nobody is forced to pack before running the suite locally.
/// </para>
/// </remarks>
[Trait("Category", "Package")]
public sealed partial class PackageContentTests
{
    /// <summary>
    /// Exactly what each package carries, named rather than matched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was a list of patterns, and it said "a new file cannot slip in under one" while
    /// permitting any <c>*.nuspec</c> at the root and any <c>*.dll</c>, <c>*.xml</c> or
    /// <c>*.pdb</c> under <c>lib/net10.0/</c> — so a second assembly, or a resource file beside
    /// the intended one, was inside the allowlist. The claim and the check disagreed.
    /// </para>
    /// <para>
    /// The set is small and known, so it is written out. A package that grows an entry fails here
    /// and somebody decides whether it belongs, which is the whole point of having the test.
    /// </para>
    /// </remarks>
    private static string[] ExpectedEntries(string packageId) =>
    [
        $"{packageId}.nuspec",
        "README.md",
        "NOTICE",
        "LICENSE",
        "[Content_Types].xml",
        "_rels/.rels",
        $"lib/net10.0/{packageId}.dll",
        $"lib/net10.0/{packageId}.xml",
    ];

    /// <summary>The one entry NuGet names for itself, matched by location rather than by name.</summary>
    /// <remarks>
    /// This used to require a hexadecimal name, which held on the 10.0.3xx SDK and failed on
    /// 10.0.4xx, where the name is the constant <c>nuget.psmdcp</c> instead. Because
    /// <c>global.json</c> rolls forward by feature band, that was a check no local run could fail
    /// and no CI run could pass. The part is identified by the OPC directory it has to live in, so
    /// whatever NuGet calls the file next is already covered — and the caller still asserts there
    /// is exactly one of them.
    /// </remarks>
    [GeneratedRegex(@"^package/services/metadata/core-properties/[^/]+\.psmdcp$")]
    private static partial Regex OpcCoreProperties();

    /// <summary>The package id, taken from the file name rather than guessed.</summary>
    [GeneratedRegex(@"^(?<id>.+?)\.\d+\.\d+\.\d+.*$")]
    private static partial Regex PackageFileName();

    /// <summary>
    /// An absolute path belonging to the machine that built the package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By shape, not by name. This used to list the directories that happen to sit beside this
    /// repository — working notes, a tool's configuration, a benchmark output folder — which was
    /// both narrower and wider than the thing worth catching. Narrower, because a build on any
    /// other machine leaks <c>D:\src\…</c> and no name on that list would have noticed. Wider,
    /// because such a name can only reach a package's <em>contents</em> as part of an absolute
    /// path, and the allowlist above already governs its entries — so naming them added nothing
    /// this does not cover, while writing somebody's directory layout into a public file.
    /// </para>
    /// <para>
    /// A drive-lettered Windows path, a UNC share, or an absolute Unix path under a directory a
    /// build tree lives in all mean the same thing: something recorded about where the build ran
    /// rather than what it produced. For a build that claims to be deterministic that is a defect
    /// whoever's machine it names.
    /// </para>
    /// <para>
    /// The Windows half accepts either separator, because a path recorded by a cross-platform tool
    /// is as likely to arrive as <c>C:/src/…</c>. What it must not match is <c>/_/</c>, which is
    /// what a deterministic build normalises to, or an ordinary URL.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"[A-Za-z]:[\\/][^\\/:*?""<>|\r\n]+[\\/]"
        + @"|\\\\[A-Za-z0-9._-]+\\[A-Za-z0-9._$-]+\\"
        + @"|(?<![A-Za-z0-9.])/(?:home|Users|root|tmp|workspace|var/lib)/[A-Za-z0-9._-]+/")]
    private static partial Regex MachinePath();

    /// <summary>
    /// What counts as a build machine's path, and what does not.
    /// </summary>
    /// <remarks>
    /// The detector had no test of its own: a clean artefact passing said nothing about whether it
    /// could still find anything. These run without a package, so a change to the pattern fails
    /// here rather than the next time somebody packs locally by accident.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Projects\health-data-dotnet\src\obj\x.pdb")]
    [InlineData(@"D:\src\whatever\bin\x.pdb")]
    [InlineData(@"C:/src/whatever/bin/x.pdb")]              // a cross-platform tool writes it this way
    [InlineData(@"\\build01\share\src\x.pdb")]              // a UNC share
    [InlineData("/home/runner/work/repo/x.pdb")]
    [InlineData("/Users/alice/dev/repo/x.pdb")]
    [InlineData("/root/build/repo/x.pdb")]
    [InlineData("/tmp/build-1234/repo/x.pdb")]
    [InlineData("/workspace/repo/x.pdb")]
    public void AMachinePathIsRecognised(string text)
        => Assert.True(MachinePath().IsMatch(text), $"'{text}' should have been recognised.");

    [Theory]
    [InlineData("/_/src/Kkdev92.HealthData/Client.cs")]     // what a deterministic build records
    [InlineData("lib/net10.0/Kkdev92.HealthData.dll")]
    [InlineData("https://health.googleapis.com/v4/users/me")]
    [InlineData("users/me/dataTypes/heart-rate/dataPoints/1")]
    [InlineData("See https://developers.google.com/health for details.")]
    [InlineData("The ratio is 3:1 and rising")]
    public void OrdinaryTextIsNotAMachinePath(string text)
        => Assert.False(MachinePath().IsMatch(text), $"'{text}' should not have been recognised.");

    [Theory]
    [InlineData("package/services/metadata/core-properties/nuget.psmdcp")]                  // 10.0.4xx
    [InlineData("package/services/metadata/core-properties/fdec5d6143fe4aec8ecb67fd73335f44.psmdcp")]
    public void NuGetsOwnMetadataPartIsRecognisedWhateverItIsCalled(string name)
        => Assert.True(OpcCoreProperties().IsMatch(name), $"'{name}' should have been recognised.");

    [Theory]
    [InlineData("lib/net10.0/Kkdev92.HealthData.dll")]
    [InlineData("package/services/metadata/core-properties/nested/nuget.psmdcp")]
    [InlineData("some/other/place/nuget.psmdcp")]
    [InlineData("package/services/metadata/core-properties/nuget.nuspec")]
    public void NothingElseIsMistakenForIt(string name)
        => Assert.False(OpcCoreProperties().IsMatch(name), $"'{name}' should not have been recognised.");

    /// <summary>
    /// The packages to inspect, from wherever the last pack put them.
    /// </summary>
    /// <remarks>
    /// Searched rather than assumed. These tests skip when they find nothing, so a pack sent to
    /// some other output directory used to leave them reporting success while inspecting nothing
    /// at all — which is worse than failing, because the run says the packages are clean.
    /// </remarks>
    private static string[] Packages()
    {
        string[] roots =
        [
            Path.Combine(RepositoryRoot.Value, "artifacts"),
            Path.Combine(RepositoryRoot.Value, "artifacts", "package", "release"),
            RepositoryRoot.Value,
        ];

        foreach (var root in roots.Where(Directory.Exists))
        {
            var found = Directory
                .EnumerateFiles(root, "*.nupkg", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "*.snupkg", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}local-feed{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToArray();

            if (found.Length > 0)
            {
                return found;
            }
        }

        return [];
    }

    [Fact]
    public void EveryPackageCarriesExactlyTheExpectedEntries()
    {
        var packages = Packages()
            .Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            var name = Path.GetFileNameWithoutExtension(package);
            var match = PackageFileName().Match(name);

            Assert.True(match.Success, $"'{name}' is not a package file name.");

            var packageId = match.Groups["id"].Value;

            using var archive = ZipFile.OpenRead(package);

            var actual = archive.Entries
                .Select(e => e.FullName)
                .Where(n => !OpcCoreProperties().IsMatch(n))
                .Order(StringComparer.Ordinal)
                .ToArray();

            var expected = ExpectedEntries(packageId).Order(StringComparer.Ordinal).ToArray();

            Assert.True(
                actual.SequenceEqual(expected, StringComparer.Ordinal),
                $"{Path.GetFileName(package)} does not carry exactly what it should.\n"
                + $"  unexpected: {string.Join(", ", actual.Except(expected, StringComparer.Ordinal))}\n"
                + $"  missing:    {string.Join(", ", expected.Except(actual, StringComparer.Ordinal))}");

            // The hashed one, exactly once.
            Assert.Single(archive.Entries, e => OpcCoreProperties().IsMatch(e.FullName));
        }
    }

    [Fact]
    public void NoPackedEntryMentionsALocalOnlyPath()
    {
        var packages = Packages();
        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);

            foreach (var entry in archive.Entries)
            {
                // The name as well as the contents. An entry called after a build directory would
                // be caught by the exact-entry test above, but only for a .nupkg — the symbol
                // packages go through this test alone.
                if (MachinePath().Match(entry.FullName) is { Success: true } inName)
                {
                    Assert.Fail(
                        $"{Path.GetFileName(package)} has an entry named after the build machine's "
                        + $"path '{inName.Value}': '{entry.FullName}'.");
                }

                // Binaries are scanned too: a leaked path most often arrives compiled in, through
                // a source path or an embedded resource, rather than as a visible file.
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var text = Encoding.UTF8.GetString(buffer.ToArray());

                if (MachinePath().Match(text) is { Success: true } leaked)
                {
                    Assert.Fail(
                        $"{Path.GetFileName(package)} entry '{entry.FullName}' carries the build "
                        + $"machine's path '{leaked.Value}'. {Hint(entry.FullName)}");
                }
            }
        }
    }

    /// <summary>
    /// Explains the failure most likely to be seen, so it is actionable rather than alarming.
    /// </summary>
    /// <remarks>
    /// A build without <c>ContinuousIntegrationBuild</c> records the absolute path of the pdb in
    /// the assembly's debug directory, which on a maintainer's machine spells out the local
    /// directory layout. It is normalised to <c>/_/</c> only when the property is set, and this
    /// repository sets it from the <c>CI</c> environment variable, so a local
    /// <c>dotnet pack</c> produces a package that must not be published.
    /// </remarks>
    private static string Hint(string entryName)
        => entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || entryName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            ? "This is usually the pdb path in the debug directory of a build made without "
              + "ContinuousIntegrationBuild. Rebuild and repack with CI=true, which is what the "
              + "release workflow does, rather than publishing this artefact."
            : string.Empty;

    /// <summary>
    /// Every package carries its legal files, and they are the ones in the repository.
    /// </summary>
    /// <remarks>
    /// The allowlist above says these <em>may</em> be present, which is a different statement:
    /// deleting the pack settings that put them there would leave every package legally
    /// incomplete and every test still green. Content is compared as well as presence, because a
    /// license that has drifted from the one the repository publishes under is the same problem
    /// as a missing one.
    /// </remarks>
    [Fact]
    public void EveryPackageCarriesTheLegalFilesFromTheRepository()
    {
        var packages = Packages().Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        // NOTICE carries the attribution for the documentation text the generated doc comments
        // reproduce, and LICENSE is the license the metadata names. A package without either is
        // not correctly licensed, whatever the nuspec says.
        // README.md is on this list because it is the only place a consumer looking at nuget.org
        // is told that some of what ships is Google's under CC BY. Checking it exists would not
        // notice that paragraph being edited away.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LICENSE"] = ReadRepositoryFile("LICENSE"),
            ["NOTICE"] = ReadRepositoryFile("NOTICE"),
            ["README.md"] = ReadRepositoryFile(Path.Combine("eng", "PACKAGE-README.md")),
        };

        static string ReadRepositoryFile(string relativePath)
            => File.ReadAllText(Path.Combine(RepositoryRoot.Value, relativePath)).ReplaceLineEndings("\n");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);
            var names = archive.Entries.Select(e => e.FullName).ToArray();

            foreach (var (name, original) in expected)
            {
                Assert.True(names.Contains(name), $"{Path.GetFileName(package)} carries no {name}.");

                using var reader = new StreamReader(archive.GetEntry(name)!.Open());

                Assert.Equal(original, reader.ReadToEnd().ReplaceLineEndings("\n"));
            }
        }
    }

    [Fact]
    public void ThePackagedReadmeHasNoRelativeLinks()
    {
        var packages = Packages().Where(p => p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.SkipWhen(packages.Length == 0, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in packages)
        {
            using var archive = ZipFile.OpenRead(package);
            using var reader = new StreamReader(archive.GetEntry("README.md")!.Open());
            var markdown = reader.ReadToEnd();

            // nuget.org resolves a relative link against nuget.org, so every one of them is a 404
            // on the package page. This is why the packaged readme is a separate file.
            var relative = System.Text.RegularExpressions.Regex
                .Matches(markdown, @"\]\(([^)]+)\)")
                .Select(m => m.Groups[1].Value)
                .Where(t => !t.StartsWith("http://", StringComparison.Ordinal)
                         && !t.StartsWith("https://", StringComparison.Ordinal)
                         && !t.StartsWith('#'))
                .ToArray();

            Assert.Empty(relative);
        }
    }
}
