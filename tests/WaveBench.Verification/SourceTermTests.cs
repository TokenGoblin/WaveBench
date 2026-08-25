using FluentAssertions;
using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;

namespace WaveBench.Verification;

/// <summary>
/// §6.1 source-term verification (Phase 3 gate): well-balanced taper, wall
/// friction vs Darcy–Weisbach, wall heat transfer vs the analytical
/// exponential approach.
/// </summary>
public class SourceTermTests
{
    private static readonly PerfectGas Gas = new(1.4, 287.05);

    [Fact]
    public void Gate_stationary_state_in_a_taper_generates_no_spurious_velocity()
    {
        // 40 → 80 mm taper, uniform p and T at rest. A taper that generates
        // waves at rest is the classic silent killer (plan §5.1).
        var geometry = DuctGeometry.Taper(0.5, 100, 0.040, 0.080);
        var solver = new DuctSolver(geometry, new PerfectGasModel(Gas));
        for (var i = 0; i < 100; i++)
        {
            solver.SetState(i, new PrimitiveState(1.2, 0.0, 101_325.0));
        }

        for (var step = 0; step < 500; step++)
        {
            solver.Step(solver.StableTimestep());
        }

        for (var i = 0; i < 100; i++)
        {
            Math.Abs(solver.GetPrimitive(i).U).Should().BeLessThan(1e-10,
                $"gate: spurious velocity < 1e-10 m/s (cell {i})");
        }
    }

    [Fact]
    public void Gate_stationary_taper_is_well_balanced_with_the_real_gas_model_too()
    {
        var db = WaveBench.Core.Thermo.SpeciesDatabase.Default;
        var gasModel = new MultiSpeciesGasModel(db, ["N2", "O2", "AR", "CO2"]);
        var air = gasModel.MassFractionsOf(WaveBench.Core.Thermo.GasComposition.DryAir(db));

        var geometry = DuctGeometry.Taper(0.5, 60, 0.040, 0.080);
        var solver = new DuctSolver(geometry, gasModel);
        for (var i = 0; i < 60; i++)
        {
            solver.SetState(i, new PrimitiveState(1.2, 0.0, 101_325.0), air);
        }

        for (var step = 0; step < 200; step++)
        {
            solver.Step(solver.StableTimestep());
        }

        for (var i = 0; i < 60; i++)
        {
            Math.Abs(solver.GetPrimitive(i).U).Should().BeLessThan(1e-9,
                $"real-gas well-balancedness (cell {i}; tolerance covers the temperature Newton solve)");
        }
    }

    [Fact]
    public void Gate_friction_deceleration_matches_darcy_weisbach_within_1_percent()
    {
        // Uniform flow in a periodic rough pipe with friction only. The exact
        // momentum balance is du/dt = −(f_D/2D)·u|u| (density uniform, no
        // pressure gradient develops); integrate that ODE with the same f(Re)
        // and compare after a finite time.
        const double d = 0.05;
        const double u0 = 40.0;
        const double rho0 = 1.2;
        const double p0 = 101_325.0;
        const double roughness = 0.05e-3;
        const double tEnd = 0.05;

        var geometry = DuctGeometry.Uniform(1.0, 200, d, roughness);
        var solver = new DuctSolver(geometry, new PerfectGasModel(Gas))
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
            FrictionEnabled = true,
        };
        for (var i = 0; i < 200; i++)
        {
            solver.SetState(i, new PrimitiveState(rho0, u0, p0));
        }

        solver.Advance(tEnd);

        // Reference ODE with fine explicit steps (friction heating slightly
        // raises T and thus viscosity; track T from energy conservation).
        double u = u0, t = p0 / (rho0 * Gas.SpecificGasConstant);
        var cv = Gas.SpecificGasConstant / (Gas.Gamma - 1.0);
        const int steps = 200_000;
        const double dt = tEnd / steps;
        for (var s = 0; s < steps; s++)
        {
            var mu = PipeFlowPhysics.SutherlandViscosity(t);
            var re = rho0 * Math.Abs(u) * d / mu;
            var f = PipeFlowPhysics.DarcyFrictionFactor(re, roughness / d);
            var dudt = -f / (2.0 * d) * u * Math.Abs(u);
            t += -u * dudt * dt / cv; // lost KE → internal energy
            u += dudt * dt;
        }

