using FluentAssertions;
using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;

namespace WaveBench.Verification;

/// <summary>
/// §6.1 nozzle verification (Phase 4 gate): steady nozzle flow within 0.5% of
/// the isentropic solution; the FSAE restrictor chokes at the theoretically
/// correct mass flow with the throat at Mach 1.
/// </summary>
public class NozzleAndRestrictorTests
{
    private static readonly PerfectGas Gas = new(1.4, 287.05);

    private static double SmoothRamp(double x, double x0, double x1, double v0, double v1)
    {
        if (x <= x0)
        {
            return v0;
        }

        if (x >= x1)
        {
            return v1;
        }

        var s = (x - x0) / (x1 - x0);
        var smooth = s * s * (3.0 - 2.0 * s);
        return v0 + (v1 - v0) * smooth;
    }

    private static DuctSolver SteadyNozzle(
        DuctGeometry geometry, double p0, double t0, double pAmbient, double settleTime)
    {
        var solver = new DuctSolver(geometry, new PerfectGasModel(Gas))
        {
            LeftBoundary = BoundaryKind.External,
            RightBoundary = BoundaryKind.External,
            LeftEnd = new ReservoirBoundary { StagnationPressure = p0, StagnationTemperature = t0 },
            RightEnd = new ReservoirBoundary { StagnationPressure = pAmbient, StagnationTemperature = t0 },
        };

        var rho0 = pAmbient / (Gas.SpecificGasConstant * t0);
        for (var i = 0; i < geometry.CellCount; i++)
        {
            solver.SetState(i, new PrimitiveState(rho0, 0.0, pAmbient));
        }

        solver.Advance(settleTime);
        return solver;
    }

    private static double MassFlowAt(DuctSolver solver, int cell)
    {
        var w = solver.GetPrimitive(cell);
        return w.Rho * w.U * solver.Geometry.CellArea[cell];
    }

    [Fact]
    public void Gate_subsonic_nozzle_mass_flow_matches_isentropic_within_half_percent()
    {
        // 50 → 30 mm smooth contraction; p0 = 1.2 bar feeding 1.0 bar ambient.
        const double p0 = 1.2e5;
        const double t0 = 300.0;
        const double pAmb = 1.0e5;
        var geometry = DuctGeometry.FromDiameterProfile(
            0.3, 300, x => SmoothRamp(x, 0.10, 0.25, 0.050, 0.030));

        var solver = SteadyNozzle(geometry, p0, t0, pAmb, settleTime: 0.05);

        // Isentropic: exit at ambient static pressure through the exit area.
        var exitArea = geometry.FaceArea[^1];
        var expected = exitArea * p0 / Math.Sqrt(Gas.SpecificGasConstant * t0)
                       * CompressibleOrifice.FlowFunction(pAmb / p0, Gas.Gamma);

        // Steadiness: mass flow uniform along the duct.
        var mDotIn = MassFlowAt(solver, 20);
        var mDotOut = MassFlowAt(solver, geometry.CellCount - 20);
        mDotOut.Should().BeApproximately(mDotIn, Math.Abs(mDotIn) * 0.002, "steady flow is uniform");

        mDotOut.Should().BeApproximately(expected, expected * 0.005,
            $"gate: isentropic tables within 0.5% (expected {expected:F5}, got {mDotOut:F5} kg/s)");
    }

    [Fact]
    public void Gate_fsae_restrictor_chokes_at_the_theoretical_mass_flow()
    {
        // 60 mm duct → converging cone → 20 mm throat → 7.1° half-angle
        // diffuser → 60 mm (plan §2.7). Deep back pressure guarantees choke.
        const double p0 = 1.0e5;
        const double t0 = 293.15;
        const double pAmb = 0.55e5;
        var geometry = DuctGeometry.FromDiameterProfile(
            0.30, 300, x => x < 0.10
                ? SmoothRamp(x, 0.02, 0.10, 0.060, 0.020)
                : SmoothRamp(x, 0.11, 0.27, 0.020, 0.060));

        var solver = SteadyNozzle(geometry, p0, t0, pAmb, settleTime: 0.06);

        var throatArea = Math.PI / 4.0 * 0.020 * 0.020;
        var expected = throatArea * p0 / Math.Sqrt(Gas.SpecificGasConstant * t0)
                       * CompressibleOrifice.FlowFunction(0.0, Gas.Gamma); // choked Φ* = 0.6847

        var mDot = MassFlowAt(solver, 30);
        mDot.Should().BeApproximately(expected, expected * 0.01,
            $"gate: restrictor chokes at the theoretical flow (expected {expected:F5}, got {mDot:F5} kg/s)");

        // Throat runs at Mach ≈ 1.
        var iThroat = 105; // just downstream of the geometric throat
        var throat = solver.GetState(iThroat);
        (Math.Abs(throat.U) / throat.SoundSpeed).Should().BeGreaterThan(0.95, "throat is sonic when choked");

        // Choked flow must be insensitive to further back-pressure reduction.
        var lower = SteadyNozzle(geometry, p0, t0, 0.45e5, settleTime: 0.06);
        MassFlowAt(lower, 30).Should().BeApproximately(mDot, mDot * 0.005,
            "choked mass flow is independent of back pressure");
    }

    [Fact]
    public void Reservoir_boundary_settles_to_rest_at_equal_pressures()
    {
        var geometry = DuctGeometry.Uniform(0.2, 50, 0.05);
        var solver = SteadyNozzle(geometry, 1.0e5, 300.0, 1.0e5, settleTime: 0.02);
        for (var i = 0; i < 50; i++)
        {
            Math.Abs(solver.GetPrimitive(i).U).Should().BeLessThan(0.05,
                "no pressure difference, no flow");
        }
    }
}
