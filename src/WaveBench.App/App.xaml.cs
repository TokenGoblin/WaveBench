using System.Windows;
using System.Windows.Media;
using WaveBench.ViewModels;

namespace WaveBench.App;

public partial class App : Application
{
    /// <summary>
    /// Rebinds the active brushes to the light or dark token set. Because
    /// nothing outside Tokens.xaml holds a colour, this is the whole of
    /// theming (plan §8.1: ship light and dark, follow the system setting).
    /// </summary>
    public static void ApplyTheme(bool dark)
    {
        var prefix = dark ? "Dark" : "Light";
        var resources = Current.Resources;

        void Bind(string brushKey, string tokenKey, string fallbackTokenKey)
        {
            var colourKey = resources.Contains($"{prefix}.{tokenKey}") ? $"{prefix}.{tokenKey}" : fallbackTokenKey;
            if (resources[brushKey] is SolidColorBrush brush && resources[colourKey] is Color colour)
            {
                resources[brushKey] = new SolidColorBrush(colour);
                _ = brush;
            }
        }

        Bind("Brush.Canvas", "Canvas", "Light.Canvas");
        Bind("Brush.Surface", "Surface", "Light.Surface");
        Bind("Brush.SurfaceAlt", dark ? "Elevated" : "SurfaceAlt", "Light.SurfaceAlt");
        Bind("Brush.BorderSubtle", "BorderSubtle", "Light.BorderSubtle");
        Bind("Brush.BorderStrong", dark ? "BorderSubtle" : "BorderStrong", "Light.BorderStrong");
        Bind("Brush.TextPrimary", "TextPrimary", "Light.TextPrimary");
        Bind("Brush.TextSecondary", "TextSecondary", "Light.TextSecondary");

        // Interaction ink flips from black to white with the theme. Miss these
        // and dark mode gets a black hover overlay on a dark button, which is
        // indistinguishable from no feedback at all.
        Bind("Brush.OverlayHover", "OverlayHover", "Light.OverlayHover");
        Bind("Brush.OverlayPressed", "OverlayPressed", "Light.OverlayPressed");
    }

    /// <summary>
    /// The one preferences instance for the process. Previously startup
    /// built a throwaway copy and the window built another, so the window's
    /// idea of the current theme did not match what had been applied.
    /// </summary>
    public static UserPreferences Preferences { get; } = new();

    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        if (Preferences.FollowSystemTheme)
        {
            Preferences.DarkTheme = SystemPrefersDark();
        }

        ApplyTheme(Preferences.DarkTheme);

        // --screenshot <dir>: render the shell offscreen and exit. Used for
        // documentation and (later) visual regression; never shows a window
        // and never drives synthetic input at the desktop.
        var index = Array.FindIndex(e.Args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < e.Args.Length)
        {
            Preferences.DarkTheme = false;
            ApplyTheme(false);
            OffscreenRenderer.CaptureAll(e.Args[index + 1]);
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>Windows personalisation setting: 0 = dark apps, 1 = light apps.</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }
}
