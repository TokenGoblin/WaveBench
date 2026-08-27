using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>
/// An autosave found on disk that no clean exit cleared — i.e. work from a
/// session that crashed or was killed.
/// </summary>
/// <param name="Path">Where the recovery file is.</param>
/// <param name="SavedAt">When it was written.</param>
/// <param name="ProjectPath">The project it belonged to, if it had been saved.</param>
/// <param name="Document">The recovered model.</param>
public sealed record RecoveredWork(string Path, DateTimeOffset SavedAt, string? ProjectPath, EngineModelDocument Document);

/// <summary>
/// Autosave and crash recovery (plan §8.11: every 60 s).
///
/// Two rules shape this:
///
/// <b>It never touches the user's project file.</b> Autosave writes to its own
/// recovery file. Silently overwriting the file someone is editing is how a
/// tool loses an afternoon's work while believing it is helping — the user's
/// save is the user's decision.
///
/// <b>It writes only when the model actually changed.</b> Dirtiness is decided
/// by comparing the serialised document to what was last written, not by a
/// flag someone has to remember to set. A flag drifts; a comparison cannot.
/// It also means an idle session rewrites nothing, so the recovery file's
/// timestamp means "when the work last changed", which is what a recovery
/// prompt needs to say.
///
/// Time is injected rather than read from the clock, so the 60-second
/// behaviour is tested by advancing a variable instead of sleeping.
/// </summary>
public sealed class Autosave
{
    private readonly ProjectSession _session;
    private string? _lastWritten;
    private DateTimeOffset _lastWriteTime = DateTimeOffset.MinValue;

    public Autosave(ProjectSession session, string directory)
    {
        _session = session;
        Directory = directory;
        RecoveryPath = System.IO.Path.Combine(directory, "autosave.wbproj.recovery");
    }

    public string Directory { get; }

    public string RecoveryPath { get; }

    /// <summary>Plan §8.11.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The project file this session came from, recorded for the recovery prompt.</summary>
    public string? ProjectPath { get; set; }

    public DateTimeOffset LastWriteTime => _lastWriteTime;

    public int WriteCount { get; private set; }

    /// <summary>True when the document differs from what was last autosaved.</summary>
    public bool IsDirty => _session.Document.Save() != _lastWritten;

    /// <summary>
    /// Call on a timer. Writes only if the interval has elapsed AND the model
    /// has changed. Returns true if it wrote.
    /// </summary>
    public bool Tick(DateTimeOffset now)
    {
        if (now - _lastWriteTime < Interval)
        {
            return false;
        }

        return Save(now);
    }

    /// <summary>Write now, if there is anything to write. Returns true if it wrote.</summary>
    public bool Save(DateTimeOffset now)
    {
        var json = _session.Document.Save();
        if (json == _lastWritten)
        {
            // Nothing changed. Still advance the clock so an idle session does
            // not re-check the document on every single tick.
            _lastWriteTime = now;
            return false;
        }

        System.IO.Directory.CreateDirectory(Directory);

        var payload = new RecoveryFile
        {
            SavedAt = now,
            ProjectPath = ProjectPath,
            Document = json,
        };

        // Write to a temporary file and move it into place: a crash DURING the
        // autosave must not destroy the previous good recovery file, which is
        // the one thing this feature exists to protect.
        var temporary = RecoveryPath + ".tmp";
        File.WriteAllText(temporary, payload.ToJson());
        File.Move(temporary, RecoveryPath, overwrite: true);

        _lastWritten = json;
        _lastWriteTime = now;
        WriteCount++;
        return true;
    }

    /// <summary>
    /// Clear the recovery file. Call after a clean save or a clean exit —
    /// leaving it behind would offer to recover work the user already has.
    /// </summary>
    public void Discard()
    {
        if (File.Exists(RecoveryPath))
        {
            File.Delete(RecoveryPath);
        }

        _lastWritten = _session.Document.Save();
    }

    /// <summary>
    /// Look for work left behind by a session that did not exit cleanly.
    /// Returns null when there is nothing to recover, and also when the file
    /// is unreadable — a corrupt recovery file must not stop the app from
    /// starting, which is the one moment the user most needs it to.
    /// </summary>
    public static RecoveredWork? FindRecovery(string directory)
    {
        var path = System.IO.Path.Combine(directory, "autosave.wbproj.recovery");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = RecoveryFile.FromJson(File.ReadAllText(path));
            return file is null
                ? null
                : new RecoveredWork(path, file.SavedAt, file.ProjectPath, EngineModelDocument.Load(file.Document));
        }
        catch (Exception e) when (e is IOException or InvalidDataException
                                      or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

}

/// <summary>The autosave file's own envelope: when, from where, and what.</summary>
internal sealed record RecoveryFile
{
    public DateTimeOffset SavedAt { get; init; }

    public string? ProjectPath { get; init; }

    public string Document { get; init; } = string.Empty;

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(
        this, RecoveryJsonContext.Default.RecoveryFile);

    public static RecoveryFile? FromJson(string json) => System.Text.Json.JsonSerializer.Deserialize(
        json, RecoveryJsonContext.Default.RecoveryFile);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(RecoveryFile))]
internal sealed partial class RecoveryJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
