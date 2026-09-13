using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WaveBench.Model;
using WaveBench.ViewModels;

namespace WaveBench.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly ProjectSession _session;
    private bool _dark;

    public MainWindow()
        : this(SampleProject.Create(), seed: true)
    {
    }

    /// <summary>
    /// Open on a given document. Used by the offscreen renderer to capture a
    /// screen the shipped sample cannot show — a four-cylinder collector on a
    /// one-cylinder example would be a fabricated screenshot.
    /// </summary>
    public MainWindow(EngineModelDocument document, bool seed)
    {
        InitializeComponent();

        _session = new ProjectSession(document);
        if (seed)
        {
            SampleProject.Seed(_session);
        }

        _shell = new ShellViewModel(_session, App.Preferences) { HasResults = true };

        // Track whatever theme startup actually applied, or the first Theme
        // click is a no-op on a machine set to dark.
        _dark = App.Preferences.DarkTheme;

        // Ctrl+K command palette (§8.11).
        InputBindings.Add(new KeyBinding(
            new RelayCommand(_ => ShowPalette()), Key.K, ModifierKeys.Control));

        // Every design warning ends in a link, and a link that does not move
        // the user is a sentence (Phase 24 gate, §8.3).
        WorkspaceContent.Follow = Follow;

        ProjectLabel.Text = _session.Document.Name;
        Refresh();
    }

    private void Refresh()
    {
        BuildRail();
        var current = _shell.Workspaces.First(w => w.Workspace == _shell.Current);
        WorkspaceTitle.Text = current.Title;
        WorkspaceSubTabs.Text = current.SubTabs.Count > 0
            ? string.Join("   ·   ", current.SubTabs)
            : "One model, many lenses";

        ModeToggle.Content = _shell.Mode == UiMode.Simple ? "Simple  ⇄  Advanced" : "Advanced  ⇄  Simple";

        var banner = _shell.AdvancedSettingsBanner();
        AdvancedBanner.Visibility = banner is null ? Visibility.Collapsed : Visibility.Visible;
        AdvancedBannerText.Text = banner is null
            ? string.Empty
            : banner + "  " + string.Join(", ", _shell.AdvancedOnlyActivePaths());

        // Every hidden workspace is announced, not just the first — §8.3 says
        // a hidden workspace must never be merely absent.
        var hidden = _shell.Workspaces.Where(w => !w.Visible).ToList();
        HiddenHint.Visibility = hidden.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (hidden.Count > 0)
        {
            HiddenHintTitle.Text = hidden.Count == 1
                ? $"{hidden[0].Title} is hidden"
                : $"{string.Join(", ", hidden.Select(h => h.Title))} are hidden";
            HiddenHintBody.Text = string.Join(
                Environment.NewLine + Environment.NewLine,
                hidden.Select(h => $"{h.Title}: {h.HiddenReason} Find it via {h.DiscoveryPath}"));
        }

        RefreshCaveats();
        RefreshTour();

        StatusLine.Text = _shell.StatusLine(cells: 2840, timestepSeconds: 9.1e-6);
        WorkspaceContent.Render(WorkspaceHost, _shell, _session);
    }

    /// <summary>Whether the caveat banner is showing everything or just its headline.</summary>
    private bool _caveatsExpanded;

    /// <summary>
    /// The generic-defaults banner (§8.10). Collapsed to one line by default,
    /// because a permanent wall of caveats is a wall people stop reading —
    /// and the point is that they read the first line.
    /// </summary>
    private void RefreshCaveats()
    {
        var guardrails = new Guardrails(_session, _shell.Preferences);
        var caveats = guardrails.All();
        var banner = guardrails.Banner();

        CaveatBanner.Visibility = banner is null ? Visibility.Collapsed : Visibility.Visible;
        if (banner is null)
        {
            return;
        }

        CaveatBannerText.Text = "ⓘ  " + banner
            + (_caveatsExpanded ? "  (click to collapse)" : "  (click for all of them)");

        CaveatBannerDetail.Visibility = _caveatsExpanded ? Visibility.Visible : Visibility.Collapsed;
        CaveatBannerDetail.Text = string.Join(
            Environment.NewLine + Environment.NewLine,
            caveats.Select(c => $"{c.Title}. {c.Detail}  → {c.Fix}"
                                + (c.Citation is null ? "" : $"  [{c.Citation}]")));
    }

    private void CaveatBanner_Click(object sender, MouseButtonEventArgs e)
    {
        _caveatsExpanded = !_caveatsExpanded;
        RefreshCaveats();
    }

    // ---- Guided tour (§8.9) -----------------------------------------------

    private readonly TourController _tour = new();

    /// <summary>Start the tour of the current workspace — used by the menu and the offscreen renderer.</summary>
    public void StartTour()
    {
        _tour.Start(_shell.Current);
        NavigateToStep();
        Refresh();
    }

    private void RefreshTour()
    {
        // A tour belongs to a workspace, so navigating away ends it rather
        // than narrating one screen over another.
        if (_tour.IsRunning && _tour.Current!.Workspace != _shell.Current)
        {
            _tour.Stop();
        }

        // Offered only where there is one to take. A button that does nothing
        // on six of the ten workspaces teaches people not to press it.
        TourButton.Visibility = TourLibrary.For(_shell.Current) is null || _tour.IsRunning
            ? Visibility.Collapsed
            : Visibility.Visible;

        TourStrip.Visibility = _tour.CurrentStep is null ? Visibility.Collapsed : Visibility.Visible;
        if (_tour.CurrentStep is not { } step)
        {
            return;
        }

        TourTitle.Text = step.Title;
        TourBody.Text = step.Body;
        TourPosition.Text = _tour.Position;
        TourBack.IsEnabled = _tour.Step > 0;
        TourNext.Content = _tour.AtEnd ? "Done" : "Next";
    }

    /// <summary>
    /// Put the screen where the current step is talking about, reusing the
    /// warning-link navigator rather than owning a second one — so a tour step
    /// cannot point somewhere a warning could not.
    ///
    /// <b>Called from the tour's own buttons, never from <c>RefreshTour</c>.</b>
    /// Every GoTo here ends in <c>Refresh</c>, and <c>Refresh</c> calls
    /// <c>RefreshTour</c>: navigating from inside the refresh would recurse
    /// until the stack ran out.
    /// </summary>
    private void NavigateToStep()
    {
        // Field targets navigate too. Skipping them left the tour narrating
        // "start with the cylinder" over whatever tab happened to be open —
        // the Manifold canvas, in the first capture of it — which is the one
        // thing a guided tour must not do.
        if (_tour.CurrentStep?.Target is { } target)
        {
            Follow(target);
        }
    }

    private void TourNext_Click(object sender, RoutedEventArgs e)
    {
        _tour.Next();
        NavigateToStep();
        Refresh();
    }

    private void TourBack_Click(object sender, RoutedEventArgs e)
    {
        _tour.Back();
        NavigateToStep();
        Refresh();
    }

    private void TourSkip_Click(object sender, RoutedEventArgs e)
    {
        _tour.Stop();
        Refresh();
    }

    private void TourButton_Click(object sender, RoutedEventArgs e) => StartTour();

    // ---- Learn layer, without a mouse (offscreen capture and keyboard) ----

    /// <summary>Edit a field through the same path a keystroke takes.</summary>
    public void EditField(string path, string text)
    {
        _session.EditByUser(path, Parse(path, text));
        Refresh();

        static object Parse(string path, string text) =>
            FieldLocator.Find(path)?.Kind is FieldKind.Number or FieldKind.Integer
                ? double.Parse(text, CultureInfo.InvariantCulture)
                : text;
    }

    /// <summary>
    /// Run a "Show me" sweep and return the task, so a caller that needs the
    /// figures on screen can wait for them rather than sleep and hope.
    /// </summary>
    public Task ShowMe(string path)
    {
        WorkspaceContent.ShowMePanel.Changed = () => Dispatcher.BeginInvoke(Refresh);
        return WorkspaceContent.ShowMePanel.StartAsync(_session.Document, _shell.Preferences, path);
    }

    public void CloseShowMe()
    {
        WorkspaceContent.ShowMePanel.Close();
        Refresh();
    }

    /// <summary>Open a Concepts explainer, or close it with null.</summary>
    public void OpenConcept(string? id)
    {
        WorkspaceContent.OpenConcept = id;
        Refresh();
    }

    private void BuildRail()
    {
        WorkspaceRail.Items.Clear();
        foreach (var workspace in _shell.VisibleWorkspaces)
        {
            var selected = workspace.Workspace == _shell.Current;
            var target = workspace.Workspace;
            WorkspaceRail.Items.Add(new
            {
                workspace.Glyph,
                workspace.Title,
                ToolTip = workspace.SubTabs.Count > 0 ? string.Join(" · ", workspace.SubTabs) : workspace.Title,
                AutomationName = $"{workspace.Title} workspace",
                Background = selected
                    ? (Brush)FindResource("Brush.AccentSubtle")
                    : Brushes.Transparent,
                Foreground = selected
                    ? (Brush)FindResource("Brush.Accent")
                    : (Brush)FindResource("Brush.TextSecondary"),
                NavigateCommand = new RelayCommand(_ =>
                {
                    _shell.Navigate(target);
                    Refresh();
                }),
            });
        }
    }

    private void ModeToggle_Click(object sender, RoutedEventArgs e) => ToggleMode();

    /// <summary>Mode is a view preference: this cannot touch the document (§8.8).</summary>
    public void ToggleMode()
    {
        _shell.Mode = _shell.Mode == UiMode.Simple ? UiMode.Advanced : UiMode.Simple;
        Refresh();
    }

    /// <summary>Drive navigation without a mouse — used by the offscreen renderer and by keyboard nav.</summary>
    public void GoTo(Workspace workspace)
    {
        _shell.Navigate(workspace);
        Refresh();
    }

    /// <summary>Navigate to one Design sub-tab, optionally with a canvas node selected.</summary>
    public void GoToDesignTab(DesignTab tab, string? selectNodeId = null)
    {
        _shell.Navigate(Workspace.Design);
        WorkspaceContent.SelectDesignTab(_shell, _session, tab);
        if (selectNodeId is not null)
        {
            WorkspaceContent.SelectManifoldNode(_shell, _session, selectNodeId);
        }

        Refresh();
    }

    /// <summary>Build a library collector and select a node on it — offscreen capture and keyboard paths.</summary>
    public void ApplyManifoldConfiguration(string configurationId, string? selectNodeId = null)
    {
        WorkspaceContent.ApplyManifoldConfiguration(_shell, _session, configurationId);
        if (selectNodeId is not null)
        {
            WorkspaceContent.SelectManifoldNode(_shell, _session, selectNodeId);
        }

        Refresh();
    }

    /// <summary>
    /// Follow a design warning to whatever causes it (the Phase 24 gate's
    /// third clause). A field link lands on the tab the field is catalogued
    /// on, so the answer is on screen rather than one search away.
    /// </summary>
    public void Follow(WarningLink link)
    {
        switch (link.Kind)
        {
            case WarningTarget.Node:
                GoToDesignTab(DesignTab.Manifold, link.Target);
                return;

            case WarningTarget.Field:
                if (FieldLocator.SubTabOf(link.Target) is not { } tab)
                {
                    return;
                }

                if (FieldLocator.WorkspaceOf(link.Target) == Workspace.Boost)
                {
                    GoToBoostTab(BoostCatalogue.Tabs.First(t => t.Title == tab).Tab);
                }
                else
                {
                    GoToDesignTab(DesignWorkspace.Tabs.First(t => t.Title == tab).Tab);
                }

                return;

            default:
                GoToSubTab(link.Workspace, link.Target);
                return;
        }
    }

    /// <summary>
    /// Navigate to a sub-tab named by its title. Named rather than typed
    /// because a warning link is written where the warning is raised, and a
    /// workspace does not get to reach into another one's tab enum.
    /// </summary>
    private void GoToSubTab(Workspace? workspace, string subTab)
    {
        switch (workspace)
        {
            case Workspace.Design:
                GoToDesignTab(DesignWorkspace.Tabs.First(t => t.Title == subTab).Tab);
                return;
            case Workspace.Boost:
                GoToBoostTab(BoostCatalogue.Tabs.First(t => t.Title == subTab).Tab);
                return;
            case Workspace.Results:
                GoToResultsTab(Enum.Parse<ResultsTab>(subTab, ignoreCase: true));
                return;
            case Workspace.Sound:
                GoToSoundTab(Enum.Parse<SoundTab>(subTab, ignoreCase: true));
                return;
            case Workspace.Optimise:
                GoToOptimiseTab(Enum.Parse<OptimiseTab>(subTab, ignoreCase: true));
                return;
            case not null:
                GoTo(workspace.Value);
                return;
        }
    }

    /// <summary>Navigate to one Results sub-tab without a mouse.</summary>
    public void GoToResultsTab(ResultsTab tab)
    {
        _shell.Navigate(Workspace.Results);
        if (WorkspaceContent.LatestResults is { } results)
        {
            results.SelectedTab = tab;
        }

        Refresh();
    }

    /// <summary>Drive the wizard to a step without a mouse.</summary>
    public void GoToWizardStep(WizardStep step)
    {
        _shell.Mode = UiMode.Simple;
        _shell.Navigate(Workspace.Overview);
        WorkspaceContent.WizardFor(_session).GoTo(step);
        Refresh();
    }

    /// <summary>
    /// Navigate to one Boost sub-tab without a mouse. Sets the aspiration
    /// first if the model is still naturally aspirated: the workspace does not
    /// exist until it is, and an offscreen capture that silently landed
    /// somewhere else would be a screenshot of the wrong screen.
    /// </summary>
    public void GoToBoostTab(BoostTab tab, string? turboName = null)
    {
        if (!_shell.HasForcedInduction)
        {
            _session.EditByUser("ForcedInduction.Aspiration", AspirationKinds.Turbocharged);
        }

        if (turboName is not null)
        {
            _session.EditByUser("ForcedInduction.TurboName", turboName);
        }

        _shell.Navigate(Workspace.Boost);
        WorkspaceContent.SelectBoostTab(_shell, _session, tab);
        Refresh();
    }

    /// <summary>
    /// Navigate to one Optimise sub-tab without a mouse, optionally running a
    /// search first so the Pareto and Archive tabs have something real on them.
    /// </summary>
    public void GoToOptimiseTab(OptimiseTab tab, Action<OptimiseWorkspace>? configure = null)
    {
        var optimise = WorkspaceContent.OptimiseFor(_shell, _session);
        configure?.Invoke(optimise);

        _shell.Navigate(Workspace.Optimise);
        WorkspaceContent.SelectOptimiseTab(_shell, _session, tab);
        Refresh();
    }

    /// <summary>Navigate to one Sound sub-tab without a mouse.</summary>
    public void GoToSoundTab(SoundTab tab)
    {
        _shell.Navigate(Workspace.Sound);
        WorkspaceContent.SelectSoundTab(_session, tab);
        Refresh();
    }

    /// <summary>Step the manifold canvas zoom.</summary>
    public void StepManifoldZoom(int direction)
    {
        WorkspaceContent.StepManifoldZoom(_shell, _session, direction);
        Refresh();
    }

    public void SetDark(bool dark)
    {
        _dark = dark;
        App.Preferences.DarkTheme = dark;
        App.ApplyTheme(dark);
        Refresh();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e) => SetDark(!_dark);

    private CancellationTokenSource? _runCancellation;

    /// <summary>
    /// Sweep the model and capture the detail, off the UI thread.
    ///
    /// A solve takes seconds per point, so it cannot run on the dispatcher —
    /// a frozen window during a run is indistinguishable from a crash. The
    /// document is deep-copied first: the user is free to keep editing while
    /// it runs, and a result must be the model that was actually solved rather
    /// than whatever the fields happen to say when it finishes.
    /// </summary>
    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null)
        {
            _runCancellation.Cancel();
            return;
        }

        var speeds = new List<double>();
        for (var rpm = 3000.0; rpm <= 9000.0; rpm += 500.0)
        {
            speeds.Add(rpm);
        }

        var snapshot = EngineModelDocument.Load(_session.Document.Save());
        var job = _shell.Jobs.Enqueue("sweep", $"3000–9000 rpm, {speeds.Count} points", speeds.Count + 1);
        _shell.Jobs.Start(job.Id);

        _runCancellation = new CancellationTokenSource();
        var token = _runCancellation.Token;
        RunButton.Content = "⨯  Cancel";

        var progress = new Progress<RunProgress>(p =>
        {
            _shell.Jobs.Checkpoint(job.Id, p.Completed);
            StatusLine.Text = $"{p.Stage}  ·  {p.Completed}/{p.Total}";
        });

        try
        {
            var run = await Task.Run(
                () => ResultsRunner.Run(snapshot, speeds, captureRpm: 6000.0, progress: progress,
                    cancellationToken: token),
                token);

            WorkspaceContent.LatestResults = new ResultsWorkspace(run, App.Preferences);
            _shell.HasResults = true;
            _shell.Jobs.Complete(job.Id);
            _shell.Navigate(Workspace.Results);
        }
        catch (OperationCanceledException)
        {
            _shell.Jobs.Cancel(job.Id);
        }
        catch (Exception ex)
        {
            _shell.Jobs.Fail(job.Id, ex.Message);
            MessageBox.Show(ex.Message, "The run failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            RunButton.Content = "▶  Run";
            Refresh();
        }
    }

    private void ShowPalette()
    {
        var palette = new CommandPalette(_shell);
        var lines = palette.Search(string.Empty, 14)
            .Select(c => $"{c.Kind,-10} {c.Title}" + (c.Subtitle is null ? "" : $"  —  {c.Subtitle}"));
        MessageBox.Show(
            string.Join(Environment.NewLine, lines)
            + Environment.NewLine + Environment.NewLine
            + "(The palette view model is complete and tested; its popup UI lands with Phase 17.)",
            "Command palette (Ctrl+K)", MessageBoxButton.OK, MessageBoxImage.None);
    }
}

