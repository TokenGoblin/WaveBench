using System.Globalization;
using FluentAssertions;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// The Phase 17 gate, stated by the plan as: <i>a complete model can be built
/// and saved entirely through the UI and produces byte-identical results to
/// the same model run from the CLI; theme switching is instant and
/// complete.</i>
///
/// The theme half is covered by the Phase 16 token tests (every colour comes
/// from <c>Tokens.xaml</c>, so a theme swap is a dictionary swap and cannot
/// be partial). What is tested here is the model half, and it is tested by
/// building an engine through nothing but <see cref="DesignWorkspace.Edit"/>
/// calls — the same entry point a keystroke reaches — rather than by
/// constructing a document and asserting the UI could have made it.
/// </summary>
public class DesignGateTests(ITestOutputHelper output)
{
    /// <summary>
    /// Builds a complete engine using ONLY the workspace's edit API, the way
    /// a user typing into fields would. Every edit is asserted, so a field
    /// that silently refuses input fails the gate rather than quietly leaving
    /// a template value in place.
    /// </summary>
    private static ProjectSession BuildThroughTheUi(ITestOutputHelper output)
    {
        // Start from a blank-slate document: only the properties the document
        // marks `required`, all of them placeholder values the test then
        // overwrites through the UI. Nothing below is set except by an edit.
        var session = new ProjectSession(new EngineModelDocument
        {
            Name = "untitled",
            Engine = new EngineSpec
            {
                BoreMm = 80, StrokeMm = 80, RodLengthMm = 130, CompressionRatio = 10,
            },
            IntakeValves = new ValveTrainSpec
            {
                HeadDiameterMm = 30, MaxLiftMm = 9, OpenDeg = 350, CloseDeg = 580,
            },
            ExhaustValves = new ValveTrainSpec
            {
                HeadDiameterMm = 26, MaxLiftMm = 9, OpenDeg = 140, CloseDeg = 370,
            },
            IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 36 },
            ExhaustRunner = new DuctSpec { LengthMm = 500, DiameterMm = 34 },
            Combustion = new CombustionSpec(),
        });

        var workspace = new DesignWorkspace(session, new UserPreferences { Mode = UiMode.Advanced });

        var typed = new (string Path, string Text)[]
        {
            ("Name", "Gate engine"),
            ("Engine.BoreMm", "88"),
            ("Engine.StrokeMm", "64"),
            ("Engine.RodLengthMm", "112"),
            ("Engine.PinOffsetMm", "0.8"),
            ("Engine.CompressionRatio", "11.8"),
            ("Engine.CylinderCount", "4"),
            ("Combustion.WallTemperatureK", "160"),          // 160 °C

            ("IntakeValves.HeadDiameterMm", "32"),
            ("IntakeValves.ThroatDiameterMm", "28"),
            ("IntakeValves.Count", "2"),
            ("IntakeValves.MaxLiftMm", "10.2"),
            ("IntakeValves.OpenDeg", "342"),
            ("IntakeValves.CloseDeg", "588"),
            ("IntakeValves.CamShape", "Harmonic"),
            ("ExhaustValves.HeadDiameterMm", "27"),
            ("ExhaustValves.ThroatDiameterMm", "24"),
            ("ExhaustValves.Count", "2"),
            ("ExhaustValves.MaxLiftMm", "9.6"),
            ("ExhaustValves.OpenDeg", "132"),
            ("ExhaustValves.CloseDeg", "378"),
            ("ExhaustValves.CamShape", "Harmonic"),

            ("IntakeRunner.LengthMm", "330"),
            ("IntakeRunner.DiameterMm", "39"),
            ("IntakeRunner.RoughnessMm", "0.02"),
            ("ExhaustRunner.LengthMm", "540"),
            ("ExhaustRunner.DiameterMm", "37"),
            ("ExhaustRunner.RoughnessMm", "0.05"),

            ("PipeThermal.Friction", "true"),
            ("PipeThermal.WallHeatTransfer", "true"),
            ("PipeThermal.IntakeSurface", "Bare stainless"),
            ("PipeThermal.ExhaustSurface", "Header wrap"),
            ("PipeThermal.IntakeWallStartK", "62"),           // 62 °C
            ("PipeThermal.ExhaustWallStartK", "427"),         // 427 °C
            ("PipeThermal.FixIntakeWall", "true"),
            ("PipeThermal.FixExhaustWall", "false"),
            ("PipeThermal.ArealHeatCapacityJPerM2K", "7900"),
            ("PipeThermal.ExternalHtcWPerM2K", "18"),
            ("PipeThermal.WallConvergenceK", "0.5"),

            ("Combustion.Fuel", "E85"),
            ("Combustion.Lambda", "0.88"),
            ("Combustion.StartDeg", "-18"),
            ("Combustion.DurationDeg", "52"),
            ("Combustion.Efficiency", "0.97"),
            ("Combustion.HeatTransfer", "Hohenberg"),
            ("Combustion.TwoZoneHeatTransfer", "true"),
            ("Combustion.TrackKnock", "true"),
            ("Ambient.PressureKPa", "99.5"),
            ("Ambient.TemperatureK", "27"),                  // 27 °C
        };

