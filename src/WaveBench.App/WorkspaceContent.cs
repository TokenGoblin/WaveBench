using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveBench.Model;
using WaveBench.ViewModels;

namespace WaveBench.App;

/// <summary>
/// Renders workspace bodies in code so the shell stays one XAML file while
/// Phases 17–19 fill the real screens in. Every colour comes from the token
/// dictionary — nothing here holds a literal.
/// </summary>
public static class WorkspaceContent
{
    public static void Render(Panel host, ShellViewModel shell, ProjectSession session)
    {
        host.Children.Clear();
        switch (shell.Current)
        {
            case Workspace.Overview:
                RenderOverview(host, shell, session);
                break;
            case Workspace.Design:
                RenderDesign(host, shell, session);
                break;
            default:
                RenderPlaceholder(host, shell);
                break;
        }
    }

    // ---- Overview (plan §8.4): summary, metric tiles, run history --------

    private static void RenderOverview(Panel host, ShellViewModel shell, ProjectSession session)
    {
        var d = session.Document;
        var displacement = Math.PI / 4.0 * Math.Pow(d.Engine.BoreMm / 1000.0, 2)
                           * (d.Engine.StrokeMm / 1000.0) * d.Engine.CylinderCount * 1e6;

        host.Children.Add(Heading(d.Name, $"{displacement:F0} cc · {d.Engine.CylinderCount} cyl · "
            + $"{d.Engine.BoreMm:F1} × {d.Engine.StrokeMm:F1} mm · CR {d.Engine.CompressionRatio:F1}"));

        // Figures below are the committed CLI sweep of this model — real
        // solver output, not mock data.
        var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 24) };
        tiles.Children.Add(Tile("Peak torque", "58.8", "N·m @ 4000 rpm"));
        tiles.Children.Add(Tile("Peak power", "30.7", "kW @ 7000 rpm"));
        tiles.Children.Add(Tile("Peak VE", "1.27", "@ 4000 rpm"));
        tiles.Children.Add(Tile("Best BSFC", "166", "g/kWh"));
        tiles.Children.Add(Tile("Tuned length", "5015", "rpm organ-pipe estimate"));
        tiles.Children.Add(Tile("Resolved to", "5.6", "kHz at the acoustic mesh"));
        host.Children.Add(tiles);

        host.Children.Add(TorqueCard());

        host.Children.Add(Note(
            "These are this model's committed sweep results. Wiring the chart to a live in-app run "
            + "is Phase 19; the same numbers come from `wavebench sweep examples/single-360.json` today."));
    }

    private static UIElement TorqueCard()
    {
        // The committed sweep: 3000–9000 rpm in 500 rpm steps.
        double[] rpm = [3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000, 7500, 8000, 8500, 9000];
        double[] torque = [47.6, 52.3, 58.8, 52.5, 55.2, 46.3, 44.6, 43.6, 41.9, 34.2, 27.1, 23.7, 24.1];
        double[] power = [14.9, 19.2, 24.6, 24.7, 28.9, 26.7, 28.0, 29.7, 30.7, 26.9, 22.7, 21.1, 22.7];

        var canvas = new Canvas { Height = 260, ClipToBounds = true };
        canvas.Loaded += (_, _) => DrawCurves(canvas, rpm, torque, power);
        canvas.SizeChanged += (_, _) => DrawCurves(canvas, rpm, torque, power);

        var body = new StackPanel();
        body.Children.Add(Styled(new TextBlock { Text = "Torque and power" }, "Text.Body", bold: true));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        legend.Children.Add(LegendSwatch("Brush.Accent", "Torque (N·m) — solid, left axis"));
        legend.Children.Add(LegendSwatch("Brush.Info", "Power (kW) — dashed, right axis"));
        body.Children.Add(legend);
        body.Children.Add(canvas);

        return Card(body);
    }

    private static void DrawCurves(Canvas canvas, double[] rpm, double[] torque, double[] power)
    {
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 20 || h < 20)
        {
            return;
        }

        const double padLeft = 46, padBottom = 26, padTop = 10, padRight = 48;
        var plotW = w - padLeft - padRight;
        var plotH = h - padTop - padBottom;
        var axis = (Brush)Application.Current.Resources["Brush.BorderStrong"];
        var faint = (Brush)Application.Current.Resources["Brush.BorderSubtle"];
        var text = (Brush)Application.Current.Resources["Brush.TextSecondary"];
        var torqueBrush = (Brush)Application.Current.Resources["Brush.Accent"];
        var powerBrush = (Brush)Application.Current.Resources["Brush.Info"];

        // Two quantities, two scales. Sharing one axis would print the power
        // curve against numbers a reader takes for N·m — the axis must be
        // readable for every series on it, so power gets its own on the right.
        const double maxTorque = 62.0;
        const double maxPower = 35.0;

        void AxisLabel(string content, double x, double y, Brush brush)
        {
            var label = new TextBlock { Text = content, Foreground = brush, FontSize = 11 };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            canvas.Children.Add(label);
        }

        for (var value = 0; value <= 60; value += 20)
        {
            var y = padTop + plotH * (1 - value / maxTorque);
            canvas.Children.Add(Line(padLeft, y, padLeft + plotW, y, value == 0 ? axis : faint, 1));
            AxisLabel(value.ToString(CultureInfo.InvariantCulture), 14, y - 8, torqueBrush);
        }

        for (var value = 0; value <= 30; value += 10)
        {
            var y = padTop + plotH * (1 - value / maxPower);
            AxisLabel(value.ToString(CultureInfo.InvariantCulture), padLeft + plotW + 8, y - 8, powerBrush);
        }

        AxisLabel("N·m", 14, padTop + plotH + 6, torqueBrush);
        AxisLabel("kW", padLeft + plotW + 8, padTop + plotH + 6, powerBrush);

        double X(double r) => padLeft + plotW * (r - rpm[0]) / (rpm[^1] - rpm[0]);
        double YTorque(double v) => padTop + plotH * (1 - v / maxTorque);
        double YPower(double v) => padTop + plotH * (1 - v / maxPower);

        foreach (var r in new[] { 3000.0, 5000.0, 7000.0, 9000.0 })
        {
            AxisLabel($"{r / 1000:0}k", X(r) - 8, padTop + plotH + 6, text);
        }

        // Series are distinguishable by line style as well as colour (§8.11).
        canvas.Children.Add(Polyline(rpm, torque, X, YTorque, torqueBrush, null));
        canvas.Children.Add(Polyline(rpm, power, X, YPower, powerBrush, new DoubleCollection { 4, 3 }));
    }

    private static System.Windows.Shapes.Polyline Polyline(
        double[] xs, double[] ys, Func<double, double> X, Func<double, double> Y, Brush stroke, DoubleCollection? dash)
    {
        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeDashArray = dash,
        };
        for (var i = 0; i < xs.Length; i++)
        {
            line.Points.Add(new Point(X(xs[i]), Y(ys[i])));
        }

        return line;
    }

    private static System.Windows.Shapes.Line Line(double x1, double y1, double x2, double y2, Brush brush, double thickness) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness };

    // ---- Design (plan §8.4/§8.5): fields with provenance badges ----------

    /// <summary>
    /// The Design workspace (Phase 17). All behaviour is in
    /// <see cref="DesignWorkspace"/>; this method only builds controls and
    /// forwards keystrokes to it, which is what keeps the gate testable
    /// without a window.
    /// </summary>
    private static void RenderDesign(Panel host, ShellViewModel shell, ProjectSession session)
    {
        var workspace = DesignFor(shell, session);
        var tab = workspace.SelectedTab;
        var title = DesignWorkspace.Tabs.First(t => t.Tab == tab).Title;

        host.Children.Add(Heading(title,
            "Every field carries its origin. Hover a badge for the derivation and its citation."));
        host.Children.Add(SubTabs(host, shell, session, workspace));

        var all = DesignCatalogue.For(tab).Count;
        var fields = workspace.Fields(tab);

        if (fields.Count > 0)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (var i = 0; i < fields.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddFieldRow(grid, i, fields[i], workspace, host, shell, session);
            }

            host.Children.Add(Card(grid));
        }

        var derived = workspace.Derived(tab);
        if (derived.Count > 0)
        {
            host.Children.Add(DerivedCard(derived));
        }

        var issues = workspace.Issues(tab);
        if (issues.Count > 0)
        {
            host.Children.Add(IssuesCard(issues));
        }

        if (shell.Mode == UiMode.Simple && fields.Count < all)
        {
            host.Children.Add(Note(
                $"Simple mode is showing {fields.Count} of {all} fields on this tab. The rest stay ACTIVE — "
                + "switching modes never changes the model, and the banner above lists what is hidden."));
        }
    }

    /// <summary>
    /// One workspace per session, so the selected tab and any inline
    /// rejections survive a re-render.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProjectSession, DesignWorkspace>
        DesignWorkspaces = [];

    private static DesignWorkspace DesignFor(ShellViewModel shell, ProjectSession session) =>
        DesignWorkspaces.GetValue(session, s => new DesignWorkspace(s, shell.Preferences));

    private static UIElement SubTabs(Panel host, ShellViewModel shell, ProjectSession session, DesignWorkspace workspace)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        foreach (var (tab, title) in DesignWorkspace.Tabs)
        {
            var selected = tab == workspace.SelectedTab;
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
                workspace.SelectedTab = target;
                Render(host, shell, session);
            };

            strip.Children.Add(button);
        }

        return strip;
    }

    private static void AddFieldRow(
        Grid grid, int row, FieldView view, DesignWorkspace workspace,
        Panel host, ShellViewModel shell, ProjectSession session)
    {
        var field = view.Field;

        var label = Styled(new TextBlock
        {
            Text = field.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 8, 5),
            ToolTip = field.Help,
        }, "Text.Body");
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var editor = Editor(view, workspace, host, shell, session);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);

        // A name has no unit, so it takes the unit column's width rather than
        // being clipped into a box sized for "146.9".
        if (field.Kind is FieldKind.Text or FieldKind.Choice)
        {
            Grid.SetColumnSpan(editor, 2);
        }
        else
        {
            var unit = Styled(new TextBlock
            {
                Text = view.DisplayUnit,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 5, 8, 5),
            }, "Text.Secondary");
            Grid.SetRow(unit, row);
            Grid.SetColumn(unit, 2);
            grid.Children.Add(unit);
        }

        grid.Children.Add(editor);

        var badge = Badge(view.Provenance);
        Grid.SetRow(badge, row);
        Grid.SetColumn(badge, 3);
        grid.Children.Add(badge);

        if (workspace.Rejections.TryGetValue(field.Path, out var reason))
        {
            var error = Styled(new TextBlock
            {
                Text = reason,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 5, 0, 5),
                Foreground = (Brush)Application.Current.Resources["Brush.Warning"],
            }, "Text.Small");
            Grid.SetRow(error, row);
            Grid.SetColumn(error, 4);
            grid.Children.Add(error);
        }
    }

    private static FrameworkElement Editor(
        FieldView view, DesignWorkspace workspace, Panel host, ShellViewModel shell, ProjectSession session)
    {
        var field = view.Field;

        // The value as rendered. Committing this back would be a write the
        // user never made: the display is rounded for legibility, so merely
        // tabbing THROUGH a field would rewrite the model with the rounded
        // number and stamp it "You". An edit that changes nothing is not an
        // edit.
        var rendered = view.Display;

        void Commit(string text)
        {
            if (text.Trim() == rendered)
            {
                return;
            }

            workspace.Edit(field.Path, text);
            Render(host, shell, session);
        }

        switch (field.Kind)
        {
            case FieldKind.Toggle:
            {
                var box = new CheckBox
                {
                    IsChecked = string.Equals(view.Display, "true", StringComparison.OrdinalIgnoreCase),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 5),
                    Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
                };
                box.Click += (_, _) => Commit(box.IsChecked == true ? "true" : "false");
                return box;
            }

            case FieldKind.Choice:
            {
                var combo = new ComboBox
                {
                    ItemsSource = field.Choices,
                    SelectedItem = field.Choices?.FirstOrDefault(c =>
                        c.Contains(view.Display, StringComparison.OrdinalIgnoreCase)
                        || view.Display.Contains(c, StringComparison.OrdinalIgnoreCase)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string chosen)
                    {
                        Commit(chosen);
                    }
                };
                return combo;
            }

            default:
            {
                var box = new TextBox
                {
                    Text = view.Display,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 4),
                    Padding = new Thickness(6, 3, 6, 4),
                    Background = (Brush)Application.Current.Resources["Brush.Canvas"],
                    Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
                    BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
                };
                box.SetValue(
                    System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);

                // Commit on Enter or focus loss — never per keystroke, which
                // would reject "1" on the way to typing "12".
                box.LostFocus += (_, _) => Commit(box.Text);
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter)
                    {
                        Commit(box.Text);
                    }
                };
                return box;
            }
        }
    }

    private static UIElement DerivedCard(IReadOnlyList<DerivedReadout> readouts)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock
        {
            Text = "Derived",
            Margin = new Thickness(0, 0, 0, 4),
        }, "Text.Body"));
        panel.Children.Add(Styled(new TextBlock
        {
            Text = "Computed from the fields above. Nothing here is stored, so it cannot drift from the model.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        }, "Text.Secondary"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < readouts.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = readouts[i];

            var label = Styled(new TextBlock { Text = r.Label, Margin = new Thickness(0, 3, 8, 3) }, "Text.Secondary");
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var value = Styled(new TextBlock { Text = r.Value, Margin = new Thickness(0, 3, 8, 3) }, "Text.Body");
            value.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            var noteText = r.Warning ?? r.Note;
            if (noteText is not null)
            {
                var note = Styled(new TextBlock
                {
                    Text = (r.Warning is null ? "" : "⚠  ") + noteText,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3),
                }, "Text.Small");

                if (r.Warning is not null)
                {
                    note.Foreground = (Brush)Application.Current.Resources["Brush.Warning"];
                }

                Grid.SetRow(note, i);
                Grid.SetColumn(note, 2);
                grid.Children.Add(note);
            }
        }

        panel.Children.Add(grid);
        return Card(panel);
    }

    private static UIElement IssuesCard(IReadOnlyList<ModelIssue> issues)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock
        {
            Text = "Model checks",
            Margin = new Thickness(0, 0, 0, 10),
        }, "Text.Body"));

        foreach (var issue in issues)
        {
            var isError = issue.Severity == ModelIssueSeverity.Error;
            var line = Styled(new TextBlock
            {
                Text = $"{(isError ? "✕" : "⚠")}  {issue.Path}: {issue.Message}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = (Brush)Application.Current.Resources[isError ? "Brush.Danger" : "Brush.Warning"],
            }, "Text.Small");
            panel.Children.Add(line);
        }

        return Card(panel);
    }

    /// <summary>A provenance badge: colour, label AND glyph, so colour is never load-bearing (§8.11).</summary>
    private static UIElement Badge(ProvenanceEntry entry)
    {
        var (_, label, glyph) = DesignTokens.BadgeStyle(entry.Origin);
        var brushKey = entry.Origin switch
        {
            Provenance.Auto => "Brush.Info",
            Provenance.Wizard => "Brush.Accent",
            Provenance.You => "Brush.Success",
            Provenance.Imported => "Brush.Warning",
            Provenance.Optimised => "Brush.Accent",
            _ => "Brush.TextSecondary",
        };
        var brush = (Brush)Application.Current.Resources[brushKey];

        var tip = entry.Origin switch
        {
            Provenance.Auto when entry.Derivation is not null =>
                $"Auto — {entry.Derivation}" + (entry.Citation is null ? "" : $"\nSource: {entry.Citation}"),
            Provenance.Imported => $"Imported from {entry.SourceRef}. Never overwritten by the wizard.",
            Provenance.Optimised => $"Set by optimiser run {entry.SourceRef}. Never overwritten without opt-in.",
            Provenance.You => "You typed this. Never overwritten without opt-in.",
            _ => label,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph, Foreground = brush, FontSize = 11, Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = label, Foreground = brush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Child = panel,
            Padding = new Thickness(8, 2, 10, 3),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(1),
            BorderBrush = brush,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
            Margin = new Thickness(4, 0, 0, 0),
        };
    }

    // ---- Placeholder for the workspaces Phases 17–21 fill in -------------

    private static void RenderPlaceholder(Panel host, ShellViewModel shell)
    {
        var current = shell.Workspaces.First(w => w.Workspace == shell.Current);
        var phase = shell.Current switch
        {
            Workspace.Design => "Phase 17",
            Workspace.Sound => "Phase 20",
            Workspace.Results => "Phase 19",
            Workspace.Boost => "Phase 21",
            Workspace.Optimise => "Phase 22",
            _ => "a later phase",
        };

        host.Children.Add(Heading(current.Title, current.SubTabs.Count > 0
            ? string.Join("   ·   ", current.SubTabs)
            : "—"));

        var body = new StackPanel();
        body.Children.Add(Styled(new TextBlock
        {
            Text = $"This workspace's screens arrive in {phase}.",
            TextWrapping = TextWrapping.Wrap,
        }, "Text.Body"));
        body.Children.Add(Styled(new TextBlock
        {
            Text = "The physics behind it is already built, tested and reachable from the CLI — "
                 + "the shell, provenance, mode and job infrastructure landed first because the plan "
                 + "requires them before any screen can be safe to use.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        }, "Text.Secondary"));
        host.Children.Add(Card(body));
    }

    // ---- Small builders --------------------------------------------------

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
        Padding = new Thickness(20),
        Margin = new Thickness(0, 0, 0, 16),
    };

    private static UIElement Tile(string label, string value, string unit)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = label }, "Text.Caption"));
        var metric = Styled(new TextBlock { Text = value, Margin = new Thickness(0, 4, 0, 0) }, "Text.Body");
        metric.FontSize = 24;
        metric.FontWeight = FontWeights.SemiBold;
        metric.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        panel.Children.Add(metric);
        panel.Children.Add(Styled(new TextBlock { Text = unit }, "Text.Caption"));

        return new Border
        {
            Child = panel,
            Background = (Brush)Application.Current.Resources["Brush.Surface"],
            BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 190,
        };
    }

    private static UIElement LegendSwatch(string brushKey, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        panel.Children.Add(new Border
        {
            Width = 14, Height = 3, CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.Resources[brushKey],
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        panel.Children.Add(Styled(new TextBlock { Text = text }, "Text.Caption"));
        return panel;
    }

    private static UIElement Note(string text) => Card(Styled(
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, "Text.Secondary"));

    private static TextBlock Styled(TextBlock block, string styleKey, bool bold = false)
    {
        block.Style = (Style)Application.Current.Resources[styleKey];
        if (bold)
        {
            block.FontWeight = FontWeights.SemiBold;
        }

        return block;
    }
}
