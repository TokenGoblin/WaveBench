using System.Text.RegularExpressions;
using FluentAssertions;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 16 gate, fourth criterion: "a test asserts no hard-coded colours in
/// XAML". Plan §8.1 is explicit that the token set lives in one file and
/// nothing else in the app may hard-code a colour — that is what makes the
/// light/dark themes and the accessibility pass possible at all.
/// </summary>
public partial class XamlTokenTests(ITestOutputHelper output)
{
    /// <summary>#RGB, #ARGB, #RRGGBB, #AARRGGBB.</summary>
    [GeneratedRegex(@"#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b")]
    private static partial Regex HexColour();

    /// <summary>Named brushes used as literals, e.g. Background="White".</summary>
    [GeneratedRegex(@"(?:Background|Foreground|BorderBrush|Fill|Stroke)\s*=\s*""(?!\{)([A-Za-z]+)""")]
    private static partial Regex NamedColour();

    /// <summary>
    /// Only Transparent — it is the ABSENCE of a colour, not a colour choice.
    /// White and Black were allowlisted here originally, and the Run button
    /// promptly used Foreground="White": the gate passed only because the
    /// enforcing test exempted the case the code needed. Content on the
    /// accent fill now uses the Brush.OnAccent token instead.
    /// </summary>
    private static readonly string[] AllowedNamed = ["Transparent"];

    private static DirectoryInfo AppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "WaveBench.App")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the repository root must be locatable from the test output directory");
        return new DirectoryInfo(Path.Combine(dir!.FullName, "src", "WaveBench.App"));
    }

    [Fact]
    public void Gate_no_xaml_outside_the_token_dictionary_hard_codes_a_colour()
    {
        var app = AppDirectory();
        var offences = new List<string>();
        var scanned = 0;

        foreach (var file in app.GetFiles("*.xaml", SearchOption.AllDirectories))
        {
            if (file.Name.Equals("Tokens.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue; // the one file allowed to hold colour literals
            }

            scanned++;
            var text = File.ReadAllText(file.FullName);

            foreach (Match match in HexColour().Matches(text))
            {
                offences.Add($"{file.Name}: hard-coded {match.Value}");
            }

            foreach (Match match in NamedColour().Matches(text))
            {
                var name = match.Groups[1].Value;
                if (!AllowedNamed.Contains(name, StringComparer.Ordinal))
                {
                    offences.Add($"{file.Name}: named colour '{name}'");
                }
            }
        }

        scanned.Should().BeGreaterThan(0, "the scan must actually be looking at XAML");
        output.WriteLine($"scanned {scanned} XAML file(s) outside Tokens.xaml");
        offences.Should().BeEmpty(
            "plan §8.1: nothing outside Tokens.xaml may hard-code a colour — " + string.Join("; ", offences));
    }

    [Fact]
    public void The_token_dictionary_defines_every_documented_token()
    {
        var tokens = File.ReadAllText(Path.Combine(AppDirectory().FullName, "Themes", "Tokens.xaml"));

        foreach (var (name, value) in DesignTokens.AllColours)
        {
            tokens.Should().Contain($"x:Key=\"{name}\"", $"§8.1 token '{name}' must exist in the dictionary");
            tokens.Should().Contain(value, $"token '{name}' must carry its documented value {value}");
        }

        // Both themes present, and the shared accent set.
        tokens.Should().Contain("Light.Canvas").And.Contain("Dark.Canvas").And.Contain("Accent.Primary");

        // The radius, spacing and motion scales of §8.1.
        tokens.Should().Contain("Radius.Card").And.Contain("Space.CardPadding").And.Contain("Motion.Fast");
        tokens.Should().Contain("Lato", "§8.1 specifies Lato, which is SIL-OFL and safe to ship");
    }

    /// <summary>
    /// XAML resource lookups fail at RUNTIME, not compile time — a missing
    /// key is an unhandled XamlParseException on startup, which is exactly
    /// how the first launch of this window died. This resolves every
    /// Static/DynamicResource key used anywhere in the app (XAML and C#)
    /// against the dictionary, so the failure mode is a red test instead of
    /// a crash the user finds.
    /// </summary>
    [Fact]
    public void Every_resource_key_used_anywhere_is_defined_in_the_token_dictionary()
    {
        var app = AppDirectory();
        var tokens = File.ReadAllText(Path.Combine(app.FullName, "Themes", "Tokens.xaml"));

        var defined = Regex.Matches(tokens, @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        defined.Should().NotBeEmpty();

        var used = new List<(string File, string Key)>();

        foreach (var file in app.GetFiles("*.xaml", SearchOption.AllDirectories))
        {
            if (file.Name.Equals("Tokens.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file.FullName);
            foreach (Match match in Regex.Matches(text, @"\{(?:Static|Dynamic)Resource\s+([^}]+?)\s*\}"))
            {
                used.Add((file.Name, match.Groups[1].Value.Trim()));
            }
        }

        // Code-behind pulls brushes and styles by key too.
        foreach (var file in app.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file.FullName);
            foreach (Match match in Regex.Matches(text, @"(?:Resources|FindResource)\w*\[?\(?""((?:Brush|Text|Radius|Space|Motion|Type)\.[\w.]+)"""))
            {
                used.Add((file.Name, match.Groups[1].Value));
            }
        }

        used.Should().NotBeEmpty("the scan must actually find resource references");
        output.WriteLine($"resolved {used.Count} resource references against {defined.Count} definitions");

        var missing = used.Where(u => !defined.Contains(u.Key)).Distinct().ToList();
        missing.Should().BeEmpty(
            "every resource key must exist or the app throws XamlParseException on startup — missing: "
            + string.Join("; ", missing.Select(m => $"{m.File} → '{m.Key}'")));
    }

    [Fact]
    public void Code_behind_resolves_colours_through_the_dictionary_rather_than_literals()
    {
        // The same rule applies to C# that builds visuals: WorkspaceContent
        // draws charts and badges, and must pull every brush from resources.
        var app = AppDirectory();
        var offences = new List<string>();

        foreach (var file in app.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file.FullName);
            foreach (Match match in HexColour().Matches(text))
            {
                offences.Add($"{file.Name}: {match.Value}");
            }

            foreach (Match match in Regex.Matches(text, @"Brushes\.(\w+)"))
            {
                if (!AllowedNamed.Contains(match.Groups[1].Value, StringComparer.Ordinal))
                {
                    offences.Add($"{file.Name}: Brushes.{match.Groups[1].Value}");
                }
            }
        }

        offences.Should().BeEmpty(
            "UI code must resolve brushes from the token dictionary — " + string.Join("; ", offences));
    }
}
