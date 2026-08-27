using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Autosave and crash recovery (plan §8.11: every 60 s).
/// </summary>
public class AutosaveTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "wavebench-autosave-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private (Autosave Save, ProjectSession Session, DesignWorkspace Workspace) Fixture()
    {
        var session = ModelTemplates.Open(ModelTemplates.Find("single-450")!);
        return (new Autosave(session, _directory), session,
            new DesignWorkspace(session, new UserPreferences { Mode = UiMode.Advanced }));
    }

    [Fact]
    public void Gate_autosave_writes_on_the_sixty_second_interval()
    {
        var (autosave, _, workspace) = Fixture();
        autosave.Interval.Should().Be(TimeSpan.FromSeconds(60), "plan §8.11");

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        autosave.Save(t0).Should().BeTrue("the first write establishes the baseline");
        workspace.Edit("Engine.BoreMm", "97").Accepted.Should().BeTrue();

        autosave.Tick(t0 + TimeSpan.FromSeconds(59)).Should().BeFalse("not due yet");
        autosave.Tick(t0 + TimeSpan.FromSeconds(60)).Should().BeTrue("due, and the model changed");

        File.Exists(autosave.RecoveryPath).Should().BeTrue();
        output.WriteLine($"recovery file: {new FileInfo(autosave.RecoveryPath).Length} bytes");
    }

    [Fact]
    public void An_idle_session_writes_nothing()
    {
        // Rewriting an unchanged model would make the recovery timestamp mean
        // "when the app last ticked" instead of "when the work last changed".
        var (autosave, _, _) = Fixture();
        var t0 = DateTimeOffset.UnixEpoch;

        autosave.Save(t0).Should().BeTrue();
        for (var minute = 1; minute <= 10; minute++)
        {
            autosave.Tick(t0 + TimeSpan.FromMinutes(minute)).Should().BeFalse();
        }

        autosave.WriteCount.Should().Be(1);
        autosave.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Gate_work_from_a_crashed_session_is_recoverable()
    {
        var (autosave, session, workspace) = Fixture();
        autosave.ProjectPath = @"projects/mine.wbproj";

        workspace.Edit("Engine.BoreMm", "101").Accepted.Should().BeTrue();
        workspace.Edit("Name", "Work in progress").Accepted.Should().BeTrue();
        autosave.Save(DateTimeOffset.UnixEpoch).Should().BeTrue();

        // …the process dies here. A fresh start looks for leftovers.
        var recovered = Autosave.FindRecovery(_directory);

        recovered.Should().NotBeNull();
        recovered!.Document.Engine.BoreMm.Should().Be(101.0);
        recovered.Document.Name.Should().Be("Work in progress");
        recovered.ProjectPath.Should().Be(@"projects/mine.wbproj",
            "the prompt has to say which project this belongs to");
        recovered.Document.Save().Should().Be(session.Document.Save(), "recovery is exact, not approximate");
    }

    [Fact]
    public void Gate_a_clean_exit_leaves_nothing_to_recover()
    {
        var (autosave, _, workspace) = Fixture();
        workspace.Edit("Engine.BoreMm", "101").Accepted.Should().BeTrue();
        autosave.Save(DateTimeOffset.UnixEpoch);

        Autosave.FindRecovery(_directory).Should().NotBeNull();

        autosave.Discard();

        Autosave.FindRecovery(_directory).Should().BeNull(
            "offering to recover work the user already saved is worse than not offering at all");
        autosave.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Autosave_never_touches_the_users_project_file()
    {
        // The rule that matters most: this feature must not be capable of
        // destroying the file it is trying to protect.
        var projectPath = Path.Combine(_directory, "mine.wbproj");
        Directory.CreateDirectory(_directory);
        var original = ModelTemplates.Find("fsae-600")!.Create().Save();
        File.WriteAllText(projectPath, original);

        var (autosave, _, workspace) = Fixture();
        autosave.ProjectPath = projectPath;

        workspace.Edit("Engine.BoreMm", "70").Accepted.Should().BeTrue();
        autosave.Save(DateTimeOffset.UnixEpoch).Should().BeTrue();

        File.ReadAllText(projectPath).Should().Be(original, "the user's save is the user's decision");
        autosave.RecoveryPath.Should().NotBe(projectPath);
    }

    [Fact]
    public void A_corrupt_recovery_file_does_not_stop_the_app_starting()
    {
        // Startup is the one moment a user most needs the app to open. A
        // truncated recovery file — the likely artefact of a crash mid-write
        // on a machine without atomic renames — must be ignored, not thrown.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "autosave.wbproj.recovery"), "{ this is not json");

        var act = () => Autosave.FindRecovery(_directory);
        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void An_interrupted_write_cannot_destroy_the_previous_recovery_file()
    {
        // Writes go to a temporary file and are moved into place, so a crash
        // during the write leaves the LAST good autosave intact rather than a
        // half-written one.
        var (autosave, _, workspace) = Fixture();
        workspace.Edit("Name", "first").Accepted.Should().BeTrue();
        autosave.Save(DateTimeOffset.UnixEpoch);

        var good = File.ReadAllText(autosave.RecoveryPath);

        // Simulate the debris an interrupted write leaves behind.
        File.WriteAllText(autosave.RecoveryPath + ".tmp", "half a fi");

        Autosave.FindRecovery(_directory)!.Document.Name.Should().Be("first");
        File.ReadAllText(autosave.RecoveryPath).Should().Be(good);
    }

    [Fact]
    public void Recovery_reports_when_the_work_was_saved()
    {
        var (autosave, _, workspace) = Fixture();
        var when = new DateTimeOffset(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

        workspace.Edit("Name", "timed").Accepted.Should().BeTrue();
        autosave.Save(when);

        Autosave.FindRecovery(_directory)!.SavedAt.Should().Be(when,
            "\"recover work from 09:30?\" is the question the prompt asks");
    }
}
