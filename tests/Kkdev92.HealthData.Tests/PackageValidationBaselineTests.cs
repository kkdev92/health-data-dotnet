using System.Diagnostics;
using System.Xml.Linq;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The absent package-validation baseline is a decision about one version, not a habit.
/// </summary>
/// <remarks>
/// <para>
/// <c>PackageValidationBaselineVersion</c> is what compares the assemblies being packed against
/// the last version on nuget.org, so a binary breaking change fails the pack rather than reaching
/// a consumer. It is deliberately absent for 0.2.0-alpha: that release reshaped the whole surface
/// on the first real consumer's feedback, and comparing against 0.1.0-alpha would have produced
/// hundreds of intentional CP0002s whose only cure is a suppression file nobody reads.
/// </para>
/// <para>
/// The risk is not the decision, it is inheriting it. A note in a comment saying "restore this
/// when you publish" is exactly the kind of instruction that is still there three releases later,
/// with nothing having compared anything in the meantime — so the build refuses to go past the
/// version the exemption was written for.
/// </para>
/// </remarks>
public sealed class PackageValidationBaselineTests
{
    private static string PropsPath => Path.Combine(RepositoryRoot.Value, "src", "Directory.Build.props");

    private static string? Property(string name)
        => XDocument.Load(PropsPath).Descendants(name).FirstOrDefault()?.Value;

    [Fact]
    public void TheExemptionNamesTheVersionItWasWrittenFor()
    {
        // If these ever disagree, the exemption has silently widened to cover a release nobody
        // decided about.
        Assert.Equal(Property("LastVersionWithoutBaseline"), Property("VersionPrefix"));
    }

    [Fact]
    public void TheBaselineIsAbsentAndThatIsOnPurpose()
    {
        Assert.Null(Property("PackageValidationBaselineVersion"));

        // Absent, not disabled: everything package validation does that is not a comparison —
        // framework compatibility, package structure — is still running.
        Assert.Equal("true", Property("EnablePackageValidation"));
    }

    /// <summary>
    /// The guard actually stops a build, rather than being a property nobody reads.
    /// </summary>
    /// <remarks>
    /// Run as a build rather than asserted from the file, because what matters is the behaviour:
    /// an MSBuild condition can be written so it never evaluates true, and reading it would not
    /// say so. Both directions are checked — past the exemption it fails, and with a baseline set
    /// the same version builds.
    ///
    /// The baseline used here has to be a version that is actually on nuget.org. Package
    /// validation restores it, so an unpublished one fails the build with NU1102 and the test
    /// would pass for the wrong reason on a machine that happens to have it cached.
    /// </remarks>
    [Fact]
    public void PastThatVersionTheBuildStopsUntilABaselineIsSet()
    {
        var project = Path.Combine(RepositoryRoot.Value, "src", "Kkdev92.HealthData");

        var withoutBaseline = Build(project, "-p:VersionPrefix=99.0.0");

        Assert.NotEqual(0, withoutBaseline.ExitCode);
        Assert.Contains("PackageValidationBaselineVersion", withoutBaseline.Output, StringComparison.Ordinal);

        // 0.1.0-alpha, because it is on nuget.org. Naming a version that is not yet published
        // fails with NU1102 rather than passing the guard — which is what happened first, and only
        // in CI: this machine had the package cached from a local pack and the restore never went
        // out. The guard is what is under test, so the baseline has to be one that resolves.
        var withBaseline = Build(project, "-p:VersionPrefix=99.0.0", "-p:PackageValidationBaselineVersion=0.1.0-alpha");

        Assert.Equal(0, withBaseline.ExitCode);
    }

    private static (int ExitCode, string Output) Build(string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot.Value,
        };

        start.ArgumentList.Add("build");
        start.ArgumentList.Add(project);

        // A separate output path, so this cannot disturb the build the rest of the suite reads.
        start.ArgumentList.Add("-p:BaseOutputPath=" + Path.Combine(Path.GetTempPath(), "healthdata-baseline-probe") + Path.DirectorySeparatorChar);

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }
}
