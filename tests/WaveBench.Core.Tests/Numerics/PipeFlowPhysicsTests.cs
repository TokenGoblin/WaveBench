using FluentAssertions;
using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Core.Thermo;
using Xunit;

namespace WaveBench.Core.Tests.Numerics;

public class PipeFlowPhysicsTests
{
    [Fact]
    public void Sutherland_viscosity_of_air_at_300_K()
    {
        // Published: μ_air(300 K) ≈ 1.846e-5 Pa·s.
        PipeFlowPhysics.SutherlandViscosity(300.0).Should().BeApproximately(1.846e-5, 0.005e-5);
    }

    [Fact]
    public void Laminar_friction_factor_is_64_over_re() =>
        PipeFlowPhysics.DarcyFrictionFactor(1000.0, 0.0).Should().Be(0.064);

    [Fact]
    public void Haaland_matches_colebrook_for_smooth_turbulent_flow()
    {
        // Colebrook–White, smooth pipe, Re = 1e5: f = 0.01799. Haaland is
        // within ~2% of Colebrook by construction.
        PipeFlowPhysics.DarcyFrictionFactor(1e5, 0.0).Should().BeApproximately(0.01799, 0.0004);
    }

    [Fact]
    public void Rough_pipe_has_higher_friction()
    {
        var smooth = PipeFlowPhysics.DarcyFrictionFactor(1e5, 0.0);
        var rough = PipeFlowPhysics.DarcyFrictionFactor(1e5, 0.26e-3 / 0.05); // cast iron, 50 mm bore
        rough.Should().BeGreaterThan(smooth * 1.2);
    }

    [Fact]
    public void Friction_factor_is_continuous_across_the_transition_blend()
    {
        var below = PipeFlowPhysics.DarcyFrictionFactor(2299.0, 1e-4);
        var above = PipeFlowPhysics.DarcyFrictionFactor(2301.0, 1e-4);
        Math.Abs(above - below).Should().BeLessThan(below * 0.01);

        var nearTurbulent = PipeFlowPhysics.DarcyFrictionFactor(3999.0, 1e-4);
        var turbulent = PipeFlowPhysics.DarcyFrictionFactor(4001.0, 1e-4);
        Math.Abs(turbulent - nearTurbulent).Should().BeLessThan(turbulent * 0.01);
    }

    [Fact]
    public void MultiSpecies_gas_model_round_trips_conserved_state()
    {
        var db = SpeciesDatabase.Default;
        var model = new MultiSpeciesGasModel(db, ["N2", "O2", "AR", "CO2"]);
        var y = model.MassFractionsOf(GasComposition.DryAir(db));

        const double rho = 0.9;
        const double u = 120.0;
        const double p = 250_000.0;
        var energy = model.TotalEnergy(rho, u, p, y);
        var state = model.FromConserved(rho, rho * u, energy, y, tGuess: 300.0);

        state.P.Should().BeApproximately(p, p * 1e-9);
        state.U.Should().BeApproximately(u, 1e-9);
        state.T.Should().BeApproximately(p / (rho * model.GasConstant(y)), 1e-6);
        state.Gamma.Should().BeInRange(1.3, 1.41);
    }

    [Fact]
    public void MultiSpecies_model_rejects_unknown_composition()
    {
        var db = SpeciesDatabase.Default;
        var model = new MultiSpeciesGasModel(db, ["N2", "O2"]);
        var act = () => model.MassFractionsOf(GasComposition.DryAir(db)); // has Ar + CO2 too
        act.Should().Throw<ArgumentException>();
    }
}
