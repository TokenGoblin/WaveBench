using FluentAssertions;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;

namespace WaveBench.Verification;

public class AccuracyAndAcousticsTests
{
    private static readonly PerfectGas Gas = new(1.4, 287.0);

    private static double AdvectedSineL1Error(int cells)
    {
        // Smooth density wave advected at constant u and p (an entropy wave):
        // the exact solution is pure translation; after one period on a
        // periodic domain it coincides with the initial condition.
        var solver = new EulerSolver1D(cells, 1.0 / cells, Gas)
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
        };
        for (var i = 0; i < cells; i++)
        {
            var rho = 1.0 + 0.2 * Math.Sin(2.0 * Math.PI * solver.CellCentre(i));
            solver.SetPrimitive(i, new PrimitiveState(rho, 1.0, 1.0));
        }

        solver.Advance(1.0);

        var error = 0.0;
        for (var i = 0; i < cells; i++)
        {
            var exact = 1.0 + 0.2 * Math.Sin(2.0 * Math.PI * solver.CellCentre(i));
            error += Math.Abs(solver.GetPrimitive(i).Rho - exact);
        }

        return error / cells;
    }

    [Fact]
    public void Gate_observed_order_of_accuracy_is_at_least_1_8()
    {
        var e100 = AdvectedSineL1Error(100);
        var e200 = AdvectedSineL1Error(200);
        var e400 = AdvectedSineL1Error(400);

        var order1 = Math.Log2(e100 / e200);
        var order2 = Math.Log2(e200 / e400);

        order1.Should().BeGreaterThan(1.8, $"errors {e100:E3} → {e200:E3}");
        order2.Should().BeGreaterThan(1.8, $"errors {e200:E3} → {e400:E3}");
    }

    [Fact]
    public void Periodic_advection_conserves_mass_momentum_and_energy_to_machine_precision()
    {
        var solver = new EulerSolver1D(200, 1.0 / 200, Gas)
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
        };
        for (var i = 0; i < 200; i++)
        {
            var rho = 1.0 + 0.2 * Math.Sin(2.0 * Math.PI * solver.CellCentre(i));
            solver.SetPrimitive(i, new PrimitiveState(rho, 1.0, 1.0));
        }

        var before = solver.ConservedTotals();
        solver.Advance(0.5);
        var after = solver.ConservedTotals();

        after.Mass.Should().BeApproximately(before.Mass, Math.Abs(before.Mass) * 1e-12);
        after.Momentum.Should().BeApproximately(before.Momentum, Math.Abs(before.Momentum) * 1e-12);
        after.Energy.Should().BeApproximately(before.Energy, Math.Abs(before.Energy) * 1e-12);
    }

    [Fact]
    public void Gate_acoustic_pulse_keeps_98_percent_amplitude_over_20_pipe_lengths()
    {
        // Right-going linear acoustic pulse in air-like gas on a periodic
        // domain: δu = δp/(ρ₀a₀), δρ = δp/a₀². Amplitude 10 Pa on 1e5 Pa keeps
        // nonlinear steepening negligible over the run. TVD limiters clip
        // smooth extrema (locally first order), so peak retention is set by
        // resolution: at σ = 60 cells — the acoustic-mode meshing regime of
        // plan §5.5, which prescribes a finer mesh than performance runs —
        // the measured loss over 20 lengths is ~1.4% (it was 6.5% at σ = 20
        // cells and 2.4% at 40; effective extremum order ≈ 1.4).
        const int cells = 2000;
        const double rho0 = 1.2;
        const double p0 = 1e5;
        const double amplitude = 10.0;
        const double sigma = 0.03;
        var a0 = Gas.SoundSpeed(rho0, p0);

        var solver = new EulerSolver1D(cells, 1.0 / cells, Gas)
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
        };
        for (var i = 0; i < cells; i++)
        {
            var x = solver.CellCentre(i);
            var dp = amplitude * Math.Exp(-Math.Pow(x - 0.5, 2) / (2 * sigma * sigma));
            solver.SetPrimitive(i, new PrimitiveState(
                rho0 + dp / (a0 * a0),
                dp / (rho0 * a0),
                p0 + dp));
        }

        double PeakOverpressure()
        {
            var peak = 0.0;
            for (var i = 0; i < cells; i++)
            {
                peak = Math.Max(peak, solver.GetPrimitive(i).P - p0);
            }

            return peak;
        }

        var initialPeak = PeakOverpressure();
        solver.Advance(20.0 / a0); // 20 domain lengths of travel
        var finalPeak = PeakOverpressure();

        (finalPeak / initialPeak).Should().BeGreaterThan(0.98,
            $"gate: < 2% amplitude loss over 20 pipe lengths (peak {initialPeak:F3} → {finalPeak:F3} Pa)");
    }
}
