using FluentAssertions;
using WaveBench.Boost;
using WaveBench.Boost.Engine;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 21 gate (plan Part 12): <i>the Boost workspace appears and disappears
/// correctly with aspiration changes; the restrictor-upstream operating line
/// renders correctly with surge and choke warnings.</i>
///
/// Both clauses are driven through the same entry points a keystroke reaches —
/// <see cref="DesignWorkspace.Edit"/> for the aspiration selector and
/// <see cref="BoostWorkspace.Edit"/> for the hardware — rather than by
/// constructing a document and asserting the UI could have made it.
/// </summary>
public class BoostWorkspaceTests(ITestOutputHelper output)
{
    /// <summary>A 2-litre four, which is the size the mid-range library entries are drawn for.</summary>
    private static EngineModelDocument TwoLitreFour() => new()
    {
        Name = "boost gate engine",
        Engine = new EngineSpec
        {
            BoreMm = 86, StrokeMm = 86, RodLengthMm = 145, CompressionRatio = 9.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 28, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 370 },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
        ExhaustRunner = new DuctSpec { LengthMm = 400, DiameterMm = 38 },
        Combustion = new CombustionSpec { Fuel = "RON95", Lambda = 0.88 },
    };

    /// <summary>A 600 cc restricted four, which is what an FSAE car actually is.</summary>
    private static EngineModelDocument RestrictedFour() => new()
    {
        Name = "restricted gate engine",
        Engine = new EngineSpec
        {
            BoreMm = 67, StrokeMm = 42.5, RodLengthMm = 90, CompressionRatio = 11.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 23, Count = 2, MaxLiftMm = 8, OpenDeg = 350, CloseDeg = 570 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 20, Count = 2, MaxLiftMm = 8, OpenDeg = 150, CloseDeg = 370 },
        IntakeRunner = new DuctSpec { LengthMm = 250, DiameterMm = 34 },
        ExhaustRunner = new DuctSpec { LengthMm = 450, DiameterMm = 32 },
        Combustion = new CombustionSpec { Fuel = "RON95", Lambda = 0.85 },
    };

    private static BoostWorkspace Boosted(
        out ProjectSession session, EngineModelDocument? document = null, string turbo = "Analytic 54 mm")
    {
        session = new ProjectSession(document ?? TwoLitreFour());
        var preferences = new UserPreferences { Mode = UiMode.Advanced };

        // Through the UI, not by assignment — the selector is a field.
        new DesignWorkspace(session, preferences)
            .Edit(BoostCatalogue.AspirationPath, AspirationKinds.Turbocharged)
            .Accepted.Should().BeTrue();

        var workspace = new BoostWorkspace(session, preferences);
        workspace.Edit("ForcedInduction.TurboName", turbo).Accepted.Should().BeTrue();
        return workspace;
    }

    // ---- Gate clause 1: appears and disappears with the aspiration ---------

    [Fact]
    public void Gate_the_boost_workspace_appears_and_disappears_with_the_aspiration()
    {
        var session = new ProjectSession(TwoLitreFour());
        var preferences = new UserPreferences { Mode = UiMode.Advanced };
        var shell = new ShellViewModel(session, preferences);
        var design = new DesignWorkspace(session, preferences);

        WorkspaceIsVisible().Should().BeFalse("a new model is naturally aspirated");
        shell.Navigate(Workspace.Boost).Should().BeFalse("you cannot navigate to a hidden workspace");

        // ...and while hidden, it still says where to find it.
        var hidden = shell.Workspaces.Single(w => w.Workspace == Workspace.Boost);
        hidden.HiddenReason.Should().NotBeNullOrWhiteSpace();
        hidden.DiscoveryPath.Should().Contain("Aspiration",
            "the discovery path must name the field that actually reveals it");

        foreach (var kind in new[] { AspirationKinds.Turbocharged, AspirationKinds.Supercharged })
        {
            design.Edit(BoostCatalogue.AspirationPath, kind).Accepted.Should().BeTrue();
            WorkspaceIsVisible().Should().BeTrue($"'{kind}' is forced induction");
            shell.Navigate(Workspace.Boost).Should().BeTrue();
            shell.VisibleWorkspaces.Should().Contain(w => w.Workspace == Workspace.Boost);

            design.Edit(BoostCatalogue.AspirationPath, AspirationKinds.NaturallyAspirated)
                .Accepted.Should().BeTrue();
            WorkspaceIsVisible().Should().BeFalse("going back to NA hides it again");
        }

        // And it survives a save/load: the flag is the document, so a
        // turbocharged project opens with Boost already there.
        design.Edit(BoostCatalogue.AspirationPath, AspirationKinds.Turbocharged);
        var reloaded = new ShellViewModel(
            new ProjectSession(EngineModelDocument.Load(session.Document.Save())), preferences);
        reloaded.HasForcedInduction.Should().BeTrue(
            "a shell flag separate from the document would come back false after a reload");

        output.WriteLine("aspiration drives Boost visibility in both directions and across a reload");

        bool WorkspaceIsVisible() =>
            shell.Workspaces.Single(w => w.Workspace == Workspace.Boost).Visible;
    }

    [Fact]
    public void Undoing_the_aspiration_hides_the_workspace_again()
    {
        // Visibility derived from the document means undo moves it too, with
        // no extra wiring. A shell flag would have to be undone separately —
        // and would not be.
        var session = new ProjectSession(TwoLitreFour());
        var shell = new ShellViewModel(session);
        var design = new DesignWorkspace(session, new UserPreferences { Mode = UiMode.Advanced });

        design.Edit(BoostCatalogue.AspirationPath, AspirationKinds.Turbocharged);
        shell.HasForcedInduction.Should().BeTrue();

        session.Undo().Should().BeTrue();
        shell.HasForcedInduction.Should().BeFalse();

        session.Redo().Should().BeTrue();
        shell.HasForcedInduction.Should().BeTrue();
    }

