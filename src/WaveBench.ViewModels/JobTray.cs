using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveBench.ViewModels;

public enum JobState
{
    Queued,
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// A background job (plan §8.11). Jobs are checkpointed so they survive a
/// crash, and they live in a tray rather than a workspace so switching tabs
/// never cancels one (plan §8.3).
/// </summary>
public sealed record JobRecord
{
    public required string Id { get; set; }

    public required string Kind { get; set; }

    public required string Description { get; set; }

    public JobState State { get; set; } = JobState.Queued;

    /// <summary>Completed work units.</summary>
    public int Progress { get; set; }

    public int Total { get; set; } = 1;

    /// <summary>Opaque resume token — enough for the job to restart where it stopped.</summary>
    public string? Checkpoint { get; set; }

    public string? Error { get; set; }

    public double Fraction => Total > 0 ? Math.Clamp((double)Progress / Total, 0.0, 1.0) : 0.0;

    public bool IsTerminal => State is JobState.Completed or JobState.Cancelled or JobState.Failed;

    /// <summary>A job that was running when the process died — recoverable on restart.</summary>
    public bool IsResumable => State is JobState.Running or JobState.Queued;
}

/// <summary>
/// The job tray with crash-recoverable checkpointing (plan §8.11). State is
/// persisted as JSON so that killing the process mid-sweep and restarting
/// recovers the queue — one of the Phase 16 gate criteria.
/// </summary>
public sealed class JobTray
{
    private readonly List<JobRecord> _jobs = [];

    public IReadOnlyList<JobRecord> Jobs => _jobs;

    public IReadOnlyList<JobRecord> Active => _jobs.Where(j => !j.IsTerminal).ToList();

    public JobRecord Enqueue(string kind, string description, int total)
    {
        var job = new JobRecord
        {
            Id = $"{kind}-{_jobs.Count + 1:D4}",
            Kind = kind,
            Description = description,
            Total = total,
        };
        _jobs.Add(job);
        return job;
    }

    public void Start(string id) => Find(id).State = JobState.Running;

    /// <summary>Record progress and a resume token — the checkpoint a crash restores from.</summary>
    public void Checkpoint(string id, int progress, string? token = null)
    {
        var job = Find(id);
        job.Progress = progress;
        job.Checkpoint = token;
    }

    public void Complete(string id)
    {
        var job = Find(id);
        job.State = JobState.Completed;
        job.Progress = job.Total;
    }

    public void Cancel(string id) => Find(id).State = JobState.Cancelled;

    public void Fail(string id, string error)
    {
        var job = Find(id);
        job.State = JobState.Failed;
        job.Error = error;
    }

    private JobRecord Find(string id) =>
        _jobs.FirstOrDefault(j => j.Id == id) ?? throw new KeyNotFoundException($"No job '{id}'.");

    /// <summary>Status-line summary, e.g. "Jobs: sweep 14/20 · optimise queued" (plan §8.3).</summary>
    public string Summary()
    {
        var active = Active;
        if (active.Count == 0)
        {
            return "Jobs: idle";
        }

        var parts = active.Select(j => j.State == JobState.Running
            ? $"{j.Kind} {j.Progress}/{j.Total}"
            : $"{j.Kind} queued");
        return "Jobs: " + string.Join(" · ", parts);
    }

    public string Save() => JsonSerializer.Serialize(_jobs, JobJsonContext.Default.ListJobRecord);

    public void SaveTo(string path) => File.WriteAllText(path, Save());

    /// <summary>
    /// Restore a tray from persisted state. Jobs that were running when the
    /// process died come back as Queued with their checkpoint intact, so work
    /// resumes rather than restarting.
    /// </summary>
    public static JobTray Load(string json)
    {
        var tray = new JobTray();
        var jobs = JsonSerializer.Deserialize(json, JobJsonContext.Default.ListJobRecord) ?? [];
        foreach (var job in jobs)
        {
            if (job.State == JobState.Running)
            {
                job.State = JobState.Queued; // interrupted, not lost
            }

            tray._jobs.Add(job);
        }

        return tray;
    }

    public static JobTray LoadFrom(string path) =>
        File.Exists(path) ? Load(File.ReadAllText(path)) : new JobTray();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<JobRecord>))]
public partial class JobJsonContext : JsonSerializerContext;
