using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 16 gate (plan Part 12):
///  • a model round-trips Simple → Advanced → Simple → Advanced with
///    byte-identical output at every step;
///  • a simulated wizard re-run provably never modifies a You, Imported or
///    Optimised field;
///  • killing the process mid-sweep and restarting recovers model and job
///    state;
///  • no hard-coded colours (the XAML grep lives in XamlTokenTests).
/// </summary>
public class ShellGateTests(ITestOutputHelper output)
{
    private static EngineModelDocument Model() => new()
    {
        Name = "gate model",
        Engine = new EngineSpec { BoreMm = 86, StrokeMm = 62, RodLengthMm = 107, CompressionRatio = 11 },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 31, Count = 2, MaxLiftMm = 10, OpenDeg = 340, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 26, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 380 },
        IntakeRunner = new DuctSpec { LengthMm = 600, DiameterMm = 38 },
        ExhaustRunner = new DuctSpec { LengthMm = 200, DiameterMm = 35 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
    };

    [Fact]
    public void Gate_mode_round_trip_is_byte_identical_at_every_step()
    {
        var session = new ProjectSession(Model());
        var shell = new ShellViewModel(session);

        // Realistic starting state: a mix of origins, as after a wizard run
        // plus hand edits and an import.
        session.EditByUser("Engine.CompressionRatio", 12.5);
        session.EditByImport("IntakeValves.MaxLiftMm", 11.2, "cam-measured.csv");
        session.ApplyWizard(new Dictionary<string, object?> { ["IntakeRunner.LengthMm"] = 540.0 });

        var baseline = session.Document.Save();
        var provenanceBaseline = session.Provenance.Save();

        foreach (var mode in new[] { UiMode.Advanced, UiMode.Simple, UiMode.Advanced })
        {
            shell.Mode = mode;
            session.Document.Save().Should().Be(baseline,
                $"gate: switching to {mode} must not touch the document (plan §8.2/§8.8)");
            session.Provenance.Save().Should().Be(provenanceBaseline,
                "and must not touch provenance either");
        }

        shell.Mode.Should().Be(UiMode.Advanced);

        // Mode is a per-user preference, not model data: it must not appear
        // in the saved project at all (§8.8 rule 4).
        baseline.Should().NotContain("Simple").And.NotContain("Advanced");
    }

    [Fact]
    public void Gate_wizard_rerun_never_modifies_you_imported_or_optimised_fields()
    {
        var session = new ProjectSession(Model());

        session.EditByUser("Engine.CompressionRatio", 13.2);
        session.EditByImport("IntakeValves.MaxLiftMm", 11.2, "cam-measured.csv");
        session.EditByOptimiser("IntakeRunner.LengthMm", 512.5, "opt-2026-08-25-a");
        session.EditByDerivation("ExhaustRunner.LengthMm", 210.0, "0.35 × intake runner length");

        var protectedBefore = new Dictionary<string, object?>
        {
            ["Engine.CompressionRatio"] = session.Document.Engine.CompressionRatio,
            ["IntakeValves.MaxLiftMm"] = session.Document.IntakeValves.MaxLiftMm,
            ["IntakeRunner.LengthMm"] = session.Document.IntakeRunner.LengthMm,
        };

        // A wizard re-run that tries to change EVERYTHING, including the
        // protected fields.
        var wizardValues = new Dictionary<string, object?>
        {
            ["Engine.CompressionRatio"] = 9.5,
            ["IntakeValves.MaxLiftMm"] = 9.0,
            ["IntakeRunner.LengthMm"] = 400.0,
            ["ExhaustRunner.LengthMm"] = 250.0,
            ["Engine.BoreMm"] = 88.0,
        };

        // Preview first (§8.8 rule 3) — and the preview must not mutate.
        var preview = session.PreviewWizard(wizardValues);
        output.WriteLine(preview.DiffPreview());
        session.Document.Engine.CompressionRatio.Should().Be(13.2, "a preview changes nothing");

        var result = session.ApplyWizard(wizardValues);

        foreach (var (path, before) in protectedBefore)
        {
            ModelPath.Get(session.Document, path).Should().Be(before,
                $"gate: '{path}' is protected and must survive a wizard re-run untouched");
        }

        result.Blocked.Should().HaveCount(3);
        result.Blocked.Select(b => b.CurrentOrigin).Should().BeEquivalentTo(
            [Provenance.You, Provenance.Imported, Provenance.Optimised]);

        // Auto and previously-unset fields WERE updated — the wizard still works.
        session.Document.ExhaustRunner.LengthMm.Should().Be(250.0, "Auto fields are overwritten freely");
        session.Document.Engine.BoreMm.Should().Be(88.0);
        result.Applied.Should().HaveCount(2);

        // Explicit per-field opt-in is the ONLY way through (§8.5).
        var optIn = session.ApplyWizard(wizardValues, optIn: new HashSet<string> { "Engine.CompressionRatio" });
        session.Document.Engine.CompressionRatio.Should().Be(9.5, "opt-in is explicit and per field");
        session.Document.IntakeValves.MaxLiftMm.Should().Be(11.2, "the others stay protected");
        optIn.Blocked.Should().HaveCount(2);
    }

