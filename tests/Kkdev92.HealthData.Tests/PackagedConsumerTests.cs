using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Kkdev92.HealthData.Tests;

/// <summary>
/// Builds and runs a project that has never seen this repository, against the packed packages.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in the suite reaches the code through a <c>ProjectReference</c>, which resolves
/// whatever is on disk and papers over the questions that only a package can answer: whether the
/// nuspec declares its dependencies, whether the assemblies landed under a <c>lib</c> folder the
/// consumer's framework matches, and whether the four packages can be installed together. A
/// consumer finding out is the wrong place for any of that.
/// </para>
/// <para>
/// The package cache is isolated to a temporary directory, so a copy of the same version left in
/// the global cache by an earlier run cannot stand in for the one just built. The feed list is
/// cleared before the local folder is added, so the SDK packages can only come from the artefacts
/// under test; nuget.org stays for the transitive <c>Microsoft.Extensions.*</c>.
/// </para>
/// <para>
/// A restore, a build and a run — because compiling proves the reference resolved and running
/// proves the assemblies load, which are different claims.
/// </para>
/// </remarks>
[Trait("Category", "Package")]
public sealed partial class PackagedConsumerTests
{
    [Fact]
    public void AProjectThatOnlyHasThePackagesBuildsAndRuns()
    {
        var feed = FeedDirectory();
        Assert.SkipWhen(feed is null, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        var packages = Directory
            .EnumerateFiles(feed!, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => PackageName().Match(name!))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["version"].Value, StringComparer.Ordinal);

        string[] expected =
        [
            "Kkdev92.HealthData",
            "Kkdev92.HealthData.Authentication",
            "Kkdev92.HealthData.DependencyInjection",
            "Kkdev92.HealthData.Webhooks",
        ];

        foreach (var id in expected)
        {
            Assert.True(packages.ContainsKey(id), $"{id} was not packed into {feed}.");
        }

        var version = packages[expected[0]];
        Assert.All(expected, id => Assert.Equal(version, packages[id]));

        var workspace = Directory.CreateTempSubdirectory("healthdata-consumer");

        try
        {
            WriteNuGetConfig(workspace, feed!);

            Write(workspace, "Consumer.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Kkdev92.HealthData" Version="{version}" />
                    <PackageReference Include="Kkdev92.HealthData.Authentication" Version="{version}" />
                    <PackageReference Include="Kkdev92.HealthData.DependencyInjection" Version="{version}" />
                    <PackageReference Include="Kkdev92.HealthData.Webhooks" Version="{version}" />
                  </ItemGroup>
                </Project>
                """);

            // One type from each package, so that a package which restored but shipped no usable
            // assembly fails to compile rather than passing quietly.
            Write(workspace, "Program.cs", """
                using Kkdev92.HealthData;
                using Kkdev92.HealthData.Authentication;
                using Kkdev92.HealthData.Authentication.OAuth;
                using Kkdev92.HealthData.DependencyInjection;
                using Kkdev92.HealthData.Webhooks;
                using Microsoft.Extensions.DependencyInjection;

                var services = new ServiceCollection();
                services.AddHealthDataAccessToken("ya29.token");
                services.AddHealthData();

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();

                var client = scope.ServiceProvider.GetRequiredService<HealthDataClient>();

                var oauth = new GoogleOAuthClient(
                    new HttpClient(),
                    new GoogleOAuthOptions
                    {
                        ClientId = "client-123.apps.googleusercontent.com",
                        RedirectUri = new Uri("https://example.test/callback"),
                    });

                Console.WriteLine(HealthDataApiMetadata.DefaultBaseAddress);
                Console.WriteLine(client.Users is not null);
                Console.WriteLine(oauth.CreateAuthorizationUrl(
                    new GoogleAuthorizationUrlOptions { Scopes = HealthDataScopes.ReadOnly }).Host);
                Console.WriteLine(HealthDataWebhookReceiver.VerificationUserAgent);
                Console.WriteLine(HealthDataWebhookKeyProvider.DefaultKeysetUri.Host);
                Console.WriteLine("consumer ok");
                """);

            var cache = Path.Combine(workspace.FullName, ".packages");

            var run = Dotnet($"run --project \"{workspace.FullName}\" -c Release", workspace.FullName, cache);

            Assert.True(run.ExitCode == 0, $"The packaged consumer failed to build or run:\n{run.Output}");
            Assert.Contains("consumer ok", run.Output, StringComparison.Ordinal);
            Assert.Contains("health.googleapis.com", run.Output, StringComparison.Ordinal);

            // The assets file is where "it resolved from a package" stops being an assumption. A
            // project reference, or a package quietly served from somewhere else, shows up here.
            var assets = File.ReadAllText(Path.Combine(workspace.FullName, "obj", "project.assets.json"));

            foreach (var id in expected)
            {
                Assert.Contains($"\"{id}/{version}\"", assets, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("\"type\": \"project\"", assets, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    /// <summary>
    /// Each satellite package on its own, with nothing else referenced.
    /// </summary>
    /// <remarks>
    /// The test above references all four, which is how a package that forgot to declare its
    /// dependency on the core still compiles: another <c>PackageReference</c> brought it in. One
    /// package at a time is the only arrangement in which the nuspec has to be right.
    /// </remarks>
    [Theory]
    [InlineData("Kkdev92.HealthData.Authentication", "Kkdev92.HealthData.Authentication", "typeof(StaticAccessTokenProvider)")]
    [InlineData("Kkdev92.HealthData.DependencyInjection", "Kkdev92.HealthData.DependencyInjection", "typeof(HealthDataBuilderOptions)")]
    [InlineData("Kkdev92.HealthData.Webhooks", "Kkdev92.HealthData.Webhooks", "typeof(HealthDataWebhookReceiver)")]
    public void EachSatellitePackageBringsItsOwnDependencies(string packageId, string namespaceName, string probe)
    {
        var feed = FeedDirectory();
        Assert.SkipWhen(feed is null, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        var version = VersionIn(feed!, packageId);
        var workspace = Directory.CreateTempSubdirectory("healthdata-satellite");

        try
        {
            WriteNuGetConfig(workspace, feed!);

            Write(workspace, "Consumer.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{packageId}" Version="{version}" />
                  </ItemGroup>
                </Project>
                """);

            // Touching a type from the core package as well, because the point is that referencing
            // the satellite alone is enough to reach it.
            Write(workspace, "Program.cs", $"""
                using {namespaceName};

                Console.WriteLine({probe}.FullName);
                Console.WriteLine(Kkdev92.HealthData.HealthDataApiMetadata.DefaultBaseAddress);
                Console.WriteLine("satellite ok");
                """);

            var cache = Path.Combine(workspace.FullName, ".packages");
            var run = Dotnet($"run --project \"{workspace.FullName}\" -c Release", workspace.FullName, cache);

            Assert.True(run.ExitCode == 0, $"{packageId} alone failed to build or run:\n{run.Output}");
            Assert.Contains("satellite ok", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    /// <summary>
    /// The packed assemblies carry the metadata a consumer's own trimming and AOT publish needs.
    /// </summary>
    /// <remarks>
    /// <c>IsAotCompatible</c> is set on the projects, and what it does that a consumer can observe
    /// is mark the assembly trimmable. If that stopped being emitted, the AOT smoke application
    /// would keep passing — it references the projects — while every consumer publishing from the
    /// packages silently lost the guarantee the readme makes.
    /// </remarks>
    [Fact]
    public void EveryPackedAssemblyIsMarkedTrimmable()
    {
        var feed = FeedDirectory();
        Assert.SkipWhen(feed is null, "No package found; run 'dotnet pack -c Release -o artifacts' first.");

        foreach (var package in Directory.EnumerateFiles(feed!, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(package);

            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);

                var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());

                Assert.True(
                    text.Contains("IsTrimmable", StringComparison.Ordinal),
                    $"{Path.GetFileName(package)} -> {entry.FullName} is not marked trimmable.");
            }
        }
    }

    private static string VersionIn(string feed, string packageId)
    {
        var match = Directory
            .EnumerateFiles(feed, $"{packageId}.*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => PackageName().Match(name!))
            .FirstOrDefault(m => m.Success && m.Groups["id"].Value == packageId);

        Assert.True(match is not null, $"{packageId} was not packed into {feed}.");

        return match!.Groups["version"].Value;
    }

    /// <summary>
    /// Points the SDK ids at the local folder and everything else at nuget.org.
    /// </summary>
    /// <remarks>
    /// The mapping is the part that matters. Two sources with no mapping is a preference, not a
    /// rule: once these ids exist on nuget.org, restore is free to satisfy them from there, and
    /// this test would go on passing while testing a package somebody else built.
    /// </remarks>
    private static void WriteNuGetConfig(DirectoryInfo workspace, string feed)
        => Write(workspace, "nuget.config", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="artifacts" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="artifacts">
                  <package pattern="Kkdev92.HealthData*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

    /// <summary>The directory holding the packed <c>.nupkg</c> files, if there is one.</summary>
    private static string? FeedDirectory()
    {
        string[] candidates =
        [
            Path.Combine(RepositoryRoot.Value, "artifacts"),
            Path.Combine(RepositoryRoot.Value, "artifacts", "package", "release"),
        ];

        return candidates.FirstOrDefault(c =>
            Directory.Exists(c) && Directory.EnumerateFiles(c, "*.nupkg").Any());
    }

    private static void Write(DirectoryInfo workspace, string name, string content)
        => File.WriteAllText(Path.Combine(workspace.FullName, name), content);

    private static (int ExitCode, string Output) Dotnet(string arguments, string workingDirectory, string packageCache)
    {
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // The isolated cache is the point: without it, restore is free to hand back a package of
        // the same version from an earlier run instead of the one this test packed.
        start.Environment["NUGET_PACKAGES"] = packageCache;
        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true";

        using var process = Process.Start(start)!;

        // Both streams at once. Reading one to the end and then the other deadlocks as soon as the
        // child fills the pipe it is not being read from, which a restore that has something to say
        // will do. The exit code is only asked for once the process has actually finished.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(milliseconds: 10 * 60 * 1000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();

            return (-1, "The process did not finish within ten minutes and was killed.");
        }

        return (process.ExitCode, Task.WhenAll(stdout, stderr).GetAwaiter().GetResult() is { } parts
            ? string.Join(Environment.NewLine, parts)
            : string.Empty);
    }

    private static void TryDelete(DirectoryInfo workspace)
    {
        try
        {
            workspace.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A file still held open by a build server is not a test failure.
        }
    }

    [GeneratedRegex(@"^(?<id>Kkdev92\.HealthData(\.[A-Za-z]+)?)\.(?<version>\d+\.\d+\.\d+.*)$")]
    private static partial Regex PackageName();
}
