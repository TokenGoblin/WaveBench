using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>The plan §8.3 workspaces, in shell order.</summary>
public enum Workspace
{
    Overview,
    Design,
    Boost,
    Sound,
    Run,
    Results,
    Optimise,
    Compare,
    Report,
    Library,
}

/// <summary>Simple / Advanced. A per-user VIEW preference, never model data (plan §8.8 rule 4).</summary>
public enum UiMode
{
    Simple,
    Advanced,
}

public enum UnitSystem
{
    Metric,
    Imperial,
}

/// <summary>
/// Per-user view preferences, stored OUTSIDE the project file so two people
/// can share a model and each use their own mode and units (plan §8.8 rule 4,
/// §8.11 units toggle). Nothing here is model data.
/// </summary>
public sealed record UserPreferences
{
    public UiMode Mode { get; set; } = UiMode.Simple;

    public UnitSystem Units { get; set; } = UnitSystem.Metric;

    public bool FollowSystemTheme { get; set; } = true;

    public bool DarkTheme { get; set; }

    public bool ReducedMotion { get; set; }
}

/// <summary>
/// A workspace's presence in the shell. Conditional visibility is data, not
/// scattered <c>if</c> statements: Boost is hidden entirely on a naturally-
/// aspirated model and appears the moment a compressor is added (plan §8.3),
/// and a hidden workspace always names its discovery path so the feature is
/// findable rather than merely absent.
/// </summary>
public sealed record WorkspaceState(
    Workspace Workspace, string Title, string Glyph, bool Visible, string? HiddenReason, string? DiscoveryPath)
{
    public IReadOnlyList<string> SubTabs { get; init; } = [];
}

/// <summary>
/// The shell: workspace list with conditional visibility, mode and units,
/// the job tray, and the status line. Contains no UI-framework types.
/// </summary>
public sealed class ShellViewModel
{
    private readonly ProjectSession _session;

    public ShellViewModel(ProjectSession session, UserPreferences? preferences = null)
    {
        _session = session;
        Preferences = preferences ?? new UserPreferences();
        Jobs = new JobTray();
    }

    public UserPreferences Preferences { get; }

    public JobTray Jobs { get; }

    public EngineModelDocument Document => _session.Document;

    public ProjectSession Session => _session;

    public Workspace Current { get; private set; } = Workspace.Overview;

    /// <summary>True when the model has forced induction (drives Boost visibility).</summary>
    public bool HasForcedInduction { get; set; }

    /// <summary>True once at least one run has produced results (drives Results/Compare).</summary>
    public bool HasResults { get; set; }

    /// <summary>
    /// Mode is a view preference: setting it CANNOT touch the document, which
    /// is what makes the §8.8 rule-1 round trip byte-identical by
    /// construction rather than by care.
    /// </summary>
    public UiMode Mode
    {
        get => Preferences.Mode;
        set => Preferences.Mode = value;
    }

    public IReadOnlyList<WorkspaceState> Workspaces =>
    [
        new(Workspace.Overview, "Overview", "⌂", true, null, null),
        new(Workspace.Design, "Design", "⚙", true, null, null)
        {
            SubTabs = ["Engine", "Head & Cam", "Manifold", "Fuel & Combustion"],
        },
        new(Workspace.Boost, "Boost", "\U0001F300", HasForcedInduction,
            HasForcedInduction ? null : "This model is naturally aspirated.",
            "Design → Engine → Aspiration, or the command palette: \"add forced induction\"")
        {
            SubTabs = ["Compressor", "Turbine", "Control", "Charge Cooling", "Transient"],
        },
        new(Workspace.Sound, "Sound", "\U0001F50A", true, null, null)
        {
            SubTabs = ["Timing", "Spectrum", "Silencing", "Audition", "Compliance"],
        },
        new(Workspace.Run, "Run", "▶", true, null, null)
        {
            SubTabs = ["Operating points", "Solver", "Jobs"],
        },
        new(Workspace.Results, "Results", "\U0001F4CA", HasResults,
            HasResults ? null : "No run has completed yet.", "Run → Operating points → Run")
        {
            SubTabs = ["Performance", "Waves", "Cylinders", "Transient"],
        },
        new(Workspace.Optimise, "Optimise", "\U0001F3AF", true, null, null)
        {
            SubTabs = ["Variables", "Objectives", "Run", "Pareto", "Archive"],
        },
        new(Workspace.Compare, "Compare", "⇄", HasResults,
            HasResults ? null : "Nothing to compare until a run completes.", "Run → Operating points → Run"),
        new(Workspace.Report, "Report", "\U0001F4C4", true, null, null),
        new(Workspace.Library, "Library", "\U0001F4DA", true, null, null)
        {
            SubTabs = ["Fuels", "Turbos", "Cams", "Flow data", "Templates", "Presets"],
        },
    ];

    public IReadOnlyList<WorkspaceState> VisibleWorkspaces => Workspaces.Where(w => w.Visible).ToList();

    /// <summary>Navigate. Switching workspaces never cancels a job (plan §8.3).</summary>
    public bool Navigate(Workspace workspace)
    {
        var state = Workspaces.First(w => w.Workspace == workspace);
        if (!state.Visible)
        {
            return false;
        }

        Current = workspace;
        return true;
    }

    /// <summary>
    /// The §8.8 rule-2 banner: values with no Simple-mode representation stay
    /// ACTIVE and are surfaced rather than hidden. Simple mode never lies by
    /// omission (rule 7).
    /// </summary>
    public IReadOnlyList<string> AdvancedOnlyActivePaths()
    {
        if (Mode != UiMode.Simple)
        {
            return [];
        }

        // Anything the user, an import or an optimiser set that Simple mode
        // does not expose is still in force and must be declared.
        return _session.Provenance.Entries
            .Where(e => e.Value.IsProtected && !SimpleModeFields.Contains(e.Key))
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public string? AdvancedSettingsBanner()
    {
        var paths = AdvancedOnlyActivePaths();
        return paths.Count == 0
            ? null
            : $"{paths.Count} advanced setting{(paths.Count == 1 ? " is" : "s are")} active and not shown in Simple mode.";
    }

    /// <summary>Paths Simple mode surfaces directly (the wizard's own vocabulary).</summary>
    public static IReadOnlySet<string> SimpleModeFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Name",
        "Engine.BoreMm", "Engine.StrokeMm", "Engine.CompressionRatio", "Engine.CylinderCount",
        "IntakeRunner.LengthMm", "IntakeRunner.DiameterMm",
        "ExhaustRunner.LengthMm", "ExhaustRunner.DiameterMm",
        "Combustion.Fuel", "Combustion.Lambda",
        "Ambient.PressureKPa", "Ambient.TemperatureK",
    };

    /// <summary>Status line (plan §8.3): job summary plus mesh facts.</summary>
    public string StatusLine(int cells, double timestepSeconds) =>
        $"{Jobs.Summary()}        cells {cells} · Δt {timestepSeconds * 1e6:F1} µs";
}
