using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaveBench.ViewModels;

namespace WaveBench.App;

/// <summary>
/// Renders the shell to PNG files without showing a window or touching the
/// desktop — <c>WaveBench.App.exe --screenshot &lt;dir&gt;</c>.
///
/// Exists because screen-scraping a live window is unreliable (foreground
/// steal is blocked) and, worse, drives synthetic input at whatever happens
/// to be on top. Rendering the visual tree directly is deterministic, safe,
/// and usable in CI for visual regressions later.
/// </summary>
public static class OffscreenRenderer
{
    public static void CaptureAll(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        // We open more than one window in sequence. On the default
        // OnLastWindowClose, closing the first begins application shutdown and
        // tears the resource dictionary down, so the next window resolves no
        // brushes at all. App.OnAppStartup calls Shutdown() when we return.
        if (Application.Current is { } app)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var window = new MainWindow
        {
            Width = 1360,
            Height = 860,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000, // laid out but never visible
            Top = -10_000,
            ShowInTaskbar = false,
        };

        window.Show();
        Settle(window);

        Capture(window, Path.Combine(outputDirectory, "01-overview-light.png"));

        window.GoTo(Workspace.Design);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "02-design-provenance-simple.png"));

        window.ToggleMode(); // Simple → Advanced
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "03-design-advanced.png"));

        window.SetDark(true);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "04-design-advanced-dark.png"));

        window.GoTo(Workspace.Overview);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "05-overview-dark.png"));

        window.SetDark(false);
        window.Close();

        CaptureManifold(outputDirectory);
        CaptureResults(outputDirectory);
        CaptureBoost(outputDirectory);
    }

    /// <summary>
    /// The Phase 21 Boost screens, on two models rather than one.
    ///
    /// A road turbo four shows the ordinary case; the restricted FSAE car
    /// shows the one plan §4.6.4 calls the module's highest-value single
    /// feature, and neither is a fair picture of the other. Both are
    /// turbocharged from the start, because the workspace does not exist on a
    /// naturally aspirated model — that is the point of it.
    /// </summary>
    private static void CaptureBoost(string outputDirectory)
    {
        var road = new WaveBench.Model.EngineModelDocument
        {
            Name = "Two-litre turbo four",
            Engine = new WaveBench.Model.EngineSpec
            {
                BoreMm = 86, StrokeMm = 86, RodLengthMm = 145, CompressionRatio = 9.5, CylinderCount = 4,
            },
            IntakeValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580,
            },
            ExhaustValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 28, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 370,
            },
            IntakeRunner = new WaveBench.Model.DuctSpec { LengthMm = 300, DiameterMm = 40 },
            ExhaustRunner = new WaveBench.Model.DuctSpec { LengthMm = 400, DiameterMm = 38 },
            Combustion = new WaveBench.Model.CombustionSpec { Fuel = "RON95", Lambda = 0.88 },
            ForcedInduction = new WaveBench.Model.ForcedInductionSpec
            {
                Aspiration = WaveBench.Model.AspirationKinds.Turbocharged,
                TargetBoostKPa = 110,
            },
        };

        var window = new MainWindow(road, seed: false)
        {
            Width = 1360,
            Height = 1180,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
        };

        window.Show();

        // Advanced, or the capture shows the three Simple-mode fields and
        // none of the hardware the screen is about. Set rather than toggled:
        // the mode is a shared user preference, so a second window toggling
        // it would put the next capture back into Simple.
        Advanced(window);

        // The 62 mm, because auto-match ranks it first for this engine at this
        // target: a 2-litre at 110 kPa draws close to 0.3 kg/s at the top of
        // the range, which runs the 54 mm past its own choke line. Capturing
        // the mismatch would be capturing the warning rather than the screen.
        window.GoToBoostTab(BoostTab.Compressor, "Analytic 62 mm");
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "18-boost-compressor.png"));

        window.GoToBoostTab(BoostTab.Turbine);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "19-boost-turbine.png"));

        window.GoToBoostTab(BoostTab.ChargeCooling);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "20-boost-charge-cooling.png"));

        window.GoToBoostTab(BoostTab.Transient);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "21-boost-transient.png"));

        window.Close();

        // The restricted case, on its own model: a 600 cc four behind a 20 mm
        // throat, which is a different screen in every respect that matters.
        var restricted = new WaveBench.Model.EngineModelDocument
        {
            Name = "Restricted 600 cc four",
            Engine = new WaveBench.Model.EngineSpec
            {
                BoreMm = 67, StrokeMm = 42.5, RodLengthMm = 90, CompressionRatio = 11.5, CylinderCount = 4,
            },
            IntakeValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 23, Count = 2, MaxLiftMm = 8, OpenDeg = 350, CloseDeg = 570,
            },
            ExhaustValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 20, Count = 2, MaxLiftMm = 8, OpenDeg = 150, CloseDeg = 370,
            },
            IntakeRunner = new WaveBench.Model.DuctSpec { LengthMm = 250, DiameterMm = 34 },
            ExhaustRunner = new WaveBench.Model.DuctSpec { LengthMm = 450, DiameterMm = 32 },
            Combustion = new WaveBench.Model.CombustionSpec { Fuel = "RON95", Lambda = 0.85 },
            ForcedInduction = new WaveBench.Model.ForcedInductionSpec
            {
                Aspiration = WaveBench.Model.AspirationKinds.Turbocharged,
                TargetBoostKPa = 120,
                RestrictorFitted = true,
                RestrictorThroatMm = 20,
            },
        };

        var fsae = new MainWindow(restricted, seed: false)
        {
            Width = 1360,
            Height = 1180,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
        };

        fsae.Show();
        Advanced(fsae);
        fsae.GoToBoostTab(BoostTab.Compressor, "Analytic 35 mm");
        Settle(fsae);
        Capture(fsae, Path.Combine(outputDirectory, "22-boost-restrictor.png"));
        fsae.Close();
    }

    /// <summary>
    /// The Phase 19 Results screens. Runs a real short sweep first — a
    /// screenshot of fabricated results would be a screenshot of nothing.
    /// </summary>
    private static void CaptureResults(string outputDirectory)
    {
        var document = new WaveBench.Model.EngineModelDocument
        {
            Name = "Four-cylinder results",
            Engine = new WaveBench.Model.EngineSpec
            {
                BoreMm = 82, StrokeMm = 56.5, RodLengthMm = 100, CompressionRatio = 11, CylinderCount = 4,
            },
            IntakeValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 29, Count = 2, MaxLiftMm = 9.5, OpenDeg = 340, CloseDeg = 590,
            },
            ExhaustValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 24, Count = 2, MaxLiftMm = 9.0, OpenDeg = 130, CloseDeg = 380,
            },
            IntakeRunner = new WaveBench.Model.DuctSpec { LengthMm = 300, DiameterMm = 36, RoughnessMm = 0.045 },
            ExhaustRunner = new WaveBench.Model.DuctSpec { LengthMm = 600, DiameterMm = 34, RoughnessMm = 0.045 },
            Combustion = new WaveBench.Model.CombustionSpec { Fuel = "RON95" },
            Solver = new WaveBench.Model.SolverSpec { CellSizeMm = 12.0, MinCycles = 4, MaxCycles = 8 },
        };

        Console.WriteLine("solving for the results screenshots...");
        var run = ResultsRunner.Run(
            document,
            [4000, 5000, 6000, 7000, 8000],
            6000.0,
            new CaptureOptions { Cycles = 2, FramesPerCycle = 360, ProbeSamplesPerCycle = 720 });

        WorkspaceContent.LatestResults = new ResultsWorkspace(run, App.Preferences);

        var window = new MainWindow(document, seed: false)
        {
            Width = 1360,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
        };

        window.Show();
        window.GoTo(Workspace.Results);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "09-results-performance.png"));

        window.GoToResultsTab(ResultsTab.Waves);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "10-results-waves.png"));

        window.GoToResultsTab(ResultsTab.Cylinders);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "11-results-cylinders.png"));

        // Sound opens on the plan's M50 comparison, which needs no run.
        window.GoToSoundTab(SoundTab.Timing);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "12-sound-timing.png"));

        window.GoToSoundTab(SoundTab.Spectrum);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "13-sound-spectrum.png"));

        window.GoToSoundTab(SoundTab.Audition);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "14-sound-audition.png"));

        window.GoToSoundTab(SoundTab.Silencing);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "15-sound-silencing.png"));

        window.GoToSoundTab(SoundTab.Intake);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "15b-sound-intake.png"));

        // The wizard mid-flow: Simple mode's Overview is the wizard (§8.6).
        window.GoToWizardStep(WizardStep.Goal);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "16-wizard-goal.png"));

        window.GoToWizardStep(WizardStep.Review);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "17-wizard-review.png"));

        window.Close();
        WorkspaceContent.LatestResults = null;
    }

    /// <summary>
    /// The Phase 18 canvas, on a four-cylinder model with a 4-2-1 header.
    /// A separate window because the shipped sample is a single: putting a
    /// four-into-one on it would be a screenshot of something that cannot
    /// exist.
    /// </summary>
    private static void CaptureManifold(string outputDirectory)
    {
        var document = new WaveBench.Model.EngineModelDocument
        {
            Name = "Four-cylinder with a 4-2-1 header",
            Engine = new WaveBench.Model.EngineSpec
            {
                BoreMm = 82, StrokeMm = 78, RodLengthMm = 133, CompressionRatio = 10.5, CylinderCount = 4,
            },
            IntakeValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580,
            },
            ExhaustValves = new WaveBench.Model.ValveTrainSpec
            {
                HeadDiameterMm = 28, Count = 2, MaxLiftMm = 9.5, OpenDeg = 140, CloseDeg = 370,
            },
            IntakeRunner = new WaveBench.Model.DuctSpec { LengthMm = 380, DiameterMm = 40 },
            ExhaustRunner = new WaveBench.Model.DuctSpec { LengthMm = 520, DiameterMm = 38 },
        };

        var window = new MainWindow(document, seed: false)
        {
            Width = 1360,
            Height = 860,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
        };

        window.Show();
        window.GoToDesignTab(DesignTab.Manifold);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "06-manifold-empty.png"));

        window.ApplyManifoldConfiguration("4-2-1", selectNodeId: "sec1");
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "07-manifold-canvas.png"));

        // Zoomed out far enough that the whole header is on screen at once —
        // a 4-2-1 is fourteen grid units end to end.
        window.StepManifoldZoom(-1);
        window.StepManifoldZoom(-1);
        Settle(window);
        Capture(window, Path.Combine(outputDirectory, "08-manifold-zoomed-out.png"));

        window.Close();
    }

    /// <summary>
    /// Put a window into Advanced mode, whatever it is in now.
    ///
    /// The mode is a shared user preference, so a captures run that toggles it
    /// per window flips it back and forth: the second window's toggle undid
    /// the first's, and the capture came out in Simple mode showing three
    /// fields of a screen that has nineteen.
    /// </summary>
    private static void Advanced(MainWindow window)
    {
        if (App.Preferences.Mode != UiMode.Advanced)
        {
            window.ToggleMode();
        }
    }

    /// <summary>Let layout, bindings and the chart's SizeChanged handler complete.</summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        for (var i = 0; i < 3; i++)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
        }
    }

    private static void Capture(Window window, string path)
    {
        var width = (int)window.ActualWidth;
        var height = (int)window.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var target = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        target.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"wrote {Path.GetFileName(path)} ({width}×{height} @2x)");
    }
}
