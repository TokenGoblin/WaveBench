using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.App;

/// <summary>
/// The Results workspace screens (plan Phase 19, §8.4).
///
/// Every figure comes from <see cref="ResultsWorkspace"/> as a
/// <see cref="PlotModel"/>; this arranges them and adds the two things a
/// static figure cannot do — scrubbing and animating the wave diagram, and
/// writing a figure to disk.
/// </summary>
public static class ResultsContent
{
    public static void Render(Panel host, ShellViewModel shell, ProjectSession session, ResultsWorkspace? results)
    {
        // Clear first: the sub-tabs, the scrub slider and the animation Play
        // button all call back into this method through the Refresh closure
        // below, so re-rendering has to replace rather than append.
        host.Children.Clear();

        if (results is null)
        {
            RenderNoRun(host);
            return;
        }

        void Refresh() => Render(host, shell, session, results);

        host.Children.Add(Heading(
            "Results",
            $"{results.Run.ModelName} · {results.Run.Points.Count} operating point"
            + (results.Run.Points.Count == 1 ? "" : "s")
            + $" · detail captured at {results.Run.CaptureRpm:F0} rpm"));

        host.Children.Add(SubTabs(results, Refresh));

        switch (results.SelectedTab)
        {
            case ResultsTab.Performance:
                RenderPerformance(host, results);
                break;
            case ResultsTab.Waves:
                RenderWaves(host, results, Refresh);
                break;
            case ResultsTab.Cylinders:
                RenderCylinders(host, results);
                break;
            default:
                RenderTransient(host);
                break;
        }
    }

    private static void RenderNoRun(Panel host)
    {
        host.Children.Add(Heading("Results", "Nothing has been run yet."));

        var body = new StackPanel();
        body.Children.Add(Styled(new TextBlock
        {
            Text = "Press Run to sweep this model and capture the detail for the wave diagram.",
            TextWrapping = TextWrapping.Wrap,
        }, "Text.Body"));
        body.Children.Add(Styled(new TextBlock
        {
            Text = "The same thing headless: wavebench sweep <model.json> --from 3000 --to 9000 --step 500",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
        }, "Text.Secondary"));

        host.Children.Add(Card(body));
    }

