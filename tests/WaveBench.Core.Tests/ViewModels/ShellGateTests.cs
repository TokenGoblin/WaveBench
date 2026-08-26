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

            // The recovered tray must reach the SHELL, not just exist beside
            // it: asserting on a standalone tray would pass while the app's
            // status line still read "Jobs: idle" after a restart.
            var recoveredShell = new ShellViewModel(recoveredSession, jobs: JobTray.LoadFrom(jobsPath));

            recoveredDocument.Save().Should().Be(session.Document.Save(),
                "gate: the model comes back exactly as it was");
            recoveredProvenance.OriginOf("Engine.CompressionRatio").Should().Be(Provenance.You,
                "gate: provenance survives too, or protection would silently lapse");

            var sweep = recoveredShell.Jobs.Jobs.Single(j => j.Kind == "sweep");
            sweep.Progress.Should().Be(14, "gate: the sweep resumes at its checkpoint, not from zero");
            sweep.Checkpoint.Should().Be("rpm=6500");
            sweep.State.Should().Be(JobState.Queued, "an interrupted job comes back runnable, not lost");
            sweep.IsResumable.Should().BeTrue();
            recoveredShell.Jobs.Active.Should().HaveCount(2);

            recoveredShell.StatusLine(2840, 9.1e-6).Should().Contain("sweep resuming 14/20",
                "gate: the restored state is what the user actually sees, resume point included");
            output.WriteLine($"recovered: {recoveredShell.Jobs.Summary()}");

            // A progress-only checkpoint must not erase the resume token.
            recoveredShell.Jobs.Checkpoint(sweep.Id, 15);
            sweep.Checkpoint.Should().Be("rpm=6500", "a null token leaves the existing one alone");
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
    public void Protection_survives_a_path_spelled_with_different_casing()
    {
        // Model paths resolve case-insensitively, so a provenance map keyed
        // case-sensitively would record protection under one spelling and
        // miss it under another — silently overwriting the user's value.
        var session = new ProjectSession(Model());
        session.EditByUser("engine.boreMm", 87.0);

        session.Provenance.OriginOf("Engine.BoreMm").Should().Be(Provenance.You);
        session.Provenance.IsProtected("ENGINE.BOREMM").Should().BeTrue();

        session.EditByDerivation("Engine.BoreMm", 84.0, "derived").Should().BeFalse(
            "a differently-cased path is the SAME field and must stay protected");
        session.Document.Engine.BoreMm.Should().Be(87.0);

        var wizard = session.ApplyWizard(new Dictionary<string, object?> { ["ENGINE.BOREMM"] = 80.0 });
        session.Document.Engine.BoreMm.Should().Be(87.0);
        wizard.Blocked.Should().ContainSingle();

        // And the map holds one entry, not one per spelling.
        session.Provenance.Entries.Keys.Where(k => k.Contains("Bore", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle();
    }

    [Fact]
    public void A_wizard_apply_never_leaves_the_document_half_written()
    {
        // Combustion is nullable and Combustion.Lambda is in the wizard's own
        // vocabulary, so a model saved without a combustion block used to
        // throw partway through — after earlier paths had already committed.
        var bare = Model() with { Combustion = null };
        var session = new ProjectSession(bare);

        var result = session.ApplyWizard(new Dictionary<string, object?>
        {
            ["Ambient.PressureKPa"] = 95.0,   // sorts before Combustion
            ["Combustion.Lambda"] = 0.88,
            ["Engine.BoreMm"] = 88.0,
        });

        session.Document.Ambient.PressureKPa.Should().Be(95.0);
        session.Document.Combustion.Should().NotBeNull("a missing block is created, not thrown over");
        session.Document.Combustion!.Lambda.Should().Be(0.88);
        session.Document.Engine.BoreMm.Should().Be(88.0);
        result.Rejected.Should().BeEmpty();

        // A genuinely impossible path is reported, not thrown, and nothing
        // else in the batch is lost.
        var second = session.ApplyWizard(new Dictionary<string, object?>
        {
            ["Engine.StrokeMm"] = 64.0,
            ["Engine.NoSuchField"] = 1.0,
            ["Engine.BoreMm"] = null,          // non-nullable double
        });

        session.Document.Engine.StrokeMm.Should().Be(64.0, "valid changes still apply");
        session.Document.Engine.BoreMm.Should().Be(88.0, "the invalid one is untouched");
        second.Rejected.Should().HaveCount(2);
        second.Rejected.Should().Contain(r => r.Path == "Engine.NoSuchField");
        output.WriteLine(second.DiffPreview());
    }

    [Fact]
    public void Undo_restores_the_whole_provenance_entry_not_just_its_origin()
    {
        var session = new ProjectSession(Model());
        session.EditByImport("IntakeValves.MaxLiftMm", 11.2, "cam-measured.csv");
        session.EditByUser("IntakeValves.MaxLiftMm", 9.8);

        session.Undo().Should().BeTrue();

        var entry = session.Provenance["IntakeValves.MaxLiftMm"];
        entry.Origin.Should().Be(Provenance.Imported);
        entry.SourceRef.Should().Be("cam-measured.csv",
            "otherwise the badge reads 'Imported from .' after an undo");

        // Same for an Auto value's derivation and citation.
        session.EditByDerivation("ExhaustValves.ThroatDiameterMm", 22.1, "0.85 × head diameter", "Blair");
        session.EditByUser("ExhaustValves.ThroatDiameterMm", 23.0);
        session.Undo();

        var auto = session.Provenance["ExhaustValves.ThroatDiameterMm"];
        auto.Derivation.Should().Be("0.85 × head diameter");
        auto.Citation.Should().Be("Blair");
    }

    [Fact]
    public void A_protected_field_the_wizard_would_not_change_is_not_reported_as_a_conflict()
    {
        var session = new ProjectSession(Model());
        session.EditByUser("Engine.CompressionRatio", 11.0);

        // The wizard proposes exactly what is already there.
        var result = session.ApplyWizard(new Dictionary<string, object?>
        {
            ["Engine.CompressionRatio"] = 11.0,
        });

        result.Blocked.Should().BeEmpty("a no-op is not a conflict, even on a protected field");
        result.Unchanged.Should().ContainSingle();
        result.AnythingBlocked.Should().BeFalse();
        result.DiffPreview().Should().Be("  (no changes)");
    }

    [Fact]
    public void Persisted_state_uses_string_enums_so_reordering_cannot_reinterpret_it()
    {
        var session = new ProjectSession(Model());
        session.EditByUser("Engine.BoreMm", 87.0);
        session.EditByImport("IntakeValves.MaxLiftMm", 11.2, "cam.csv");

        var json = session.Provenance.Save();
        json.Should().Contain("\"You\"").And.Contain("\"Imported\"");
        json.Should().NotContain("\"origin\": 2", "an integer origin is reinterpreted if the enum is reordered");

        ProvenanceMap.Load(json).OriginOf("Engine.BoreMm").Should().Be(Provenance.You);

        var tray = new JobTray();
        var job = tray.Enqueue("sweep", "test", 10);
        tray.Start(job.Id);
        tray.Save().Should().Contain("\"Running\"");
    }

    [Fact]
    public void Every_hidden_workspace_is_announced()
    {
        // §8.3: a hidden workspace must never be merely absent.
        var shell = new ShellViewModel(new ProjectSession(Model()));
        shell.HasForcedInduction = false;
        shell.HasResults = false;

        var hidden = shell.Workspaces.Where(w => !w.Visible).ToList();
        hidden.Should().HaveCountGreaterThan(1, "Boost, Results and Compare are all hidden here");
        hidden.Should().OnlyContain(w => !string.IsNullOrWhiteSpace(w.HiddenReason));
        hidden.Should().OnlyContain(w => !string.IsNullOrWhiteSpace(w.DiscoveryPath));
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
