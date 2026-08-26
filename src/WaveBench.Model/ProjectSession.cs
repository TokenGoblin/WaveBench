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
    /// <summary>
    /// The canonical form of a path: the declared property names, exactly as
    /// the CLR spells them.
    ///
    /// Path lookup is case-insensitive (a UI, a validation message and a
    /// wizard all spell paths differently — <c>engine.boreMm</c> vs
    /// <c>Engine.BoreMm</c>), so EVERY consumer must agree on one spelling
    /// before using a path as a dictionary key. Keying provenance by the raw
    /// string while resolving the document case-insensitively would let
    /// <c>Engine.BoreMm</c> miss the protection recorded under
    /// <c>engine.boreMm</c> and silently overwrite a user's value.
    /// </summary>
    public static string Canonicalise(object root, string path) => Canonicalise(root.GetType(), path);

    /// <summary>
    /// Canonicalises against the TYPE, never an instance: property names are
    /// a property of the shape, not the data, so this must work on a document
    /// whose optional blocks are still null — otherwise canonicalising
    /// <c>Combustion.Lambda</c> throws on a model that has no combustion
    /// block, which is exactly the model a wizard is about to fill in.
    /// </summary>
    public static string Canonicalise(Type rootType, string path)
    {
        var parts = path.Split('.');
        var names = new string[parts.Length];
        var current = rootType;

        for (var i = 0; i < parts.Length; i++)
        {
            var property = PropertyOf(current, parts[i], path);
            names[i] = property.Name;
            current = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        }

        return string.Join('.', names);
    }

    public static object? Get(object root, string path)
    {
        var (owner, property) = Resolve(root, path, createMissing: false);
        return property.GetValue(owner);
    }

    /// <summary>
    /// The value if the path resolves today, otherwise null. Reading the
    /// "before" value of a field inside an optional block that does not exist
    /// yet is a normal case, not an error.
    /// </summary>
    public static object? GetOrDefault(object root, string path) =>
        TryResolve(root, path, out var owner, out var property) ? property!.GetValue(owner) : null;

    /// <summary>
    /// Set a value. <paramref name="createMissing"/> instantiates absent
    /// intermediate objects — a wizard must be able to set
    /// <c>Combustion.Lambda</c> on a model that has no combustion block yet,
    /// and the alternative is throwing halfway through an apply.
    /// </summary>
    public static void Set(object root, string path, object? value, bool createMissing = false)
    {
        var (owner, property) = Resolve(root, path, createMissing);
        property.SetValue(owner, Coerce(value, property, path));
    }

    /// <summary>Convert a loosely-typed value to the property's type, or explain why it cannot.</summary>
    public static object? Coerce(object? value, PropertyInfo property, string path)
    {
        var target = property.PropertyType;
        var underlying = Nullable.GetUnderlyingType(target);

        if (value is null)
        {
            if (underlying is null && target.IsValueType)
            {
                throw new ModelPathException(
                    $"'{path}' is a non-nullable {target.Name}; null is not a valid value.");
            }

            return null;
        }

        var concrete = underlying ?? target;
        if (concrete.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            return Convert.ChangeType(value, concrete, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new ModelPathException($"'{path}' cannot accept {value.GetType().Name} '{value}'.", ex);
        }
    }

    /// <summary>True when the path resolves against the document as it stands.</summary>
    public static bool Exists(object root, string path) => TryResolve(root, path, out _, out _);

    /// <summary>
    /// True when the path COULD be written — resolvable, or resolvable once
    /// missing intermediates are created. This is what an apply must check
    /// before it writes anything.
    /// </summary>
    public static bool CanWrite(object root, string path, object? value, out string? reason)
    {
        reason = null;
        try
        {
            var parts = path.Split('.');
            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var step = Property(current!, parts[i], path);
                var next = step.GetValue(current);
                if (next is null)
                {
                    if (!CanInstantiate(step.PropertyType))
                    {
                        reason = $"'{step.Name}' is null and cannot be created automatically.";
                        return false;
                    }

                    // Probe the rest of the path against a throwaway instance.
                    next = Activator.CreateInstance(Nullable.GetUnderlyingType(step.PropertyType) ?? step.PropertyType);
                }

                current = next;
            }

            var property = Property(current!, parts[^1], path);
            Coerce(value, property, path);
            if (!property.CanWrite)
            {
                reason = $"'{path}' is read-only.";
                return false;
            }

            return true;
        }
        catch (ModelPathException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool TryResolve(object root, string path, out object? owner, out PropertyInfo? property)
    {
        owner = null;
        property = null;
        try
        {
            (owner, property) = Resolve(root, path, createMissing: false);
            return true;
        }
        catch (ModelPathException)
        {
            return false;
        }
    }

    private static (object Owner, PropertyInfo Property) Resolve(object root, string path, bool createMissing)
    {
        var parts = path.Split('.');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var step = Property(current!, parts[i], path);
            var next = step.GetValue(current);
            if (next is null)
            {
                if (!createMissing || !CanInstantiate(step.PropertyType))
                {
                    throw new ModelPathException($"'{step.Name}' is null in path '{path}'.");
                }

                next = Activator.CreateInstance(Nullable.GetUnderlyingType(step.PropertyType) ?? step.PropertyType);
                step.SetValue(current, next);
            }

            current = next;
        }

        return (current!, Property(current!, parts[^1], path));
    }

    private static bool CanInstantiate(Type type)
    {
        var concrete = Nullable.GetUnderlyingType(type) ?? type;
        return !concrete.IsAbstract && concrete.GetConstructor(Type.EmptyTypes) is not null;
    }

    private static PropertyInfo Property(object owner, string name, string path) =>
        PropertyOf(owner.GetType(), name, path);

    private static PropertyInfo PropertyOf(Type type, string name, string path) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
        ?? throw new ModelPathException($"'{type.Name}' has no property '{name}' (path '{path}').");
}

