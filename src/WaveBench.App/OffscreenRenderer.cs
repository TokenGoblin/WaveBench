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

        window.Close();
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
