using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.App;

/// <summary>
/// The Boost workspace screens (plan Phase 21, §8.4).
///
/// Every figure and every number comes from <see cref="BoostWorkspace"/>; this
/// arranges them and drives the controls. Fields are drawn by
/// <see cref="WorkspaceContent.AddFieldRow"/> — the same rows Design uses,
/// because they are the same kind of thing and two renderers would be two
/// places for a field to drift.
/// </summary>
public static class BoostContent
{
    public static void Render(Panel host, ShellViewModel shell, ProjectSession session, BoostWorkspace boost)
    {
        // Clear FIRST. Every control below is handed a closure that calls
        // straight back into this method, so this is not only the entry point
        // from WorkspaceContent — it is what a sub-tab button and a field
        // commit call too. Appending instead would stack a second copy of the
        // whole workspace under the stale one and the app would look frozen.
        host.Children.Clear();

        void Refresh() => Render(host, shell, session, boost);

        // Chrome above, redrawn body below. The speed and A/R sliders update
        // on ValueChanged, which fires while the mouse is DOWN — rebuilding
        // the tree from there disconnects the capturing Thumb and cancels the
        // drag. So sliders live in the chrome and only `body` is rebuilt.
        // ContentHostTests enforces this.
        var body = new StackPanel();

        void RedrawBody()
        {
            body.Children.Clear();
            FillBody(body, boost);
        }

        var spec = boost.Spec;
        host.Children.Add(WorkspaceContent.Heading(
            "Boost",
            $"{spec.Aspiration}"
            + (string.IsNullOrWhiteSpace(spec.TurboName) ? " · no turbo chosen" : $" · {spec.TurboName}")
            + $" · target {spec.TargetBoostKPa:F0} kPa"));

        host.Children.Add(SubTabs(boost, Refresh));
        host.Children.Add(FieldsCard(boost, Refresh));

        // The learn panels sit with the fields that opened them, above the
        // figures — a "Show me" study of the boost target belongs next to the
        // boost target, not underneath eight charts (plan §8.9).
        foreach (var card in WorkspaceContent.LearnCards(Refresh))
        {
            host.Children.Add(card);
        }

        host.Children.Add(ViewControls(boost, RedrawBody));

        host.Children.Add(body);
        RedrawBody();
    }

    /// <summary>
    /// The tab body — everything a slider or a field change has to redraw, and
    /// nothing that can be holding a mouse capture while it does.
    /// </summary>
    private static void FillBody(Panel body, BoostWorkspace boost)
    {
        var warnings = boost.Warnings();
        if (warnings.Count > 0)
        {
            body.Children.Add(WarningsCard(warnings));
        }

        var issues = boost.Issues();
        if (issues.Count > 0)
        {
            body.Children.Add(WorkspaceContent.IssuesCard(issues));
        }

        // Figures first, readouts under them. This screen IS the compressor
        // map; a reference table between the controls and the figure they
        // drive pushes the point of the screen below the fold.
        switch (boost.SelectedTab)
        {
            case BoostTab.Compressor:
                body.Children.Add(PlotCard(boost.CompressorMapChart()));
                body.Children.Add(PlotCard(boost.MarginChart()));
                break;

            case BoostTab.Turbine:
                body.Children.Add(PlotCard(boost.AreaRatioSweep()));
                body.Children.Add(PlotCard(boost.BladeSpeedRatioChart()));
                break;

            case BoostTab.Control:
                body.Children.Add(PlotCard(boost.BoostControlChart()));
                break;

            case BoostTab.ChargeCooling:
                body.Children.Add(PlotCard(boost.ChargeCoolingChart()));
                body.Children.Add(PlotCard(boost.HeatSoakChart()));
                break;

            case BoostTab.Transient:
            default:
                body.Children.Add(PlotCard(boost.SpoolEstimate()));
                break;
        }

        var derived = boost.Derived(boost.SelectedTab);
        if (derived.Count > 0)
        {
            body.Children.Add(WorkspaceContent.DerivedCard(derived));
        }

        if (boost.SelectedTab == BoostTab.Compressor)
        {
            body.Children.Add(AutoMatchCard(boost));
        }
    }

    // ---- Chrome -------------------------------------------------------------

    private static UIElement SubTabs(BoostWorkspace boost, Action refresh)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        foreach (var (tab, title) in BoostCatalogue.Tabs)
        {
            var selected = tab == boost.SelectedTab;
            var button = new Button
            {
                Content = title,
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
                boost.SelectedTab = target;
                refresh();
            };
            strip.Children.Add(button);
        }