        foreach (var (path, text) in typed)
        {
            var outcome = workspace.Edit(path, text);
            outcome.Accepted.Should().BeTrue($"typing '{text}' into {path} must be accepted: {outcome.Reason}");
        }

        // Everything the catalogue offers was exercised — if a field exists in
        // the UI it is in the list above, so the gate really does cover the
        // whole editable surface.
        var untouched = DesignCatalogue.Fields
            .Select(f => f.Path)
            .Except(typed.Select(t => t.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        untouched.Should().BeEmpty("the gate must type into every field the Design workspace exposes");

        output.WriteLine($"built {typed.Length} fields through the workspace edit API");
        return session;
    }

    [Fact]
    public void Gate_a_model_built_entirely_through_the_ui_runs_identically_from_the_cli()
    {
        var session = BuildThroughTheUi(output);

        // Save exactly as the app would, then load exactly as the CLI does.
        var saved = session.Document.Save();
        var reloaded = EngineModelDocument.Load(saved);

        reloaded.Validate().Should().NotContain(i => i.Severity == ModelIssueSeverity.Error,
            "a model built through the UI must be runnable without hand-editing the file");

        const double rpm = 6000.0;
        var fromUi = OperatingPointRunner.Run(session.Document, rpm);
        var fromCli = OperatingPointRunner.Run(reloaded, rpm);

        output.WriteLine($"UI  : VE {fromUi.VolumetricEfficiency:F6}, torque {fromUi.TorqueNm:F6} N·m, " +
                         $"IMEP {fromUi.ImepPa:F3} Pa, knock {fromUi.KnockIntegral:F6}");
        output.WriteLine($"CLI : VE {fromCli.VolumetricEfficiency:F6}, torque {fromCli.TorqueNm:F6} N·m, " +
                         $"IMEP {fromCli.ImepPa:F3} Pa, knock {fromCli.KnockIntegral:F6}");

        // Bit-identical, not "close": the plan's determinism rule (Part 0)
        // makes this an equality, and anything less would mean the save/load
        // round trip is losing precision somewhere.
        fromUi.VolumetricEfficiency.Should().Be(fromCli.VolumetricEfficiency);
        fromUi.ImepPa.Should().Be(fromCli.ImepPa);
        fromUi.BmepPa.Should().Be(fromCli.BmepPa);
        fromUi.TorqueNm.Should().Be(fromCli.TorqueNm);
        fromUi.PowerW.Should().Be(fromCli.PowerW);
        fromUi.BsfcGPerKwh.Should().Be(fromCli.BsfcGPerKwh);
        fromUi.PeakPressurePa.Should().Be(fromCli.PeakPressurePa);
        fromUi.KnockIntegral.Should().Be(fromCli.KnockIntegral);
    }

    [Fact]
    public void Gate_the_saved_file_round_trips_byte_for_byte()
    {
        var session = BuildThroughTheUi(output);

        var first = session.Document.Save();
        var second = EngineModelDocument.Load(first).Save();

        second.Should().Be(first, "save → load → save must be a fixed point, or the file is lossy");
    }

    [Fact]
    public void Gate_the_ui_actually_wrote_the_values_that_were_typed()
    {
        // Guards against the round trip being identical because BOTH sides are
        // wrong — a save/load fixed point proves nothing if the edits never
        // landed. Spot-checks each kind of field, including the two that
        // convert units and the one that canonicalises a choice.
        var session = BuildThroughTheUi(output);
        var d = session.Document;

        d.Name.Should().Be("Gate engine");
        d.Engine.BoreMm.Should().Be(88.0);
        d.Engine.CylinderCount.Should().Be(4);
        d.Engine.PinOffsetMm.Should().Be(0.8);
        d.IntakeValves.CamShape.Should().Be("Harmonic");
        d.Combustion!.Lambda.Should().Be(0.88);
        d.Combustion.StartDeg.Should().Be(-18.0);
        d.Combustion.TrackKnock.Should().BeTrue();
        d.Combustion.TwoZoneHeatTransfer.Should().BeTrue();
        d.Combustion.HeatTransfer.Should().Be("Hohenberg");

        // Typed in °C, stored in K.
        d.Ambient.TemperatureK.Should().BeApproximately(300.15, 1e-9);
        d.Combustion.WallTemperatureK.Should().BeApproximately(433.15, 1e-9);

        // Typed as "E85", stored as the canonical library name.
        d.Combustion.Fuel.Should().Contain("E85");
        WaveBench.Core.Thermo.Fuels.FuelLibrary.All
            .Should().Contain(f => f.Name == d.Combustion.Fuel,
                "the document should hold a name the fuel library recognises exactly");
    }

    [Fact]
    public void Gate_every_field_the_user_set_is_marked_as_theirs()
    {
        // §8.5: a value the user typed is protected from any later derivation
        // or wizard. If the workspace wrote around the session this would be
        // Auto, and a wizard could silently overwrite the whole engine.
        var session = BuildThroughTheUi(output);

        foreach (var field in DesignCatalogue.Fields)
        {
            session.Provenance[field.Path].Origin.Should().Be(Provenance.You, $"{field.Path} was typed by the user");
            session.Provenance[field.Path].IsProtected.Should().BeTrue();
        }
    }

    [Fact]
    public void Gate_every_shipped_template_validates_and_runs()
    {
        // "New from template → Run" has to work without the user guessing a
        // number first, or the templates are decoration.
        foreach (var template in ModelTemplates.All)
        {
            var session = ModelTemplates.Open(template);
            var errors = session.Document.Validate()
                .Where(i => i.Severity == ModelIssueSeverity.Error)
                .ToList();
            errors.Should().BeEmpty($"template '{template.Id}' must be runnable as shipped");

            var fast = session.Document with
            {
                Solver = session.Document.Solver with { CellSizeMm = 12.0, MinCycles = 3, MaxCycles = 5 },
            };
            var result = OperatingPointRunner.Run(fast, 5000.0);

            output.WriteLine($"{template.Id,-24} VE {result.VolumetricEfficiency:F3}, torque {result.TorqueNm:F1} N·m");
            result.VolumetricEfficiency.Should().BeInRange(0.2, 1.6);
            double.IsFinite(result.TorqueNm).Should().BeTrue();

            template.Provenance.Should().NotBeNullOrWhiteSpace(
                "a template must say where its numbers came from");
        }
    }

    [Fact]
    public void Gate_a_template_is_marked_auto_so_a_wizard_may_still_refine_it()
    {
        // The mirror of the provenance gate above: template values are NOT
        // the user's choices, so they must stay overwritable.
        var session = ModelTemplates.Open(ModelTemplates.All[0]);

        foreach (var field in DesignCatalogue.Fields)
        {
            session.Provenance[field.Path].Origin.Should().Be(Provenance.Auto);
            session.Provenance[field.Path].IsProtected.Should().BeFalse();
            session.Provenance[field.Path].Derivation.Should().NotBeNullOrEmpty(
                "the badge hover has to name the template");
        }

        session.EditByDerivation("Engine.BoreMm", 90.0, "test derivation")
            .Should().BeTrue("a derived default may refine a template value");
    }

    [Fact]
    public void A_units_preference_cannot_change_the_document()
    {
        // §8.8 rule 4: units are a view preference. Switching them must not
        // touch a single byte of the model.
        var session = BuildThroughTheUi(output);
        var preferences = new UserPreferences { Mode = UiMode.Advanced };
        var workspace = new DesignWorkspace(session, preferences);

        var before = session.Document.Save();
        preferences.Units = UnitSystem.Imperial;
        _ = workspace.Fields(DesignTab.Engine);
        _ = workspace.Derived(DesignTab.Engine);
        preferences.Mode = UiMode.Simple;
        _ = workspace.Fields(DesignTab.HeadAndCam);

        session.Document.Save().Should().Be(before, "looking at a model is not editing it");
    }
}