    [Fact]
    public void A_naturally_aspirated_model_produces_no_boost_figures_at_all()
    {
        var session = new ProjectSession(TwoLitreFour());
        var workspace = new BoostWorkspace(session);

        workspace.Spec.IsForced.Should().BeFalse();
        workspace.AllPlots().Should().BeEmpty("there is nothing to plot on an NA model");
        workspace.Warnings().Should().BeEmpty("and nothing to warn about");
    }

    // ---- Gate clause 2: the restrictor-upstream operating line -------------

    [Fact]
    public void Gate_the_restrictor_upstream_operating_line_renders_with_surge_and_choke_warnings()
    {
        // Plan §4.6.4: the restrictor sits upstream of the compressor, the
        // inlet runs sub-atmospheric, the operating line moves across the map,
        // and once it chokes the compressor cannot pull more mass however fast
        // the shaft turns. The workspace has to SHOW that, not merely compute it.
        //
        // The two ways a restricted car goes wrong need two configurations,
        // because one match cannot be in both kinds of trouble at once: a
        // correctly-sized compressor runs out of CHOKE at the top, and an
        // oversized one SURGES from the bottom. Both warnings are gate
        // criteria, so both are exercised.

        // ---- Correctly sized: the throat becomes the ceiling ---------------
        var sized = Boosted(out _, RestrictedFour(), "Analytic 35 mm");
        sized.Edit("ForcedInduction.RestrictorFitted", "true").Accepted.Should().BeTrue();
        sized.Edit("ForcedInduction.RestrictorThroatMm", "20").Accepted.Should().BeTrue();
        sized.Edit("ForcedInduction.TargetBoostKPa", "120").Accepted.Should().BeTrue();
        sized.FromRpm = 3000;
        sized.ToRpm = 12_000;

        var line = sized.OperatingLine();
        line.Should().NotBeEmpty();

        output.WriteLine("20 mm restrictor, 35 mm compressor:");
        output.WriteLine("  rpm   air kg/s  inlet kPa   PR   shaft rpm   surge%   choke%  choked");
        foreach (var p in line)
        {
            output.WriteLine(
                $"{p.EngineRpm,6:N0}  {p.Demand.AirFlowKgPerS,8:F4}  {p.Inlet.OutletTotalPressureKPa,8:F2}  "
                + $"{p.Match.Compressor.PressureRatio,5:F2}  {p.Match.ShaftRpm,9:N0}  "
                + $"{p.Match.Compressor.SurgeMarginPercent,7:F1}  {p.Match.Compressor.ChokeMarginPercent,7:F1}  "
                + $"{(p.RestrictorChoked ? "yes" : "")}");
        }

        // 1. The inlet is sub-atmospheric everywhere the engine is drawing.
        line.Should().OnlyContain(p => p.Inlet.OutletTotalPressureKPa < p.AmbientKPa,
            "a restrictor upstream of the compressor can only take pressure away");

        // 2. The throat's ceiling is a real ceiling, and the line reaches it.
        var restrictor = sized.Restrictor;
        restrictor.Should().NotBeNull();
        var ceiling = restrictor!.ChokedFlow(sized.Ambient.PressurePa, sized.Ambient.TemperatureK);

        line.Should().OnlyContain(p => p.Demand.AirFlowKgPerS <= ceiling + 1e-9,
            "no shaft speed and no boost beats the choked flow");
        line.Should().Contain(p => p.RestrictorChoked,
            "a 600 cc four at 120 kPa of boost runs out of restrictor well inside this speed range");

        // 3. Past choke the flow is PINNED — the throat is in charge, not the
        //    engine and not the turbo. Every point flagged choked carries the
        //    same mass flow, and the flag means exactly that.
        var choked = line.Where(p => p.RestrictorChoked).OrderBy(p => p.EngineRpm).ToList();
        choked.Should().HaveCountGreaterThan(1);
        choked.Should().OnlyContain(p => Math.Abs(p.Demand.AirFlowKgPerS - ceiling) < 1e-9,
            "a point is flagged choked when, and only when, its own flow sits at the ceiling");

        var unchoked = line.Where(p => !p.RestrictorChoked).ToList();
        unchoked.Should().OnlyContain(p => p.Demand.AirFlowKgPerS < ceiling - 1e-9);

        // 4. The chart renders the map, the line and the ceiling.
        var chart = sized.CompressorMapChart();
        chart.Series.Should().Contain(s => s.Name == "Operating line");
        chart.Series.Should().Contain(s => s.Name == "Surge line");
        chart.Series.Should().Contain(s => s.Name == "Choke line");
        chart.Markers.Should().Contain(m => m.Label.Contains("restrictor"),
            "the choke-limited ceiling has to be ON the map, not just in a readout");
        chart.Notes.Should().Contain(n => n.Contains("4.6.4"),
            "the figure has to say why the line sits where it does");

        // A NaN in a series is an invisible gap on screen and a crash in an
        // exporter, so nothing drawn may contain one.
        foreach (var series in chart.Series)
        {
            series.X.Should().OnlyContain(v => double.IsFinite(v), $"series '{series.Name}'");
            series.Y.Should().OnlyContain(v => double.IsFinite(v), $"series '{series.Name}'");
        }

        // 5. The choke-side trouble is reported in words, with somewhere to go.
        var sizedWarnings = sized.Warnings();
        Report("correctly sized", sizedWarnings);

        sizedWarnings.Should().Contain(w => w.Message.Contains("Choke margin", StringComparison.Ordinal),
            "a line that ends its sweep this close to the choke line has to say so before it arrives there");

        sized.Derived(BoostTab.Compressor).Should().Contain(r => r.Label == "Choked from",
            "the speed at which the throat takes over is the number this screen exists to give an FSAE team");

        // ---- Oversized compressor, small hot side: the surge trajectory -----
        //
        // Plan §4.6.4's second consequence, and it takes BOTH halves to
        // produce: an oversized compressor alone simply never spools, because
        // the steady shaft balance only turns as fast as the exhaust drives it.
        // Put a small turbine housing behind it and the shaft is driven hard
        // against a mass flow the throat has already capped — pressure ratio
        // rises against fixed flow, which is a trajectory straight into surge.
        // That is the mistake a team makes when it sizes the compressor for the
        // power it wants and the housing for the response it wants.
        var oversized = Boosted(out _, RestrictedFour(), "Analytic 54 mm");
        oversized.Edit("ForcedInduction.RestrictorFitted", "true");
        oversized.Edit("ForcedInduction.TargetBoostKPa", "250");
        oversized.Edit("ForcedInduction.TurbineAreaRatio", "0.30");
        oversized.FromRpm = 3000;
        oversized.ToRpm = 12_000;

        var oversizedLine = oversized.OperatingLine();
        output.WriteLine("");
        output.WriteLine("20 mm restrictor, 54 mm compressor (oversized):");
        output.WriteLine("  rpm   air kg/s   PR   shaft rpm    surge%");
        foreach (var p in oversizedLine)
        {
            output.WriteLine(
                $"{p.EngineRpm,6:N0}  {p.Demand.AirFlowKgPerS,8:F4}  "
                + $"{p.Match.Compressor.PressureRatio,5:F2}  {p.Match.ShaftRpm,9:N0}  "
                + $"{p.Match.Compressor.SurgeMarginPercent,7:F1}");
        }

        oversizedLine.Min(p => p.Match.Compressor.SurgeMarginPercent).Should().BeLessThan(10.0,
            "a compressor this far oversized for the throat cannot clear the surge line comfortably");

        var surgeWarnings = oversized.Warnings();
        Report("oversized", surgeWarnings);

        surgeWarnings.Should().Contain(w => w.Message.Contains("urge", StringComparison.Ordinal),
            "an operating line this close to surge must say so");
        surgeWarnings.Where(w => w.Message.Contains("urge", StringComparison.Ordinal))
            .Should().OnlyContain(w => w.CrossLink != null,
                "plan §8.3: a surge warning links to the field causing it");

        // Every warning, in either configuration, carries a source. A limit
        // without one is just this tool's opinion.
        sizedWarnings.Concat(surgeWarnings).Should()
            .OnlyContain(w => !string.IsNullOrWhiteSpace(w.Citation));

        void Report(string which, IReadOnlyList<DesignWarning> warnings)
        {
            output.WriteLine("");
            output.WriteLine($"warnings ({which}):");
            foreach (var w in warnings)
            {
                output.WriteLine($"  ! {w.Message}  → {w.Suggestion}  [{w.Citation}]  {w.CrossLink}");
            }
        }
    }