        var uSolver = solver.GetPrimitive(100).U;
        uSolver.Should().BeApproximately(u, Math.Abs(u0 - u) * 0.01,
            $"gate: friction within 1% of Darcy–Weisbach (ODE {u:F4}, solver {uSolver:F4})");
    }

    [Fact]
    public void Gate_wall_heat_transfer_matches_the_analytical_exponential_approach_within_1_percent()
    {
        // Uniform flow past a wall held at 500 K: gas temperature approaches
        // the wall exponentially, dT/dt = hA(Tw−T)/(ρ cp V) with A/V = 4/D.
        // (Constant-pressure heating in a periodic uniform state.)
        const double d = 0.05;
        const double u0 = 30.0;
        const double rho0 = 1.2;
        const double p0 = 101_325.0;
        const double tWall = 500.0;
        const double tEnd = 0.25;

        var geometry = DuctGeometry.Uniform(1.0, 100, d);
        var solver = new DuctSolver(geometry, new PerfectGasModel(Gas))
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
            HeatTransferEnhancement = 1.0, // isolate the Colburn correlation itself
        };
        // Huge areal heat capacity → wall effectively isothermal over the run.
        var wall = new WallThermalModel(100, WallSurface.BareStainless, tWall, 300.0,
            arealHeatCapacity: 1e12, externalHeatTransferCoefficient: 0.0);
        solver.AttachWall(wall);

        for (var i = 0; i < 100; i++)
        {
            solver.SetState(i, new PrimitiveState(rho0, u0, p0));
        }

        var t0 = p0 / (rho0 * Gas.SpecificGasConstant);
        solver.Advance(tEnd);

        // Reference ODE with the same correlation chain. In the solver the
        // energy source raises E at constant volume per step, so cv governs.
        var cp = Gas.Gamma * Gas.SpecificGasConstant / (Gas.Gamma - 1.0);
        var cv = Gas.SpecificGasConstant / (Gas.Gamma - 1.0);
        double t = t0, u = u0;
        const int steps = 100_000;
        const double dt = tEnd / steps;
        for (var s = 0; s < steps; s++)
        {
            var mu = PipeFlowPhysics.SutherlandViscosity(t);
            var re = rho0 * Math.Abs(u) * d / mu;
            var f = PipeFlowPhysics.DarcyFrictionFactor(re, 0.0);
            var h = PipeFlowPhysics.ColburnHeatTransferCoefficient(f, rho0, u, cp, 0.71, 1.0);
            t += dt * h * 4.0 / d * (tWall - t) / (rho0 * cv);
        }

        var tSolver = solver.GetState(50).T;
        tSolver.Should().BeApproximately(t, (t - t0) * 0.01,
            $"gate: heat transfer within 1% of analytical (ODE {t:F3} K, solver {tSolver:F3} K)");
        (tSolver - t0).Should().BeGreaterThan(5.0, "the gas must actually have heated");
    }

    [Fact]
    public void Wrapped_wall_runs_hotter_than_bare_for_the_same_gas()
    {
        // §2.9's differentiator: insulation raises wall temperature.
        var bare = new WallThermalModel(1, WallSurface.BareStainless, 300.0, 300.0, arealHeatCapacity: 100.0);
        var wrapped = new WallThermalModel(1, WallSurface.Wrapped, 300.0, 300.0, arealHeatCapacity: 100.0);

        Span<double> h = [150.0];
        Span<double> tGas = [900.0];
        for (var s = 0; s < 20_000; s++)
        {
            bare.Update(1e-3, h, tGas);
            wrapped.Update(1e-3, h, tGas);
        }

        wrapped.Temperature[0].Should().BeGreaterThan(bare.Temperature[0] + 10.0);
        bare.Temperature[0].Should().BeInRange(300.0, 900.0);
    }
}
