using FluentAssertions;
using WaveBench.Core.Numerics;
using Xunit;

namespace WaveBench.Core.Tests.Numerics;

/// <summary>
/// Anchors the exact Riemann solver against the published star-region values
/// of Toro, "Riemann Solvers and Numerical Methods for Fluid Dynamics",
/// 3rd ed., Table 4.3 — the independent reference that then lets the exact
/// solver serve as truth for the shock-tube verification tests.
/// </summary>
public class ExactRiemannSolverTests
{
    private static readonly PerfectGas Gas = new(1.4, 287.0);

    [Fact]
    public void Sod_problem_star_values_match_toro_table_4_3()
    {
        var solver = new ExactRiemannSolver(
            new PrimitiveState(1.0, 0.0, 1.0),
            new PrimitiveState(0.125, 0.0, 0.1),
            Gas);

        solver.PressureStar.Should().BeApproximately(0.30313, 1e-5);
        solver.VelocityStar.Should().BeApproximately(0.92745, 1e-5);
    }

    [Fact]
    public void Double_rarefaction_123_star_values_match_toro_table_4_3()
    {
        var solver = new ExactRiemannSolver(
            new PrimitiveState(1.0, -2.0, 0.4),
            new PrimitiveState(1.0, 2.0, 0.4),
            Gas);

        solver.PressureStar.Should().BeApproximately(0.00189, 1e-5);
        solver.VelocityStar.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Left_blast_wave_star_values_match_toro_table_4_3()
    {
        var solver = new ExactRiemannSolver(
            new PrimitiveState(1.0, 0.0, 1000.0),
            new PrimitiveState(1.0, 0.0, 0.01),
            Gas);

        solver.PressureStar.Should().BeApproximately(460.894, 1e-2);
        solver.VelocityStar.Should().BeApproximately(19.5975, 1e-3);
    }

    [Fact]
    public void Uniform_data_return_the_uniform_state_everywhere()
    {
        var w = new PrimitiveState(1.2, 30.0, 101_325.0);
        var solver = new ExactRiemannSolver(w, w, Gas);

        solver.PressureStar.Should().BeApproximately(w.P, w.P * 1e-9);
        solver.VelocityStar.Should().BeApproximately(w.U, 1e-6);
        foreach (var xi in new[] { -500.0, 0.0, 200.0 })
        {
            var s = solver.Sample(xi);
            s.Rho.Should().BeApproximately(w.Rho, 1e-9);
            s.P.Should().BeApproximately(w.P, 1e-4);
        }
    }

    [Fact]
    public void Sampled_solution_is_continuous_across_the_contact()
    {
        var solver = new ExactRiemannSolver(
            new PrimitiveState(1.0, 0.0, 1.0),
            new PrimitiveState(0.125, 0.0, 0.1),
            Gas);

        var eps = 1e-9;
        var below = solver.Sample(solver.VelocityStar - eps);
        var above = solver.Sample(solver.VelocityStar + eps);

        // Pressure and velocity are continuous across a contact; density jumps.
        below.P.Should().BeApproximately(above.P, 1e-6);
        below.U.Should().BeApproximately(above.U, 1e-6);
        below.Rho.Should().NotBeApproximately(above.Rho, 0.05);
    }

    [Fact]
    public void Vacuum_generating_data_throw()
    {
        var act = () => new ExactRiemannSolver(
            new PrimitiveState(1.0, -20.0, 0.4),
            new PrimitiveState(1.0, 20.0, 0.4),
            Gas);
        act.Should().Throw<ArgumentException>();
    }
}
