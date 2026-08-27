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

        StatusLine.Text = _shell.StatusLine(cells: 2840, timestepSeconds: 9.1e-6);
        WorkspaceContent.Render(WorkspaceHost, _shell, _session);
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
