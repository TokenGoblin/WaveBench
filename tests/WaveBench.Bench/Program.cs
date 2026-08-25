using BenchmarkDotNet.Running;

namespace WaveBench.Bench;

/// <summary>
/// BenchmarkDotNet host. Solver hot-path benchmarks with CI regression
/// thresholds arrive with the solver (Phase 7, plan Part 11).
/// </summary>
public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
