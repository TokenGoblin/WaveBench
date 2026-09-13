using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Reporting;

namespace WaveBench.App;

/// <summary>
/// The Report workspace (plan §8.4): one click, both formats, and a preview of
/// what the click will produce before it is spent.
/// </summary>
public static class ReportContent
{
    public static void Render(Panel host, ShellViewModel shell, ProjectSession session, ReportWorkspace workspace)
    {
        // Clear first: every control below is handed a closure that re-enters
        // this method (ContentHostTests enforces it).
        host.Children.Clear();

        void Refresh() => Render(host, shell, session, workspace);

        host.Children.Add(WorkspaceContent.Heading(
            "Report",
            "One document, two formats, and nothing in it drawn by hand."));

        host.Children.Add(ReadinessCard(workspace));
        host.Children.Add(ActionsCard(workspace, Refresh));

        if (workspace.Error is { } error)
        {
            var text = WorkspaceContent.Styled(
                new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap }, "Text.Small");
            text.Foreground = (Brush)Application.Current.Resources["Brush.Warning"];
            host.Children.Add(WorkspaceContent.Card(text));
        }

        host.Children.Add(PreviewCard(workspace));
    }

    /// <summary>
    /// What the report will be able to say, BEFORE the button is pressed.
    ///
    /// A generation costs a mesh study — three extra solves — so discovering
    /// afterwards that the acoustics section was going to be one apologetic
    /// sentence is a minute wasted and a screen that failed to say so.
    /// </summary>
    private static UIElement ReadinessCard(ReportWorkspace workspace)
    {
        var panel = new StackPanel();
        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = "What this report will contain", Margin = new Thickness(0, 0, 0, 10) },
            "Text.Body", bold: true));

        foreach (var readout in workspace.Readiness())
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = readout.Label, Width = 150 }, "Text.Body"));
            head.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = readout.Value }, "Text.Body", bold: true));
            row.Children.Add(head);

            if (readout.Note is { } note)
            {
                row.Children.Add(WorkspaceContent.Styled(
                    new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(150, 1, 0, 0) },
                    "Text.Caption"));
            }

            if (readout.Warning is { } warning)
            {
                var text = WorkspaceContent.Styled(
                    new TextBlock
                    {
                        Text = "⚠  " + warning,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(150, 1, 0, 0),
                    }, "Text.Small");
                text.Foreground = (Brush)Application.Current.Resources["Brush.Warning"];
                row.Children.Add(text);
            }

            panel.Children.Add(row);
        }

        return WorkspaceContent.Card(panel);
    }

    private static UIElement ActionsCard(ReportWorkspace workspace, Action refresh)
    {
        var panel = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var generate = new Button
        {
            Content = workspace.IsGenerating ? "Generating…" : "Generate report",
            IsEnabled = !workspace.IsGenerating,
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.Resources["Brush.Accent"],
            Foreground = (Brush)Application.Current.Resources["Brush.OnAccent"],
        };

        generate.Click += (_, _) =>
        {
            // Beside the project rather than somewhere the user has to find:
            // a report is an artefact of this model.
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WaveBench reports");

            // Posted, not invoked: the generation finishes on a worker and a
            // Dispatcher.Invoke from there would block against anything on the
            // UI thread waiting for it.
            workspace.Changed = () => Application.Current?.Dispatcher.BeginInvoke(refresh);
            workspace.GenerateAsync(directory);
        };

        row.Children.Add(generate);

        var mesh = new CheckBox
        {
            Content = "Include mesh-sensitivity evidence",
            IsChecked = workspace.IncludeMeshStudy,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Three extra solves at 0.5×, 1× and 2× cell size. Published by default (plan §5.3).",
        };

        // Checked/Unchecked fires on a click that is already finished, so a
        // full rebuild here is safe — unlike a slider's ValueChanged.
        mesh.Checked += (_, _) =>
        {
            workspace.IncludeMeshStudy = true;
            refresh();
        };
        mesh.Unchecked += (_, _) =>
        {
            workspace.IncludeMeshStudy = false;
            refresh();
        };

        row.Children.Add(mesh);
        panel.Children.Add(row);

        if (workspace.WrittenFiles.Count > 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = "Written", Margin = new Thickness(0, 14, 0, 4) }, "Text.Body", bold: true));

            foreach (var file in workspace.WrittenFiles)
            {
                var link = WorkspaceContent.Styled(
                    new TextBlock { Text = file, TextWrapping = TextWrapping.Wrap }, "Text.Caption");
                link.Foreground = (Brush)Application.Current.Resources["Brush.Accent"];
                link.Cursor = System.Windows.Input.Cursors.Hand;
                link.ToolTip = "Open";

                var path = file;
                link.MouseLeftButtonUp += (_, _) => Open(path);
                panel.Children.Add(link);
            }
        }

        return WorkspaceContent.Card(panel);
    }

    /// <summary>
    /// The report's own table of contents, with what each section will say.
    /// Built from the same <see cref="ReportDocument"/> the writers render, so
    /// the preview cannot describe a document the export does not produce.
    /// </summary>
    private static UIElement PreviewCard(ReportWorkspace workspace)
    {
        var report = workspace.Latest ?? workspace.Build();
        var panel = new StackPanel();

        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock
            {
                Text = workspace.Latest is null ? "Preview" : "Contents of the report just written",
                Margin = new Thickness(0, 0, 0, 4),
            }, "Text.Body", bold: true));

        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock
            {
                Text = $"{report.Sections.Count} sections · {report.Figures.Count} figures · "
                       + $"{report.Caveats.Count} caveats · {report.Claims.Count} sourced claims",
                Margin = new Thickness(0, 0, 0, 12),
            }, "Text.Caption"));

        foreach (var section in report.Sections)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = section.Title }, "Text.Body", bold: true));

            if (section.Standfirst is { } standfirst)
            {
                row.Children.Add(WorkspaceContent.Styled(
                    new TextBlock { Text = standfirst, TextWrapping = TextWrapping.Wrap }, "Text.Caption"));
            }

            var figures = section.Blocks.OfType<ReportFigure>().Count();
            var caveats = section.Blocks.OfType<ReportCaveat>().Count();
            var claims = section.Blocks.OfType<ReportClaim>().Count();

            var parts = new List<string>();
            if (figures > 0)
            {
                parts.Add($"{figures} figure{(figures == 1 ? "" : "s")}");
            }

            if (claims > 0)
            {
                parts.Add($"{claims} sourced claim{(claims == 1 ? "" : "s")}");
            }

            if (caveats > 0)
            {
                parts.Add($"{caveats} caveat{(caveats == 1 ? "" : "s")}");
            }

            if (parts.Count > 0)
            {
                row.Children.Add(WorkspaceContent.Styled(
                    new TextBlock { Text = string.Join(" · ", parts), Margin = new Thickness(0, 1, 0, 0) },
                    "Text.Caption"));
            }

            panel.Children.Add(row);
        }

        return WorkspaceContent.Card(panel);
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // No handler registered for the type, or the file has been moved.
            // Not worth an error dialogue: the path is on screen and
            // selectable.
        }
    }
}