    [Fact]
    public void The_restrictor_moves_the_operating_line_right_in_corrected_coordinates()
    {
        // The mechanism behind the gate, isolated: corrected flow divides by
        // inlet pressure, and the restrictor has taken some of that away. The
        // same physical mass flow therefore lands further right on the map —
        // which is why a match made without the restrictor in place is wrong
        // in a direction that looks fine.
        var workspace = Boosted(out _, RestrictedFour(), "Analytic 35 mm");
        workspace.Edit("ForcedInduction.TargetBoostKPa", "80");
        workspace.FromRpm = 4000;
        workspace.ToRpm = 9000;

        var without = workspace.OperatingLine();
        workspace.Edit("ForcedInduction.RestrictorFitted", "true");
        var with = workspace.OperatingLine();

        var map = workspace.Turbo!.Compressor;

        double CorrectedAt(IReadOnlyList<BoostOperatingPoint> line, int i) => Corrected.Flow(
            line[i].Demand.AirFlowKgPerS, line[i].Inlet.OutletTotalTemperatureK,
            line[i].Inlet.OutletTotalPressureKPa, map.Reference);

        var shifted = 0;
        for (var i = 0; i < with.Count; i++)
        {
            var ratio = CorrectedAt(with, i) / (with[i].Demand.AirFlowKgPerS / without[i].Demand.AirFlowKgPerS)
                        / CorrectedAt(without, i);
            if (ratio > 1.0)
            {
                shifted++;
            }
        }

        output.WriteLine($"{shifted} of {with.Count} points shift right per unit of mass flow behind the restrictor");
        shifted.Should().Be(with.Count,
            "corrected flow rises for the same mass flow once the inlet is sub-atmospheric");
    }

    // ---- Boost control -------------------------------------------------------

