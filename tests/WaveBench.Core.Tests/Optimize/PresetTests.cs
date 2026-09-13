using FluentAssertions;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.Optimize;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// The §9.7 presets, and the valve-to-piston clearance constraint the
/// cam-timing one rests on.
///
/// <b>The clearance model is the part worth testing hardest.</b> A clearance
/// check that reads as a safety limit while permitting the collision it
/// appears to prevent is worse than no check, and the classic way to build one
/// is to evaluate at TDC — where the piston is highest and both valves are
/// nearly shut.
/// </summary>
public class PresetTests(ITestOutputHelper output)
{
    private static EngineModelDocument FourCylinder() => new()
    {
        Name = "preset test",
        Engine = new EngineSpec
        {
            BoreMm = 86, StrokeMm = 86, RodLengthMm = 145, CompressionRatio = 10.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 28, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 370 },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
        ExhaustRunner = new DuctSpec { LengthMm = 450, DiameterMm = 38 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
    };

    // ---- The clearance model ------------------------------------------------

    [Fact]
    public void Gate_the_pinch_point_is_not_at_top_dead_centre()
    {
        // The piston is highest AT tdc — but both valves are near their seats
        // there. The tightest gap is 10–20° either side, where the piston has
        // barely dropped and the valve is substantially open. A check
        // evaluated at TDC alone passes an engine that bends a valve, which is
        // why this constraint sweeps the whole overlap window.
        var document = FourCylinder();

        // Wide overlap, so there is genuinely something to catch.
        document.IntakeValves.OpenDeg = 330;
        document.ExhaustValves.CloseDeg = 390;

        var clearance = new ValveClearance(ClearanceAtTdcMm: 4.0, MinimumMm: 1.5);

        var crank = new WaveBench.Core.EngineModel.CrankGeometry
        {
            Bore = document.Engine.BoreMm * 1e-3,
            Stroke = document.Engine.StrokeMm * 1e-3,
            RodLength = document.Engine.RodLengthMm * 1e-3,
            CompressionRatio = document.Engine.CompressionRatio,
        };

        var intake = WaveBench.Core.EngineModel.CamProfile.Harmonic(
            document.IntakeValves.OpenDeg, document.IntakeValves.CloseDeg, document.IntakeValves.MaxLiftMm * 1e-3);
        var exhaust = WaveBench.Core.EngineModel.CamProfile.Harmonic(
            document.ExhaustValves.OpenDeg, document.ExhaustValves.CloseDeg, document.ExhaustValves.MaxLiftMm * 1e-3);

        var atTdc = crank.PistonPosition(360.0);

        double GapAt(double angle)
        {
            var dropped = (crank.PistonPosition(angle) - atTdc) * 1000.0;
            return 4.0 + dropped - Math.Max(intake.Lift(angle), exhaust.Lift(angle)) * 1000.0;
        }

        var overall = clearance.Minimum(document);
        var atTdcGap = GapAt(360.0);

        // Where the minimum actually is.
        var worstAngle = 360.0;
        var worst = double.PositiveInfinity;
        for (var a = 300.0; a <= 420.0; a += 0.5)
        {
            if (GapAt(a) < worst)
            {
                worst = GapAt(a);
                worstAngle = a;
            }
        }

        output.WriteLine($"gap at TDC: {atTdcGap:F2} mm");
        output.WriteLine($"tightest gap: {overall:F2} mm at {worstAngle:F0}° ({worstAngle - 360:+0;-0}° from TDC)");

        overall.Should().BeLessThan(atTdcGap,
            "the tightest point is not at TDC, and a check evaluated there would report a gap that does not "
            + "exist at the angle that matters");
        Math.Abs(worstAngle - 360.0).Should().BeGreaterThan(3.0, "the pinch point is off TDC");
    }

    [Fact]
    public void Clearance_falls_as_overlap_widens()
    {
        var clearance = new ValveClearance(ClearanceAtTdcMm: 4.0);

        var tight = FourCylinder();
        tight.IntakeValves.OpenDeg = 355;
        tight.ExhaustValves.CloseDeg = 365;

        var wide = FourCylinder();
        wide.IntakeValves.OpenDeg = 320;
        wide.ExhaustValves.CloseDeg = 400;

        var tightGap = clearance.Minimum(tight);
        var wideGap = clearance.Minimum(wide);

        output.WriteLine(
            $"overlap {OptimisationPresets.Overlap(tight):F0}° → {tightGap:F2} mm; "
            + $"overlap {OptimisationPresets.Overlap(wide):F0}° → {wideGap:F2} mm");

        wideGap.Should().BeLessThan(tightGap, "more overlap is less clearance — that is the whole trade");
    }

    [Fact]
    public void More_lift_is_less_clearance()
    {
        var clearance = new ValveClearance(ClearanceAtTdcMm: 4.0);

        var mild = FourCylinder();
        mild.IntakeValves.MaxLiftMm = 8;
        mild.ExhaustValves.MaxLiftMm = 8;
        mild.IntakeValves.OpenDeg = 330;

        var wild = FourCylinder();
        wild.IntakeValves.MaxLiftMm = 13;
        wild.ExhaustValves.MaxLiftMm = 13;
        wild.IntakeValves.OpenDeg = 330;

        clearance.Minimum(wild).Should().BeLessThan(clearance.Minimum(mild));
        output.WriteLine($"8 mm lift → {clearance.Minimum(mild):F2} mm; 13 mm → {clearance.Minimum(wild):F2} mm");
    }

    // ---- The cam-timing preset ----------------------------------------------

    [Fact]
    public void Gate_the_cam_preset_never_returns_a_design_that_would_hit_a_piston()
    {
        // Plan Phase 22's gate names this constraint. Without it the answer is
        // reliably "more overlap" — which breathes beautifully and bends a
        // valve on the first start.
        var session = new ProjectSession(FourCylinder());
        var workspace = new OptimiseWorkspace(session);

        var clearance = new ValveClearance(ClearanceAtTdcMm: 3.0, MinimumMm: 1.5);
        OptimisationPresets.CamTiming(clearance).Apply(workspace);

        workspace.Variables.Select(v => v.Path).Should().BeEquivalentTo(
            "IntakeValves.OpenDeg", "IntakeValves.CloseDeg",
            "ExhaustValves.OpenDeg", "ExhaustValves.CloseDeg");
        workspace.Constraints.Should().ContainSingle();

        workspace.Budget = 120;
        workspace.Run(new OverlapLoves());

        var best = workspace.LastResult!.Best;
        var chosen = best.Design.Materialise(session.Document);
        var margin = clearance.Minimum(chosen);

        output.WriteLine(
            $"chosen: overlap {OptimisationPresets.Overlap(chosen):F0}°, LSA "
            + $"{OptimisationPresets.LobeSeparation(chosen):F1}°, clearance {margin:F2} mm");

        best.Feasible.Should().BeTrue();
        margin.Should().BeGreaterThanOrEqualTo(1.5 - 1e-9, "the gate says never");

        // And the constraint really was binding — an objective that wants
        // unlimited overlap would otherwise have taken it.
        var everyDesign = workspace.Archive!.Designs;
        everyDesign.Should().Contain(d => !d.Feasible,
            "the search must have explored designs the constraint rejected, or it was never tested");

        output.WriteLine(
            $"{everyDesign.Count(d => !d.Feasible)} of {everyDesign.Count} designs were rejected on clearance");
    }

    /// <summary>
    /// An objective that rewards overlap without limit — the adversary the
    /// clearance constraint exists to stop. If the constraint is not enforced,
    /// this drives the search straight into the piston.
    /// </summary>
    private sealed class OverlapLoves : IDesignEvaluator
    {
        public IReadOnlyList<EvaluationFidelity> Fidelities => [EvaluationFidelity.Solved];

        public DesignEvaluation Evaluate(
            DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default)
        {
            var ivo = design.ValueOf("IntakeValves.OpenDeg");
            var evc = design.ValueOf("ExhaustValves.CloseDeg");
            var overlap = Math.Max(0.0, evc - ivo);

            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Sweep =
                [
                    new OperatingPointResult
                    {
                        Rpm = 6000,
                        VolumetricEfficiency = 0.9,
                        ImepPa = 1e6,
                        BmepPa = 9e5,
                        TorqueNm = 100.0 + overlap,
                        PowerW = 5e4,
                        BsfcGPerKwh = 280,
                        PeakPressurePa = 6e6,
                        KnockIntegral = 0.2,
                        CyclesToConvergence = 4,
                    },
                ],
            };
        }
    }

