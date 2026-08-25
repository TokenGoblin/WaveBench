using BenchmarkDotNet.Attributes;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;

namespace WaveBench.Bench;

/// <summary>
/// Solver hot-path benchmarks (plan §5.7). The CI regression check pins the
/// per-step cost of the duct kernel; the full budget measurement lives in
/// the `budget` mode of this executable.
/// </summary>
[MemoryDiagnoser]
public class SolverBenchmarks
{
    private DuctSolver _perfectGas = null!;
    private DuctSolver _realGas = null!;
    private double _dt;

    [GlobalSetup]
    public void Setup()
    {
        _perfectGas = MakeDuct(new PerfectGasModel(PerfectGas.Air));

        var db = WaveBench.Core.Thermo.SpeciesDatabase.Default;
        var multi = new MultiSpeciesGasModel(db, ["N2", "O2", "AR", "CO2", "H2O"]);
        _realGas = MakeDuct(multi, multi.MassFractionsOf(WaveBench.Core.Thermo.GasComposition.DryAir(db)));

        _dt = _perfectGas.StableTimestep();
    }

    private static DuctSolver MakeDuct(IGasModel gas, double[]? y = null)
    {
        var duct = new DuctSolver(DuctGeometry.Uniform(3.0, 3000, 0.04), gas)
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
        };
        for (var i = 0; i < duct.CellCount; i++)
        {
            var rho = 1.18 + 0.02 * Math.Sin(2 * Math.PI * duct.CellCentre(i) / 3.0);
            duct.SetState(i, new PrimitiveState(rho, 15.0, 101_325.0), y);
        }

        return duct;
    }

    /// <summary>One step of a 3000-cell duct, perfect gas.</summary>
    [Benchmark(Baseline = true)]
    public void PerfectGasStep3000Cells() => _perfectGas.Step(_dt);

    /// <summary>One step of a 3000-cell duct with the species-resolved EOS.</summary>
    [Benchmark]
    public void RealGasStep3000Cells() => _realGas.Step(_dt);
}