    private static UIElement SubTabs(ResultsWorkspace results, Action refresh)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        foreach (var (tab, title) in ResultsWorkspace.Tabs)
        {
            var selected = tab == results.SelectedTab;
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
                results.SelectedTab = target;
                refresh();
            };
            strip.Children.Add(button);
        }

        return strip;
    }

    // ---- Performance ------------------------------------------------------

    private static void RenderPerformance(Panel host, ResultsWorkspace results)
    {
        var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var readout in results.Headlines())
        {
            tiles.Children.Add(Tile(readout));
        }

        host.Children.Add(tiles);

        if (!results.Run.HasSweep)
        {
            host.Children.Add(Note(
                "A single operating point: the curves need a sweep. Set a range on the Run workspace."));
        }
        else
        {
            host.Children.Add(PlotCard(results.TorqueAndPower()));
            host.Children.Add(PlotCard(results.VolumetricEfficiency()));
            host.Children.Add(PlotCard(results.BmepAndBsfc()));
        }
    }

    // ---- Waves ------------------------------------------------------------

    private static void RenderWaves(Panel host, ResultsWorkspace results, Action refresh)
    {
        if (results.FieldNames.Count > 1)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            strip.Children.Add(Styled(new TextBlock
            {
                Text = "Pipe",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            }, "Text.Secondary"));

            for (var i = 0; i < results.FieldNames.Count; i++)
            {
                var index = i;
                var selected = i == results.SelectedField;
                var button = new Button
                {
                    Content = results.FieldNames[i],
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(12, 4, 12, 5),
                    Background = (Brush)Application.Current.Resources[selected ? "Brush.Accent" : "Brush.Surface"],
                    Foreground = (Brush)Application.Current.Resources[selected ? "Brush.OnAccent" : "Brush.TextSecondary"],
                    BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                button.Click += (_, _) =>
                {
                    results.SelectedField = index;
                    refresh();
                };
                strip.Children.Add(button);
            }

            host.Children.Add(strip);
        }

        host.Children.Add(WaveDiagramCard(results));
        host.Children.Add(PlotCard(results.WaveDecompositionPlot()));

        foreach (var probe in results.Run.Probes)
        {
            host.Children.Add(PlotCard(results.ProbeTracePlot(probe.Name)));
        }
    }

    /// <summary>
    /// The wave diagram, plus the scrub bar and the play control.
    ///
    /// Animation redraws only the cursor and the slice — never the heat map,
    /// which is a bitmap built once. Rebuilding the field per frame is what
    /// makes a wave diagram stutter, and the plan's gate is that a 30-cycle
    /// result animates without it.
    /// </summary>
    private static UIElement WaveDiagramCard(ResultsWorkspace results)
    {
        var body = new StackPanel();
        var plot = results.WaveDiagram();

        var view = new PlotView(plot) { Height = 420 };
        body.Children.Add(view);

        if (plot.HeatMap is null || results.Run.Fields.Count == 0)
        {
            return Card(body);
        }

        var field = results.Run.Fields[Math.Clamp(results.SelectedField, 0, results.Run.Fields.Count - 1)];

        var slice = new PlotView(results.SliceAt(0)) { Height = 240 };

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(64, 6, 0, 4),
        };

        var play = new Button
        {
            Content = "▶  Play",
            Width = 88,
            Padding = new Thickness(0, 4, 0, 5),
            Background = (Brush)Application.Current.Resources["Brush.Surface"],
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
            BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var scrub = new Slider
        {
            Minimum = 0,
            Maximum = Math.Max(1, field.FrameCount - 1),
            Width = 420,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
        };

        var readout = Styled(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 200,
        }, "Text.Secondary");

        void ShowFrame(int frame)
        {
            frame = Math.Clamp(frame, 0, Math.Max(0, field.FrameCount - 1));
            slice.Model = results.SliceAt(frame);
            readout.Text = $"frame {frame + 1} of {field.FrameCount} · {results.ScrubAngleDeg:F1}° crank";
        }

        scrub.ValueChanged += (_, e) => ShowFrame((int)Math.Round(e.NewValue));

        // 60 fps timer. It advances the slider, which redraws the slice only —
        // the heat map underneath is untouched.
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0),
        };
        timer.Tick += (_, _) =>
        {
            var next = scrub.Value + 1;
            scrub.Value = next > scrub.Maximum ? 0 : next;
        };

        play.Click += (_, _) =>
        {
            if (timer.IsEnabled)
            {
                timer.Stop();
                play.Content = "▶  Play";
            }
            else
            {
                timer.Start();
                play.Content = "❚❚  Pause";
            }
        };

        // A timer outliving its window keeps the whole visual tree alive and
        // ticks into a dead canvas.
        view.Unloaded += (_, _) => timer.Stop();

        controls.Children.Add(play);
        controls.Children.Add(scrub);
        controls.Children.Add(readout);

        body.Children.Add(controls);
        body.Children.Add(slice);
        body.Children.Add(ExportRow(plot, view));

        ShowFrame(0);
        return Card(body);
    }

    // ---- Cylinders --------------------------------------------------------

    private static void RenderCylinders(Panel host, ResultsWorkspace results)
    {
        host.Children.Add(PlotCard(results.PerCylinderVolumetricEfficiency()));
        host.Children.Add(PlotCard(results.PerCylinderKnockAndEgt()));
    }

    private static void RenderTransient(Panel host)
    {
        var body = new StackPanel();
        body.Children.Add(Styled(new TextBlock
        {
            Text = "Transient traces arrive with forced induction.",
            TextWrapping = TextWrapping.Wrap,
        }, "Text.Body"));
        body.Children.Add(Styled(new TextBlock
        {
            Text = "Step-response and vehicle-acceleration transients, time-to-torque and heat-soak "
                 + "are Phase 15, which is deferred until the turbo phases land. Steady-state results "
                 + "on the other tabs are complete.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        }, "Text.Secondary"));
        host.Children.Add(Card(body));
    }

    // ---- Shared -----------------------------------------------------------

    private static UIElement PlotCard(PlotModel model)
    {
        var body = new StackPanel();
        var view = new PlotView(model) { Height = 360 };
        body.Children.Add(view);
        body.Children.Add(ExportRow(model, view));
        return Card(body);
    }

    /// <summary>
    /// PNG and SVG export for one figure. Both come from the same
    /// <see cref="PlotModel"/> — the PNG off the visual tree, the SVG from the
    /// writer — so the two agree with each other and with the screen.
    /// </summary>
    private static UIElement ExportRow(PlotModel model, PlotView view)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };

        row.Children.Add(ExportButton("Export PNG", () =>
        {
            var path = AskWhereToSave(model.FileStem(), "PNG image|*.png");
            if (path is null)
            {
                return;
            }

            File.WriteAllBytes(path, view.ToPng());
            return;
        }));

        row.Children.Add(ExportButton("Export SVG", () =>
        {
            var path = AskWhereToSave(model.FileStem(), "SVG vector|*.svg");
            if (path is null)
            {
                return;
            }

            File.WriteAllText(
                path,
                SvgPlotWriter.Write(model, 900, 520, PlotView.CurrentPalette()),
                new System.Text.UTF8Encoding(false));
        }));

        return row;
    }

    private static UIElement ExportButton(string label, Action click)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 5),
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

    private static UIElement Tile(DerivedReadout readout)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = readout.Label }, "Text.Caption"));

        var metric = Styled(new TextBlock { Text = readout.Value, Margin = new Thickness(0, 4, 0, 0) }, "Text.Body");
        metric.FontSize = 21;
        metric.FontWeight = FontWeights.SemiBold;
        metric.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        panel.Children.Add(metric);

        if (readout.Note is not null)
        {
            panel.Children.Add(Styled(new TextBlock
            {
                Text = readout.Note, TextWrapping = TextWrapping.Wrap,
            }, "Text.Caption"));
        }

        if (readout.Warning is not null)
        {
            var warn = Styled(new TextBlock
            {
                Text = "⚠  " + readout.Warning, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            }, "Text.Caption");
            warn.Foreground = (Brush)Application.Current.Resources["Brush.Warning"];
            panel.Children.Add(warn);
        }

        return new Border
        {
            Child = panel,
            Background = (Brush)Application.Current.Resources["Brush.Surface"],
            BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 210,
        };
    }

    private static UIElement Heading(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(Styled(new TextBlock { Text = title }, "Text.Title"));
        panel.Children.Add(Styled(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap }, "Text.Secondary"));
        return panel;
    }

    private static Border Card(UIElement child) => new()
    {
        Child = child,
        Background = (Brush)Application.Current.Resources["Brush.Surface"],
        BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 0, 0, 16),
    };

    private static UIElement Note(string text) => Card(Styled(
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, "Text.Secondary"));

    private static TextBlock Styled(TextBlock block, string styleKey)
    {
        block.Style = (Style)Application.Current.Resources[styleKey];
        return block;
    }
}
