using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Every workspace renderer must REPLACE the content of its host, not add to
/// it.
///
/// <b>This test exists because the app shipped unusable and the cause took a
/// while to find.</b> Each renderer hands its child controls a
/// <c>Refresh</c> closure that calls straight back into the renderer, so
/// clicking a sub-tab, a speed button or a silencer slider re-enters the same
/// method. Three of the four renderers appended without clearing, so every
/// click stacked a second complete copy of the workspace below the first —
/// inside a StackPanel, off the bottom of the viewport. The old copy stayed
/// exactly where it was, so the app looked frozen: the buttons worked
/// perfectly and nothing on screen ever moved.
///
/// The symptom read as "the buttons do nothing", which sent the first
/// investigation at the button STYLING — a real problem, and not this one.
/// What actually settled it was counting the buttons in the live window
/// through the accessibility tree and finding eighty-one of them, with the
/// Sound workspace present five times over.
///
/// The check is a source scan rather than a WPF test because the test projects
/// target plain net10.0 and cannot construct a Panel; scanning the App source
/// is the same idiom <see cref="XamlTokenTests"/> already uses for the colour
/// rule, and it catches the defect at exactly the point it would be
/// reintroduced.
/// </summary>
public partial class ContentHostTests(ITestOutputHelper output)
{
    /// <summary>A renderer entry point: <c>Render(Panel host, …)</c>.</summary>
    [GeneratedRegex(@"static\s+void\s+Render\s*\(\s*Panel\s+host\b")]
    private static partial Regex RenderEntryPoint();

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
    public void Gate_every_workspace_renderer_clears_its_host_before_adding_to_it()
    {
        var app = AppDirectory();
        var offences = new List<string>();
        var checkedMethods = 0;

        foreach (var file in app.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file.FullName);

            foreach (Match match in RenderEntryPoint().Matches(text))
            {
                checkedMethods++;
                var body = MethodBody(text, match.Index);

                // The clear has to come before anything is added, or the first
                // control added in this pass is the one that gets wiped.
                var clear = body.IndexOf("host.Children.Clear()", StringComparison.Ordinal);
                var add = body.IndexOf("host.Children.Add(", StringComparison.Ordinal);

                if (clear < 0)
                {
                    offences.Add($"{file.Name}: Render(Panel host, …) never clears its host");
                }
                else if (add >= 0 && clear > add)
                {
                    offences.Add($"{file.Name}: Render(Panel host, …) clears its host AFTER adding to it");
                }
            }
        }

        output.WriteLine($"checked {checkedMethods} renderer entry point(s) across {app.Name}");

        checkedMethods.Should().BeGreaterThanOrEqualTo(4,
            "the scan must actually be finding the workspace renderers");

        offences.Should().BeEmpty(
            "a renderer that appends instead of replacing stacks a fresh copy of the whole workspace "
            + "under the stale one on every click, and the app looks frozen:"
            + Environment.NewLine + string.Join(Environment.NewLine, offences));
    }

    [Fact]
    public void Gate_a_renderer_that_re_enters_itself_is_the_one_that_must_clear()
    {
        // The narrower statement of the same rule, and the one that explains
        // why it matters: a renderer handing out a closure that calls itself is
        // re-entered on every interaction, so it is precisely those renderers
        // that cannot afford to append.
        var app = AppDirectory();
        var reentrant = new List<string>();

        foreach (var file in app.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file.FullName);
            if (!Regex.IsMatch(text, @"void\s+Refresh\(\)\s*=>\s*Render\("))
            {
                continue;
            }

            reentrant.Add(file.Name);
            text.Should().Contain("host.Children.Clear()",
                $"{file.Name} re-enters its own Render through a Refresh closure, so it must replace "
                + "its host content rather than add to it");
        }

        output.WriteLine($"re-entrant renderers: {string.Join(", ", reentrant)}");
        reentrant.Should().NotBeEmpty("the scan must actually find the re-entrant renderers");
    }

    [Fact]
    public void Gate_no_slider_rebuilds_the_tree_it_is_being_dragged_in()
    {
        // The other half of the rule, and the one that bit immediately after
        // the renderers were made to clear. A Slider updating on ValueChanged
        // fires while the mouse is DOWN. Rebuild the tree from that handler and
        // WPF disconnects the capturing Thumb, drops its capture, cancels the
        // drag, and hands back a brand-new slider holding nothing — so the
        // control can only be stepped one click at a time and never dragged.
        //
        // CLAUDE.md already recorded this for the manifold canvas. It applies
        // to every control that fires under a held pointer, so it is enforced
        // rather than remembered.
        //
        // The check is on the NAME of the callback, which makes it a convention
        // test: `refresh` is the full rebuild, anything else is targeted. That
        // is a feature rather than a weakness — a factory taking `Action
        // refresh` and calling it from a drag is wrong whatever the caller
        // happens to pass today, and renaming the parameter to say what it does
        // is the fix, not a way around the test.
        var app = AppDirectory();
        var offences = new List<string>();
        var handlers = 0;

        foreach (var file in app.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file.FullName);

            foreach (Match match in Regex.Matches(text, @"\.ValueChanged\s*\+="))
            {
                handlers++;
                var body = LambdaBody(text, match.Index);

                // `refresh` and `Refresh` are the full-rebuild closures. A
                // targeted callback under any other name is exactly what this
                // test is asking for.
                if (Regex.IsMatch(body, @"\b[Rr]efresh\s*\(\s*\)"))
                {
                    offences.Add(
                        $"{file.Name}: a ValueChanged handler calls the full-rebuild Refresh, which will "
                        + "destroy the slider mid-drag");
                }
            }
        }

        output.WriteLine($"checked {handlers} ValueChanged handler(s)");
        handlers.Should().BeGreaterThan(0, "the scan must actually find the sliders");
        offences.Should().BeEmpty(string.Join(Environment.NewLine, offences));
    }

    /// <summary>Body of the lambda that follows an event subscription, by brace matching.</summary>
    private static string LambdaBody(string text, int subscriptionIndex)
    {
        var open = text.IndexOf('{', subscriptionIndex);
        return open < 0 ? string.Empty : MethodBody(text, subscriptionIndex);
    }

    /// <summary>Text of the method body starting at a signature match, by brace matching.</summary>
    private static string MethodBody(string text, int signatureIndex)
    {
        var open = text.IndexOf('{', signatureIndex);
        if (open < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[open..i];
                }
            }
        }

        return text[open..];
    }
}