    // ---- The study ----------------------------------------------------------

    [Fact]
    public void The_cam_study_says_whether_variable_timing_is_worth_the_mechanism()
    {
        // Plan §9.7's "result view plotting optimum LCA against rpm so the user
        // can see whether VVT is worth the complexity." The SPREAD is the
        // answer: flat means a fixed cam gives away nothing.
        var session = new ProjectSession(FourCylinder());
        var workspace = new OptimiseWorkspace(session);
        var clearance = new ValveClearance(ClearanceAtTdcMm: 4.0, MinimumMm: 1.0);

        OptimisationPresets.CamTiming(clearance).Apply(workspace);

        var study = OptimisationPresets.CamTimingStudy(
            workspace,
            [3000, 5000, 7000],
            clearance,
            budget: 40,
            evaluator: new SpeedDependentOptimum());

        output.WriteLine("   rpm      LSA   overlap   clearance");
        foreach (var point in study)
        {
            output.WriteLine(
                $"{point.Rpm,6:N0}  {point.LobeSeparationDeg,7:F1}  {point.OverlapDeg,7:F0}  {point.ClearanceMm,9:F2}");
        }

        study.Should().HaveCount(3);
        study.Should().OnlyContain(p => p.ClearanceMm >= 1.0 - 1e-9,
            "every point in the study is a design that could actually be built");

        // The evaluator below genuinely wants more overlap at higher speed, so
        // the study must find the optimum MOVING — if it reported a flat line
        // here it would be telling a user that VVT is pointless on an engine
        // where it plainly is not.
        var spread = study.Max(p => p.OverlapDeg) - study.Min(p => p.OverlapDeg);
        output.WriteLine($"overlap spread across the band: {spread:F0}° crank");
        spread.Should().BeGreaterThan(5.0);

        var chart = OptimisationPresets.CamTimingChart(study);
        chart.Series.Should().Contain(s => s.Name == "Optimum LSA");
        chart.Series.Should().Contain(s => s.Name == "One fixed cam");
        chart.Notes.Should().Contain(n => n.Contains("clearance limit"));

        foreach (var series in chart.Series)
        {
            series.Y.Should().OnlyContain(v => double.IsFinite(v), $"series '{series.Name}'");
        }
    }

