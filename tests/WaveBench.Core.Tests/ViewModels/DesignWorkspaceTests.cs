using System.Reflection;
using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 17 (§8.4) Design workspace: the field catalogue, editing through the
/// session, unit conversion at the boundary, and the derived readouts.
/// </summary>
public class DesignWorkspaceTests(ITestOutputHelper output)
{
    private static DesignWorkspace Advanced(out ProjectSession session)
    {
        session = ModelTemplates.Open(ModelTemplates.Find("single-450")!);
        return new DesignWorkspace(session, new UserPreferences { Mode = UiMode.Advanced });
    }

    [Fact]
    public void Gate_the_catalogue_covers_every_editable_document_property()
    {
        // The Phase 17 gate asks that a complete model can be built through
        // the UI. A document property missing from the catalogue is a
        // property nobody can set, so this walks the SCHEMA rather than a
        // hand-maintained list — adding a field to the document and
        // forgetting the UI fails here.
        var missing = new List<string>();

        void Walk(Type type, string prefix)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                if (DesignCatalogue.OwnedElsewhere.Contains(path.Split('.')[0]))
                {
                    continue;
                }

                var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (t.IsClass && t != typeof(string) && t.Namespace == typeof(EngineModelDocument).Namespace)
                {
                    Walk(t, path);
                    continue;
                }

                if (DesignCatalogue.Find(path) is null)
                {
                    missing.Add(path);
                }
            }
        }

        Walk(typeof(EngineModelDocument), string.Empty);

        foreach (var path in missing)
        {
            output.WriteLine("NOT EDITABLE IN THE UI: " + path);
        }

        missing.Should().BeEmpty("every model field a user owns must be reachable from the Design workspace");
    }

    [Fact]
    public void Every_catalogue_path_resolves_against_the_document()
    {
        // The mirror of the test above: a catalogue entry pointing at a path
        // the document does not have would render a permanently blank field.
        var document = ModelTemplates.Find("single-450")!.Create();
        foreach (var field in DesignCatalogue.Fields)
        {
            var act = () => ModelPath.Canonicalise(typeof(EngineModelDocument), field.Path);
            act.Should().NotThrow($"'{field.Path}' must exist on the document");
        }
    }

    [Fact]
    public void No_path_appears_twice_and_every_field_has_a_tab()
    {
        DesignCatalogue.Fields.Select(f => f.Path)
            .Should().OnlyHaveUniqueItems("a field drawn on two tabs would edit itself from two places");

        DesignCatalogue.Fields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Label));
        DesignCatalogue.Fields.Where(f => f.Kind == FieldKind.Choice)
            .Should().OnlyContain(f => f.Choices != null && f.Choices.Count > 0,
                "a choice field with no choices cannot be edited");
    }

    [Fact]
    public void Gate_an_edit_writes_through_the_session_and_stamps_provenance()
    {
        var workspace = Advanced(out var session);

        session.Provenance["Engine.BoreMm"].Origin.Should().Be(Provenance.Auto, "it came from a template");

        workspace.Edit("Engine.BoreMm", "99.5").Accepted.Should().BeTrue();

        session.Document.Engine.BoreMm.Should().Be(99.5);
        session.Provenance["Engine.BoreMm"].Origin.Should().Be(Provenance.You,
            "a value the user typed is theirs and is protected from later derivation");
        session.CanUndo.Should().BeTrue();

        session.Undo().Should().BeTrue();
        session.Document.Engine.BoreMm.Should().Be(96.0);
        session.Provenance["Engine.BoreMm"].Origin.Should().Be(Provenance.Auto, "undo restores the whole entry");
    }

    [Theory]
    [InlineData("Engine.BoreMm", "not a number", "Not a number")]
    [InlineData("Engine.BoreMm", "5", "Below the plausible")]
    [InlineData("Engine.BoreMm", "5000", "Above the plausible")]
    [InlineData("Combustion.Fuel", "Kerosene", "Must be one of")]
    [InlineData("Combustion.TrackKnock", "maybe", "true or false")]
    [InlineData("Name", "   ", "cannot be empty")]
    public void A_rejected_edit_says_why_and_changes_nothing(string path, string text, string expected)
    {
        var workspace = Advanced(out var session);
        var before = session.Document.Save();

        var outcome = workspace.Edit(path, text);

        outcome.Accepted.Should().BeFalse();
        outcome.Reason.Should().Contain(expected);
        session.Document.Save().Should().Be(before, "a rejected edit must not half-apply");
        session.CanUndo.Should().BeFalse("and must not leave an undo step behind");
    }

    [Fact]
    public void Gate_units_convert_at_the_boundary_and_the_document_stays_metric()
    {
        // Plan §8.11 mm/inch toggle, and the CLAUDE.md rule that the document
        // is SI internally, everywhere, converted once at the boundary.
        var session = ModelTemplates.Open(ModelTemplates.Find("single-450")!);
        var preferences = new UserPreferences { Mode = UiMode.Advanced, Units = UnitSystem.Imperial };
        var workspace = new DesignWorkspace(session, preferences);

        var bore = workspace.View("Engine.BoreMm");
        bore.DisplayUnit.Should().Be("in");
        bore.Display.Should().Be("3.780", "96 mm is 3.780 in");

        // Type 4 inches; the document must hold 101.6 mm.
        workspace.Edit("Engine.BoreMm", "4.0").Accepted.Should().BeTrue();
        session.Document.Engine.BoreMm.Should().BeApproximately(101.6, 1e-9);

        // And the metric view of the same document reads back in mm.
        preferences.Units = UnitSystem.Metric;
        workspace.View("Engine.BoreMm").DisplayUnit.Should().Be("mm");
        workspace.View("Engine.BoreMm").Display.Should().Be("101.6");
    }

    [Fact]
    public void Unit_conversion_round_trips_exactly_for_every_quantity()
    {
        foreach (var units in new[] { UnitSystem.Metric, UnitSystem.Imperial })
        {
            var workspace = new DesignWorkspace(
                new ProjectSession(ModelTemplates.Find("single-450")!.Create()),
                new UserPreferences { Units = units });

            foreach (var field in DesignCatalogue.Fields.Where(f => f.Quantity != Quantity.None))
            {
                foreach (var value in new[] { 1.0, 42.5, 300.0 })
                {
                    var back = workspace.ToModel(field, workspace.ToDisplay(field, value));
                    back.Should().BeApproximately(value, 1e-9,
                        $"{field.Path} must survive a {units} round trip");
                }
            }
        }
    }

    [Fact]
    public void Simple_mode_shows_fewer_fields_but_never_silently_drops_an_active_one()
    {
        var session = ModelTemplates.Open(ModelTemplates.Find("single-450")!);
        var preferences = new UserPreferences { Mode = UiMode.Simple };
        var workspace = new DesignWorkspace(session, preferences);
        var shell = new ShellViewModel(session, preferences);

        var simple = workspace.Fields(DesignTab.Engine).Count;
        preferences.Mode = UiMode.Advanced;
        var advanced = workspace.Fields(DesignTab.Engine).Count;

        advanced.Should().BeGreaterThan(simple);

        // Set an advanced-only field, then go back to Simple: it is still in
        // force, so the shell must declare it (plan §8.8 rule 7).
        workspace.Edit("Engine.PinOffsetMm", "1.5").Accepted.Should().BeTrue();
        preferences.Mode = UiMode.Simple;

        workspace.Fields(DesignTab.Engine).Should().NotContain(f => f.Field.Path == "Engine.PinOffsetMm");
        shell.AdvancedSettingsBanner().Should().NotBeNull();
        shell.AdvancedOnlyActivePaths().Should().Contain("Engine.PinOffsetMm");
    }

    [Fact]
    public void Derived_readouts_are_computed_and_flag_implausible_geometry()
    {
        var workspace = Advanced(out var session);

        var engine = workspace.Derived(DesignTab.Engine);
        engine.Should().Contain(r => r.Label == "Displacement");
        var displacement = engine.First(r => r.Label == "Displacement");
        output.WriteLine($"{displacement.Label}: {displacement.Value} ({displacement.Note})");
        displacement.Value.Should().Contain("449").And.Contain("cc", "96 × 62.1 is a 450");

        // A deliberately short rod must raise the warning, not just render.
        // 85 mm on a 62.1 mm stroke is a rod ratio of 2.74 — short, but still
        // longer than the stroke, so the document itself stays valid.
        workspace.Edit("Engine.RodLengthMm", "85").Accepted.Should().BeTrue();
        workspace.Derived(DesignTab.Engine)
            .First(r => r.Label == "Rod ratio").Warning.Should().NotBeNull();
    }

    [Fact]
    public void Head_readouts_include_the_overlap_the_plan_asks_to_be_live()
    {
        var workspace = Advanced(out _);
        var head = workspace.Derived(DesignTab.HeadAndCam);

        var overlap = head.First(r => r.Label == "Valve overlap");
        output.WriteLine($"{overlap.Label}: {overlap.Value}");
        overlap.Value.Should().Be("40°", "intake opens 340, exhaust closes 380");

        head.Should().Contain(r => r.Label == "Intake curtain area");
    }

    [Fact]
    public void Fuel_readouts_show_charge_cooling_and_density_altitude()
    {
        var workspace = Advanced(out var session);
        var fuel = workspace.Derived(DesignTab.FuelAndCombustion);

        var cooling = fuel.First(r => r.Label == "Charge cooling ΔT");
        output.WriteLine($"{cooling.Label}: {cooling.Value}");
        cooling.Value.Should().StartWith("−").And.Contain("K");

        fuel.Should().Contain(r => r.Label == "Density altitude");
        fuel.Should().Contain(r => r.Label == "Air/fuel ratio");

        // E85 must cool the charge markedly more than petrol — the reason the
        // plan wants this readout prominent.
        // The readout is signed as a drop ("−5.1 K"), so "cools more" is a
        // larger magnitude, not a larger number.
        var petrol = Math.Abs(ParseCooling(workspace));
        workspace.Edit("Combustion.Fuel", "E85").Accepted.Should().BeTrue();
        var e85 = Math.Abs(ParseCooling(workspace));
        output.WriteLine($"charge cooling: RON95 −{petrol:F1} K, E85 −{e85:F1} K ({e85 / petrol:F1}×)");
        e85.Should().BeGreaterThan(petrol * 1.5, "ethanol's latent heat is far higher than petrol's");

        static double ParseCooling(DesignWorkspace w) => double.Parse(
            w.Derived(DesignTab.FuelAndCombustion).First(r => r.Label == "Charge cooling ΔT").Value
                .Replace("−", "-").Replace(" K", ""),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void The_manifold_tab_says_the_canvas_is_still_to_come()
    {
        // Honesty rule: an absent feature is named, not merely missing.
        var workspace = Advanced(out _);
        workspace.Derived(DesignTab.Manifold)
            .Should().Contain(r => r.Value.Contains("Phase 18"));
    }
}