/// <summary>A path could not be resolved or written. Carries the offending path.</summary>
public sealed class ModelPathException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One undoable model edit. Carries the whole <see cref="ProvenanceEntry"/>
/// on each side, not just the origin enum: undoing over an imported value
/// must restore its source file, and undoing over an Auto value must restore
/// the derivation and citation the §8.5 badge hover shows.
/// </summary>
public sealed record ModelEdit(
    string Path, object? Before, object? After, ProvenanceEntry BeforeEntry, ProvenanceEntry AfterEntry)
{
    public Provenance BeforeOrigin => BeforeEntry.Origin;

    public Provenance AfterOrigin => AfterEntry.Origin;
}

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
        List<RejectedChange> rejected = [];

        foreach (var (rawPath, proposed) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            // Every path is canonicalised before it is used as a provenance
            // key, so casing cannot split one field into two protection
            // records (see ModelPath.Canonicalise).
            string path;
            try
            {
                path = ModelPath.Canonicalise(Document, rawPath);
            }
            catch (ModelPathException)
            {
                path = rawPath; // unresolvable today; CanWrite reports why
            }

            if (!ModelPath.CanWrite(Document, path, proposed, out var reason))
            {
                rejected.Add(new RejectedChange(rawPath, proposed, reason ?? "unresolvable"));
                continue;
            }

            var current = ModelPath.GetOrDefault(Document, path);
            var origin = Provenance.OriginOf(path);
            var change = new ProposedChange(path, current, proposed, origin, Blocked: false);

            // A no-op is a no-op even on a protected field: reporting it as a
            // conflict would make the §8.8 diff preview warn about a re-run
            // that changes nothing.
            if (change.IsNoOp)
            {
                unchanged.Add(change);
            }
            else if (Provenance.IsProtected(path) && !(optIn?.Contains(path) ?? false))
            {
                blocked.Add(change with { Blocked = true });
            }
            else
            {
                applied.Add(change);
            }
        }

        return new ApplyResult(applied, blocked, unchanged, rejected);
    }

    private void Write(string path, object? value, Provenance origin, string? derivation, string? citation, string? sourceRef)
    {
        var canonical = ModelPath.Canonicalise(Document, path);
        var before = ModelPath.GetOrDefault(Document, canonical);
        var beforeEntry = Provenance[canonical];
        var afterEntry = new ProvenanceEntry
        {
            Origin = origin,
            Derivation = derivation,
            Citation = citation,
            SourceRef = sourceRef,
        };

        ModelPath.Set(Document, canonical, value, createMissing: true);
        Provenance.Set(canonical, afterEntry);
        _undo.Add(new ModelEdit(canonical, before, ModelPath.Get(Document, canonical), beforeEntry, afterEntry));
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
        ModelPath.Set(Document, edit.Path, edit.Before, createMissing: true);
        Provenance.Set(edit.Path, edit.BeforeEntry);
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
        ModelPath.Set(Document, edit.Path, edit.After, createMissing: true);
        Provenance.Set(edit.Path, edit.AfterEntry);
        _undo.Add(edit);
        return true;
    }
}