    /// <summary>An engine that wants more overlap as it revs — which is what a real one does.</summary>
    private sealed class SpeedDependentOptimum : IDesignEvaluator
    {
        public IReadOnlyList<EvaluationFidelity> Fidelities => [EvaluationFidelity.Solved];

        public DesignEvaluation Evaluate(
            DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default)
        {
            var ivo = design.ValueOf("IntakeValves.OpenDeg");
            var evc = design.ValueOf("ExhaustValves.CloseDeg");
            var overlap = Math.Max(0.0, evc - ivo);

            // The study sets a single operating point per run; read it back so
            // the "best overlap" moves with speed.
            const double rpm = 5000.0;
            _ = rpm;

            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Sweep = new[] { 3000.0, 5000.0, 7000.0 }.Select(speed =>
                {
                    // Wants 10° of overlap at 3000 and 45° at 7000.
                    var wanted = 10.0 + (35.0 * (speed - 3000.0) / 4000.0);
                    var penalty = Math.Pow(overlap - wanted, 2.0) * 0.02;

                    return new OperatingPointResult
                    {
                        Rpm = speed,
                        VolumetricEfficiency = 0.9,
                        ImepPa = 1e6,
                        BmepPa = 9e5,
                        TorqueNm = 150.0 - penalty,
                        PowerW = 5e4,
                        BsfcGPerKwh = 280,
                        PeakPressurePa = 6e6,
                        KnockIntegral = 0.2,
                        CyclesToConvergence = 4,
                    };
                }).ToList(),
            };
        }
    }

    // ---- Preset wiring -------------------------------------------------------

    [Fact]
    public void Presets_are_offered_only_where_they_make_sense()
    {
        var na = OptimisationPresets.For(FourCylinder());
        na.Should().NotContain(p => p.Id == "boost-trade",
            "a response-against-power front is not a question a naturally aspirated engine has");
        na.Should().Contain(p => p.Id == "cam-timing").And.Contain(p => p.Id == "intake-tuning");

        var boosted = FourCylinder();
        boosted.ForcedInduction.Aspiration = AspirationKinds.Turbocharged;
        OptimisationPresets.For(boosted).Should().Contain(p => p.Id == "boost-trade");

        // Every preset says what it is asking and what it costs.
        foreach (var preset in OptimisationPresets.All())
        {
            preset.Why.Should().NotBeNullOrWhiteSpace();
            preset.Name.Should().NotBeNullOrWhiteSpace();
            output.WriteLine($"{preset.Name}: {preset.Why}");
        }
    }

    [Fact]
    public void Applying_a_preset_replaces_the_previous_run_definition_rather_than_adding_to_it()
    {
        // Two presets applied in turn must not leave the variables of the first
        // in the second's search — a silently merged definition optimises
        // something nobody asked for.
        var session = new ProjectSession(FourCylinder());
        var workspace = new OptimiseWorkspace(session);

        OptimisationPresets.CamTiming().Apply(workspace);
        workspace.Variables.Should().HaveCount(4);
        workspace.Constraints.Should().ContainSingle();

        OptimisationPresets.IntakeTuning.Apply(workspace);
        workspace.Variables.Select(v => v.Path).Should().BeEquivalentTo(
            "IntakeRunner.LengthMm", "IntakeRunner.DiameterMm");
        workspace.Constraints.Should().BeEmpty("the cam preset's clearance limit does not apply to this question");
        workspace.Objectives.Should().BeEmpty("and its objectives are the workspace's default again");
    }

    [Fact]
    public void The_boost_preset_asks_for_a_front_rather_than_a_single_answer()
    {
        var boosted = FourCylinder();
        boosted.ForcedInduction.Aspiration = AspirationKinds.Turbocharged;

        var workspace = new OptimiseWorkspace(new ProjectSession(boosted));
        OptimisationPresets.BoostTrade.Apply(workspace);

        workspace.Algorithm.Should().Be(SearchAlgorithm.NsgaII,
            "there is no single best answer to the response-against-power trade, only a choice");
        workspace.Objectives.Should().HaveCount(2);
        workspace.Variables.Select(v => v.Path).Should().BeEquivalentTo(
            "ForcedInduction.TargetBoostKPa", "ForcedInduction.TurbineAreaRatio");
    }

    [Fact]
    public void Lobe_separation_and_overlap_are_computed_the_way_a_cam_grinder_would()
    {
        // A symmetric cam: intake centred 110° after TDC, exhaust 110° before.
        // Lobe separation is then 110° of cam by definition.
        var document = FourCylinder();
        document.IntakeValves.OpenDeg = 360 + 110 - 115;
        document.IntakeValves.CloseDeg = 360 + 110 + 115;
        document.ExhaustValves.OpenDeg = 360 - 110 - 115;
        document.ExhaustValves.CloseDeg = 360 - 110 + 115;

        OptimisationPresets.LobeSeparation(document).Should().BeApproximately(110.0, 1e-9);

        // Overlap: intake opens at 355, exhaust closes at 365 → 10° crank.
        output.WriteLine(
            $"LSA {OptimisationPresets.LobeSeparation(document):F1}° cam, "
            + $"overlap {OptimisationPresets.Overlap(document):F0}° crank");
        OptimisationPresets.Overlap(document).Should().BeApproximately(10.0, 1e-9);
    }
}
