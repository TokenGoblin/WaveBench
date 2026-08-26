namespace WaveBench.ViewModels;

/// <summary>
/// The plan §8.1 design tokens, in one place. Nothing else in the
/// application may hard-code a colour — a test greps the XAML to enforce it,
/// and the token names here are the vocabulary that XAML resources mirror.
///
/// The look is "UniFi-like": calm, flat, spacious, card-based, one strong
/// blue accent, generous whitespace, restrained motion. Deliberately built
/// from these values rather than copying any vendor's assets or icons.
/// </summary>
public static class DesignTokens
{
    public static class Light
    {
        public const string Canvas = "#F7F8F9";
        public const string Surface = "#FFFFFF";
        public const string SurfaceAlt = "#F2F4F5";
        public const string BorderSubtle = "#12000000";   // rgba(0,0,0,0.07)
        public const string BorderStrong = "#DCE0E4";
        public const string TextPrimary = "#1A1D21";
        public const string TextSecondary = "#7C8593";
        public const string TextDisabled = "#B4BBC4";
    }

    public static class Dark
    {
        public const string Canvas = "#16181A";
        public const string Surface = "#1E2124";
        public const string Elevated = "#24282C";
        public const string BorderSubtle = "#14FFFFFF";   // rgba(255,255,255,0.08)
        public const string TextPrimary = "#F2F4F5";
        public const string TextSecondary = "#98A1AC";
    }

    /// <summary>Accent and semantic colours — identical in both themes.</summary>
    public static class Accent
    {
        public const string Primary = "#006FFF";
        public const string Hover = "#0559C9";
        public const string Subtle = "#1A006FFF";         // rgba(0,111,255,0.10)
        public const string Success = "#00A657";
        public const string Warning = "#F5A623";
        public const string Danger = "#F03A3E";
        public const string Info = "#7B61FF";
    }

    public static class Radius
    {
        public const double Card = 8;
        public const double Input = 6;
        public const double Pill = 999;
    }

    public static class Spacing
    {
        /// <summary>4 px base grid.</summary>
        public const double Base = 4;

        public const double CardPadding = 20;
        public const double SectionGap = 24;
        public const double TableRow = 32;
    }

    public static class Motion
    {
        /// <summary>150–200 ms ease-out; no bounce, no springs.</summary>
        public const int FastMs = 150;

        public const int SlowMs = 200;
    }

    public static class Typography
    {
        /// <summary>Lato is SIL-OFL licensed and safe to ship.</summary>
        public const string Family = "Lato";

        public const string MonoFamily = "Cascadia Mono";

        public static IReadOnlyList<double> Scale { get; } = [12, 13, 14, 16, 20, 24, 32];
    }

    /// <summary>
    /// Provenance badge colours (plan §8.5). Colour alone must never carry
    /// meaning (§8.11 accessibility), so each badge also has a text label and
    /// a distinct glyph.
    /// </summary>
    public static (string Colour, string Label, string Glyph) BadgeStyle(Model.Provenance origin) => origin switch
    {
        Model.Provenance.Auto => (Accent.Info, "Auto", "◇"),
        Model.Provenance.Wizard => (Accent.Primary, "Wizard", "✦"),
        Model.Provenance.You => (Accent.Success, "You", "●"),
        Model.Provenance.Imported => (Accent.Warning, "Imported", "▤"),
        Model.Provenance.Optimised => (Accent.Primary, "Optimised", "◎"),
        _ => (Light.TextSecondary, "Unknown", "?"),
    };

    /// <summary>Every token as name → value, for generating the XAML resource dictionary and for tests.</summary>
    public static IReadOnlyDictionary<string, string> AllColours { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Light.Canvas"] = Light.Canvas,
        ["Light.Surface"] = Light.Surface,
        ["Light.SurfaceAlt"] = Light.SurfaceAlt,
        ["Light.BorderSubtle"] = Light.BorderSubtle,
        ["Light.BorderStrong"] = Light.BorderStrong,
        ["Light.TextPrimary"] = Light.TextPrimary,
        ["Light.TextSecondary"] = Light.TextSecondary,
        ["Light.TextDisabled"] = Light.TextDisabled,
        ["Dark.Canvas"] = Dark.Canvas,
        ["Dark.Surface"] = Dark.Surface,
        ["Dark.Elevated"] = Dark.Elevated,
        ["Dark.BorderSubtle"] = Dark.BorderSubtle,
        ["Dark.TextPrimary"] = Dark.TextPrimary,
        ["Dark.TextSecondary"] = Dark.TextSecondary,
        ["Accent.Primary"] = Accent.Primary,
        ["Accent.Hover"] = Accent.Hover,
        ["Accent.Subtle"] = Accent.Subtle,
        ["Accent.Success"] = Accent.Success,
        ["Accent.Warning"] = Accent.Warning,
        ["Accent.Danger"] = Accent.Danger,
        ["Accent.Info"] = Accent.Info,
    };
}