    [Fact]
    public void Gate_killing_the_process_mid_sweep_recovers_model_and_job_state()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wavebench-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var modelPath = Path.Combine(dir, "model.json");
        var provenancePath = Path.Combine(dir, "provenance.json");
        var jobsPath = Path.Combine(dir, "jobs.json");

        try
        {
            // --- session one: start a 20-point sweep, get to 14, then "crash"
            var session = new ProjectSession(Model());
            session.EditByUser("Engine.CompressionRatio", 12.0);
            var shell = new ShellViewModel(session);

            var job = shell.Jobs.Enqueue("sweep", "3000–9000 rpm, 20 points", total: 20);
            shell.Jobs.Start(job.Id);
            shell.Jobs.Checkpoint(job.Id, 14, token: "rpm=6500");
            shell.Jobs.Enqueue("optimise", "cam timing", total: 100);

            shell.Jobs.Summary().Should().Be("Jobs: sweep 14/20 · optimise queued");

            // Autosave (plan §8.11, every 60 s) is what survives the crash.
            File.WriteAllText(modelPath, session.Document.Save());
            File.WriteAllText(provenancePath, session.Provenance.Save());
            shell.Jobs.SaveTo(jobsPath);

            // --- process dies here; nothing else is in memory ---

            // --- session two: restart
            var recoveredDocument = EngineModelDocument.Load(File.ReadAllText(modelPath));
            var recoveredProvenance = ProvenanceMap.Load(File.ReadAllText(provenancePath));
            var recoveredSession = new ProjectSession(recoveredDocument, recoveredProvenance);
            var recoveredShell = new ShellViewModel(recoveredSession)
            {
                // tray restored from disk
            };
            var recoveredJobs = JobTray.LoadFrom(jobsPath);

            recoveredDocument.Save().Should().Be(session.Document.Save(),
                "gate: the model comes back exactly as it was");
            recoveredProvenance.OriginOf("Engine.CompressionRatio").Should().Be(Provenance.You,
                "gate: provenance survives too, or protection would silently lapse");

            var sweep = recoveredJobs.Jobs.Single(j => j.Kind == "sweep");
            sweep.Progress.Should().Be(14, "gate: the sweep resumes at its checkpoint, not from zero");
            sweep.Checkpoint.Should().Be("rpm=6500");
            sweep.State.Should().Be(JobState.Queued, "an interrupted job comes back runnable, not lost");
            sweep.IsResumable.Should().BeTrue();
            recoveredJobs.Active.Should().HaveCount(2);

            output.WriteLine($"recovered: {recoveredJobs.Summary()}");
            _ = recoveredShell;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Boost_is_hidden_on_a_naturally_aspirated_model_but_discoverable()
    {
        var shell = new ShellViewModel(new ProjectSession(Model()));

        var boost = shell.Workspaces.Single(w => w.Workspace == Workspace.Boost);
        boost.Visible.Should().BeFalse("plan §8.3: Boost is hidden entirely on an NA model");
        boost.HiddenReason.Should().NotBeNullOrWhiteSpace();
        boost.DiscoveryPath.Should().NotBeNullOrWhiteSpace("a hidden workspace still needs a discovery path");
        shell.Navigate(Workspace.Boost).Should().BeFalse();

        // The command palette is one of those paths, even while hidden.
        var palette = new CommandPalette(shell);
        palette.Search("forced induction").Should().Contain(c => c.Title == "Add forced induction");
        palette.Search("turbo").Should().Contain(c => c.Title == "Add forced induction",
            "aliases make the feature findable by the word a user would actually type");

        // Add a compressor and it appears immediately.
        shell.HasForcedInduction = true;
        shell.Workspaces.Single(w => w.Workspace == Workspace.Boost).Visible.Should().BeTrue();
        shell.Navigate(Workspace.Boost).Should().BeTrue();
        shell.VisibleWorkspaces.Should().Contain(w => w.Workspace == Workspace.Boost);
    }

    [Fact]
    public void Simple_mode_declares_active_advanced_settings_rather_than_hiding_them()
    {
        var session = new ProjectSession(Model());
        var shell = new ShellViewModel(session) { Mode = UiMode.Simple };

        shell.AdvancedSettingsBanner().Should().BeNull("nothing advanced is set yet");

        // Two settings Simple mode does not expose, both user-owned.
        session.EditByUser("Solver.Cfl", 0.6);
        session.EditByImport("ExhaustValves.ThroatDiameterMm", 22.5, "flowbench.csv");

        var banner = shell.AdvancedSettingsBanner();
        banner.Should().Be("2 advanced settings are active and not shown in Simple mode.",
            "plan §8.8 rule 2 / rule 7: Simple mode never lies by omission");
        shell.AdvancedOnlyActivePaths().Should().BeEquivalentTo(
            ["Solver.Cfl", "ExhaustValves.ThroatDiameterMm"]);

        // A Simple-mode field the user set is NOT advanced-only.
        session.EditByUser("Engine.BoreMm", 87.0);
        shell.AdvancedOnlyActivePaths().Should().HaveCount(2);

        shell.Mode = UiMode.Advanced;
        shell.AdvancedSettingsBanner().Should().BeNull("in Advanced everything is already visible");
    }

    [Fact]
    public void Undo_spans_mode_switches_and_restores_provenance()
    {
        var session = new ProjectSession(Model());
        var shell = new ShellViewModel(session);

        var original = session.Document.Engine.BoreMm;
        session.EditByUser("Engine.BoreMm", 90.0);
        session.Provenance.OriginOf("Engine.BoreMm").Should().Be(Provenance.You);

        // Mode changes are not model changes (§8.8 rule 5), so they must not
        // consume or disturb the undo stack.
        shell.Mode = UiMode.Advanced;
        shell.Mode = UiMode.Simple;
        session.UndoStack.Should().HaveCount(1);

        session.Undo().Should().BeTrue();
        session.Document.Engine.BoreMm.Should().Be(original);
        session.Provenance.OriginOf("Engine.BoreMm").Should().Be(Provenance.Auto,
            "undo restores the previous origin, not just the value");

        session.Redo().Should().BeTrue();
        session.Document.Engine.BoreMm.Should().Be(90.0);
        session.Provenance.OriginOf("Engine.BoreMm").Should().Be(Provenance.You);
    }

    [Fact]
    public void Auto_derivations_carry_their_citation_for_the_badge_hover()
    {
        var session = new ProjectSession(Model());
        session.EditByDerivation("ExhaustValves.ThroatDiameterMm", 22.1,
            derivation: "0.85 × valve head diameter", citation: "Blair, Design and Simulation of Four-Stroke Engines");

        var entry = session.Provenance["ExhaustValves.ThroatDiameterMm"];
        entry.Origin.Should().Be(Provenance.Auto);
        entry.Derivation.Should().NotBeNullOrWhiteSpace("plan §8.5: hovering Auto shows the derivation");
        entry.Citation.Should().NotBeNullOrWhiteSpace("and its source citation");
        entry.IsProtected.Should().BeFalse();

        // Imported and Optimised record where they came from.
        session.EditByImport("IntakeValves.MaxLiftMm", 11.2, "cam-measured.csv");
        session.Provenance["IntakeValves.MaxLiftMm"].SourceRef.Should().Be("cam-measured.csv");
        session.EditByOptimiser("IntakeRunner.LengthMm", 512.5, "opt-run-7");
        session.Provenance["IntakeRunner.LengthMm"].SourceRef.Should().Be("opt-run-7");
    }

    [Fact]
    public void Badges_never_rely_on_colour_alone()
    {
        // Plan §8.11: no information conveyed by colour alone.
        var styles = Enum.GetValues<Provenance>().Select(DesignTokens.BadgeStyle).ToList();
        styles.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Label));
        styles.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Glyph));
        styles.Select(s => s.Label).Should().OnlyHaveUniqueItems();
        styles.Select(s => s.Glyph).Should().OnlyHaveUniqueItems("each badge is distinguishable without colour");
    }
}
