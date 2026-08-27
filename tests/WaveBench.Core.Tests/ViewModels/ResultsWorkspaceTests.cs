using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 19 Results workspace (plan §8.4): performance plots, the x–t wave
/// diagram, per-cylinder charts, and the rule that every figure exports.
///
/// The run itself is real — solved, not mocked — because a workspace that
/// renders fabricated numbers proves nothing about the workspace.
/// </summary>
public class ResultsWorkspaceTests(ITestOutputHelper output)
{
    private static EngineModelDocument FourCylinder(ManifoldSpec? manifold = null) => new()
    {
        Name = "results fixture",
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
        ExhaustRunner = new DuctSpec { LengthMm = 600, DiameterMm = 34, RoughnessMm = 0.045 },
        ExhaustManifold = manifold,
        Combustion = new CombustionSpec { Fuel = "RON95" },

        // Coarse and short: these tests are about the workspace, not about a
        // converged performance number.
        Solver = new SolverSpec { CellSizeMm = 16.0, MinCycles = 4, MaxCycles = 8 },
    };

    private static readonly CaptureOptions Small = new()
    {
        Cycles = 1,
        FramesPerCycle = 180,
        ProbeSamplesPerCycle = 360,
    };

    private static RunResult Solve(EngineModelDocument? document = null, params double[] rpms)
    {
        var doc = document ?? FourCylinder();
        var speeds = rpms.Length > 0 ? rpms : [5000.0, 6000.0, 7000.0];
        return ResultsRunner.Run(doc, speeds, captureRpm: 6000.0, options: Small);
    }

