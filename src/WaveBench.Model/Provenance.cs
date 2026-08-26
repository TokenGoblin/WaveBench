using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveBench.Model;

/// <summary>
/// Where a field's value came from (plan §8.5). This is the mechanism that
/// makes mode switching and wizard re-runs SAFE, and the plan is explicit
/// that it must exist before the wizard does because it cannot be
/// retrofitted.
/// </summary>
public enum Provenance
{
    /// <summary>Derived by a correlation or default. Overwritten freely.</summary>
    Auto,

    /// <summary>Set from a wizard answer. Overwritten, with a diff preview.</summary>
    Wizard,

    /// <summary>Typed by the user. NEVER overwritten without explicit opt-in.</summary>
    You,

    /// <summary>From a file (cam, flow bench, map). Never overwritten.</summary>
    Imported,

    /// <summary>Set by an optimiser run. Never overwritten without opt-in.</summary>
    Optimised,
}

/// <summary>
/// The origin of one field, plus the explanation the UI shows on hover — a
/// derivation and its citation, so an <c>Auto</c> default is legible rather
/// than a black box (plan §8.5).
/// </summary>
public sealed record ProvenanceEntry
{
    public required Provenance Origin { get; set; }

    /// <summary>How an Auto value was derived ("0.85 × valve head diameter").</summary>
    public string? Derivation { get; set; }

    /// <summary>Source for a correlation-derived default.</summary>
    public string? Citation { get; set; }

    /// <summary>For Imported: the file it came from. For Optimised: the run id.</summary>
    public string? SourceRef { get; set; }

    /// <summary>True for origins a wizard or auto-derivation may never silently overwrite.</summary>
    public bool IsProtected => Origin is Provenance.You or Provenance.Imported or Provenance.Optimised;
}

/// <summary>
/// Field origins keyed by model path ("engine.boreMm"). A side map rather
/// than a wrapper on every value: it keeps the document plain and
/// git-diffable, survives schema evolution, and applies uniformly to
/// canvas components and imported tables as well as scalar fields.
///
/// Unrecorded paths are <see cref="Provenance.Auto"/> — a value nobody
/// claimed is a default, which is the safe assumption.
/// </summary>
public sealed class ProvenanceMap
{
    private readonly Dictionary<string, ProvenanceEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ProvenanceEntry> Entries => _entries;

    public ProvenanceEntry this[string path] =>
        _entries.TryGetValue(path, out var entry) ? entry : new ProvenanceEntry { Origin = Provenance.Auto };

    public Provenance OriginOf(string path) => this[path].Origin;

    public bool IsProtected(string path) => this[path].IsProtected;

    public void Set(string path, ProvenanceEntry entry) => _entries[path] = entry;

    public void Set(string path, Provenance origin, string? derivation = null, string? citation = null,
        string? sourceRef = null) =>
        _entries[path] = new ProvenanceEntry
        {
            Origin = origin,
            Derivation = derivation,
            Citation = citation,
            SourceRef = sourceRef,
        };

    public void Clear(string path) => _entries.Remove(path);

    /// <summary>Paths a wizard or auto-derivation must not touch without opt-in.</summary>
    public IReadOnlyList<string> ProtectedPaths =>
        _entries.Where(e => e.Value.IsProtected).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();

    public string Save() => JsonSerializer.Serialize(_entries, ProvenanceJsonContext.Default.DictionaryStringProvenanceEntry);

    public static ProvenanceMap Load(string json)
    {
        var map = new ProvenanceMap();
        var entries = JsonSerializer.Deserialize(json, ProvenanceJsonContext.Default.DictionaryStringProvenanceEntry);
        if (entries is not null)
        {
            foreach (var (path, entry) in entries)
            {
                map._entries[path] = entry;
            }
        }

        return map;
    }
}

/// <summary>One proposed change from a wizard re-run or derivation pass.</summary>
public sealed record ProposedChange(
    string Path, object? CurrentValue, object? ProposedValue, Provenance CurrentOrigin, bool Blocked)
{
    public bool IsNoOp => Equals(CurrentValue, ProposedValue);

    public override string ToString() =>
        $"{Path}: {CurrentValue} → {ProposedValue} [{CurrentOrigin}]{(Blocked ? " BLOCKED" : "")}";
}

/// <summary>Outcome of applying a set of derived values.</summary>
public sealed record ApplyResult(
    IReadOnlyList<ProposedChange> Applied,
    IReadOnlyList<ProposedChange> Blocked,
    IReadOnlyList<ProposedChange> Unchanged)
{
    public bool AnythingBlocked => Blocked.Count > 0;

    /// <summary>The §8.8 rule-3 diff preview: what a re-run would do, before it does it.</summary>
    public string DiffPreview()
    {
        var lines = new List<string>();
        foreach (var change in Applied)
        {
            lines.Add($"  will set {change}");
        }

        foreach (var change in Blocked)
        {
            lines.Add($"  KEPT (yours) {change.Path}: {change.CurrentValue} [{change.CurrentOrigin}]");
        }

        return lines.Count == 0 ? "  (no changes)" : string.Join(Environment.NewLine, lines);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, ProvenanceEntry>))]
public partial class ProvenanceJsonContext : JsonSerializerContext;
