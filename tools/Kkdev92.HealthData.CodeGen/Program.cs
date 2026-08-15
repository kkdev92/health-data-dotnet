using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Kkdev92.HealthData.CodeGen.Commands;

namespace Kkdev92.HealthData.CodeGen;

/// <summary>
/// Entry point for the repository-internal contract generator.
/// </summary>
/// <remarks>
/// An explicit CLI, never a Roslyn source generator, and never a network call during
/// <c>generate</c>.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var versionOption = new Option<string>("--version", "-v")
        {
            Description = "The API contract version to operate on, for example v4.",
            DefaultValueFactory = _ => "v4",
        };

        var fetch = new Command("fetch", "Refresh the Discovery snapshot and metadata.json. Uses the network.");
        var timestampOption = new Option<string?>("--retrieved-at")
        {
            Description = "UTC timestamp recorded as provenance, for example 2026-08-09T00:00:00Z.",
        };
        fetch.Options.Add(versionOption);
        fetch.Options.Add(timestampOption);
        fetch.SetAction((parseResult, cancellationToken) => FetchCommand.RunAsync(
            parseResult.GetValue(versionOption)!,
            parseResult.GetValue(timestampOption),
            cancellationToken));

        var generate = new Command("generate", "Generate C# from the committed snapshot. Offline.");
        generate.Options.Add(versionOption);
        generate.SetAction(parseResult => Guard(() =>
            GenerateCommand.Run(parseResult.GetValue(versionOption)!, verifyOnly: false)));

        var verify = new Command("verify", "Fail if the checked-in generated sources are stale. Offline.");
        verify.Options.Add(versionOption);
        verify.SetAction(parseResult => Guard(() =>
            GenerateCommand.Run(parseResult.GetValue(versionOption)!, verifyOnly: true)));

        var diff = new Command("diff", "Report contract changes against another Discovery document.");
        var againstOption = new Option<string?>("--against")
        {
            Description = "Path to a Discovery document. When omitted, the live document is fetched.",
        };
        diff.Options.Add(versionOption);
        diff.Options.Add(againstOption);
        diff.SetAction((parseResult, cancellationToken) => DiffCommand.RunAsync(
            parseResult.GetValue(versionOption)!,
            parseResult.GetValue(againstOption),
            cancellationToken));

        // Not contract generation, but the same job: something the build produces that has to be
        // trimmed before it ships. It lives here rather than in a task assembly of its own because
        // this project is already the repository's build-time tool.
        var filterDocs = new Command(
            "filter-docs",
            "Remove documentation for members outside the public surface. Offline.");

        var documentationOption = new Option<string>("--documentation") { Required = true };
        var assemblyOption = new Option<string>("--assembly") { Required = true };

        filterDocs.Options.Add(documentationOption);
        filterDocs.Options.Add(assemblyOption);
        filterDocs.SetAction(parseResult => FilterDocsCommand.Run(
            parseResult.GetValue(documentationOption)!,
            parseResult.GetValue(assemblyOption)!));

        var root = new RootCommand("Deterministic contract generator for Kkdev92.HealthData.")
        {
            fetch,
            generate,
            verify,
            diff,
            filterDocs,
        };

        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>
    /// Converts an unexpected failure into a diagnostic and a non-zero exit code.
    /// </summary>
    /// <remarks>
    /// A stack trace is not useful output for a build step; the message is. The full exception is
    /// still printed when the tool is run with a debugger attached.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Top-level CLI boundary: every failure becomes an exit code.")]
    private static int Guard(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"codegen: {ex.Message}");

            if (Debugger.IsAttached)
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
    }
}
