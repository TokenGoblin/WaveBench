using FluentAssertions;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Mesh convergence of the rotor boundary.
///
/// A boundary condition that changes its answer as the mesh is refined is not a
/// boundary condition, it is a discretisation artefact — and every result in
/// this phase is read through this one. It gets its own test because the
/// hysteresis work found the question the hard way: shrinking the volute
/// changed the answer, and the cause turned out to be the cell size next to the
/// rotor rather than the volute at all.
/// </summary>
public class RotorMeshProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate_the_rotor_boundary_converges_under_mesh_refinement()
    {
        var results = new List<(int Cells, double Power, double MassKg)>();

        foreach (var cells in new[] { 20, 40, 80, 160, 320 })
        {
            var rig = new PulsatingTurbineRig(
                TurbineModelKind.QuasiSteady, SyntheticTurbo.Turbine(), 90_000.0,
                meanPressurePa: 175_000.0,
                pulseAmplitudePa: 120_000.0,
                pulseFrequencyHz: 100.0,
                manifoldCells: cells,
                manifoldLengthM: 0.50);

            rig.Run(periods: 6);
            results.Add((cells, rig.MeanPowerW(), rig.Stage.MassKg));
        }

        output.WriteLine("  cells   cell mm   mean kW   change from previous");
        for (var i = 0; i < results.Count; i++)
        {
            var change = i == 0
                ? double.NaN
                : Math.Abs(results[i].Power - results[i - 1].Power) / results[i - 1].Power;

            output.WriteLine(
                $"{results[i].Cells,7}   {500.0 / results[i].Cells,7:F2}   {results[i].Power / 1000,7:F2}   "
                + $"{(double.IsNaN(change) ? "     —" : change.ToString("P1")),20}");
        }

        // The absolute value is not the assertion — the mesh-independence is.
        //
        // Note what is NOT asserted: that each halving moves the answer less
        // than the last. It does not, and requiring it was wrong. Every step
        // here moves the answer by well under a hundredth of a percent, and at
        // that level the ordering is sampling noise in where the recorded cycle
        // lands, not a convergence rate. Asserting monotone deltas would be
        // asserting the noise.
        var deltas = new List<double>();
        for (var i = 1; i < results.Count; i++)
        {
            deltas.Add(Math.Abs(results[i].Power - results[i - 1].Power) / results[i - 1].Power);
        }

        deltas.Should().AllSatisfy(d => d.Should().BeLessThan(0.01,
            "no halving of the cell may move the answer by as much as a percent"));

        var spread = (results.Max(r => r.Power) - results.Min(r => r.Power)) / results.Min(r => r.Power);
        spread.Should().BeLessThan(0.01,
            "and the whole 16× refinement must land inside one percent");

        output.WriteLine($"total spread over a 16× refinement: {spread:P3}");
    }
}
