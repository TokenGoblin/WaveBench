using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveBench.Model;
using WaveBench.Optimize;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.App;

/// <summary>
/// The Optimise workspace screens (plan Phase 22, §8.4).
///
/// Everything comes from <see cref="OptimiseWorkspace"/>; this arranges it and
/// drives the controls. The Pareto tab is the one the phase exists for —
/// selecting a design there yields its geometry, its torque curve and its
/// sound, which are the three things a builder decides on.
/// </summary>
public static class OptimiseContent
{
    public static void Render(Panel host, ShellViewModel shell, ProjectSession session, OptimiseWorkspace optimise)
    {
        // Clear FIRST: every control below is handed a closure that calls
        // straight back into this method.
        host.Children.Clear();

        void Refresh() => Render(host, shell, session, optimise);

        // Chrome above, redrawn body below. The front selector is a slider and
        // fires under a held pointer, so it lives in the chrome and only the
        // body is rebuilt — see ContentHostTests.
        var body = new StackPanel();

        void RedrawBody()
        {
            body.Children.Clear();
            FillBody(body, optimise);
        }

        host.Children.Add(WorkspaceContent.Heading(
            "Optimise",
            optimise.HasRun
                ? optimise.Summary()!.ToString()
                : $"{optimise.Variables.Count} variables · budget {optimise.Budget} · nothing run yet"));

        host.Children.Add(SubTabs(optimise, Refresh));

        if (optimise.SelectedTab == OptimiseTab.Pareto && optimise.FrontDesigns.Count > 1)
        {
            host.Children.Add(FrontSelector(optimise, RedrawBody));
        }

        host.Children.Add(body);
        RedrawBody();
    }

    private static void FillBody(Panel body, OptimiseWorkspace optimise)
    {
        switch (optimise.SelectedTab)
        {
            case OptimiseTab.Variables:
                body.Children.Add(VariablesCard(optimise));
                break;

            case OptimiseTab.Objectives:
                body.Children.Add(ObjectivesCard(optimise));
                break;

            case OptimiseTab.Run:
                body.Children.Add(RunCard(optimise));
                break;

            case OptimiseTab.Pareto:
                if (!optimise.HasRun)
                {
                    body.Children.Add(WorkspaceContent.Note(
                        "Nothing to explore yet. Run a search on the Run tab; a front needs two objectives."));
                    break;
                }

                body.Children.Add(PlotCard(optimise.ParetoChart()));
                body.Children.Add(PlotCard(optimise.ParallelCoordinates()));
                body.Children.Add(InspectCard(optimise));
                break;

            case OptimiseTab.Archive:
            default:
                body.Children.Add(ArchiveCard(optimise));
                break;
        }
    }

    // ---- Chrome -------------------------------------------------------------

    private static UIElement SubTabs(OptimiseWorkspace optimise, Action refresh)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        foreach (var tab in Enum.GetValues<OptimiseTab>())
        {
            var selected = tab == optimise.SelectedTab;
            var button = new Button
            {
                Content = tab.ToString(),
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(14, 6, 14, 7),
                Background = (Brush)Application.Current.Resources[selected ? "Brush.Accent" : "Brush.Surface"],
                Foreground = (Brush)Application.Current.Resources[selected ? "Brush.OnAccent" : "Brush.TextSecondary"],
                BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            var target = tab;
            button.Click += (_, _) =>
            {
                optimise.SelectedTab = target;
                refresh();
            };
            strip.Children.Add(button);
        }

        return strip;
    }

