using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveBench.Model;
using WaveBench.ViewModels;

namespace WaveBench.App;

/// <summary>
/// The Simple-mode wizard screens (plan Phase 23, §8.6).
///
/// Three regions per step, as the plan specifies: the question, a "why this
/// matters" explainer, and a live preview that updates as the user answers.
/// All of the wizard's behaviour is in <see cref="Wizard"/> and
/// <see cref="BriefBuilder"/>; this only builds controls.
/// </summary>
public static class WizardContent
{
    public static void Render(Panel host, ShellViewModel shell, ProjectSession session, Wizard wizard)
    {
        void Refresh() => Render(host, shell, session, wizard);

        host.Children.Add(Heading(wizard));
        host.Children.Add(StepRail(wizard, Refresh));

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

        var question = QuestionCard(wizard, Refresh);
        Grid.SetColumn(question, 0);
        columns.Children.Add(question);

        var preview = PreviewCard(wizard, shell, session, Refresh);
        Grid.SetColumn(preview, 1);
        columns.Children.Add(preview);

        host.Children.Add(columns);
        host.Children.Add(Navigation(wizard, Refresh));
    }

    private static UIElement Heading(Wizard wizard)
    {
        var (_, title, question) = Wizard.Steps.First(s => s.Step == wizard.Step);
        var index = Array.IndexOf(Enum.GetValues<WizardStep>(), wizard.Step) + 1;

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(Styled(new TextBlock
        {
            Text = $"Step {index} of {Wizard.Steps.Count} · {title}",
        }, "Text.Caption"));
        panel.Children.Add(Styled(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }, "Text.Title"));
        return panel;
    }

    private static UIElement StepRail(Wizard wizard, Action refresh)
    {
        var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };

        foreach (var (step, title, _) in Wizard.Steps)
        {
            var selected = step == wizard.Step;
            var button = new Button
            {
                Content = title,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(11, 5, 11, 6),
                Background = (Brush)Application.Current.Resources[selected ? "Brush.Accent" : "Brush.Surface"],
                Foreground = (Brush)Application.Current.Resources[selected ? "Brush.OnAccent" : "Brush.TextSecondary"],
                BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            var target = step;
            button.Click += (_, _) =>
            {
                wizard.GoTo(target);
                refresh();
            };
            strip.Children.Add(button);
        }

        return strip;
    }

    // ---- The question region ---------------------------------------------

    private static UIElement QuestionCard(Wizard wizard, Action refresh)
    {
        var panel = new StackPanel();

        switch (wizard.Step)
        {
            case WizardStep.Purpose:
                panel.Children.Add(Choice(
                    Enum.GetValues<BuildPurpose>().Select(p => (object)p).ToList(),
                    wizard.Purpose,
                    v => { wizard.Purpose = (BuildPurpose)v; refresh(); }));
                break;

            case WizardStep.Engine:
                panel.Children.Add(Number("Bore", "mm", wizard.BoreMm, v => wizard.BoreMm = v, refresh));
                panel.Children.Add(Number("Stroke", "mm", wizard.StrokeMm, v => wizard.StrokeMm = v, refresh));
                panel.Children.Add(Number("Cylinders", "", wizard.Cylinders,
                    v => wizard.Cylinders = (int)Math.Round(v), refresh));
                panel.Children.Add(Number("Compression ratio", ":1", wizard.CompressionRatio,
                    v => wizard.CompressionRatio = v, refresh));
                panel.Children.Add(Number("Redline", "rpm", wizard.RedlineRpm, v => wizard.RedlineRpm = v, refresh));
                break;

            case WizardStep.Head:
                panel.Children.Add(Choice(
                    Enum.GetValues<CamCharacter>().Select(c => (object)c).ToList(),
                    wizard.Cam,
                    v => { wizard.Cam = (CamCharacter)v; refresh(); }));
                panel.Children.Add(Number("Intake valve Ø", "mm (0 to derive)", wizard.IntakeValveMm,
                    v => wizard.IntakeValveMm = v, refresh));
                panel.Children.Add(Number("Exhaust valve Ø", "mm (0 to derive)", wizard.ExhaustValveMm,
                    v => wizard.ExhaustValveMm = v, refresh));
                break;

            case WizardStep.Fuel:
                panel.Children.Add(Text("Fuel", wizard.Fuel, v => wizard.Fuel = v, refresh));
                panel.Children.Add(Number("λ (relative AFR)", "", wizard.Lambda, v => wizard.Lambda = v, refresh));
                panel.Children.Add(Number("Ambient temperature", "°C", wizard.AmbientTemperatureC,
                    v => wizard.AmbientTemperatureC = v, refresh));
                panel.Children.Add(Number("Altitude", "m", wizard.AltitudeM, v => wizard.AltitudeM = v, refresh));
                break;

            case WizardStep.Aspiration:
                panel.Children.Add(Toggle("Forced induction", wizard.ForcedInduction,
                    v => { wizard.ForcedInduction = v; refresh(); }));
                panel.Children.Add(Styled(new TextBlock
                {
                    Text = Wizard.AspirationNote,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0),
                }, "Text.Caption"));
                break;

            case WizardStep.Constraints:
                panel.Children.Add(Number("Longest runner that fits", "mm", wizard.PackagingLimitMm,
                    v => wizard.PackagingLimitMm = v, refresh));
                panel.Children.Add(Number("Noise limit", "dBC", wizard.NoiseLimitDbc,
                    v => wizard.NoiseLimitDbc = v, refresh));
                break;

            case WizardStep.Goal:
                panel.Children.Add(Number("Band from", "rpm", wizard.BandFromRpm,
                    v => wizard.BandFromRpm = v, refresh));
                panel.Children.Add(Number("Band to", "rpm", wizard.BandToRpm, v => wizard.BandToRpm = v, refresh));
                panel.Children.Add(Choice(
                    Enum.GetValues<TorqueShape>().Select(s => (object)s).ToList(),
                    wizard.Shape,
                    v => { wizard.Shape = (TorqueShape)v; refresh(); }));
                break;

            case WizardStep.Review:
                panel.Children.Add(ReviewList(wizard));
                break;

            default:
                panel.Children.Add(ComputeRegion(wizard, refresh));
                break;
        }

        // The "why this matters" region. Below the question rather than above
        // it: a user who already knows why is not made to read past it.
        panel.Children.Add(new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(12, 10, 12, 11),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["Brush.SurfaceAlt"],
            Child = Styled(new TextBlock
            {
                Text = Wizard.Explainer(wizard.Step),
                TextWrapping = TextWrapping.Wrap,
            }, "Text.Caption"),
        });

        return Card(panel);
    }

    private static UIElement ReviewList(Wizard wizard)
    {
        var panel = new StackPanel();
        var derived = wizard.Derive().OrderBy(kv => kv.Key, StringComparer.Ordinal);

        foreach (var (path, value) in derived)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = Styled(new TextBlock { Text = path }, "Text.Secondary");
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var shown = Styled(new TextBlock
            {
                Text = value is double d
                    ? d.ToString("0.###", CultureInfo.InvariantCulture)
                    : value?.ToString() ?? "—",
            }, "Text.Body");
            Grid.SetColumn(shown, 1);
            row.Children.Add(shown);

            panel.Children.Add(row);
        }

        return panel;
    }

    // ---- The compute step -------------------------------------------------

    private static CancellationTokenSource? _compute;

    private static UIElement ComputeRegion(Wizard wizard, Action refresh)
    {
        var panel = new StackPanel();

        var status = Styled(new TextBlock
        {
            Text = WorkspaceContent.LatestBrief is null
                ? "Nothing computed yet."
                : "Brief ready — see the preview beside this.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        }, "Text.Secondary");
        panel.Children.Add(status);

        var progress = new ProgressBar { Height = 6, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        panel.Children.Add(progress);

        var run = new Button
        {
            Content = "Compute the brief",
            Padding = new Thickness(16, 7, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)Application.Current.Resources["Brush.Accent"],
            Foreground = (Brush)Application.Current.Resources["Brush.OnAccent"],
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        run.Click += async (_, _) =>
        {
            if (_compute is not null)
            {
                _compute.Cancel();
                return;
            }

            _compute = new CancellationTokenSource();
            var token = _compute.Token;
            run.Content = "Cancel";
            progress.Visibility = Visibility.Visible;
            progress.Value = 0;

            var reporter = new Progress<BriefProgress>(p =>
            {
                progress.Maximum = Math.Max(1, p.Total);
                progress.Value = p.Completed;
                status.Text = $"{p.Stage}  ·  {p.Completed}/{p.Total}";
            });

            try
            {
                // Off the UI thread: the search is a few dozen solves, and a
                // frozen window during it is indistinguishable from a crash.
                var brief = await Task.Run(
                    () => BriefBuilder.Build(wizard, quick: true, reporter, token), token);

                WorkspaceContent.LatestBrief = brief;
                status.Text = "Brief ready.";
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelled. The model keeps whatever the search had already committed.";
            }
            catch (Exception e)
            {
                status.Text = "The compute failed: " + e.Message;
            }
            finally
            {
                _compute.Dispose();
                _compute = null;
                run.Content = "Compute the brief";
                progress.Visibility = Visibility.Collapsed;
                refresh();
            }
        };

        panel.Children.Add(run);
        return panel;
    }

    // ---- The live preview region -----------------------------------------

    private static UIElement PreviewCard(
        Wizard wizard, ShellViewModel shell, ProjectSession session, Action refresh)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = "Live preview" }, "Text.Body"));

        var brief = WorkspaceContent.LatestBrief ?? BriefBuilder.Preview(wizard);

        panel.Children.Add(Styled(new TextBlock
        {
            Text = $"Overall confidence: {brief.WeakestConfidence.ToString().ToLowerInvariant()} — the weakest "
                 + "of the recommendations, not an average.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        }, "Text.Caption"));

        foreach (var group in brief.Groups)
        {
            panel.Children.Add(Styled(new TextBlock
            {
                Text = group,
                Margin = new Thickness(0, 8, 0, 2),
            }, "Text.Caption"));

            foreach (var line in brief.Lines.Where(l => l.Group == group))
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(Styled(new TextBlock { Text = line.Label, Width = 128 }, "Text.Secondary"));
                var value = Styled(new TextBlock { Text = line.Value, Width = 96 }, "Text.Body");
                value.SetValue(
                    System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
                head.Children.Add(value);
                head.Children.Add(Styled(new TextBlock
                {
                    Text = $"{line.Indicator} {line.ConfidenceWord}",
                }, "Text.Caption"));
                row.Children.Add(head);

                row.Children.Add(Styled(new TextBlock
                {
                    Text = "↳ " + line.Why,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0),
                    ToolTip = line.Basis,
                }, "Text.Caption"));

                panel.Children.Add(row);
            }
        }

        if (brief.Predictions.Count > 0)
        {
            panel.Children.Add(Styled(new TextBlock
            {
                Text = "PREDICTED",
                Margin = new Thickness(0, 10, 0, 2),
            }, "Text.Caption"));

            foreach (var prediction in brief.Predictions)
            {
                panel.Children.Add(Styled(new TextBlock
                {
                    Text = $"{prediction.Label}: {prediction.Format()}",
                    TextWrapping = TextWrapping.Wrap,
                }, "Text.Body"));
            }
        }

        if (brief.BuildList.Count > 0)
        {
            panel.Children.Add(Styled(new TextBlock
            {
                Text = "BUILD LIST",
                Margin = new Thickness(0, 10, 0, 2),
            }, "Text.Caption"));

            foreach (var item in brief.BuildList)
            {
                panel.Children.Add(Styled(new TextBlock
                {
                    Text = $"{item.Quantity} ×  {item.Description}",
                    TextWrapping = TextWrapping.Wrap,
                }, "Text.Caption"));
            }
        }

        foreach (var caveat in brief.Caveats)
        {
            var warn = Styled(new TextBlock
            {
                Text = "⚠  " + caveat,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            }, "Text.Caption");
            warn.Foreground = (Brush)Application.Current.Resources["Brush.Warning"];
            panel.Children.Add(warn);
        }

        panel.Children.Add(Actions(wizard, shell, session, brief, refresh));

        var scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 640,
            Margin = new Thickness(16, 0, 0, 0),
        };

        return Card(scroller);
    }

    private static UIElement Actions(
        Wizard wizard, ShellViewModel shell, ProjectSession session, DesignBrief brief, Action refresh)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };

        row.Children.Add(ActionButton("Export PDF", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "design-brief",
                Filter = "PDF document|*.pdf",
                AddExtension = true,
            };

            if (dialog.ShowDialog() == true)
            {
                File.WriteAllBytes(dialog.FileName, BriefPdf.Render(brief, $"{wizard.Purpose} build"));
            }
        }));

        // Plan §8.6 asks for exactly one button here. The wizard has written
        // into the full model all along, so this is navigation and nothing
        // else — no conversion, no export, nothing to lose.
        row.Children.Add(ActionButton("Open in Advanced", () =>
        {
            shell.Mode = UiMode.Advanced;
            shell.Navigate(Workspace.Design);
            refresh();
        }));

        _ = session;
        return row;
    }

    // ---- Navigation -------------------------------------------------------

    private static UIElement Navigation(Wizard wizard, Action refresh)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };

        var back = ActionButton("← Back", () =>
        {
            wizard.Back();
            refresh();
        });
        ((Button)back).IsEnabled = wizard.CanGoBack;
        row.Children.Add(back);

        var next = ActionButton("Next →", () =>
        {
            wizard.Next();
            refresh();
        });
        ((Button)next).IsEnabled = wizard.CanGoNext;
        row.Children.Add(next);

        foreach (var issue in wizard.Check())
        {
            var warn = Styled(new TextBlock
            {
                Text = (issue.Severity == ModelIssueSeverity.Error ? "✕  " : "⚠  ") + issue.Message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            }, "Text.Caption");
            warn.Foreground = (Brush)Application.Current.Resources[
                issue.Severity == ModelIssueSeverity.Error ? "Brush.Danger" : "Brush.Warning"];
            row.Children.Add(warn);
            break;
        }

        return row;
    }

    // ---- Controls ---------------------------------------------------------

    private static UIElement Number(string label, string unit, double value, Action<double> set, Action refresh)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(Styled(new TextBlock
        {
            Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center,
        }, "Text.Secondary"));

        var box = new TextBox
        {
            Text = value.ToString("0.###", CultureInfo.InvariantCulture),
            Width = 110,
            Padding = new Thickness(6, 3, 6, 4),
            Background = (Brush)Application.Current.Resources["Brush.Canvas"],
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
            BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
        };

        var rendered = box.Text;
        void Commit()
        {
            if (box.Text.Trim() == rendered.Trim())
            {
                return;
            }

            if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                set(parsed);
                refresh();
            }
            else
            {
                box.Text = rendered;
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Commit();
            }
        };

        row.Children.Add(box);
        row.Children.Add(Styled(new TextBlock
        {
            Text = unit, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        }, "Text.Caption"));

        return row;
    }

    private static UIElement Text(string label, string value, Action<string> set, Action refresh)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(Styled(new TextBlock
        {
            Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center,
        }, "Text.Secondary"));

        var box = new TextBox
        {
            Text = value,
            Width = 200,
            Padding = new Thickness(6, 3, 6, 4),
            Background = (Brush)Application.Current.Resources["Brush.Canvas"],
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
            BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
        };

        box.LostFocus += (_, _) =>
        {
            if (box.Text != value)
            {
                set(box.Text);
                refresh();
            }
        };

        row.Children.Add(box);
        return row;
    }

    private static UIElement Toggle(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
        };
        box.Click += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static UIElement Choice(IReadOnlyList<object> options, object selected, Action<object> set)
    {
        var strip = new WrapPanel();

        foreach (var option in options)
        {
            var isSelected = option.Equals(selected);
            var button = new Button
            {
                Content = Spaced(option.ToString() ?? ""),
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(14, 6, 14, 7),
                Background = (Brush)Application.Current.Resources[isSelected ? "Brush.Accent" : "Brush.Surface"],
                Foreground = (Brush)Application.Current.Resources[isSelected ? "Brush.OnAccent" : "Brush.TextSecondary"],
                BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            var target = option;
            button.Click += (_, _) => set(target);
            strip.Children.Add(button);
        }

        return strip;
    }

    /// <summary>"BroadMidrange" reads as "Broad midrange" to someone who did not write the enum.</summary>
    private static string Spaced(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append(' ').Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }

        return sb.ToString();
    }

    private static UIElement ActionButton(string label, Action click)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 7),
            Background = (Brush)Application.Current.Resources["Brush.Surface"],
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
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

    private static Border Card(UIElement child) => new()
    {
        Child = child,
        Background = (Brush)Application.Current.Resources["Brush.Surface"],
        BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(18),
        Margin = new Thickness(0, 0, 0, 12),
    };

    private static TextBlock Styled(TextBlock block, string styleKey)
    {
        block.Style = (Style)Application.Current.Resources[styleKey];
        return block;
    }
}
