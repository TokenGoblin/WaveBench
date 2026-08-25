using FluentAssertions;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Core.Thermo;
using WaveBench.Core.Thermo.Fuels;
using Xunit;

namespace WaveBench.Verification;

/// <summary>
/// §6.1 species and real-gas verification (Phase 3 gate): species bounded and
/// summing to one; a hot burnt-gas cell reports the composition-correct local
/// sound speed (plan §2.2 — the single most important modelling decision).
/// </summary>
public class SpeciesAndRealGasTests
{
    private static readonly SpeciesDatabase Db = SpeciesDatabase.Default;

    private static MultiSpeciesGasModel ExhaustModel() =>
        new(Db, ["N2", "O2", "AR", "CO2", "H2O", "CO", "H2"]);

    [Fact]
    public void Gate_species_stay_bounded_and_sum_to_one_through_advection()
    {
        // Fresh air / burnt gas interface advected around a periodic duct —
        // the fresh-charge/residual interface of plan §2.2 in miniature.
        var gasModel = ExhaustModel();
        var air = gasModel.MassFractionsOf(GasComposition.DryAir(Db));
        var burnt = gasModel.MassFractionsOf(
            CombustionProducts.Of(new FuelFormula(8, 18, 0), 1.0, Db));

        const int cells = 200;
        var solver = new DuctSolver(DuctGeometry.Uniform(1.0, cells, 0.05), gasModel)
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
        };
        for (var i = 0; i < cells; i++)
        {
            var isBurnt = solver.CellCentre(i) is > 0.25 and < 0.5;
            solver.SetState(i, new PrimitiveState(1.0, 50.0, 101_325.0), isBurnt ? burnt : air);
        }

        var initialSpeciesMass = Enumerable.Range(0, gasModel.SpeciesCount)
            .Select(solver.SpeciesTotalMass).ToArray();

        solver.Advance(0.01); // ~0.5 domain lengths of advection

        for (var i = 0; i < cells; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < gasModel.SpeciesCount; k++)
            {
                var y = solver.GetMassFraction(k, i);
                y.Should().BeGreaterThanOrEqualTo(0.0, $"species {k} bounded below (cell {i})");
                y.Should().BeLessThanOrEqualTo(1.0 + 1e-12, $"species {k} bounded above (cell {i})");
                sum += y;
            }

            sum.Should().BeApproximately(1.0, 1e-12, $"gate: ΣY = 1 to machine precision (cell {i})");
        }

        for (var k = 0; k < gasModel.SpeciesCount; k++)
        {
            solver.SpeciesTotalMass(k).Should().BeApproximately(
                initialSpeciesMass[k], Math.Max(initialSpeciesMass[k], 1e-12) * 1e-9,
                $"species {k} mass conserved");
        }
    }

    [Fact]
    public void Gate_hot_exhaust_cell_reports_the_composition_correct_sound_speed()
    {
        // Burnt iso-octane products at 950 K: a ≈ 600 m/s, nowhere near the
        // 343 m/s of cold air (plan §2.2). Must match the thermo layer's own
        // hand calculation.
        var gasModel = ExhaustModel();
        var products = CombustionProducts.Of(new FuelFormula(8, 18, 0), 1.0, Db);
        var y = gasModel.MassFractionsOf(products);

        var mix = new MixtureThermo(products, Db);
        const double t = 950.0;
        const double p = 101_325.0;
        var rho = p / (mix.SpecificGasConstant * t);
        var expected = mix.SoundSpeed(t);

        var solver = new DuctSolver(DuctGeometry.Uniform(0.1, 10, 0.05), gasModel);
        for (var i = 0; i < 10; i++)
        {
            solver.SetState(i, new PrimitiveState(rho, 0.0, p), y);
        }

        var state = solver.GetState(5);
        state.T.Should().BeApproximately(t, 0.05, "temperature recovery from conserved energy");
        state.SoundSpeed.Should().BeApproximately(expected, expected * 0.001,
            $"gate: solver sound speed ({state.SoundSpeed:F1}) vs hand calculation ({expected:F1})");
        expected.Should().BeInRange(580.0, 640.0, "plan §2.2: hot exhaust propagates near 600 m/s");

        // And it must survive being stepped: a uniform hot state stays put.
        solver.Advance(1e-3);
        Math.Abs(solver.GetPrimitive(5).U).Should().BeLessThan(1e-8);
    }

    [Fact]
    public void Cold_intake_cell_reports_353_m_per_s()
    {
        // The other half of the plan §2.2 example: 310 K intake air ≈ 353 m/s.
        var gasModel = ExhaustModel();
        var y = gasModel.MassFractionsOf(GasComposition.DryAir(Db));
        var mix = new MixtureThermo(GasComposition.DryAir(Db), Db);
        var rho = 101_325.0 / (mix.SpecificGasConstant * 310.0);

        var solver = new DuctSolver(DuctGeometry.Uniform(0.1, 10, 0.05), gasModel);
        for (var i = 0; i < 10; i++)
        {
            solver.SetState(i, new PrimitiveState(rho, 0.0, 101_325.0), y);
        }

        solver.GetState(5).SoundSpeed.Should().BeApproximately(353.0, 1.0);
    }
}
