using FluentAssertions;
using WaveBench.Model;
using WaveBench.Optimize;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// Phase 22 gate, fourth clause: <i>"the power-vs-sound and response-vs-power
/// fronts are explorable with click-to-audition and click-to-inspect"</i>.
///
/// <b>Explorable is the word being tested.</b> A front that renders is not a
/// front a user can make a decision on. What the clause actually demands is
/// that clicking a point on it yields the three things a builder decides with:
/// the geometry in millimetres, the torque curve behind the score, and — on a
/// sound front — what it sounds like. Each of those is asserted here.
///
/// The evaluator is analytic so the whole suite runs in milliseconds; the same
/// workspace against the real solver is exercised by the FSAE case in
/// WaveBench.Verification.
/// </summary>
public class OptimiseWorkspaceTests(ITestOutputHelper output)
{
    private const string PowerKey = "power";
    private const string SoundKey = "sound";

    private static EngineModelDocument FourCylinder() => new()
    {
        Name = "explorer test",
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

    /// <summary>
    /// A two-objective problem with a genuine trade: a longer primary makes
    /// more "power" and worse "sound", so the front is a real curve rather
    /// than a point.
    ///
    /// Analytic, because this test is about the EXPLORER — whether a point on
    /// a front can be turned back into geometry, a curve and audio — and
    /// driving the real solver for that would test the solver instead.
    /// </summary>
    private sealed class PowerAndSound : IDesignEvaluator
    {
        public IReadOnlyList<EvaluationFidelity> Fidelities => [EvaluationFidelity.Solved];

        public DesignEvaluation Evaluate(
            DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default)
        {
            // Read whichever of these the run actually varies, and fall back
            // to the document's own value for the rest — a stub that demanded
            // a particular variable set would only be testable against one.
            var length = Of(design, "ExhaustRunner.LengthMm", 450.0);
            var diameter = Of(design, "ExhaustRunner.DiameterMm", 38.0);

            // Power peaks at a long primary and a wide pipe; order purity
            // falls away as the primary lengthens. Deliberately opposed, so
            // the front has an interior.
            var power = (200.0 - (0.0008 * Math.Pow(length - 700.0, 2.0))) + (0.4 * diameter);
            var sound = 0.99 - (0.0006 * (length - 200.0));

            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Sweep = Curve(length, diameter),
                Metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    [PowerKey] = power,
                    [SoundKey] = Math.Clamp(sound, 0.0, 1.0),
                },
            };
        }

        private static double Of(DesignPoint design, string path, double fallback) =>
            design.Space.IndexOf(path) >= 0 ? design.ValueOf(path) : fallback;

