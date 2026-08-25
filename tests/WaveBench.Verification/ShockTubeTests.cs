using FluentAssertions;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;

namespace WaveBench.Verification;

/// <summary>
/// §6.1 shock-tube verification: MUSCL-Hancock + HLLC against the exact
/// Riemann solution (which is itself anchored to Toro Table 4.3 in the unit
/// tests). Domain [0,1], diaphragm at 0.5, γ = 1.4.
/// </summary>
public class ShockTubeTests
{
    private static readonly PerfectGas Gas = new(1.4, 287.0);

    private static DuctSolver RunShockTube(
        PrimitiveState left, PrimitiveState right, int cells, double tEnd)
    {
        var solver = new DuctSolver(DuctGeometry.Uniform(1.0, cells, 0.05), new PerfectGasModel(Gas));
        for (var i = 0; i < cells; i++)
        {
            solver.SetState(i, solver.CellCentre(i) < 0.5 ? left : right);
        }

        solver.Advance(tEnd);
        return solver;
    }

    private static double L1DensityError(
        DuctSolver solver, PrimitiveState left, PrimitiveState right, double tEnd)
    {
        var exact = new ExactRiemannSolver(left, right, Gas);
        var error = 0.0;
        for (var i = 0; i < solver.CellCount; i++)
        {
            var xi = (solver.CellCentre(i) - 0.5) / tEnd;
            error += Math.Abs(solver.GetPrimitive(i).Rho - exact.Sample(xi).Rho);
        }

        return error * solver.CellSize;
    }

    public static readonly PrimitiveState SodLeft = new(1.0, 0.0, 1.0);
    public static readonly PrimitiveState SodRight = new(0.125, 0.0, 0.1);

    [Fact]
    public void Sod_matches_the_exact_solution_and_converges_under_refinement()
    {
        const double tEnd = 0.25;
        var e100 = L1DensityError(RunShockTube(SodLeft, SodRight, 100, tEnd), SodLeft, SodRight, tEnd);
        var e200 = L1DensityError(RunShockTube(SodLeft, SodRight, 200, tEnd), SodLeft, SodRight, tEnd);
        var e400 = L1DensityError(RunShockTube(SodLeft, SodRight, 400, tEnd), SodLeft, SodRight, tEnd);

        e200.Should().BeLessThan(0.008, "gate: L1 density error at 200 cells");
        e200.Should().BeLessThan(e100);
        e400.Should().BeLessThan(e200);
    }

    [Fact]
    public void Lax_matches_the_exact_solution_and_converges_under_refinement()
    {
        var left = new PrimitiveState(0.445, 0.698, 3.528);
        var right = new PrimitiveState(0.5, 0.0, 0.571);
        const double tEnd = 0.12;

        var e200 = L1DensityError(RunShockTube(left, right, 200, tEnd), left, right, tEnd);
        var e400 = L1DensityError(RunShockTube(left, right, 400, tEnd), left, right, tEnd);

        e200.Should().BeLessThan(0.025, "gate: L1 density error at 200 cells");
        e400.Should().BeLessThan(e200);
    }

    [Fact]
    public void Double_rarefaction_123_preserves_positivity_and_matches_exact()
    {
        var left = new PrimitiveState(1.0, -2.0, 0.4);
        var right = new PrimitiveState(1.0, 2.0, 0.4);
        const double tEnd = 0.15;

        var solver = RunShockTube(left, right, 200, tEnd);
        for (var i = 0; i < solver.CellCount; i++)
        {
            var w = solver.GetPrimitive(i);
            w.Rho.Should().BePositive($"density must stay positive (cell {i})");
            w.P.Should().BePositive($"pressure must stay positive (cell {i})");
        }

        var e200 = L1DensityError(solver, left, right, tEnd);
        e200.Should().BeLessThan(0.02, "gate: L1 density error at 200 cells");
    }

    [Fact]
    public void All_limiters_survive_the_123_problem()
    {
        var left = new PrimitiveState(1.0, -2.0, 0.4);
        var right = new PrimitiveState(1.0, 2.0, 0.4);

        foreach (var limiter in Enum.GetValues<SlopeLimiterKind>())
        {
            var solver = new DuctSolver(DuctGeometry.Uniform(1.0, 200, 0.05), new PerfectGasModel(Gas)) { Limiter = limiter };
            for (var i = 0; i < 200; i++)
            {
                solver.SetState(i, solver.CellCentre(i) < 0.5 ? left : right);
            }

            solver.Advance(0.15);
            for (var i = 0; i < 200; i++)
            {
                var w = solver.GetPrimitive(i);
                w.Rho.Should().BePositive($"{limiter}, cell {i}");
                w.P.Should().BePositive($"{limiter}, cell {i}");
            }
        }
    }

    [Fact]
    public void Reflective_wall_returns_the_incoming_shock()
    {
        // Sod tube with a solid right wall: after the shock reflects, gas at the
        // wall is brought to rest at raised pressure; velocity at the wall ≈ 0.
        var solver = new DuctSolver(DuctGeometry.Uniform(1.0, 200, 0.05), new PerfectGasModel(Gas))
        {
            RightBoundary = BoundaryKind.Reflective,
        };
        for (var i = 0; i < 200; i++)
        {
            solver.SetState(i, solver.CellCentre(i) < 0.5 ? SodLeft : SodRight);
        }

        solver.Advance(0.45); // shock reaches x=1 near t≈0.31, then reflects
        var wall = solver.GetPrimitive(199);
        Math.Abs(wall.U).Should().BeLessThan(0.05);
        wall.P.Should().BeGreaterThan(0.30313, "reflection raises pressure above the incident star value");
    }
}
