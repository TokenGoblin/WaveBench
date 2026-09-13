namespace WaveBench.ViewModels;

/// <summary>What a <see cref="WarningLink"/> points at.</summary>
public enum WarningTarget
{
    /// <summary><see cref="WarningLink.Target"/> is a catalogue field path.</summary>
    Field,

    /// <summary><see cref="WarningLink.Target"/> is a manifold node id on the canvas.</summary>
    Node,

    /// <summary><see cref="WarningLink.Target"/> is a sub-tab name within <see cref="WarningLink.Workspace"/>.</summary>
    Plot,
}

/// <summary>
/// Where to go to deal with a warning — the Phase 24 gate's third clause:
/// <i>"every design warning links to the field or plot causing it"</i>.
///
/// <b>It is a resolvable address, not a sentence.</b> The earlier form was a
/// free-text hint like <c>"Design → Manifold (plenum volume)"</c>, which reads
/// correctly and cannot be clicked, cannot be checked, and goes stale the
/// moment a tab is renamed. A link carries the same identifier the command
/// palette, provenance and undo already use, so the view can navigate to it
/// and a test can assert it resolves.
/// </summary>
public sealed record WarningLink
{
    public required WarningTarget Kind { get; init; }

    /// <summary>Field path, node id, or sub-tab name, by <see cref="Kind"/>.</summary>
    public required string Target { get; init; }

    /// <summary>Where to navigate. Null for a node, which is on the canvas already.</summary>
    public Workspace? Workspace { get; init; }

    /// <summary>What to write on the link, when the resolved label is not enough.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// A link to the field that causes the warning. The workspace and tab come
    /// from whichever catalogue owns the path, so a field that moves between
    /// tabs does not leave a lying link behind it.
    /// </summary>
    public static WarningLink Field(string path, string? note = null) => new()
    {
        Kind = WarningTarget.Field,
        Target = path,
        Workspace = FieldLocator.WorkspaceOf(path),
        Note = note,
    };

    /// <summary>A link to the figure that shows the consequence.</summary>
    public static WarningLink Plot(Workspace workspace, string subTab, string? note = null) => new()
    {
        Kind = WarningTarget.Plot,
        Target = subTab,
        Workspace = workspace,
        Note = note,
    };

    /// <summary>A link to a component on the manifold canvas.</summary>
    public static WarningLink Node(string nodeId, string? note = null) => new()
    {
        Kind = WarningTarget.Node,
        Target = nodeId,
        Workspace = ViewModels.Workspace.Design,
        Note = note,
    };

    /// <summary>What the view writes on the link.</summary>
    public string Describe()
    {
        var text = Kind switch
        {
            WarningTarget.Field => FieldLocator.Describe(Target),
            WarningTarget.Plot => $"{Workspace} → {Target}",
            _ => $"Select {Target} on the canvas",
        };

        return Note is { Length: > 0 } note ? $"{text} ({note})" : text;
    }
}

/// <summary>
/// Finds an editable field by path across every catalogue, and says where it
/// lives. One lookup rather than two, because a caller that has to know which
/// catalogue owns a path is a caller that will guess wrong for the one field
/// that moves between them.
/// </summary>
public static class FieldLocator
{
    public static IEditableField? Find(string path) =>
        (IEditableField?)DesignCatalogue.Find(path) ?? BoostCatalogue.Find(path);

    public static Workspace? WorkspaceOf(string path) =>
        DesignCatalogue.Find(path) is not null ? Workspace.Design
        : BoostCatalogue.Find(path) is not null ? Workspace.Boost
        : null;

    /// <summary>The sub-tab a field sits on, or null if the path is unknown.</summary>
    public static string? SubTabOf(string path)
    {
        if (DesignCatalogue.Find(path) is { } design)
        {
            return DesignWorkspace.Tabs.First(t => t.Tab == design.Tab).Title;
        }

        if (BoostCatalogue.Find(path) is { } boost)
        {
            return BoostCatalogue.Tabs.First(t => t.Tab == boost.Tab).Title;
        }

        return null;
    }

    /// <summary>"Design → Fuel &amp; Combustion · λ (relative AFR)", derived rather than written out.</summary>
    public static string Describe(string path)
    {
        var field = Find(path);
        if (field is null || WorkspaceOf(path) is not { } workspace)
        {
            return path;
        }

        return $"{workspace} → {SubTabOf(path)} · {field.Label}";
    }

    /// <summary>Every catalogued field, in the order the workspaces show them.</summary>
    public static IReadOnlyList<IEditableField> All { get; } =
        [.. DesignCatalogue.Fields.Cast<IEditableField>(), .. BoostCatalogue.Fields.Cast<IEditableField>()];
}