    /// <summary>
    /// Click-to-inspect, as a slider along the front.
    ///
    /// A slider rather than picking points off the chart, because the front is
    /// ordered and walking ALONG it is what a user actually does — "show me
    /// the next one that trades a bit more power for purity" is the question,
    /// and a slider answers it directly. Redraws only the body: it fires under
    /// a held pointer.
    /// </summary>
    private static UIElement FrontSelector(OptimiseWorkspace optimise, Action redraw)
    {
        var front = optimise.FrontDesigns;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        panel.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = "Along the front",
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
        }, "Text.Secondary"));

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = front.Count - 1,
            Value = Math.Clamp(optimise.SelectedIndex, 0, front.Count - 1),
            Width = 360,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var readout = WorkspaceContent.Styled(new TextBlock
        {
            Text = Describe(optimise),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        }, "Text.Body");

        slider.ValueChanged += (_, e) =>
        {
            optimise.SelectedIndex = (int)e.NewValue;
            readout.Text = Describe(optimise);
            redraw();
        };

        panel.Children.Add(slider);
        panel.Children.Add(readout);
        return panel;
    }

    private static string Describe(OptimiseWorkspace optimise) =>
        optimise.Selected is { } selected
            ? $"#{optimise.SelectedIndex + 1} of {optimise.FrontDesigns.Count}  ·  "
              + string.Join("  ", selected.Objectives.Select(o => o.ToString("N3")))
            : "";

    // ---- Tabs ---------------------------------------------------------------

    private static UIElement VariablesCard(OptimiseWorkspace optimise)
    {
        var panel = new StackPanel();
        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = "In the search", Margin = new Thickness(0, 0, 0, 4) }, "Text.Body", bold: true));
        panel.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = "Bounds are not a formality: an optimiser will happily return a 3 m runner if you let it. "
                 + "Every field here ships with a range a builder would recognise and the grid it is cut to.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        }, "Text.Caption"));

        var grid = new Grid();
        foreach (var width in new double[] { 240, 110, 110, 90 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddRow(grid, 0, ["Variable", "Minimum", "Maximum", "Step", ""], "Text.Caption");

        var row = 1;
        foreach (var variable in optimise.Variables)
        {
            AddRow(grid, row++,
            [
                variable.Name,
                variable.Minimum.ToString("G6"),
                variable.Maximum.ToString("G6"),
                variable.Step?.ToString("G4") ?? "continuous",
                OptimisationCatalogue.Find(variable.Path)?.Why ?? "",
            ], "Text.Small");
        }

        panel.Children.Add(grid);

        if (optimise.Available.Count > 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(new TextBlock
            {
                Text = "Also available: " + string.Join(", ", optimise.Available.Select(f => f.Label))
                     + ". Not assumed, because a twelve-variable search costs roughly the square of a "
                     + "three-variable one — run a screening pass first to find out which of them matter.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            }, "Text.Caption"));
        }

        return WorkspaceContent.Card(panel);
    }

    private static UIElement ObjectivesCard(OptimiseWorkspace optimise)
    {
        var panel = new StackPanel();
        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = "What better means", Margin = new Thickness(0, 0, 0, 8) }, "Text.Body", bold: true));

        if (optimise.Objectives.Count == 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(new TextBlock
            {
                Text = $"Area under the torque curve over {optimise.BandFromRpm:N0}–{optimise.BandToRpm:N0} rpm — "
                     + "the plan's default and the right objective for a race car. An engine that makes more at "
                     + "its peak and less either side is slower everywhere that matters, and a peak-torque "
                     + "objective would choose it.",
                TextWrapping = TextWrapping.Wrap,
            }, "Text.Body"));
        }
        else
        {
            foreach (var objective in optimise.Objectives)
            {
                panel.Children.Add(WorkspaceContent.Styled(new TextBlock
                {
                    Text = $"{(objective.Sense == ObjectiveSense.Maximise ? "Maximise" : "Minimise")} "
                         + $"{objective.Name}"
                         + (string.IsNullOrWhiteSpace(objective.Unit) ? "" : $" ({objective.Unit})"),
                    Margin = new Thickness(0, 2, 0, 2),
                }, "Text.Body"));
            }
        }

        if (optimise.Constraints.Count > 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = "Never broken", Margin = new Thickness(0, 14, 0, 6) }, "Text.Body", bold: true));

            foreach (var constraint in optimise.Constraints)
            {
                panel.Children.Add(WorkspaceContent.Styled(
                    new TextBlock { Text = constraint.Name, Margin = new Thickness(0, 2, 0, 2) }, "Text.Small"));
            }
        }

        panel.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = "A constraint is not an objective with a big weight. An infeasible design ranks below every "
                 + "feasible one by construction, so a returned design has never broken a hard limit.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        }, "Text.Caption"));

        return WorkspaceContent.Card(panel);
    }

    private static UIElement RunCard(OptimiseWorkspace optimise)
    {
        var panel = new StackPanel();
        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = $"{optimise.Algorithm}, budget {optimise.Budget}" }, "Text.Body", bold: true));

        panel.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = optimise.Algorithm switch
            {
                SearchAlgorithm.Doe =>
                    "A space-filling pass. Answers what the space looks like, not what is best — the right first "
                    + "move when nothing is known.",
                SearchAlgorithm.Screening =>
                    "Morris elementary effects: which variables actually move the answer, for about a fiftieth of "
                    + "the price of a full study. Run this before spending a long search on twelve variables.",
                SearchAlgorithm.Bayesian =>
                    "Fits a model of everything measured so far and spends each new evaluation where that model "
                    + "says the most is to be learned. The right default when one evaluation is a converged "
                    + "sweep — which here it is.",
                SearchAlgorithm.NsgaII =>
                    "Multi-objective. Returns a front rather than a point, because a weighted sum needs the "
                    + "weights chosen before the trade is known — which is backwards.",
                _ =>
                    "Single-objective global search. Learns the shape of the response, which matters because a "
                    + "manifold's optimum lies along a ridge that follows no single variable.",
            },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        }, "Text.Caption"));

        if (optimise.Log.Count == 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(new TextBlock
            {
                Text = "Runs go to the job tray, so switching workspaces never cancels one.",
                TextWrapping = TextWrapping.Wrap,
            }, "Text.Secondary"));
        }
        else
        {
            foreach (var line in optimise.Log)
            {
                panel.Children.Add(WorkspaceContent.Styled(new TextBlock
                {
                    Text = line,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                }, "Text.Small"));
            }
        }

        if (optimise.LastScreening.Count > 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = "Sensitivity", Margin = new Thickness(0, 14, 0, 6) }, "Text.Body", bold: true));

            var strongest = optimise.LastScreening[0].MeanAbsoluteEffect;
            foreach (var effect in optimise.LastScreening)
            {
                var share = strongest > 0 ? effect.MeanAbsoluteEffect / strongest : 0.0;
                panel.Children.Add(WorkspaceContent.Styled(new TextBlock
                {
                    Text = $"{effect.Variable.Name,-28} {share,6:P0}"
                         + (effect.EffectVaries ? "   (effect varies across the space)" : "")
                         + (effect.ChangesSign ? "   (optimum is inside the bounds)" : ""),
                    Margin = new Thickness(0, 2, 0, 2),
                }, "Text.Small"));
            }
        }

        return WorkspaceContent.Card(panel);
    }

    /// <summary>
    /// Click-to-inspect and click-to-audition for the selected front design —
    /// the affordances plan §9.6 names, and the reason a front is a decision
    /// rather than a picture.
    /// </summary>
    private static UIElement InspectCard(OptimiseWorkspace optimise)
    {
        var panel = new StackPanel();

        if (optimise.Selected is null)
        {
            panel.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = "Select a design on the front." }, "Text.Secondary"));
            return WorkspaceContent.Card(panel);
        }

        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = "The selected design", Margin = new Thickness(0, 0, 0, 8) },
            "Text.Body", bold: true));

        var geometry = optimise.InspectGeometry();
        if (geometry.Count > 0)
        {
            panel.Children.Add(WorkspaceContent.DerivedCard(geometry));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        buttons.Children.Add(ActionButton("Export this design's audition (WAV)", () =>
        {
            if (!optimise.AuditionIsMeaningful)
            {
                MessageBox.Show(
                    "This run never varied the exhaust geometry, so the optimised design and the baseline would "
                    + "render the same collector. Two identical clips are worse than none — add the exhaust "
                    + "primary length as a variable to make the comparison mean something.",
                    "Nothing to hear", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (optimise.Audition() is not { } audition)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "optimised-vs-baseline",
                Filter = "WAV audio|*.wav",
                AddExtension = true,
            };

            if (dialog.ShowDialog() == true)
            {
                WaveBench.Acoustics.Auralisation.WavWriter.Write(dialog.FileName, audition.B);
            }
        }));

        panel.Children.Add(buttons);

        if (!optimise.AuditionIsMeaningful)
        {
            panel.Children.Add(WorkspaceContent.Styled(new TextBlock
            {
                Text = "This run did not vary the exhaust, so there is nothing to hear between these designs.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            }, "Text.Caption"));
        }

        return WorkspaceContent.Card(panel);
    }

    private static UIElement ArchiveCard(OptimiseWorkspace optimise)
    {
        var panel = new StackPanel();
        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = "Every design measured", Margin = new Thickness(0, 0, 0, 4) },
            "Text.Body", bold: true));

        var rows = optimise.ArchiveRows(30);
        if (rows.Count == 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = "Nothing run yet." }, "Text.Secondary"));
            return WorkspaceContent.Card(panel);
        }

        panel.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = "★ marks a design on the front. The whole run is kept, so a design that was optimal early and "
                 + "got crowded out later is still here — it may still be the one you want.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        }, "Text.Caption"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddRow(grid, 0, ["", "Design", "Objectives", "Measured at"], "Text.Caption");

        var row = 1;
        foreach (var entry in rows)
        {
            AddRow(grid, row++,
            [
                entry.OnFront ? "★" : "",
                entry.Design,
                string.Join("   ", entry.Objectives),
                entry.Fidelity,
            ], "Text.Small");
        }

        panel.Children.Add(grid);
        return WorkspaceContent.Card(panel);
    }

    // ---- Shared -------------------------------------------------------------

    private static void AddRow(Grid grid, int row, IReadOnlyList<string> cells, string style)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < cells.Count && c < grid.ColumnDefinitions.Count; c++)
        {
            var block = WorkspaceContent.Styled(new TextBlock
            {
                Text = cells[c],
                TextWrapping = c == cells.Count - 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Margin = new Thickness(0, 3, 12, 3),
            }, style);

            Grid.SetRow(block, row);
            Grid.SetColumn(block, c);
            grid.Children.Add(block);
        }
    }

    private static UIElement PlotCard(PlotModel model)
    {
        var body = new StackPanel();
        body.Children.Add(new PlotView(model) { Height = 360 });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        row.Children.Add(ActionButton("Export SVG", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = model.FileStem(),
                Filter = "SVG vector|*.svg",
                AddExtension = true,
            };

            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(
                    dialog.FileName,
                    SvgPlotWriter.Write(model, 900, 520, PlotView.CurrentPalette()),
                    new System.Text.UTF8Encoding(false));
            }
        }));

        body.Children.Add(row);
        return WorkspaceContent.Card(body);
    }

    private static UIElement ActionButton(string label, Action click)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 5, 12, 6),
            Background = (Brush)Application.Current.Resources["Brush.Surface"],
            Foreground = (Brush)Application.Current.Resources["Brush.TextSecondary"],
            BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        button.Click += (_, _) =>
        {
            try
            {
                click();
            }
            catch (IOException e)
            {
                MessageBox.Show(e.Message, "Could not write the file", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (UnauthorizedAccessException e)
            {
                MessageBox.Show(e.Message, "Could not write the file", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        return button;
    }
}
