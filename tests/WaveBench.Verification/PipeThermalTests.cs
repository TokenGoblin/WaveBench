using FluentAssertions;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Duct wall friction and wall heat transfer on a BUILT ENGINE (plan §2.1,
/// §2.3, §2.9).
///
/// Phase 3 gated these at component level — a single duct, a hand-set wall,
/// steady heat transfer within 1% of the analytical answer — and they passed.
/// What no test covered was whether an engine's ducts ever got them, and for
/// a long time they did not: nothing outside a unit test set
/// <c>FrictionEnabled</c> or called <c>AttachWall</c>, so every pipe in every
/// engine the product built ran adiabatic and frictionless.
///
/// These are the assembly-level tests that would have caught it.
/// </summary>
public class PipeThermalTests(ITestOutputHelper output)
{
    private static EngineModelDocument Engine(PipeThermalSpec? thermal = null, double primaryMm = 450) => new()
    {
        Name = "pipe thermal",
        Engine = new EngineSpec
        {
            BoreMm = 82, StrokeMm = 56.5, RodLengthMm = 100, CompressionRatio = 12, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec
        {
            HeadDiameterMm = 29, Count = 2, MaxLiftMm = 9.5, OpenDeg = 340, CloseDeg = 590,
        },
        ExhaustValves = new ValveTrainSpec
        {
            HeadDiameterMm = 24, Count = 2, MaxLiftMm = 9.0, OpenDeg = 130, CloseDeg = 380,
        },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 36, RoughnessMm = 0.045 },
        ExhaustRunner = new DuctSpec { LengthMm = primaryMm, DiameterMm = 34, RoughnessMm = 0.045 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
        PipeThermal = thermal ?? new PipeThermalSpec(),
        Solver = new SolverSpec { CellSizeMm = 10.0, MinCycles = 5, MaxCycles = 40 },
    };

    /// <summary>Build, converge, and hand back the engine so its walls can be read.</summary>
    private static (EngineSimulator Engine, int Cycles) Converge(EngineModelDocument document, double rpm = 6000.0)
    {
        var engine = EngineBuilder.Build(document, rpm);
        engine.WallConvergenceK = document.PipeThermal.WallConvergenceK;
        var (_, cycles) = engine.RunToConvergence(
            r => r.NetValveMass.Length > 0 ? r.NetValveMass[0] : 0.0,
            document.Solver.ConvergenceTolerance,
            document.Solver.MinCycles,
            document.Solver.MaxCycles);
        return (engine, cycles);
    }

    private static double ExhaustWall(EngineSimulator engine) =>
        engine.Ducts.Where(d => d.Wall is not null && d.Geometry.Length >= 0.4)
            .SelectMany(d => d.Wall!.Temperature)
            .Average();

    [Fact]
    public void Gate_every_duct_in_a_built_engine_has_friction_and_a_wall()
    {
        // The regression test for the hole itself. A duct without these is a
        // duct the plan's physics never reaches.
        foreach (var manifold in new ManifoldSpec?[] { null, CollectorLibrary.Build("4-2-1", new CollectorGeometry()) })
        {
            var document = Engine() with { ExhaustManifold = manifold };
            var engine = EngineBuilder.Build(document, 6000.0);

            engine.Ducts.Should().NotBeEmpty();
            engine.Ducts.Should().OnlyContain(d => d.FrictionEnabled,
                "plan §2.1 friction must reach every pipe, not just the ones a unit test builds");
            engine.Ducts.Should().OnlyContain(d => d.HeatTransferEnabled,
                "plan §2.3 wall heat transfer must reach every pipe");
            engine.Ducts.Should().OnlyContain(d => d.Wall!.Temperature.Length == d.CellCount);
        }
    }

    [Fact]
    public void Gate_the_converged_wall_temperature_does_not_depend_on_the_wall_it_started_from()
    {
        // The whole reason the wall is solved between cycles rather than
        // integrated within them. A steel wall's time constant is on the order
        // of ten seconds against a 20 ms cycle, so transient integration would
        // still be climbing when the run ends — and the answer would then be
        // set by an assumed wall thickness rather than by the physics.
        var cold = Converge(Engine(new PipeThermalSpec { ExhaustWallStartK = 400.0 }));
        var hot = Converge(Engine(new PipeThermalSpec { ExhaustWallStartK = 1100.0 }));
        var thin = Converge(Engine(new PipeThermalSpec { ArealHeatCapacityJPerM2K = 100.0 }));
        var thick = Converge(Engine(new PipeThermalSpec { ArealHeatCapacityJPerM2K = 40_000.0 }));

        output.WriteLine($"start 400 K  -> {ExhaustWall(cold.Engine):F1} K in {cold.Cycles} cycles");
        output.WriteLine($"start 1100 K -> {ExhaustWall(hot.Engine):F1} K in {hot.Cycles} cycles");
        output.WriteLine($"c = 100      -> {ExhaustWall(thin.Engine):F1} K");
        output.WriteLine($"c = 40000    -> {ExhaustWall(thick.Engine):F1} K");

        ExhaustWall(hot.Engine).Should().BeApproximately(ExhaustWall(cold.Engine), 1.0,
            "the balance point is a property of the operating point, not of where the wall started");
        ExhaustWall(thick.Engine).Should().BeApproximately(ExhaustWall(thin.Engine), 1.0,
            "wall heat capacity sets how fast the wall gets there, never where it ends up");

        cold.Cycles.Should().BeLessThan(20, "the cyclic-steady solve must actually converge in a run");
    }

    [Fact]
    public void Gate_the_wall_balance_it_settles_on_actually_balances()
    {
        // Verifying the solve rather than trusting it: at the converged
        // temperature the cycle-average energy crossing the wall must sum to
        // zero. LastResidual is that sum, in W/m².
        var (engine, _) = Converge(Engine());

        var exhaust = engine.Ducts.Where(d => d.Wall is not null && d.Geometry.Length >= 0.4).ToList();
        exhaust.Should().NotBeEmpty();

        foreach (var duct in exhaust)
        {
            var wall = duct.Wall!;
            var throughput = wall.ExternalConductance * (wall.Temperature.Average() - wall.AmbientTemperature);
            output.WriteLine(
                $"wall {wall.Temperature.Average():F1} K, residual {wall.LastResidual:E2} W/m², "
                + $"carrying {throughput:F0} W/m²");

            // Essentially zero, not "small relative to something": Newton runs
            // to a 1e-9 K step, so a residual worth noticing would mean the
            // adopted temperature is not the root of the balance at all.
            wall.LastResidual.Should().BeLessThan(1e-3,
                "the temperature the solve adopted must satisfy the balance it solved");
            throughput.Should().BeGreaterThan(1000.0, "a header wall really is shedding kilowatts per square metre");
        }
    }

    [Fact]
    public void Gate_a_wrapped_header_runs_hotter_and_carries_a_faster_wave()
    {
        // Plan §2.9 calls this out by name: "exhaust gas temperature sets a,
        // and a sets the tuned length AND the acoustic resonance frequencies.
        // A wrapped header runs hotter... The software must be able to
        // demonstrate this — it is a differentiator and a validation test."
        var results = new List<(string Surface, double Wall, double SoundSpeed, double TunedLength)>();

        // The out-and-back window the §2.10 organ-pipe estimate uses for the
        // exhaust, at a fixed target speed.
        const double targetRpm = 6000.0;

        foreach (var surface in new[] { "Bare stainless", "Ceramic coated", "Header wrap", "Insulated" })
        {
            var document = Engine(new PipeThermalSpec { ExhaustSurface = surface });
            var (engine, _) = Converge(document);

            var pipes = engine.Ducts.Where(d => d.Wall is not null && d.Geometry.Length >= 0.4).ToList();
            var soundSpeed = pipes.Average(ManifoldPulseState.MassWeightedSoundSpeed);

            var window = document.ExhaustValves.CloseDeg - document.ExhaustValves.OpenDeg;
            var tuned = QuickEstimate.OrganPipeTunedLength(soundSpeed, window, targetRpm);

            results.Add((surface, ExhaustWall(engine), soundSpeed, tuned));
            output.WriteLine(
                $"{surface,-18} wall {ExhaustWall(engine):F0} K, a {soundSpeed:F1} m/s, "
                + $"tuned primary {tuned * 1000:F0} mm at {targetRpm:F0} rpm");
        }

        var bare = results[0];
        var wrapped = results.Single(r => r.Surface == "Header wrap");
        var insulated = results.Single(r => r.Surface == "Insulated");

        wrapped.Wall.Should().BeGreaterThan(bare.Wall + 5.0,
            "a wrap is thermal resistance in series with the outside air");
        insulated.Wall.Should().BeGreaterThan(wrapped.Wall,
            "more resistance, hotter wall");

        // A hotter wall means hotter gas and a faster wave.
        wrapped.SoundSpeed.Should().BeGreaterThan(bare.SoundSpeed);

        // NOTE ON THE PLAN'S WORDING. Plan §2.9 says "a wrapped header runs
        // hotter and its optimum primary length is correspondingly SHORTER".
        // The first half is what we measure. The second half does not follow
        // from the tuning relation the plan itself gives in §2.10:
        //
        //     L = a·Δθ / (12·N)
        //
        // L rises with a at fixed N, so a faster wave wants a LONGER primary
        // to bring the reflection back at the same crank angle. The direction
        // that IS shorter is the other reading of the same relation — at a
        // fixed length, a wrapped header tunes at a HIGHER rpm. We assert what
        // the physics gives rather than the sentence; see docs/physics.md
        // §1.11.
        wrapped.TunedLength.Should().BeGreaterThan(bare.TunedLength,
            "a faster wave needs a longer pipe to return at the same crank angle");

        // a ∝ √T, so the shift in wave speed must track the shift in wall
        // temperature rather than being some unrelated wobble.
        var speedRatio = wrapped.SoundSpeed / bare.SoundSpeed;
        speedRatio.Should().BeGreaterThan(1.0).And.BeLessThan(1.10,
            "tens of kelvin on a ~900 K wall is a percent or two on the sound speed, not a step change");
    }

    [Fact]
    public void Gate_switching_the_source_terms_off_is_a_diagnostic_with_a_stated_cost()
    {
        // Both flags exist so a user can isolate an effect. What they must not
        // be is a silent default — which is exactly what they were.
        var full = OperatingPointRunner.Run(Engine(), 6000.0);
        var noWall = OperatingPointRunner.Run(
            Engine(new PipeThermalSpec { WallHeatTransfer = false }), 6000.0);
        var neither = OperatingPointRunner.Run(
            Engine(new PipeThermalSpec { Friction = false, WallHeatTransfer = false }), 6000.0);

        output.WriteLine($"friction + wall : VE {full.VolumetricEfficiency:F4}, torque {full.TorqueNm:F2} N·m");
        output.WriteLine($"friction only   : VE {noWall.VolumetricEfficiency:F4}, torque {noWall.TorqueNm:F2} N·m");
        output.WriteLine($"neither         : VE {neither.VolumetricEfficiency:F4}, torque {neither.TorqueNm:F2} N·m");

        var frictionCost = 1.0 - (noWall.TorqueNm / neither.TorqueNm);
        var wallCost = 1.0 - (full.TorqueNm / noWall.TorqueNm);
        output.WriteLine($"friction costs {frictionCost * 100:F2}% torque, the wall a further {wallCost * 100:F2}%");

        neither.TorqueNm.Should().BeGreaterThan(noWall.TorqueNm,
            "an adiabatic frictionless pipe flatters the engine");
        noWall.TorqueNm.Should().BeGreaterThan(full.TorqueNm,
            "a 330 K intake wall heats the charge and costs density");

        // Bounds, not point values: this is a sanity band on the size of the
        // effect, so a future change that makes it 30% cannot pass unnoticed.
        frictionCost.Should().BeInRange(0.001, 0.03);
        wallCost.Should().BeInRange(0.005, 0.10);
    }

    [Fact]
    public void A_fixed_wall_stays_where_it_was_put_and_reports_how_far_off_balance_it_is()
    {
        var document = Engine(new PipeThermalSpec
        {
            FixExhaustWall = true,
            ExhaustWallStartK = 500.0,
        });
        var (engine, _) = Converge(document);

        var wall = engine.Ducts.First(d => d.Wall is not null && d.Geometry.Length >= 0.4).Wall!;

        wall.Temperature.Should().OnlyContain(t => Math.Abs(t - 500.0) < 1e-9,
            "an imposed wall temperature is an input, not a starting guess");
        wall.LastChange.Should().Be(0.0);

        // It is out of balance by construction — 500 K is far below where this
        // header wants to sit — and the residual is how the user finds out.
        output.WriteLine($"imposed 500 K, residual {wall.LastResidual:F0} W/m²");
        wall.LastResidual.Should().BeGreaterThan(0.0,
            "holding a wall away from its balance point means net heat flow, and the user should be able to see it");
    }

    [Fact]
    public void An_unknown_surface_treatment_is_rejected_by_name()
    {
        var act = () => WallSurface.ByName("titanium foil");
        act.Should().Throw<ArgumentException>().WithMessage("*Bare stainless*");

        // Documents should stay readable, so the obvious short names work.
        WallSurface.ByName("Header wrap").Should().BeSameAs(WallSurface.Wrapped);
        WallSurface.ByName("wrap").Should().BeSameAs(WallSurface.Wrapped);
        WallSurface.ByName("BARE STAINLESS (OXIDISED)").Should().BeSameAs(WallSurface.BareStainless);
    }
}