    [Fact]
    public void Gate_every_plot_the_workspace_offers_exports_to_svg()
    {
        // Phase 19's third gate clause. AllPlots is what the export-all path
        // and the report generator walk, so a plot on a tab but missing here
        // would be a plot missing from the report — which is why the workspace
        // enumerates its own figures rather than the view listing them.
        var workspace = new ResultsWorkspace(Solve());
        var plots = workspace.AllPlots();

        plots.Should().HaveCountGreaterThan(5);
        output.WriteLine($"{plots.Count} figures:");

        foreach (var plot in plots)
        {
            var svg = SvgPlotWriter.Write(plot);
            System.Xml.Linq.XDocument.Parse(svg);

            svg.Should().Contain(plot.Title.Replace("&", "&amp;", StringComparison.Ordinal));
            plot.FileStem().Should().NotBeEmpty();

            output.WriteLine($"  {plot.FileStem(),-46} {svg.Length,8} bytes SVG");
        }

        // Titles must be distinct, or exporting them all overwrites files.
        plots.Select(p => p.FileStem()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_sweep_produces_torque_power_ve_and_bsfc_against_speed()
    {
        var run = Solve();
        var workspace = new ResultsWorkspace(run);

        run.HasSweep.Should().BeTrue();

        var tp = workspace.TorqueAndPower();
        tp.Series.Should().HaveCount(2);
        tp.Series[0].X.Should().Equal(5000.0, 6000.0, 7000.0);
        tp.RightAxis.Should().NotBeNull("power has its own unit and must not share the torque scale");
        tp.Series[1].RightAxis.Should().BeTrue();
        tp.Markers.Should().Contain(m => m.Label == "peak torque");

        var ve = workspace.VolumetricEfficiency();
        ve.Series[0].Y.Should().OnlyContain(v => v > 0.2 && v < 2.0);

        var bmep = workspace.BmepAndBsfc();
        bmep.Series.Should().HaveCount(2);

        output.WriteLine(string.Join("\n", run.Points.Select(p =>
            $"{p.Rpm,6:F0} rpm  VE {p.VolumetricEfficiency:F3}  torque {p.TorqueNm,6:F1} N·m  "
            + $"BSFC {p.BsfcGPerKwh:F0} g/kWh")));
    }

    [Fact]
    public void Per_cylinder_volumetric_efficiency_reports_the_spread_a_mean_would_hide()
    {
        // The point of the chart. Four cylinders sharing one runner geometry
        // are still fed differently, and the spread is the number that says
        // whether a header is worth changing.
        var run = Solve();
        var workspace = new ResultsWorkspace(run);
        var plot = workspace.PerCylinderVolumetricEfficiency();

        plot.Series.Should().ContainSingle();
        plot.Series[0].Kind.Should().Be(PlotSeriesKind.Bar);
        plot.Series[0].Y.Should().HaveCount(4);
        plot.Series[0].Y.Should().OnlyContain(v => v > 0.2 && v < 2.0);

        var point = run.Points.Single(p => Math.Abs(p.Rpm - 6000.0) < 1);
        point.PerCylinderVolumetricEfficiency.Should().HaveCount(4);
        point.VolumetricEfficiencySpread.Should().BeGreaterThan(0.0);

        // The mean of the per-cylinder values must be the headline VE, or the
        // bar chart and the curve above it are telling different stories.
        point.PerCylinderVolumetricEfficiency.Average()
            .Should().BeApproximately(point.VolumetricEfficiency, 1e-9);

        plot.Notes.Should().ContainSingle(n => n.Contains("spread", StringComparison.Ordinal));
        output.WriteLine(plot.Notes[0]);
        output.WriteLine("per cylinder: " + string.Join(", ",
            point.PerCylinderVolumetricEfficiency.Select(v => v.ToString("F4"))));
    }

    [Fact]
    public void Egt_and_knock_come_out_per_cylinder_and_are_physically_sane()
    {
        var run = Solve();
        var point = run.Points.Single(p => Math.Abs(p.Rpm - 6000.0) < 1);

        point.PerCylinderExhaustTemperatureK.Should().HaveCount(4);
        output.WriteLine("EGT: " + string.Join(", ",
            point.PerCylinderExhaustTemperatureK.Select(v => $"{v:F0} K")));

        // Mass-weighted at the port, so this is blowdown-dominated: hot, but
        // nowhere near peak flame temperature.
        point.PerCylinderExhaustTemperatureK.Should().OnlyContain(t => t > 600 && t < 2000,
            "EGT is the mass-weighted mean of what left the port, not a peak");

        point.PerCylinderKnockIntegral.Should().HaveCount(4);
        point.PerCylinderKnockIntegral.Should().OnlyContain(k => k >= 0 && double.IsFinite(k));

        var plot = new ResultsWorkspace(run).PerCylinderKnockAndEgt();
        plot.Notes.Should().NotBeEmpty();
        output.WriteLine(string.Join("\n", plot.Notes));
    }

    [Fact]
    public void The_wave_diagram_carries_the_field_and_the_valve_events()
    {
        var run = Solve();
        var workspace = new ResultsWorkspace(run);

        run.Fields.Should().NotBeEmpty("a run must capture at least one pipe for the wave diagram");

        var plot = workspace.WaveDiagram();
        plot.HeatMap.Should().NotBeNull();
        plot.HeatMap!.Columns.Should().Be(run.Fields[0].CellCount);
        plot.HeatMap.Rows.Should().Be(run.Fields[0].FrameCount);
        plot.HeatMap.Max.Should().BeGreaterThan(plot.HeatMap.Min);

        plot.XAxis.Unit.Should().Be("m");
        plot.YAxis.Unit.Should().Be("°");
        plot.YAxis.ResolvedTicks().Should().OnlyContain(t => Math.Abs(t % 180.0) < 1e-9,
            "crank angle divides by 180, not by the generic decimal rule");

        // And it renders.
        var svg = SvgPlotWriter.Write(plot);
        svg.Should().Contain("data:image/png;base64,");
        output.WriteLine($"{plot.Title}: {plot.HeatMap.Columns}×{plot.HeatMap.Rows} cells, {svg.Length} bytes SVG");
    }

    [Fact]
    public void The_decomposition_plot_annotates_the_return_against_EVC()
    {
        // The plan's §8.4 example, produced from a real solve rather than a
        // hand-made arrival.
        var run = Solve();
        var workspace = new ResultsWorkspace(run);
        var plot = workspace.WaveDecompositionPlot();

        plot.Series.Should().HaveCount(3, "measured, rightward and leftward");
        plot.Series.Select(s => s.Kind).Should().OnlyHaveUniqueItems(
            "three overlaid curves must be distinguishable without colour");

        plot.Markers.Should().Contain(m => m.Label == "EVC");
        plot.Notes.Should().NotBeEmpty();

        var annotation = plot.Notes[0];
        output.WriteLine(annotation);
        annotation.Should().MatchRegex("(expansion|compression) arrives .*EVC");
    }

    [Fact]
    public void Headline_readouts_flag_knock_and_an_uneven_set_of_cylinders()
    {
        var workspace = new ResultsWorkspace(Solve());
        var headlines = workspace.Headlines();

        headlines.Select(h => h.Label).Should().Contain(["Peak torque", "Peak power", "Peak VE"]);
        foreach (var h in headlines)
        {
            output.WriteLine($"{h.Label,-22} {h.Value,-16} {h.Note} {h.Warning}");
        }

        var knock = headlines.Single(h => h.Label == "Knock integral");
        knock.Value.Should().NotBeNullOrEmpty();

        // The spread readout only appears when there is more than one
        // cylinder — a single has nothing to be uneven about.
        headlines.Should().Contain(h => h.Label == "Cylinder VE spread");
    }

    [Fact]
    public void A_manifold_graph_names_its_pipes_the_way_the_canvas_does()
    {
        // A user who called a pipe "pri1" on the canvas must find "pri1" in
        // the results, not "exhaust2".
        var manifold = CollectorLibrary.Build("4-2-1", new CollectorGeometry(
            Cylinders: 4, PrimaryLengthMm: 400, PrimaryDiameterMm: 34,
            SecondaryLengthMm: 250, SecondaryDiameterMm: 42,
            CollectorLengthMm: 200, CollectorDiameterMm: 50,
            TailLengthMm: 400, TailDiameterMm: 55));

        var run = ResultsRunner.Run(
            FourCylinder(manifold), [6000.0], 6000.0,
            Small with { Fields = ["pri1", "collector"] });

        run.Fields.Select(f => f.Name).Should().Equal("pri1", "collector");
        run.Probes.Should().Contain(p => p.Name.StartsWith("pri1", StringComparison.Ordinal));

        var workspace = new ResultsWorkspace(run);
        workspace.FieldNames.Should().Equal("pri1", "collector");

        workspace.SelectedField = 1;
        workspace.WaveDiagram().Title.Should().Contain("collector");

        output.WriteLine($"captured {string.Join(", ", run.Fields.Select(f => $"{f.Name} ({f.CellCount} cells)"))}");
    }

    [Fact]
    public void A_run_with_no_capture_still_produces_plots_that_say_why_they_are_empty()
    {
        // A results screen that renders blank axes with no explanation is a
        // screen the user assumes is broken.
        var run = new RunResult { ModelName = "bare", Points = [] };
        var workspace = new ResultsWorkspace(run);

        workspace.WaveDiagram().Notes.Should().ContainSingle(n => n.Contains("No pipe", StringComparison.Ordinal));
        workspace.WaveDecompositionPlot().Notes.Should().ContainSingle(n => n.Contains("No probe", StringComparison.Ordinal));
        workspace.Headlines().Should().BeEmpty();
        workspace.AllPlots().Should().BeEmpty();
    }

    [Fact]
    public void Cancelling_a_run_stops_it_rather_than_finishing_quietly()
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        var progress = new Progress<RunProgress>(p =>
        {
            seen.Add(p.Stage);
            cts.Cancel();
        });

        var act = () => ResultsRunner.Run(
            FourCylinder(), [4000.0, 5000.0, 6000.0], 5000.0, Small, progress, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
