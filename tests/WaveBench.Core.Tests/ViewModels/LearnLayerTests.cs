using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// The rest of the Phase 24 scope beyond the three gate clauses: the Concepts
/// panel, guided tours, the generic-defaults banner and the global search
/// (plan §8.9, §8.10, §8.11).
/// </summary>
public class LearnLayerTests(ITestOutputHelper output)
{
    private static EngineModelDocument TestEngine() => new()
    {
        Name = "learn layer engine",
        Engine = new EngineSpec
        {
            BoreMm = 86, StrokeMm = 86, RodLengthMm = 145, CompressionRatio = 10.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 28, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 370 },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
        ExhaustRunner = new DuctSpec { LengthMm = 400, DiameterMm = 38 },
        Combustion = new CombustionSpec { Fuel = "RON95", Lambda = 0.95 },
    };

    // ---- Concepts (§8.9) ---------------------------------------------------

    /// <summary>
    /// A concept that names a field nobody can edit is a dead link, and the
    /// field-to-concept lookup runs off exactly these strings.
    /// </summary>
    [Fact]
    public void Every_concept_points_at_fields_that_exist()
    {
        foreach (var concept in ConceptLibrary.All)
        {
            output.WriteLine($"{concept.Id}: {concept.Fields.Count} fields, {concept.Body.Count} paragraphs");

            concept.Body.Should().NotBeEmpty($"{concept.Id} has nothing to say");
            concept.Summary.Should().NotBeNullOrWhiteSpace();

            foreach (var path in concept.Fields)
            {
                FieldLocator.Find(path).Should().NotBeNull(
                    $"concept '{concept.Id}' governs '{path}', which is in no catalogue");
            }
        }

        ConceptLibrary.All.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Plan §8.9 asks that the explainers be <i>"linked from the fields they
    /// govern"</i>. The six it names by name are the ones to check.
    /// </summary>
    [Fact]
    public void The_concepts_the_plan_names_are_all_present()
    {
        string[] required =
        [
            "wave-tuning", "helmholtz", "discharge-coefficient",
            "engine-orders", "surge", "blade-speed-ratio",
        ];

        foreach (var id in required)
        {
            ConceptLibrary.Find(id).Should().NotBeNull($"plan §8.9 names {id} explicitly");
        }
    }

    [Fact]
    public void A_field_finds_the_concept_that_explains_it()
    {
        ConceptLibrary.For("IntakeRunner.LengthMm").Should().Contain(c => c.Id == "wave-tuning");
        ConceptLibrary.For("ForcedInduction.TurbineAreaRatio").Should().Contain(c => c.Id == "blade-speed-ratio");

        // ...and a field with no concept behind it simply has none, rather
        // than matching everything.
        ConceptLibrary.For("Name").Should().BeEmpty();
    }

    /// <summary>A figure a concept points at has to be a real place to go.</summary>
    [Fact]
    public void Every_concept_figure_resolves_to_a_real_sub_tab()
    {
        var shell = new ShellViewModel(new ProjectSession(TestEngine()), new UserPreferences());

        foreach (var concept in ConceptLibrary.All.Where(c => c.Figure is not null))
        {
            var figure = concept.Figure!;
            output.WriteLine($"{concept.Id} → {figure.Describe()}");

            shell.Workspaces.First(w => w.Workspace == figure.Workspace!.Value)
                .SubTabs.Should().Contain(figure.Target);
        }
    }

    // ---- Tours (§8.9) ------------------------------------------------------

    [Fact]
    public void Every_tour_step_points_somewhere_that_exists()
    {
        var shell = new ShellViewModel(new ProjectSession(TestEngine()), new UserPreferences());

        foreach (var tour in TourLibrary.All)
        {
            output.WriteLine($"{tour.Workspace}: {tour.Steps.Count} steps");
            tour.Steps.Should().NotBeEmpty();

            foreach (var step in tour.Steps)
            {
                step.Body.Length.Should().BeGreaterThan(40, $"\"{step.Title}\" needs to say something");

                if (step.Target is not { } target)
                {
                    continue;
                }

                switch (target.Kind)
                {
                    case WarningTarget.Field:
                        FieldLocator.Find(target.Target).Should().NotBeNull(
                            $"{tour.Workspace} step \"{step.Title}\" points at a field that does not exist");
                        break;
                    case WarningTarget.Plot:
                        shell.Workspaces.First(w => w.Workspace == target.Workspace!.Value)
                            .SubTabs.Should().Contain(target.Target,
                                $"{tour.Workspace} step \"{step.Title}\" points at a sub-tab that does not exist");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// <i>"Skippable and re-runnable"</i> (§8.9) — so skipping must not record
    /// anything that would stop it being taken again.
    /// </summary>
    [Fact]
    public void A_skipped_tour_can_be_taken_again()
    {
        var tour = new TourController();

        tour.Start(Workspace.Design).Should().BeTrue();
        tour.Next();
        tour.Position.Should().Be("Step 2 of 5");

        tour.Stop();
        tour.IsRunning.Should().BeFalse();

        tour.Start(Workspace.Design).Should().BeTrue();
        tour.Step.Should().Be(0, "a tour taken again starts at the beginning");
        tour.CurrentStep.Should().NotBeNull();
    }

    [Fact]
    public void A_tour_ends_after_its_last_step_rather_than_running_off_the_end()
    {
        var tour = new TourController();
        tour.Start(Workspace.Boost);

        for (var i = 0; i < 50; i++)
        {
            tour.Next();
        }

        tour.IsRunning.Should().BeFalse();
        tour.CurrentStep.Should().BeNull();

        // Back at the start is a no-op rather than an index below zero.
        tour.Back();
        tour.Step.Should().Be(0);
    }

    // ---- Generic-defaults banner (§8.10) -----------------------------------

    /// <summary>
    /// The regression that made this test worth writing: the discharge-
    /// coefficient caveat — the largest one in the tool — used to be
    /// suppressed by ANY import under the valve block, and the shipped sample
    /// imports a measured CAM file. The caveat silently vanished on evidence
    /// about a different quantity.
    /// </summary>
    [Fact]
    public void A_measured_cam_does_not_silence_the_discharge_coefficient_caveat()
    {
        var session = new ProjectSession(TestEngine());
        session.EditByImport("IntakeValves.MaxLiftMm", 10.0, "cam-measured.csv");

        var caveats = new Guardrails(session, new UserPreferences()).GenericDefaults();
        foreach (var caveat in caveats)
        {
            output.WriteLine($"[{caveat.Weight}] {caveat.Title} → {caveat.Fix}");
        }

        caveats.Should().Contain(c => c.Title.Contains("Discharge", StringComparison.Ordinal),
            "a measured cam says nothing about how well the port flows");

        // ...and the cam caveat correctly drops to the exhaust side only.
        caveats.Should().Contain(c => c.Title.Contains("exhaust cam", StringComparison.Ordinal));
        caveats.Should().NotContain(c => c.Title.Contains("Both cam", StringComparison.Ordinal));
    }

    [Fact]
    public void The_banner_leads_with_the_dominant_error_source()
    {
        var guardrails = new Guardrails(new ProjectSession(TestEngine()), new UserPreferences());
        var banner = guardrails.Banner();

        output.WriteLine(banner);

        banner.Should().NotBeNull();
        banner.Should().Contain("largest error",
            "§8.10: a beginner should know the biggest error source is the data they did not supply");

        guardrails.GenericDefaults()[0].Weight.Should().Be(CaveatWeight.Dominant,
            "the list is ordered worst-first, so the banner and the list agree");
    }

    /// <summary>Every caveat offers something the user can actually do.</summary>
    [Fact]
    public void Every_caveat_carries_a_remedy_and_somewhere_to_go()
    {
        var session = new ProjectSession(TestEngine());
        session.EditByUser("Engine.CompressionRatio", 17.0); // unusual, not refused

        var caveats = new Guardrails(session, new UserPreferences()).All();
        caveats.Should().NotBeEmpty();

        foreach (var caveat in caveats)
        {
            caveat.Fix.Should().NotBeNullOrWhiteSpace($"'{caveat.Title}' states a problem with no way out");
            caveat.Links.Should().NotBeEmpty($"'{caveat.Title}' has nowhere to send the user");

            foreach (var link in caveat.Links.Where(l => l.Kind == WarningTarget.Field))
            {
                FieldLocator.Find(link.Target).Should().NotBeNull();
            }
        }

        caveats.Should().Contain(c => c.Title.Contains("Compression ratio", StringComparison.Ordinal));
    }

    /// <summary>
    /// A forced-induction field on a naturally aspirated model is unused, not
    /// unusual. Warning about inert values is how a warning list teaches
    /// people to ignore warning lists.
    /// </summary>
    [Fact]
    public void Unused_forced_induction_fields_are_not_flagged_on_an_na_model()
    {
        var guardrails = new Guardrails(new ProjectSession(TestEngine()), new UserPreferences());

        guardrails.UnusualInputs().Should().NotContain(
            c => c.Links.Any(l => l.Target.StartsWith("ForcedInduction.", StringComparison.Ordinal)));
    }

    // ---- Global search (§8.11) --------------------------------------------

    /// <summary>
    /// §8.11 asks for a palette <i>"reaching every field"</i>. It used to
    /// carry the Simple-mode subset, which made it a shortcut to the fields
    /// already on screen and no help for the ones that are not.
    /// </summary>
    [Fact]
    public void The_palette_reaches_every_design_field()
    {
        var shell = new ShellViewModel(new ProjectSession(TestEngine()), new UserPreferences());
        var palette = new CommandPalette(shell);
        var commands = palette.AllCommands();

        var reachable = commands
            .Where(c => c.Kind == CommandKind.EditField && c.Path is not null)
            .Select(c => c.Path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = DesignCatalogue.Fields.Where(f => !reachable.Contains(f.Path)).ToList();
        output.WriteLine($"{commands.Count} commands; {reachable.Count} fields reachable, {missing.Count} missing");

        missing.Should().BeEmpty();
    }

    [Fact]
    public void The_palette_finds_concepts_and_sweeps_by_what_they_are_about()
    {
        var shell = new ShellViewModel(new ProjectSession(TestEngine()), new UserPreferences());
        var palette = new CommandPalette(shell);

        // Somebody puzzled by their runner length types "runner", not
        // "Helmholtz" — so a concept has to be findable by what it governs.
        var byField = palette.Search("runner");
        foreach (var command in byField.Take(8))
        {
            output.WriteLine($"  [{command.Kind}] {command.Title}");
        }

        byField.Should().Contain(c => c.Kind == CommandKind.Concept);
        byField.Should().Contain(c => c.Kind == CommandKind.ShowMe);

        palette.Search("surge").Should().Contain(c => c.Kind == CommandKind.Concept && c.Path == "surge");
    }

    // ---- "Show me" controller ---------------------------------------------

    [Fact]
    public async Task The_show_me_controller_reports_running_then_a_result()
    {
        var controller = new ShowMeController();
        var states = new List<string>();
        controller.Changed = () => states.Add(controller.IsRunning ? "running" : "done");

        var showMe = new ShowMe(TestEngine(), new UserPreferences());
        await controller.StartAsync(TestEngine(), new UserPreferences(), "Combustion.Lambda");

        output.WriteLine(string.Join(" → ", states));
        output.WriteLine(controller.Study?.Narration ?? controller.Error ?? "nothing");

        states.Should().Equal("running", "done");
        controller.Error.Should().BeNull();
        controller.Study.Should().NotBeNull();
        controller.Path.Should().Be("Combustion.Lambda");

        // Closing clears it, so the panel does not reopen on the next render.
        controller.Close();
        controller.Path.Should().BeNull();
        controller.Study.Should().BeNull();

        showMe.Speeds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_refused_field_reports_an_error_rather_than_throwing_into_the_ui()
    {
        var controller = new ShowMeController();
        await controller.StartAsync(TestEngine(), new UserPreferences(), "PipeThermal.WallConvergenceK");

        output.WriteLine(controller.Error);

        controller.Error.Should().NotBeNullOrWhiteSpace();
        controller.Study.Should().BeNull();
        controller.IsRunning.Should().BeFalse();
    }
}