/// <summary>Minimal ICommand so the rail can bind without a MVVM package in the head.</summary>
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>A representative project so the window opens with something real in it.</summary>
public static class SampleProject
{
    public static EngineModelDocument Create() => new()
    {
        Name = "Example 360cc tuned single",
        Engine = new EngineSpec { BoreMm = 86, StrokeMm = 62, RodLengthMm = 107, CompressionRatio = 11 },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 31, Count = 2, MaxLiftMm = 10, OpenDeg = 340, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 26, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 380 },
        IntakeRunner = new DuctSpec { LengthMm = 600, DiameterMm = 38 },
        ExhaustRunner = new DuctSpec { LengthMm = 200, DiameterMm = 35 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
    };

    /// <summary>Give the fields a realistic mix of origins so the badges mean something.</summary>
    public static void Seed(ProjectSession session)
    {
        session.EditByUser("Engine.CompressionRatio", 11.0);
        session.EditByImport("IntakeValves.MaxLiftMm", 10.0, "cam-measured.csv");
        session.EditByOptimiser("IntakeRunner.LengthMm", 600.0, "opt-2026-08-25");
        session.EditByDerivation("ExhaustValves.ThroatDiameterMm", 22.1,
            "0.85 × valve head diameter", "Blair, Design and Simulation of Four-Stroke Engines");
        session.EditByUser("Solver.Cfl", 0.8);
    }
}
