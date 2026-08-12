using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Kkdev92.HealthData.Benchmarks;

/// <summary>
/// Entry point for the benchmark suite.
/// </summary>
/// <remarks>
/// <para>
/// The point is not to make Google's network faster. It is to keep this SDK's own per-request
/// cost visible: deserialization, request construction, and the pagination loop.
/// </para>
/// <para>
/// Run everything:
/// </para>
/// <code>
/// dotnet run --project benchmarks/Kkdev92.HealthData.Benchmarks -c Release -- --filter '*'
/// </code>
/// <para>
/// Run one group, or take a quick indicative reading:
/// </para>
/// <code>
/// dotnet run --project benchmarks/Kkdev92.HealthData.Benchmarks -c Release -- --filter '*Serialization*'
/// dotnet run --project benchmarks/Kkdev92.HealthData.Benchmarks -c Release -- --filter '*' --job short
/// </code>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));

        return 0;
    }
}