    [Fact]
    public void Gate_the_operating_line_is_the_controlled_point_not_the_wide_open_one()
    {
        // The compressor map, the margins, the charge temperature and the
        // shaft-speed check are all read off one point, and it has to be the
        // point the engine RUNS at. A raw shaft balance answers "where does
        // this settle with nothing bled off", which for any turbo not at its
        // own limit is well above the boost the user asked for — so drawing it
        // would put the whole screen at an operating point the wastegate
        // exists to prevent.
        var workspace = Boosted(out _, TwoLitreFour(), "Analytic 62 mm");
        workspace.Edit("ForcedInduction.TargetBoostKPa", "110").Accepted.Should().BeTrue();

        var line = workspace.OperatingLine();

        output.WriteLine("  rpm   gate shut   controlled   target   shaft (shut)  shaft (ctl)  ER shut  ER ctl");
        foreach (var p in line)
        {
            output.WriteLine(
                $"{p.EngineRpm,6:N0}  {p.WideOpenBoostKPa,10:F1}  {p.DeliveredBoostKPa,11:F1}  "
                + $"{p.TargetBoostKPa,7:F0}  {p.WideOpen.ShaftRpm,12:N0}  {p.Match.ShaftRpm,11:N0}  "
                + $"{p.WideOpen.ExpansionRatio,7:F2}  {p.Match.ExpansionRatio,6:F2}");
        }

        // Wherever the turbo CAN exceed the target, it is held to it.
        var gated = line.Where(p => p.Gated).ToList();
        gated.Should().NotBeEmpty("a 62 mm on a 2-litre at 110 kPa has surplus to spare over most of the range");

        foreach (var p in gated)
        {
            p.DeliveredBoostKPa.Should().BeApproximately(p.TargetBoostKPa, 2.0,
                $"at {p.EngineRpm:F0} rpm the gate holds the target");
            p.Match.ShaftRpm.Should().BeLessThan(p.WideOpen.ShaftRpm,
                "holding boost down means holding the shaft down");

            // The half a boost gauge cannot see: bleeding exhaust around the
            // rotor gives the engine its back-pressure back. Stated on the
            // expansion ratio rather than on the exhaust-to-intake RATIO,
            // because the gate lowers boost and back-pressure together and
            // their ratio can land either side by a fraction of a percent —
            // it is the absolute back-pressure the engine pumps against.
            p.Match.ExpansionRatio.Should().BeLessThan(p.WideOpen.ExpansionRatio,
                $"at {p.EngineRpm:F0} rpm an open gate lowers the expansion ratio, not just the boost");
        }

        // Where it CANNOT reach the target, the two are the same point: a
        // shortfall is not something a gate can fix, and pretending otherwise
        // would invent boost.
        foreach (var p in line.Where(p => !p.Gated))
        {
            p.Match.ShaftRpm.Should().Be(p.WideOpen.ShaftRpm);
            p.DeliveredBoostKPa.Should().BeLessThan(p.TargetBoostKPa + 1.0);
        }

        // And the chart shows both, because the difference IS the setup
        // decision this tab exists for.
        var chart = workspace.BoostControlChart();
        chart.Series.Should().Contain(s => s.Name.Contains("Gate shut"));
        chart.Series.Should().Contain(s => s.Name.Contains("gate controlling"));
        chart.Series.Should().Contain(s => s.Name == "Target");
    }

    [Fact]
    public void A_turbo_that_cannot_reach_the_target_is_reported_as_short_not_held()
    {
        // The mirror case: an undersized unit. Nothing is gated, the delivered
        // curve sits below the target everywhere, and the note says the
        // wastegate is not the answer.
        var workspace = Boosted(out _, TwoLitreFour(), "Analytic 35 mm");
        workspace.Edit("ForcedInduction.TargetBoostKPa", "200");

        var line = workspace.OperatingLine();
        line.Should().OnlyContain(p => p.DeliveredBoostKPa < p.TargetBoostKPa);
        line.Should().NotContain(p => p.Gated);

        workspace.BoostControlChart().Notes.Should()
            .Contain(n => n.Contains("never reaches the target"));
    }

    // ---- Figures -----------------------------------------------------------

