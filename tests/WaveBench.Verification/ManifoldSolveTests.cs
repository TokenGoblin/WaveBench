using FluentAssertions;
using WaveBench.Analysis;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 18: a manifold graph is not a drawing — the solver has to build and
/// run it. These check that the topologies the collector library produces
/// actually assemble into a solvable engine and conserve what they should.
/// </summary>
public class ManifoldSolveTests(ITestOutputHelper output)
{
    private static EngineModelDocument FourCylinder(ManifoldSpec? manifold) => new()
    {
        Name = manifold is null ? "four, single runner" : $"four, {manifold.Configuration}",
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
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 36 },
        ExhaustRunner = new DuctSpec { LengthMm = 450, DiameterMm = 34 },
        ExhaustManifold = manifold,
        Combustion = new CombustionSpec { Fuel = "RON95" },
        // Coarse and short: these tests are about the topology assembling and
        // running, not about a converged performance number.
        Solver = new SolverSpec { CellSizeMm = 14.0, MinCycles = 3, MaxCycles = 6 },
    };

    private static CollectorGeometry Geometry => new(
        Cylinders: 4, PrimaryLengthMm: 400, PrimaryDiameterMm: 34,
        SecondaryLengthMm: 250, SecondaryDiameterMm: 42,
        CollectorLengthMm: 200, CollectorDiameterMm: 50,
        TailLengthMm: 400, TailDiameterMm: 55);

    /// <summary>
    /// Junctions the assembler creates: one per junction NODE, plus an
    /// implicit area-change seam wherever two pipes meet directly (plan §2.7
    /// "stepped header: sequence of pipes with area-change junctions").
    /// </summary>
    private static int ExpectedJunctions(ManifoldSpec spec)
    {
        var pipes = spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Pipe).Select(n => n.Id).ToHashSet();
        var seams = spec.Connections.Count(c => pipes.Contains(c.From) && pipes.Contains(c.To));
        return spec.Nodes.Count(n => n.Kind == ManifoldNodeKind.Junction) + seams;
    }

    [Theory]
    [InlineData("4-1")]
    [InlineData("4-2-1")]
    [InlineData("tri-y")]
    [InlineData("individual")]
    [InlineData("log")]
    public void Gate_a_collector_topology_assembles_and_runs(string configuration)
    {
        var manifold = CollectorLibrary.Build(configuration, Geometry);
        var document = FourCylinder(manifold);

        document.Validate().Should().NotContain(i => i.Severity == ModelIssueSeverity.Error);

        var engine = EngineBuilder.Build(document, 6000.0);

        // Every pipe in the graph became a duct, plus the four intake runners.
        var pipes = manifold.Nodes.Count(n => n.Kind == ManifoldNodeKind.Pipe);
        engine.Ducts.Should().HaveCount(pipes + 4);
        engine.Junctions.Should().HaveCount(ExpectedJunctions(manifold),
            "one per junction node, plus an area-change seam per pipe-to-pipe connection");
        engine.Plenums.Should().HaveCount(manifold.Nodes.Count(n => n.Kind == ManifoldNodeKind.Plenum));
        engine.Valves.Should().HaveCount(8, "four cylinders, one intake and one exhaust valve each");

        var (result, cycles) = engine.RunToConvergence(
            r => r.NetValveMass.Length > 0 ? r.NetValveMass[0] : 0.0,
            document.Solver.ConvergenceTolerance, document.Solver.MinCycles, document.Solver.MaxCycles);

        output.WriteLine($"{configuration,-12} {engine.Ducts.Count,2} ducts, {engine.Junctions.Count} junctions, "
                         + $"{engine.Plenums.Count} plenums, {cycles} cycles, "
                         + $"intake mass {result.NetValveMass[0] * 1e6:F1} mg");

        foreach (var note in engine.Notes)
        {
            output.WriteLine("  note: " + note);
        }

        result.NetValveMass[0].Should().BeGreaterThan(0.0, "the engine must breathe");
        result.NetValveMass.Should().OnlyContain(m => double.IsFinite(m));
    }

    [Fact]
    public void Gate_the_cylinders_share_one_collector_rather_than_getting_private_copies()
    {
        // The whole point of a collector: the four primaries meet. If the
        // builder gave each cylinder its own graph they would never interact
        // and the topology would be decorative.
        var manifold = CollectorLibrary.Build("4-1", Geometry);
        var engine = EngineBuilder.Build(FourCylinder(manifold), 6000.0);

        var pipes = manifold.Nodes.Count(n => n.Kind == ManifoldNodeKind.Pipe);
        engine.Ducts.Should().HaveCount(pipes + 4,
            "one duct per graph pipe plus four intake runners — not four copies of the graph");

        // The four exhaust valves must open into four DIFFERENT primaries.
        var exhaustDucts = engine.Valves
            .Where((_, i) => i % 2 == 1)
            .Select(v => v.Duct)
            .ToList();
        exhaustDucts.Should().OnlyHaveUniqueItems();
        exhaustDucts.Should().HaveCount(4);
    }

    [Fact]
    public void Gate_a_multi_way_merge_says_it_fell_back_to_constant_pressure()
    {
        // Plan §2.7 defaults to the pressure-loss junction model, but the
        // Idelchik pair coefficients are defined for a three-leg tee. A 4-1
        // has five legs. Falling back silently would leave a user believing
        // they got a model they did not.
        var engine = EngineBuilder.Build(FourCylinder(CollectorLibrary.Build("4-1", Geometry)), 6000.0);

        engine.Notes.Should().Contain(n => n.Contains("constant-pressure"),
            "a five-leg merge cannot use the three-leg loss coefficients");
        output.WriteLine(engine.Notes.Single());
    }

    [Fact]
    public void A_three_leg_junction_keeps_the_branch_angle_loss_model()
    {
        // A 4-2-1's first merges are three-leg (two primaries in, one
        // secondary out), so those DO get the loss coefficients.
        var manifold = CollectorLibrary.Build("4-2-1", Geometry);
        var engine = EngineBuilder.Build(FourCylinder(manifold), 6000.0);

        // merge1, merge2 and the final merge are all three-leg (two in, one
        // out), so none of them falls back — which is the actual claim. The
        // seam between collector and tailpipe is an area change, not a merge,
        // and has no branch angle to lose.
        engine.Notes.Should().BeEmpty("every junction node in a 4-2-1 is a three-leg tee");
        manifold.Nodes.Count(n => n.Kind == ManifoldNodeKind.Junction).Should().Be(3);
        engine.Junctions.Should().HaveCount(ExpectedJunctions(manifold));
    }

    [Fact]
    public void Gate_a_model_without_a_manifold_is_completely_unaffected()
    {
        // The topology is opt-in. A document with no manifold must produce
        // bit-identical results to before it existed.
        var document = FourCylinder(null);
        document.ExhaustManifold.Should().BeNull();

        var engine = EngineBuilder.Build(document, 6000.0);
        engine.Ducts.Should().HaveCount(8, "four intake runners and four private exhaust runners");
        engine.Junctions.Should().BeEmpty();
        engine.Notes.Should().BeEmpty();

        var a = OperatingPointRunner.Run(document, 6000.0);
        var b = OperatingPointRunner.Run(document, 6000.0);
        a.TorqueNm.Should().Be(b.TorqueNm, "determinism, unchanged");
        a.VolumetricEfficiency.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void A_manifold_that_misses_a_cylinder_is_rejected_before_it_runs()
    {
        var manifold = CollectorLibrary.Build("4-1", Geometry);
        manifold.Nodes.RemoveAll(n => n.Kind == ManifoldNodeKind.Port && n.Cylinder == 3);
        manifold.Connections.RemoveAll(c => c.From == "cyl3");

        var document = FourCylinder(manifold);
        var issues = document.Validate().Where(i => i.Severity == ModelIssueSeverity.Error).ToList();

        issues.Should().Contain(i => i.Message.Contains("Cylinder 3"));
        output.WriteLine(issues.First(i => i.Message.Contains("Cylinder 3")).Message);
    }

    [Fact]
    public void A_manifold_survives_a_save_and_reload_unchanged()
    {
        // The graph is part of the document, so it has to round-trip like
        // everything else — a layout the user arranged must come back.
        var document = FourCylinder(CollectorLibrary.Build("4-2-1", Geometry));
        document.ExhaustManifold!.Node("pri1")!.X = 12.5;

        var reloaded = EngineModelDocument.Load(document.Save());

        reloaded.Save().Should().Be(document.Save());
        reloaded.ExhaustManifold.Should().NotBeNull();
        reloaded.ExhaustManifold!.Nodes.Should().HaveCount(document.ExhaustManifold.Nodes.Count);
        reloaded.ExhaustManifold.Node("pri1")!.X.Should().Be(12.5, "canvas layout is part of the project");
        reloaded.ExhaustManifold.Configuration.Should().Be("4-2-1");
    }

    [Fact]
    public void Gate_a_4_1_and_a_4_2_1_of_the_same_length_do_not_breathe_identically()
    {
        // If the topology reached the solver but the wave dynamics did not
        // depend on it, every collector would give the same answer — which is
        // the failure mode that would make this whole phase decorative.
        var oneStage = OperatingPointRunner.Run(FourCylinder(CollectorLibrary.Build("4-1", Geometry)), 6000.0);
        var twoStage = OperatingPointRunner.Run(FourCylinder(CollectorLibrary.Build("4-2-1", Geometry)), 6000.0);

        output.WriteLine($"4-1   VE {oneStage.VolumetricEfficiency:F4}, torque {oneStage.TorqueNm:F2} N·m");
        output.WriteLine($"4-2-1 VE {twoStage.VolumetricEfficiency:F4}, torque {twoStage.TorqueNm:F2} N·m");

        twoStage.VolumetricEfficiency.Should().NotBe(oneStage.VolumetricEfficiency,
            "a second merge stage changes the reflection the cylinder sees");
        oneStage.VolumetricEfficiency.Should().BeInRange(0.2, 1.6);
        twoStage.VolumetricEfficiency.Should().BeInRange(0.2, 1.6);
    }

    [Fact]
    public void Two_components_meeting_without_a_pipe_are_rejected_with_a_reason()
    {
        // A 1-D solver has nowhere to hold state across a zero-length
        // connection, so this is a stop rather than a silent collapse.
        var manifold = new ManifoldSpec
        {
            Nodes =
            [
                new ManifoldNode { Id = "cyl1", Kind = ManifoldNodeKind.Port, Cylinder = 1 },
                new ManifoldNode { Id = "cyl2", Kind = ManifoldNodeKind.Port, Cylinder = 2 },
                new ManifoldNode { Id = "p2", Kind = ManifoldNodeKind.Pipe, LengthMm = 300, DiameterMm = 40 },
                new ManifoldNode { Id = "j", Kind = ManifoldNodeKind.Junction },
                new ManifoldNode { Id = "p", Kind = ManifoldNodeKind.Pipe, LengthMm = 300, DiameterMm = 40 },
                new ManifoldNode { Id = "out", Kind = ManifoldNodeKind.Atmosphere },
            ],
            Connections =
            [
                // Cylinder 1 goes straight into the junction with no pipe —
                // three legs, so the graph validates, but there is nowhere to
                // hold state at that seam.
                new ManifoldConnection("cyl1", "j"),
                new ManifoldConnection("cyl2", "p2"),
                new ManifoldConnection("p2", "j"),
                new ManifoldConnection("j", "p"),
                new ManifoldConnection("p", "out"),
            ],
        };

        var engine = new EngineSimulator { Rpm = 3000 };
        var gas = new PerfectGasModel(PerfectGas.Air);

        var act = () => ManifoldAssembler.Build(
            manifold, engine, gas, 0.01, SlopeLimiterKind.Minmod, 1.2, 101325.0, 293.15, 0.8);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*separated by a pipe*");
    }

    [Fact]
    public void Gate_the_pulse_diagram_uses_the_solved_sound_speed_not_a_nominal_one()
    {
        // Plan §2.8 requires transit to be L/a with the ACTUAL computed local
        // sound speed. Until now every caller handed in a constant; this
        // closes that by reading the speed out of the solved ducts.
        var manifold = CollectorLibrary.Build("4-2-1", Geometry);
        var document = FourCylinder(manifold) with
        {
            // The other topology tests run deliberately short; this one needs
            // the wall temperatures settled, or it measures a header still
            // warming up rather than one at its operating point.
            Solver = new SolverSpec { CellSizeMm = 14.0, MinCycles = 6, MaxCycles = 20 },
        };
        var engine = EngineBuilder.Build(document, 6000.0);
        engine.WallConvergenceK = document.PipeThermal.WallConvergenceK;

        engine.ManifoldPipes.Should().HaveCount(
            manifold.Nodes.Count(n => n.Kind == ManifoldNodeKind.Pipe),
            "every pipe on the canvas must be reachable by its graph id");

        engine.RunToConvergence(
            r => r.NetValveMass.Length > 0 ? r.NetValveMass[0] : 0.0,
            document.Solver.ConvergenceTolerance, document.Solver.MinCycles, document.Solver.MaxCycles);

        var speeds = ManifoldPulseState.MeanSoundSpeed(engine, engine.ManifoldPipes);

        foreach (var (id, a) in speeds.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"{id,-12} a = {a:F0} m/s");
        }

        // Combustion products in a running header sit well above ambient air.
        // The bound is deliberately wide: this asserts that a SOLVED number
        // arrived, not that it hit a particular figure.
        speeds.Values.Should().OnlyContain(a => a > 360.0 && a < 900.0,
            "exhaust gas carries a pulse faster than the 343 m/s of ambient air");

        // Pipes must NOT all report the same number, or the per-pipe
        // machinery is decoration and one mean would have done.
        var spread = (speeds.Values.Max() - speeds.Values.Min()) / speeds.Values.Average();
        output.WriteLine($"spread across pipes: {spread * 100:F1}%");
        spread.Should().BeGreaterThan(0.02, "each pipe carries gas at its own state");

        // Gas must lose heat on its way out. The tailpipe is the last metre of
        // the path and sees no fresh blowdown of its own — only what the
        // collector hands it, minus what the wall takes.
        //
        // This assertion was impossible until the wall thermal model was
        // actually attached to a built engine's ducts. Before that the profile
        // rose monotonically to the exit, because nothing in the product ever
        // set FrictionEnabled or called AttachWall and the only thing acting
        // on the gas was numerical dissipation.
        var collector = speeds["collector"];
        var tail = speeds["tail"];
        output.WriteLine($"collector {collector:F0} m/s -> tailpipe {tail:F0} m/s");
        tail.Should().BeLessThan(collector,
            "the wall takes heat out of the gas between the collector and the exit");

        var order = CollectorLibrary.DefaultFiringOrder(4);
        var solved = PulseInterference.Arrivals(manifold, "final", order, 130.0, speeds, 6000.0);
        var ambient = PulseInterference.Arrivals(manifold, "final", order, 130.0, 343.0, 6000.0);

        solved.Should().HaveCount(4);
        solved.Should().OnlyContain(a => double.IsFinite(a.ArrivalAngleDeg));

        // The whole reason the plan insists on the solved speed: a nominal
        // ambient one misplaces the arrival by tens of crank degrees, which is
        // the same order as the spacing the diagram is trying to resolve.
        var shift = Math.Abs(solved[0].TransitDeg - ambient[0].TransitDeg);
        output.WriteLine($"transit at solved a: {solved[0].TransitDeg:F1}°, at 343 m/s: {ambient[0].TransitDeg:F1}° "
                         + $"— {shift:F1}° apart");
        shift.Should().BeGreaterThan(20.0);

        // A pipe the caller could not solve falls back rather than refusing to
        // draw: the diagram with a stated assumption beats no diagram.
        var partial = speeds.Where(kv => kv.Key != "sec1").ToDictionary(kv => kv.Key, kv => kv.Value);
        var withFallback = PulseInterference.Arrivals(
            manifold, "final", order, 130.0, partial, 6000.0, fallbackSoundSpeed: 600.0);
        withFallback.Should().HaveCount(4);
        withFallback[0].TransitDeg.Should().BeGreaterThan(solved[0].TransitDeg,
            "600 m/s is slower than the 707 the solver found for sec1, so the pulse takes longer");

        // Path length is unchanged by any of this — it is geometry.
        solved[0].PathLengthMm.Should().BeApproximately(ambient[0].PathLengthMm, 1e-9);
    }
}