        /// <summary>A plausible torque curve, so click-to-inspect has something real to draw.</summary>
        private static IReadOnlyList<WaveBench.Core.Solver.OperatingPointResult> Curve(
            double length, double diameter)
        {
            double[] speeds = [3000, 4000, 5000, 6000, 7000, 8000];
            var peak = 3000.0 + (600_000.0 / Math.Max(length, 1.0));

            return speeds.Select(rpm => new WaveBench.Core.Solver.OperatingPointResult
            {
                Rpm = rpm,
                VolumetricEfficiency = 0.95,
                ImepPa = 1e6,
                BmepPa = 9e5,
                TorqueNm = (180.0 + (0.5 * diameter)) * Math.Exp(-Math.Pow((rpm - peak) / 2600.0, 2.0)),
                PowerW = 5e4,
                BsfcGPerKwh = 280,
                PeakPressurePa = 6e6,
                KnockIntegral = 0.3,
                CyclesToConvergence = 5,
            }).ToList();
        }
    }

    private static OptimiseWorkspace Explored(out ProjectSession session, int budget = 400)
    {
        session = new ProjectSession(FourCylinder());
        var workspace = new OptimiseWorkspace(session);

        // A two-variable exhaust problem, so the audition has a primary
        // length to work with.
        foreach (var path in workspace.Variables.Select(v => v.Path).ToList())
        {
            workspace.Remove(path);
        }

        workspace.Add("ExhaustRunner.LengthMm").Should().BeTrue();
        workspace.Add("ExhaustRunner.DiameterMm").Should().BeTrue();

        workspace.Objectives.Add(new MetricObjective("Peak power", "kW", PowerKey, ObjectiveSense.Maximise));
        workspace.Objectives.Add(new MetricObjective("Order purity", "", SoundKey, ObjectiveSense.Maximise));

        workspace.Algorithm = SearchAlgorithm.NsgaII;
        workspace.Budget = budget;
        workspace.Run(new PowerAndSound());

        return workspace;
    }

    // ---- Gate clause 4 ------------------------------------------------------

    [Fact]
    public void Gate_the_power_versus_sound_front_is_explorable()
    {
        var workspace = Explored(out _);

        var front = workspace.FrontDesigns;
        output.WriteLine($"{front.Count} designs on the front of {workspace.Archive!.Count} evaluated");
        output.WriteLine(workspace.Summary()!.ToString());

        front.Should().HaveCountGreaterThan(5, "a front of a handful of points is not explorable");

        // It is a real trade: more power really does cost order purity here,
        // so the front spans both objectives rather than clustering.
        var power = front.Select(d => d.Objectives[0]).ToList();
        var sound = front.Select(d => d.Objectives[1]).ToList();

        output.WriteLine($"power spans {power.Min():F1}–{power.Max():F1} kW");
        output.WriteLine($"purity spans {sound.Min():F3}–{sound.Max():F3}");

        (power.Max() - power.Min()).Should().BeGreaterThan(1.0);
        (sound.Max() - sound.Min()).Should().BeGreaterThan(0.02);

        // The front is monotone: that IS what a Pareto front means, and a
        // "front" where one design beats another on both axes is not one.
        var ordered = front.OrderBy(d => d.Objectives[0]).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            ordered[i].Objectives[1].Should().BeLessThanOrEqualTo(ordered[i - 1].Objectives[1] + 1e-9,
                "more power must cost purity, or these two designs are not both optimal");
        }

        // The chart renders, names both objectives in their own units, and
        // marks the baseline the user is deciding against.
        var chart = workspace.ParetoChart();
        chart.Series.Should().Contain(s => s.Name == "Front");
        chart.Series.Should().Contain(s => s.Name == "Baseline");
        chart.Series.Should().Contain(s => s.Name == "Explored");
        chart.XAxis.Label.Should().Be("Peak power");
        chart.YAxis.Label.Should().Be("Order purity");
        chart.Notes.Should().Contain(n => n.Contains("baseline sits at"));

        foreach (var series in chart.Series)
        {
            series.X.Should().OnlyContain(v => double.IsFinite(v), $"series '{series.Name}'");
            series.Y.Should().OnlyContain(v => double.IsFinite(v), $"series '{series.Name}'");

            // Everything drawn must be INSIDE the axes. Ranging the axes over
            // the front alone put the baseline off the bottom of the chart
            // while the note underneath quoted its coordinates — and comparing
            // against the baseline is the only question a user has here.
            series.X.Should().OnlyContain(
                v => v >= chart.XAxis.Min - 1e-9 && v <= chart.XAxis.Max + 1e-9,
                $"series '{series.Name}' must fit the x axis");
            series.Y.Should().OnlyContain(
                v => v >= chart.YAxis.Min - 1e-9 && v <= chart.YAxis.Max + 1e-9,
                $"series '{series.Name}' must fit the y axis");
        }
    }

    [Fact]
    public void Gate_clicking_a_front_design_yields_its_geometry_in_millimetres()
    {
        // Plan §9.7: "always show the geometry, not just the number." A front
        // point that cannot be turned back into millimetres is a score, and
        // nobody builds a score.
        var workspace = Explored(out _);

        workspace.SelectedIndex = workspace.FrontDesigns.Count / 2;
        var geometry = workspace.InspectGeometry();

        foreach (var readout in geometry)
        {
            output.WriteLine($"{readout.Label,-28} {readout.Value,10}   {readout.Note}");
        }

        geometry.Should().HaveCountGreaterThanOrEqualTo(2);
        geometry.Should().Contain(r => r.Label.Contains("primary length", StringComparison.OrdinalIgnoreCase));
        geometry.Should().Contain(r => r.Label.Contains("primary Ø", StringComparison.OrdinalIgnoreCase));

        // Every row says what it is against the baseline, because "310 mm" is
        // only actionable next to "you had 450".
        geometry.Take(2).Should().OnlyContain(r => r.Note != null && r.Note.Contains("baseline"));
    }

    [Fact]
    public void Gate_clicking_a_front_design_yields_the_torque_curve_behind_its_score()
    {
        // An area under a curve is one number, and a design that won it by
        // spiking at one speed while collapsing either side is not the design
        // anyone wanted. The curve is the only thing that tells them apart.
        var workspace = Explored(out _);
        workspace.SelectedIndex = 0;

        var chart = workspace.InspectTorque(new PowerAndSound());

        chart.Series.Should().Contain(s => s.Name == "Selected");
        chart.Series.Should().Contain(s => s.Name == "Baseline",
            "a curve with nothing to compare it against is half an answer");
        chart.Subtitle.Should().Contain("primary length", Exactly.Once());

        var selected = chart.Series.Single(s => s.Name == "Selected");
        selected.Y.Should().OnlyContain(v => double.IsFinite(v));
        selected.Y.Should().Contain(v => v > 0);

        output.WriteLine($"{chart.Title} — {chart.Subtitle}");
        output.WriteLine(
            $"selected peaks at {selected.Y.Max():F1} N·m, baseline at "
            + $"{chart.Series.Single(s => s.Name == "Baseline").Y.Max():F1} N·m");
    }

    [Fact]
    public void Gate_clicking_a_front_design_yields_a_level_matched_audition()
    {
        var workspace = Explored(out _);
        workspace.AuditionIsMeaningful.Should().BeTrue("the run varied the primary length, which is what is heard");

        // Two designs far apart on the front, so the comparison is a real one.
        workspace.SelectedIndex = 0;
        var first = workspace.Audition();
        workspace.SelectedIndex = workspace.FrontDesigns.Count - 1;
        var last = workspace.Audition();

        first.Should().NotBeNull();
        last.Should().NotBeNull();

        foreach (var audition in new[] { first!, last! })
        {
            audition.A.Samples.Length.Should().BeGreaterThan(0);
            audition.B.Samples.Length.Should().Be(audition.A.Samples.Length, "an A/B must be gapless");
            audition.A.Samples.Should().OnlyContain(s => float.IsFinite(s));
            audition.B.Samples.Should().OnlyContain(s => float.IsFinite(s));
        }

        // The two ends of the front genuinely sound different — the primary
        // length that separates them is exactly what sets collector arrival
        // timing.
        var shortest = workspace.FrontDesigns[0].Values[0];
        var longest = workspace.FrontDesigns[^1].Values[0];
        output.WriteLine($"front spans primaries {shortest:F0}–{longest:F0} mm");

        first!.B.Samples.Should().NotEqual(last!.B.Samples,
            "two designs with different primary lengths must not render identical audio");
    }

    [Fact]
    public void An_audition_that_would_be_two_identical_clips_says_so_instead()
    {
        // Playing two identical clips and inviting someone to hear a
        // difference is worse than offering nothing at all.
        var session = new ProjectSession(FourCylinder());
        var workspace = new OptimiseWorkspace(session);

        foreach (var path in workspace.Variables.Select(v => v.Path).ToList())
        {
            workspace.Remove(path);
        }

        workspace.Add("IntakeRunner.LengthMm");
        workspace.Objectives.Add(new MetricObjective("Peak power", "kW", PowerKey, ObjectiveSense.Maximise));
        workspace.Budget = 40;
        workspace.Run(new PowerAndSound());

        workspace.AuditionIsMeaningful.Should().BeFalse(
            "this run never touched the exhaust, so both stems would be the same collector");
    }

    // ---- The parallel-coordinates view --------------------------------------

    [Fact]
    public void Three_objectives_are_drawn_as_parallel_coordinates_with_up_meaning_better()
    {
        // Plan §9.6: "three objectives as a parallel-coordinates plot, which
        // is more readable than a 3D front."
        var session = new ProjectSession(FourCylinder());
        var workspace = new OptimiseWorkspace(session);

        foreach (var path in workspace.Variables.Select(v => v.Path).ToList())
        {
            workspace.Remove(path);
        }

        workspace.Add("ExhaustRunner.LengthMm");
        workspace.Add("ExhaustRunner.DiameterMm");

        workspace.Objectives.Add(new MetricObjective("Peak power", "kW", PowerKey, ObjectiveSense.Maximise));
        workspace.Objectives.Add(new MetricObjective("Order purity", "", SoundKey, ObjectiveSense.Maximise));
        workspace.Objectives.Add(new PeakOf("Peak torque", "N·m", p => p.TorqueNm));

        workspace.Algorithm = SearchAlgorithm.NsgaII;
        workspace.Budget = 400;
        workspace.Run(new PowerAndSound());

        var chart = workspace.ParallelCoordinates();
        output.WriteLine($"{chart.Title}: {chart.Series.Count} lines");
        output.WriteLine(chart.Notes[0]);

        chart.Series.Should().NotBeEmpty();
        chart.YAxis.Label.Should().Contain("Better");

        // Every axis is normalised into [0, 1] with up meaning better,
        // whatever that objective's own sense is — a plot where some axes mean
        // "more" and others "less" is one every reader misreads once.
        foreach (var series in chart.Series)
        {
            series.Y.Should().OnlyContain(v => v >= -1e-9 && v <= 1.0 + 1e-9, $"series '{series.Name}'");
            series.X.Should().HaveCount(3, "one point per objective");
        }

        chart.Series.Should().Contain(s => s.Name == "Baseline");
        chart.Notes[0].Should().Contain("Peak power").And.Contain("Order purity").And.Contain("Peak torque");
    }

    // ---- Definition and run -------------------------------------------------

    [Fact]
    public void A_new_run_suggests_the_variables_that_dominate_rather_than_everything()
    {
        // A twelve-variable search costs roughly the square of a
        // three-variable one. The honest default is the small set, plus a
        // screening pass to find out whether the rest are worth adding.
        var workspace = new OptimiseWorkspace(new ProjectSession(FourCylinder()));

        output.WriteLine("suggested: " + string.Join(", ", workspace.Variables.Select(v => v.Name)));
        output.WriteLine("available: " + string.Join(", ", workspace.Available.Select(f => f.Label)));

        workspace.Variables.Should().HaveCountLessThanOrEqualTo(4);
        workspace.Variables.Should().Contain(v => v.Path == "IntakeRunner.LengthMm");
        workspace.Available.Should().NotBeEmpty("the rest are offered, just not assumed");

        // Every suggested variable arrives with bounds a builder would
        // recognise and a grid it can be cut to.
        workspace.Variables.Should().OnlyContain(v => v.Maximum > v.Minimum);
        workspace.Variables.Should().OnlyContain(v => v.Step != null && v.Step > 0);
    }

    [Fact]
    public void Forced_induction_variables_are_offered_only_on_a_boosted_model()
    {
        var na = OptimisationCatalogue.For(FourCylinder());
        na.Should().NotContain(f => f.Path.StartsWith("ForcedInduction", StringComparison.Ordinal),
            "a turbine A/R on a naturally aspirated engine is not a variable");

        var boosted = FourCylinder();
        boosted.ForcedInduction.Aspiration = AspirationKinds.Turbocharged;

        OptimisationCatalogue.For(boosted).Should()
            .Contain(f => f.Path == "ForcedInduction.TurbineAreaRatio")
            .And.Contain(f => f.Path == "ForcedInduction.TargetBoostKPa");
    }

    [Fact]
    public void The_run_scores_the_baseline_first_and_says_what_it_improved_on()
    {
        var workspace = Explored(out _);

        workspace.Baseline.Should().NotBeNull();
        workspace.Log.Should().Contain(l => l.Contains("baseline scored"));
        workspace.Log.Should().Contain(l => l.Contains("on the front"));

        foreach (var line in workspace.Log)
        {
            output.WriteLine(line);
        }

        // The baseline is IN the archive, so the explorer can draw it against
        // the front rather than beside it.
        workspace.Archive!.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void The_archive_survives_a_checkpoint_and_comes_back_the_same()
    {
        // Plan §9.5: checkpoint/resume. A killed process must come back with
        // its designs, or an overnight run is one power cut from nothing.
        var workspace = Explored(out _, budget: 120);
        var archive = workspace.Archive!;

        var json = archive.Save();
        var restored = DesignArchive.Load(json, archive.Space, archive.Objectives);

        restored.Count.Should().Be(archive.Count);
        restored.RunId.Should().Be(archive.RunId);
        restored.Front().Count.Should().Be(archive.Front().Count);
        restored.Save().Should().Be(json, "save → load → save must be a fixed point");

        // ...and it can seed a new search, at which point the cache makes
        // every archived design free the second time.
        restored.Resume(10).Should().HaveCountLessThanOrEqualTo(10);
        restored.Resume(10).Should().OnlyContain(p => p.Coordinates.Count == archive.Space.Dimension);

        output.WriteLine($"checkpoint is {json.Length:N0} characters for {archive.Count} designs");
    }

    [Fact]
    public void A_checkpoint_from_a_different_problem_is_refused_rather_than_mixed_in()
    {
        var workspace = Explored(out _, budget: 60);
        var json = workspace.Archive!.Save();

        // One variable instead of two: a different problem entirely.
        var other = new DesignSpace([new OptimisationVariable("IntakeRunner.LengthMm", 100, 500)]);

        var act = () => DesignArchive.Load(json, other, workspace.Archive.Objectives);
        act.Should().Throw<InvalidDataException>().WithMessage("*different problem*");
    }

    [Fact]
    public void The_archive_table_marks_which_designs_are_on_the_front()
    {
        var workspace = Explored(out _);
        var rows = workspace.ArchiveRows(20);

        foreach (var row in rows.Take(5))
        {
            output.WriteLine(
                $"{(row.OnFront ? "★" : " ")} {row.Design,-60} {string.Join("  ", row.Objectives)}  {row.Fidelity}");
        }

        rows.Should().NotBeEmpty();
        rows.Should().Contain(r => r.OnFront);
        rows.Should().OnlyContain(r => r.Feasible, "the table ranks answers, and an illegal design is not one");
        rows.Should().OnlyContain(r => r.Objectives.Count == 2);
    }

    [Fact]
    public void Every_figure_the_workspace_produces_is_finite_and_token_coloured()
    {
        var workspace = Explored(out _);

        foreach (var plot in new[] { workspace.ParetoChart(), workspace.ParallelCoordinates() })
        {
            plot.Notes.Should().NotBeEmpty($"'{plot.Title}' must explain what it is showing");
            plot.XAxis.Max.Should().BeGreaterThan(plot.XAxis.Min);
            plot.YAxis.Max.Should().BeGreaterThan(plot.YAxis.Min);

            foreach (var series in plot.Series)
            {
                series.X.Should().HaveCount(series.Y.Count);
                series.ColourToken.Should().StartWith("Brush.", "plan §8.11: series name tokens, never colours");
                series.StyleDescription.Should().NotBeNullOrWhiteSpace("colour is never the only cue");
            }

            // And it survives the exporter.
            var svg = SvgPlotWriter.Write(plot, 900, 520, PlotPalette.Default);
            svg.Should().StartWith("<svg").And.NotContain("NaN");
        }
    }

    [Fact]
    public void A_single_objective_run_says_a_front_needs_two_rather_than_drawing_one()
    {
        var session = new ProjectSession(FourCylinder());
        var workspace = new OptimiseWorkspace(session);
        workspace.Objectives.Add(new MetricObjective("Peak power", "kW", PowerKey, ObjectiveSense.Maximise));
        workspace.Budget = 30;
        workspace.Run(new PowerAndSound());

        var chart = workspace.ParetoChart();
        chart.Series.Should().BeEmpty();
        chart.Notes.Should().ContainSingle().Which.Should().Contain("A front needs two objectives");
    }
}