        return strip;
    }

    /// <summary>
    /// The tab's own settings. Text boxes and combos commit after the
    /// interaction is over, so a full rebuild from here is safe — and a field
    /// change moves every figure on the screen, so a full rebuild is also
    /// what is wanted.
    /// </summary>
    private static UIElement FieldsCard(BoostWorkspace boost, Action refresh)
    {
        var fields = boost.Fields(boost.SelectedTab);
        var grid = new Grid();

        if (fields.Count == 0)
        {
            return WorkspaceContent.Note("Nothing to set on this tab in Simple mode.");
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < fields.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceContent.AddFieldRow(grid, i, fields[i], boost, refresh);
        }

        return WorkspaceContent.Card(grid);
    }

    /// <summary>
    /// How the figures are drawn, as opposed to what the design is: the
    /// ambient condition and the speed range. One card, because none of it is
    /// model data and separating it from the fields above is what stops a
    /// user reading the altitude toggle as an edit.
    /// </summary>
    private static UIElement ViewControls(BoostWorkspace boost, Action redraw)
    {
        var panel = new StackPanel();
        panel.Children.Add(AmbientStrip(boost, redraw));

        var sliders = new WrapPanel();
        if (boost.SelectedTab == BoostTab.Transient)
        {
            sliders.Children.Add(Slider("Step at", "rpm", 1000, 8000, boost.TransientRpm, v =>
            {
                boost.TransientRpm = v;
                redraw();
            }));
        }
        else
        {
            sliders.Children.Add(Slider("From", "rpm", 500, 8000, boost.FromRpm, v =>
            {
                boost.FromRpm = Math.Min(v, boost.ToRpm - 500);
                redraw();
            }));

            sliders.Children.Add(Slider("To", "rpm", 2000, 16_000, boost.ToRpm, v =>
            {
                boost.ToRpm = Math.Max(v, boost.FromRpm + 500);
                redraw();
            }));

            sliders.Children.Add(Slider("Assumed VE", "%", 40, 130, boost.AssumedVolumetricEfficiency * 100.0, v =>
            {
                boost.AssumedVolumetricEfficiency = v / 100.0;
                redraw();
            }));
        }

        panel.Children.Add(sliders);
        return WorkspaceContent.Card(panel);
    }

    /// <summary>
    /// The altitude / hot-day toggle (plan §4.7: "because that is where matches
    /// fail"). Buttons, not a slider: these are named conditions, and a
    /// continuous control would imply the user should be inventing days.
    /// </summary>
    private static UIElement AmbientStrip(BoostWorkspace boost, Action redraw)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = "Draw the figures at",
            Margin = new Thickness(0, 0, 0, 6),
        }, "Text.Caption"));

        var strip = new WrapPanel();

        foreach (var condition in boost.AmbientChoices)
        {
            var selected = Math.Abs(condition.PressurePa - boost.Ambient.PressurePa) < 1e-6
                           && Math.Abs(condition.TemperatureK - boost.Ambient.TemperatureK) < 1e-9;

            var button = new Button
            {
                Content = condition.Label,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 5, 12, 6),
                Background = (Brush)Application.Current.Resources[selected ? "Brush.Accent" : "Brush.Surface"],
                Foreground = (Brush)Application.Current.Resources[selected ? "Brush.OnAccent" : "Brush.TextSecondary"],
                BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{condition.PressurePa / 1000.0:F1} kPa, {condition.TemperatureK - 273.15:F0} °C "
                          + $"({condition.DensityRatio():P0} of sea-level density)",
            };

            var target = condition;
            button.Click += (_, _) =>
            {
                boost.Ambient = target;
                redraw();
            };
            strip.Children.Add(button);
        }

        panel.Children.Add(strip);
        return panel;
    }

    private static UIElement Slider(string label, string unit, double min, double max, double value, Action<double> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 24, 0) };

        row.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = label,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,
        }, "Text.Secondary"));

        var slider = new System.Windows.Controls.Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 180,
            TickFrequency = (max - min) / 100.0,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var readout = WorkspaceContent.Styled(new TextBlock
        {
            Text = $"{slider.Value:F0} {unit}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 78,
        }, "Text.Body");

        // `changed`, never the full-rebuild Refresh: this fires under a held
        // pointer.
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = $"{e.NewValue:F0} {unit}";
            changed(e.NewValue);
        };

        row.Children.Add(slider);
        row.Children.Add(readout);
        return row;
    }

    // ---- Cards ---------------------------------------------------------------

    /// <summary>
    /// Design warnings with their citation and, where there is one, the
    /// workspace that shows the consequence (plan §8.3).
    /// </summary>
    private static UIElement WarningsCard(IReadOnlyList<DesignWarning> warnings)
    {
        var panel = new StackPanel();
        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = "Design warnings", Margin = new Thickness(0, 0, 0, 8) }, "Text.Body", bold: true));

        foreach (var warning in warnings)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var headline = WorkspaceContent.Styled(new TextBlock
            {
                Text = "⚠  " + warning.Message,
                TextWrapping = TextWrapping.Wrap,
            }, "Text.Small");
            headline.Foreground = (Brush)Application.Current.Resources["Brush.Warning"];
            block.Children.Add(headline);

            if (warning.Suggestion is { } suggestion)
            {
                block.Children.Add(WorkspaceContent.Styled(new TextBlock
                {
                    Text = suggestion,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20, 2, 0, 0),
                }, "Text.Secondary"));
            }

            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 4, 0, 0) };

            if (warning.Citation is { } citation)
            {
                footer.Children.Add(WorkspaceContent.Styled(new TextBlock
                {
                    Text = citation,
                    Margin = new Thickness(0, 0, 14, 0),
                }, "Text.Caption"));
            }

            foreach (var link in warning.Targets)
            {
                footer.Children.Add(WorkspaceContent.LinkChip(link));
            }

            block.Children.Add(footer);
            panel.Children.Add(block);
        }

        return WorkspaceContent.Card(panel);
    }

    /// <summary>
    /// Auto-match: the top five with their trade-offs, never a single "best"
    /// (plan §4.7). Every candidate shows its own margins and its own
    /// disqualifications, because a rejection a user disagrees with is still
    /// information they need.
    /// </summary>
    private static UIElement AutoMatchCard(BoostWorkspace boost)
    {
        var panel = new StackPanel();
        panel.Children.Add(WorkspaceContent.Styled(
            new TextBlock { Text = "Auto-match", Margin = new Thickness(0, 0, 0, 4) }, "Text.Body", bold: true));
        panel.Children.Add(WorkspaceContent.Styled(new TextBlock
        {
            Text = "The library ranked against this engine's demand at the current target. A ranking is a place "
                 + "to start a judgement, not a substitute for one — every candidate shows what it costs.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        }, "Text.Caption"));

        var candidates = boost.AutoMatch();
        if (candidates.Count == 0)
        {
            panel.Children.Add(WorkspaceContent.Styled(
                new TextBlock { Text = "Nothing to rank yet." }, "Text.Secondary"));
            return WorkspaceContent.Card(panel);
        }

        var grid = new Grid();
        foreach (var width in new double[] { 190, 80, 90, 110, 100, 110 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var headers = new[] { "Turbo", "Score", "Mean η", "Worst surge", "Back-p", "Onset", "" };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = WorkspaceContent.Styled(new TextBlock { Text = headers[c] }, "Text.Caption");
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }

        var row = 1;
        foreach (var candidate in candidates)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var values = new[]
            {
                candidate.Entry.Turbo.Name,
                candidate.Score.ToString("F1"),
                candidate.MeanEfficiency.ToString("P0"),
                candidate.WorstSurgeMargin.ToString("F1") + "%",
                candidate.WorstBackPressureRatio.ToString("F2"),
                double.IsNaN(candidate.BoostOnsetRpm) ? "never" : candidate.BoostOnsetRpm.ToString("N0") + " rpm",
                candidate.Viable ? "" : "✕ " + string.Join("  ", candidate.Disqualifications),
            };

            for (var c = 0; c < values.Length; c++)
            {
                var cell = WorkspaceContent.Styled(new TextBlock
                {
                    Text = values[c],
                    TextWrapping = c == values.Length - 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Margin = new Thickness(0, 3, 12, 3),
                }, "Text.Small");

                if (!candidate.Viable)
                {
                    cell.Foreground = (Brush)Application.Current.Resources["Brush.TextSecondary"];
                }

                if (c == values.Length - 1 && !candidate.Viable)
                {
                    cell.Foreground = (Brush)Application.Current.Resources["Brush.Warning"];
                }

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            row++;
        }

        panel.Children.Add(grid);
        return WorkspaceContent.Card(panel);
    }

    private static UIElement PlotCard(PlotModel model)
    {
        var body = new StackPanel();
        // PlotView draws the model's Notes inside the figure itself, and the
        // SVG export writes the same ones — repeating them under the card
        // printed every note twice.
        var view = new PlotView(model) { Height = 360 };
        body.Children.Add(view);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        row.Children.Add(ExportButton("Export PNG", () =>
        {
            var path = AskWhereToSave(model.FileStem(), "PNG image|*.png");
            if (path is not null)
            {
                File.WriteAllBytes(path, view.ToPng());
            }
        }));

        row.Children.Add(ExportButton("Export SVG", () =>
        {
            var path = AskWhereToSave(model.FileStem(), "SVG vector|*.svg");
            if (path is not null)
            {
                File.WriteAllText(
                    path,
                    SvgPlotWriter.Write(model, 900, 520, PlotView.CurrentPalette()),
                    new System.Text.UTF8Encoding(false));
            }
        }));

        body.Children.Add(row);
        return WorkspaceContent.Card(body);
    }

    private static string? AskWhereToSave(string stem, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = stem,
            Filter = filter,
            AddExtension = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static UIElement ExportButton(string label, Action click)
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
