using System.Globalization;
using System.Reflection;

namespace WaveBench.Model;

/// <summary>
/// Reads and writes document fields by dotted path ("engine.boreMm"), which
/// is what lets provenance, undo, the command palette and the wizard all
/// address the same model without each knowing its shape.
/// </summary>
public static class ModelPath
{
    public static object? Get(object root, string path)
    {
        var (owner, property) = Resolve(root, path);
        return property.GetValue(owner);
    }

    public static void Set(object root, string path, object? value)
    {
        var (owner, property) = Resolve(root, path);
        var target = property.PropertyType;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        object? converted = value is null
            ? null
            : underlying.IsInstanceOfType(value)
                ? value
                : Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);

        property.SetValue(owner, converted);
    }

    public static bool Exists(object root, string path)
    {
        try
        {
            Resolve(root, path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static (object Owner, PropertyInfo Property) Resolve(object root, string path)
    {
        var parts = path.Split('.');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var step = Property(current!, parts[i]);
            current = step.GetValue(current)
                      ?? throw new ArgumentException($"'{parts[i]}' is null in path '{path}'.", nameof(path));
        }

        return (current!, Property(current!, parts[^1]));
    }

    private static PropertyInfo Property(object owner, string name) =>
        owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
        ?? throw new ArgumentException($"'{owner.GetType().Name}' has no property '{name}'.", nameof(name));
}

/// <summary>One undoable model edit.</summary>
public sealed record ModelEdit(string Path, object? Before, object? After, Provenance BeforeOrigin, Provenance AfterOrigin);

/// <summary>
/// The editing session around a document: THE only supported way to change
/// model values. It enforces the §8.5 provenance rules structurally rather
/// than by convention, and records undo history (plan §8.11: undo across the
/// whole model tree).
///
/// Mode is deliberately NOT here. Simple/Advanced is a per-user view
/// preference stored outside the project (plan §8.8 rule 4), so switching it
/// cannot touch the document — which is what makes the round-trip
/// byte-identical by construction rather than by care.
/// </summary>
public sealed class ProjectSession(EngineModelDocument document, ProvenanceMap? provenance = null)
{
    private readonly List<ModelEdit> _undo = [];
    private readonly List<ModelEdit> _redo = [];

    public EngineModelDocument Document { get; } = document;

    public ProvenanceMap Provenance { get; } = provenance ?? new ProvenanceMap();

    public IReadOnlyList<ModelEdit> UndoStack => _undo;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Direct user edit — always allowed, always stamps <see cref="Model.Provenance.You"/>.</summary>
    public void EditByUser(string path, object? value) => Write(path, value, Model.Provenance.You, null, null, null);

    /// <summary>Value from an imported file; protected from later overwriting.</summary>
    public void EditByImport(string path, object? value, string sourceFile) =>
        Write(path, value, Model.Provenance.Imported, null, null, sourceFile);

    /// <summary>Value from an optimiser run; protected, and linked to the run.</summary>
    public void EditByOptimiser(string path, object? value, string runId) =>
        Write(path, value, Model.Provenance.Optimised, null, null, runId);

    /// <summary>A derived default. Refuses to overwrite protected fields.</summary>
    public bool EditByDerivation(string path, object? value, string derivation, string? citation = null)
    {
        if (Provenance.IsProtected(path))
        {
            return false;
        }

        Write(path, value, Model.Provenance.Auto, derivation, citation, null);
        return true;
    }

    /// <summary>
    /// Preview what a wizard re-run would do WITHOUT doing it (plan §8.8
    /// rule 3). Protected fields appear as blocked, never as pending changes.
    /// </summary>
    public ApplyResult PreviewWizard(IReadOnlyDictionary<string, object?> values, ISet<string>? optIn = null) =>
        Plan(values, optIn);

    /// <summary>
    /// Apply wizard answers. Touches only <see cref="Model.Provenance.Auto"/>
    /// and <see cref="Model.Provenance.Wizard"/> fields unless the caller
    /// opts in per path. This is the plan's central safety guarantee, and it
    /// is enforced here so no caller can bypass it.
    /// </summary>
    public ApplyResult ApplyWizard(IReadOnlyDictionary<string, object?> values, ISet<string>? optIn = null)
    {
        var plan = Plan(values, optIn);
        foreach (var change in plan.Applied)
        {
            Write(change.Path, change.ProposedValue, Model.Provenance.Wizard, null, null, null);
        }

        return plan;
    }

    private ApplyResult Plan(IReadOnlyDictionary<string, object?> values, ISet<string>? optIn)
    {
        List<ProposedChange> applied = [], blocked = [], unchanged = [];
        foreach (var (path, proposed) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var current = ModelPath.Exists(Document, path) ? ModelPath.Get(Document, path) : null;
            var origin = Provenance.OriginOf(path);
            var isProtected = Provenance.IsProtected(path) && !(optIn?.Contains(path) ?? false);
            var change = new ProposedChange(path, current, proposed, origin, isProtected);

            if (isProtected)
            {
                blocked.Add(change);
            }
            else if (change.IsNoOp)
            {
                unchanged.Add(change);
            }
            else
            {
                applied.Add(change);
            }
        }

        return new ApplyResult(applied, blocked, unchanged);
    }

    private void Write(string path, object? value, Provenance origin, string? derivation, string? citation, string? sourceRef)
    {
        var before = ModelPath.Get(Document, path);
        var beforeOrigin = Provenance.OriginOf(path);
        ModelPath.Set(Document, path, value);
        Provenance.Set(path, origin, derivation, citation, sourceRef);
        _undo.Add(new ModelEdit(path, before, ModelPath.Get(Document, path), beforeOrigin, origin));
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        ModelPath.Set(Document, edit.Path, edit.Before);
        Provenance.Set(edit.Path, edit.BeforeOrigin);
        _redo.Add(edit);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        ModelPath.Set(Document, edit.Path, edit.After);
        Provenance.Set(edit.Path, edit.AfterOrigin);
        _undo.Add(edit);
        return true;
    }
}