    [Fact]
    public void Every_figure_renders_finite_data_and_says_what_fidelity_it_is()
    {
        var workspace = Boosted(out _);

        var plots = workspace.AllPlots();
        plots.Should().HaveCount(8, "export-all walks AllPlots, so a figure missing here is missing from exports");

        foreach (var plot in plots)
        {
            output.WriteLine($"{plot.Title,-28} {plot.Series.Count} series, {plot.Notes.Count} notes");

            plot.Title.Should().NotBeNullOrWhiteSpace();
            plot.Notes.Should().NotBeEmpty($"'{plot.Title}' must explain what it is showing");
            plot.FileStem().Should().NotBeNullOrWhiteSpace();

            foreach (var series in plot.Series)
            {
                series.X.Should().HaveCount(series.Y.Count, $"'{plot.Title}' / '{series.Name}'");
                series.X.Should().OnlyContain(v => double.IsFinite(v), $"'{plot.Title}' / '{series.Name}'");
                series.Y.Should().OnlyContain(v => double.IsFinite(v), $"'{plot.Title}' / '{series.Name}'");

                // Plan §8.11: never colour alone. Every series names a TOKEN,
                // and the legend carries a style word beside it.
                series.ColourToken.Should().StartWith("Brush.");
                series.StyleDescription.Should().NotBeNullOrWhiteSpace();
            }

            double.IsFinite(plot.XAxis.Min).Should().BeTrue($"'{plot.Title}' x min");
            double.IsFinite(plot.XAxis.Max).Should().BeTrue($"'{plot.Title}' x max");
            plot.XAxis.Max.Should().BeGreaterThan(plot.XAxis.Min, $"'{plot.Title}' x range");
            plot.YAxis.Max.Should().BeGreaterThan(plot.YAxis.Min, $"'{plot.Title}' y range");
        }

        // Plan §7.2: every plot exports. These figures carry the awkward
        // cases — a right-hand axis, markers, eight series on one set of axes
        // — so exercising the writer on them is worth more than on a
        // two-series example.
        foreach (var plot in plots)
        {
            var svg = SvgPlotWriter.Write(plot, 900, 520, PlotPalette.Default);
            svg.Should().StartWith("<svg");
            svg.TrimEnd().Should().EndWith("</svg>");
            svg.Should().NotContain("NaN", $"'{plot.Title}' must not write a coordinate no renderer can read");
            svg.Should().NotContain("Infinity");
            foreach (var series in plot.Series.Where(s => s.Y.Count > 0))
            {
                svg.Should().Contain(series.Name, $"'{plot.Title}' must label '{series.Name}' in its legend");
            }
        }

        // Fidelity is stated, because an estimate presented as a solve is
        // worse than no estimate.
        workspace.Fidelity.Should().Be(BoostFidelity.Instant, "no run has been done");
        plots.Where(p => p.Series.Count > 0).Should()
            .Contain(p => p.Subtitle.Contains("instant", StringComparison.OrdinalIgnoreCase)
                          || p.Subtitle.Contains("quasi-steady", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_turbo_is_reported_rather_than_drawn_as_an_empty_axis()
    {
        var session = new ProjectSession(TwoLitreFour());
        new DesignWorkspace(session).Edit(BoostCatalogue.AspirationPath, AspirationKinds.Turbocharged);
        var workspace = new BoostWorkspace(session);

        workspace.Turbo.Should().BeNull();
        workspace.OperatingLine().Should().BeEmpty();

        var chart = workspace.CompressorMapChart();
        chart.Series.Should().BeEmpty();
        chart.Notes.Should().ContainSingle().Which.Should().Contain("No turbocharger is selected");

        workspace.Warnings().Should().ContainSingle()
            .Which.Message.Should().Contain("No turbocharger");

        // The document's own validator agrees, so the problem surfaces in
        // Design too rather than only on this screen.
        workspace.Issues().Should().Contain(i => i.Path.Contains("turboName", StringComparison.OrdinalIgnoreCase));
    }

    // ---- The altitude / hot-day toggle --------------------------------------

    [Fact]
    public void Gate_the_altitude_toggle_moves_the_operating_line_and_never_the_document()
    {
        // Plan §4.7 asks for the toggle "because that is where matches fail".
        // §8.8 rule 4 says a view preference cannot touch the model, and this
        // is one: the design's own ambient is a document field.
        var workspace = Boosted(out var session);
        var before = session.Document.Save();

        var atSeaLevel = workspace.OperatingLine();
        workspace.Ambient = AmbientCondition.HotAndHigh;
        var atAltitude = workspace.OperatingLine();

        session.Document.Save().Should().Be(before, "looking at a different day is not editing the model");

        output.WriteLine("  rpm   sea-level PR   hot-and-high PR   surge% sea   surge% high");
        for (var i = 0; i < atSeaLevel.Count; i++)
        {
            output.WriteLine(
                $"{atSeaLevel[i].EngineRpm,6:N0}  {atSeaLevel[i].Match.Compressor.PressureRatio,12:F2}  "
                + $"{atAltitude[i].Match.Compressor.PressureRatio,15:F2}  "
                + $"{atSeaLevel[i].Match.Compressor.SurgeMarginPercent,10:F1}  "
                + $"{atAltitude[i].Match.Compressor.SurgeMarginPercent,10:F1}");
        }

        // Thin air means less mass for the same manifold pressure, so the
        // compressor has to make a bigger ratio to reach the same boost.
        atAltitude.Should().OnlyContain(p => p.AmbientKPa < 90.0);
        for (var i = 0; i < atSeaLevel.Count; i++)
        {
            atAltitude[i].Demand.TargetPressureRatio.Should()
                .BeGreaterThan(atSeaLevel[i].Demand.TargetPressureRatio,
                    "the same gauge boost is a bigger ratio when ambient is lower");
        }

        // And the workspace says out loud that it is not showing this model's
        // own condition, so a screenshot cannot be mistaken for one.
        workspace.ShowingDocumentAmbient.Should().BeFalse();
        workspace.Warnings().Should().Contain(w => w.Message.Contains("not at this model's own ambient"));
    }

    // ---- Auto-match ---------------------------------------------------------

    [Fact]
    public void Auto_match_ranks_the_library_and_shows_every_candidates_trade_offs()
    {
        // Plan §4.7: "Always show the top five with their trade-offs, never a
        // single 'best'."
        var workspace = Boosted(out _);
        workspace.Edit("ForcedInduction.TargetBoostKPa", "100");

        var ranked = workspace.AutoMatch();
        ranked.Should().NotBeEmpty();
        ranked.Count.Should().BeLessThanOrEqualTo(5);

        output.WriteLine("candidate           score    η̄    worst surge   back-p   onset rpm");
        foreach (var c in ranked)
        {
            output.WriteLine(
                $"{c.Entry.Turbo.Name,-18} {c.Score,6:F1}  {c.MeanEfficiency,5:P0}  {c.WorstSurgeMargin,10:F1}%  "
                + $"{c.WorstBackPressureRatio,6:F2}  "
                + $"{(double.IsNaN(c.BoostOnsetRpm) ? "never" : c.BoostOnsetRpm.ToString("N0")),9}");
            foreach (var d in c.Disqualifications)
            {
                output.WriteLine($"    ✗ {d}");
            }
        }

        // Viable first, then score descending inside each group — the same
        // ordering the Phase 12 ranker guarantees.
        ranked.Select(c => c.Viable ? 1 : 0).Should().BeInDescendingOrder();
        foreach (var group in ranked.GroupBy(c => c.Viable))
        {
            group.Select(c => c.Score).Should().BeInDescendingOrder();
        }

        // A candidate that is rejected says why, in a sentence.
        ranked.Where(c => !c.Viable).Should().AllSatisfy(
            c => c.Disqualifications.Should().NotBeEmpty());

        // And every candidate carries its whole line, viable or not: "it
        // surges below 3000 rpm" is a trade-off the user may accept.
        ranked.Should().OnlyContain(c => c.OperatingLine.Count == workspace.Speeds().Count);
    }

    [Fact]
    public void The_shipped_turbo_library_is_analytic_and_every_entry_declares_it()
    {
        // Plan §4.7: ship no manufacturer maps without written permission. A
        // library entry with a vague provenance is how that rule gets broken
        // by accident, so every entry has to say where it came from.
        TurboLibrary.Entries.Should().NotBeEmpty();

        foreach (var entry in TurboLibrary.Entries)
        {
            entry.Turbo.Validate();
            entry.Source.Should().ContainEquivalentOf("analytic",
                "an entry that does not say it is synthetic will be taken for a measurement");
            entry.Licence.Should().NotBeNullOrWhiteSpace();
            entry.Turbo.Provenance.Should().Contain("not a product");

            // Reference conditions are required, never defaulted (plan §4.2).
            entry.Turbo.Compressor.Reference.Should().NotBeNull();
            entry.Turbo.Turbine.Reference.Should().NotBeNull();

            output.WriteLine(
                $"{entry.Turbo.Name,-18} {entry.Turbo.Compressor.SpeedLines.Count} speed lines, "
                + $"{entry.Turbo.Compressor.LowestSpeed / 1000.0:F0}k–{entry.Turbo.Compressor.HighestSpeed / 1000.0:F0}k, "
                + $"J {entry.Turbo.ShaftInertia * 1e6:F2} µkg·m²");
        }

        // Sizes are ordered, and inertia rises much faster than flow — which
        // is the whole reason a bigger turbo is slower, not just later.
        var inertias = TurboLibrary.Entries.Select(e => e.Turbo.ShaftInertia).ToList();
        inertias.Should().BeInAscendingOrder();

        // Round-tripping a library entry through the database keeps it intact.
        var database = TurboLibrary.AsDatabase();
        TurboDatabase.Load(database.Save()).Entries.Should().HaveCount(TurboLibrary.Entries.Count);
    }

    // ---- The A/R sweep -------------------------------------------------------

    [Fact]
    public void The_area_ratio_sweep_draws_a_trade_rather_than_a_recommendation()
    {
        var workspace = Boosted(out _);
        workspace.Edit("ForcedInduction.TargetBoostKPa", "100");

        var small = workspace.OperatingLineAt(0.45);
        var large = workspace.OperatingLineAt(1.10);

        output.WriteLine("  rpm    small A/R ER   large A/R ER   small PR   large PR");
        for (var i = 0; i < small.Count; i++)
        {
            output.WriteLine(
                $"{small[i].EngineRpm,6:N0}  {small[i].Match.ExpansionRatio,13:F2}  "
                + $"{large[i].Match.ExpansionRatio,13:F2}  {small[i].Match.Compressor.PressureRatio,9:F2}  "
                + $"{large[i].Match.Compressor.PressureRatio,9:F2}");
        }

        // The physical claim the sweep rests on: a bigger volute passes the
        // same flow at a lower expansion ratio, so it back-pressures the
        // engine less and drives the shaft less hard.
        for (var i = 0; i < small.Count; i++)
        {
            large[i].Match.ExpansionRatio.Should().BeLessThan(small[i].Match.ExpansionRatio,
                $"at {small[i].EngineRpm:F0} rpm, a larger housing swallows the same flow more easily");
        }

        var sweep = workspace.AreaRatioSweep();
        var front = sweep.Series.Single(s => s.Name == "A/R front");
        sweep.Markers.Should().Contain(m => m.Label.Contains("selected"),
            "the sweep marks where the current design sits rather than naming a winner");
        sweep.Notes.Should().Contain(n => n.Contains("Watson"),
            "the A/R scaling is first-order and cites where the first order comes from");

        // The y axis has to be something the housing actually moves. With the
        // gate holding the target, peak air flow is identical for every
        // housing — an axis that is flat by construction. Back-pressure at the
        // top end is what a small volute costs for its early onset, and the
        // front has to show a real spread in it or the figure says nothing.
        front.Y.Should().OnlyContain(v => double.IsFinite(v));
        (front.Y.Max() - front.Y.Min()).Should().BeGreaterThan(0.05,
            "the trade axis must vary across housings, or the figure is a horizontal line");
        (front.X.Max() - front.X.Min()).Should().BeGreaterThan(250,
            "and so must the onset");

        // Down-and-left is the impossible corner: the housing with the
        // earliest onset must not also be the one with the least
        // back-pressure, or there would be no trade to draw.
        var earliest = front.Y[front.X.Select((x, i) => (x, i)).MinBy(p => p.x).i];
        earliest.Should().BeGreaterThan(front.Y.Min(),
            "the earliest-spooling housing pays for it in back-pressure");
    }

    [Fact]
    public void Rehousing_a_turbine_scales_its_capacity_and_leaves_the_efficiency_field_alone()
    {
        var baseline = TurboLibrary.Find("Analytic 54 mm")!.Turbo.Turbine;
        var bigger = TurboLibrary.Rehoused(baseline, baseline.AreaRatio!.Value * 1.5);

        bigger.AreaRatio.Should().BeApproximately(baseline.AreaRatio!.Value * 1.5, 1e-12);
        bigger.Validate();

        for (var l = 0; l < baseline.SpeedLines.Count; l++)
        {
            for (var p = 0; p < baseline.SpeedLines[l].Points.Count; p++)
            {
                var before = baseline.SpeedLines[l].Points[p];
                var after = bigger.SpeedLines[l].Points[p];

                after.CorrectedFlowKgPerS.Should().BeApproximately(before.CorrectedFlowKgPerS * 1.5, 1e-12);
                after.Efficiency.Should().Be(before.Efficiency,
                    "the scaling is capacity only — claiming to know the new efficiency field would be inventing it");
            }
        }

        // The provenance says what was done to it, so a figure drawn from a
        // re-housed map cannot be mistaken for one drawn from a measured one.
        bigger.Provenance.Should().Contain("first-order");
        TurboLibrary.Rehoused(baseline, baseline.AreaRatio!.Value).Should().BeSameAs(baseline,
            "re-housing to the A/R it already has is not a transformation");
    }

    // ---- Transient -----------------------------------------------------------

    [Fact]
    public void The_spool_estimate_carries_a_band_the_physics_produced()
    {
        // Part 14 gotcha #25: show a sensitivity band, not a single number —
        // and the band has to come from moving the uncertain inputs, not from
        // a percentage bolted onto the answer.
        var workspace = Boosted(out _);
        workspace.Edit("ForcedInduction.TargetBoostKPa", "100");
        workspace.TransientRpm = 3000;

        var plot = workspace.SpoolEstimate();
        plot.Series.Should().HaveCount(3, "nominal plus two bounds");

        var nominal = plot.Series.Single(s => s.Name == "Nominal");
        var bounds = plot.Series.Where(s => s.Name != "Nominal").ToList();

        nominal.Y.Should().OnlyContain(v => double.IsFinite(v));
        nominal.Y[^1].Should().BeGreaterThan(nominal.Y[0], "the shaft spools up, not down");

        // The bounds genuinely straddle the nominal: a band that collapses
        // onto the nominal is a band that was not computed.
        var mid = nominal.Y.Count / 2;
        bounds.Select(b => b.Y[mid]).Should().Contain(v => v > nominal.Y[mid])
            .And.Contain(v => v < nominal.Y[mid],
                "less inertia and friction spools faster, more spools slower");

        output.WriteLine(plot.Notes[0]);
        plot.Notes.Should().Contain(n => n.Contains("Quasi-steady"),
            "the estimate has to say what it leaves out, or it will be read as a solve");

        // A zero declared uncertainty collapses the band, which is the honest
        // response to "I know these numbers exactly".
        workspace.Edit("ForcedInduction.TransientUncertaintyPercent", "0");
        var certain = workspace.SpoolEstimate();
        certain.Series.Select(s => s.Y[mid]).Distinct().Should().ContainSingle(
            "with no declared uncertainty there is no band");
    }

    [Fact]
    public void A_step_that_produces_no_boost_is_not_reported_as_a_fast_spool()
    {
        // Reaching 90% of a 0.6 kPa rise in 0.09 s is arithmetically true and
        // says nothing. A turbo sized for the top end has no exhaust energy to
        // work with at 2000 rpm, and a confident tenth of a second there would
        // be the most flattering possible way to state a mismatch.
        var workspace = Boosted(out _, TwoLitreFour(), "Analytic 71 mm");
        workspace.Edit("ForcedInduction.TargetBoostKPa", "110");
        workspace.TransientRpm = 2000;

        var plot = workspace.SpoolEstimate();
        var nominal = plot.Series.Single(s => s.Name == "Nominal");
        var rise = nominal.Y[^1] - nominal.Y[0];
        output.WriteLine($"rise at 2000 rpm on the 71 mm: {rise:F2} kPa");

        rise.Should().BeLessThan(11.0, "a 71 mm on a 2-litre makes nothing at 2000 rpm");
        plot.Markers.Should().BeEmpty("no 90% marker, because there is no spool to mark");
        plot.Notes.Should().Contain(n => n.Contains("This is not a spool"));

        // The axis is scaled to what happened, not to the target: a flat trace
        // on an axis sized for boost it never made reads as a rendering fault
        // rather than as the answer.
        plot.YAxis.Max.Should().BeLessThan(60.0);
    }

    // ---- Fields ---------------------------------------------------------------

    [Fact]
    public void Gate_every_forced_induction_field_is_reachable_and_writes_through_the_session()
    {
        var workspace = Boosted(out var session);

        // Every field the catalogue offers, typed into as a user would.
        var typed = new (string Path, string Text)[]
        {
            ("ForcedInduction.TurboName", "Analytic 62 mm"),
            ("ForcedInduction.TargetBoostKPa", "140"),
            ("ForcedInduction.RestrictorFitted", "true"),
            ("ForcedInduction.RestrictorThroatMm", "19"),
            ("ForcedInduction.RestrictorDischargeCoefficient", "0.97"),
            ("ForcedInduction.RestrictorDiffuserRecovery", "0.85"),
            ("ForcedInduction.TurbineAreaRatio", "0.82"),
            ("ForcedInduction.ExhaustBackPressureKPa", "104"),
            ("ForcedInduction.BoostControl", BoostControlModes.ClosedLoop),
            ("ForcedInduction.WastegateSpringKPa", "90"),
            ("ForcedInduction.WastegateDiameterMm", "44"),
            ("ForcedInduction.WastegatePlacement", "External"),
            ("ForcedInduction.BlowOffCrackingKPa", "35"),
            ("ForcedInduction.BlowOffRecirculates", "false"),
            ("ForcedInduction.ChargeCooler", ChargeCoolerKinds.AirToWater),
            ("ForcedInduction.CoolerEffectiveness", "0.82"),
            ("ForcedInduction.CoolerPressureDropKPa", "12"),
            ("ForcedInduction.CoolantTemperatureK", "35"),          // 35 °C
            ("ForcedInduction.TransientUncertaintyPercent", "30"),
        };

        foreach (var (path, text) in typed)
        {
            workspace.Edit(path, text).Accepted.Should().BeTrue($"typing '{text}' into {path}");
        }

        var untouched = BoostCatalogue.Fields.Select(f => f.Path)
            .Except(typed.Select(t => t.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        untouched.Should().BeEmpty("the gate must type into every field the Boost workspace exposes");

        var fi = session.Document.ForcedInduction;
        fi.TargetBoostKPa.Should().Be(140.0);
        fi.WastegatePlacement.Should().Be("External");
        fi.BlowOffRecirculates.Should().BeFalse();
        fi.ChargeCooler.Should().Be(ChargeCoolerKinds.AirToWater);
        fi.CoolantTemperatureK.Should().BeApproximately(308.15, 1e-9, "typed in °C, stored in K");

        // §8.5: everything the user typed is theirs and protected from later
        // derivation — the same contract Design honours, because it is the
        // same edit path.
        foreach (var (path, _) in typed)
        {
            session.Provenance[path].Origin.Should().Be(Provenance.You);
            session.Provenance[path].IsProtected.Should().BeTrue();
        }

        // And it survives the file.
        var reloaded = EngineModelDocument.Load(session.Document.Save());
        reloaded.Save().Should().Be(session.Document.Save(), "save → load → save must be a fixed point");
        reloaded.ForcedInduction.Should().Be(fi);
    }

    [Fact]
    public void An_implausible_value_is_rejected_with_a_reason_rather_than_written()
    {
        var workspace = Boosted(out var session);

        var outcome = workspace.Edit("ForcedInduction.TargetBoostKPa", "5000");
        outcome.Accepted.Should().BeFalse();
        outcome.Reason.Should().Contain("maximum");
        workspace.Rejections.Should().ContainKey("ForcedInduction.TargetBoostKPa");
        session.Document.ForcedInduction.TargetBoostKPa.Should().Be(100.0, "the old value stands");

        workspace.Edit("ForcedInduction.ChargeCooler", "Air to fire").Accepted.Should().BeFalse();
        workspace.Edit("ForcedInduction.TurbineAreaRatio", "not a number").Accepted.Should().BeFalse();

        // A good value clears the rejection.
        workspace.Edit("ForcedInduction.TargetBoostKPa", "120").Accepted.Should().BeTrue();
        workspace.Rejections.Should().NotContainKey("ForcedInduction.TargetBoostKPa");
    }

    [Fact]
    public void Sub_fields_of_a_switched_off_feature_are_not_offered()
    {
        var workspace = Boosted(out _);

        Paths(BoostTab.Compressor).Should().NotContain("ForcedInduction.RestrictorThroatMm",
            "a throat diameter on a car with no restrictor is not a setting");

        workspace.Edit("ForcedInduction.RestrictorFitted", "true");
        Paths(BoostTab.Compressor).Should().Contain("ForcedInduction.RestrictorThroatMm");

        workspace.Edit("ForcedInduction.ChargeCooler", ChargeCoolerKinds.None);
        Paths(BoostTab.ChargeCooling).Should().NotContain("ForcedInduction.CoolerEffectiveness");
        Paths(BoostTab.ChargeCooling).Should().Contain("ForcedInduction.ChargeCooler",
            "the parent toggle is always shown, so nothing becomes unreachable");

        IEnumerable<string> Paths(BoostTab tab) => workspace.Fields(tab).Select(f => f.Field.Path);
    }

    [Fact]
    public void A_units_preference_cannot_change_the_model()
    {
        // §8.8 rule 4, on this screen too.
        var workspace = Boosted(out var session);
        var before = session.Document.Save();

        workspace.Preferences.Units = UnitSystem.Imperial;
        _ = workspace.Fields(BoostTab.Compressor);
        _ = workspace.Derived(BoostTab.ChargeCooling);
        _ = workspace.AllPlots();

        session.Document.Save().Should().Be(before, "looking at a model is not editing it");

        // The boost target reads in psi, and typing psi back gives the same kPa.
        var view = workspace.Fields(BoostTab.Compressor)
            .Single(f => f.Field.Path == "ForcedInduction.TargetBoostKPa");
        view.DisplayUnit.Should().Be("psi");

        workspace.Edit("ForcedInduction.TargetBoostKPa", view.Display).Accepted.Should().BeTrue();
        session.Document.ForcedInduction.TargetBoostKPa.Should().BeApproximately(100.0, 0.05,
            "a round trip through the display unit must not move the value");
    }

    [Fact]
    public void Every_catalogue_path_resolves_against_the_document()
    {
        // The mirror of the completeness walk: a catalogue entry pointing at a
        // path the document does not have would render a permanently blank field.
        foreach (var field in BoostCatalogue.Fields)
        {
            var act = () => ModelPath.Canonicalise(typeof(EngineModelDocument), field.Path);
            act.Should().NotThrow($"'{field.Path}' must exist on the document");
        }

        BoostCatalogue.Fields.Select(f => f.Path).Should().OnlyHaveUniqueItems();
        BoostCatalogue.Fields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Label));
        BoostCatalogue.Fields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Help),
            "every field on a conditional screen needs its why-text: there is no other screen to learn it from");
        BoostCatalogue.Fields.Where(f => f.Kind == FieldKind.Choice)
            .Should().OnlyContain(f => f.Choices != null && f.Choices.Count > 0);

        // Every tab has at least one field, or the sub-tab is a dead end.
        foreach (var (tab, title) in BoostCatalogue.Tabs)
        {
            BoostCatalogue.For(tab).Should().NotBeEmpty($"the {title} tab must have something on it");
        }

        // And the aspiration selector belongs to Design, not here — one field,
        // one home.
        BoostCatalogue.Find(BoostCatalogue.AspirationPath).Should().BeNull();
        DesignCatalogue.Find(BoostCatalogue.AspirationPath).Should().NotBeNull();
    }

    [Fact]
    public void The_command_palette_reaches_boost_fields_once_the_model_is_boosted()
    {
        // §8.11: Ctrl+K reaches every field. A field on a conditional
        // workspace is exactly the one a user cannot find by looking.
        var session = new ProjectSession(TwoLitreFour());
        var shell = new ShellViewModel(session);

        new CommandPalette(shell).Search("wastegate").Should().BeEmpty("there is no wastegate on an NA model");

        new DesignWorkspace(session).Edit(BoostCatalogue.AspirationPath, AspirationKinds.Turbocharged);

        var palette = new CommandPalette(shell);
        palette.Search("wastegate").Should().NotBeEmpty();
        palette.Search("intercool").Should().NotBeEmpty("a user types what they call it, not what we do");

        // The discovery command itself now carries the field it acts on.
        var add = new CommandPalette(shell).AllCommands().Single(c => c.Title == "Add forced induction");
        add.Path.Should().Be(BoostCatalogue.AspirationPath);
    }
}
