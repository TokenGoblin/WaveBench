using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.App;

/// <summary>
/// The Sound workspace screens (plan Phase 20, §8.4).
///
/// Every figure comes from <see cref="SoundWorkspace"/>; this arranges them,
/// drives the A/B and rpm controls, and writes the audition to disk. The
/// header strip is the §8.4 layout: two named designs, a swap, and the
/// level-matched export.
/// </summary>
public static class SoundContent
{
    public static void Render(Panel host, ShellViewModel shell, ProjectSession session, SoundWorkspace sound)
    {
        // Clear FIRST. Every control below is handed a Refresh closure that
        // calls straight back into this method, so this is not only the entry
        // point from WorkspaceContent — it is also what a sub-tab, a speed
        // button and a silencer slider all call. Without the clear, each of
        // those appended a second complete copy of the workspace below the
        // first, inside a StackPanel, off the bottom of the viewport. The old
        // copy stayed exactly where it was and the app looked dead: you could
        // click Spectrum all day and the Timing charts never moved.
        host.Children.Clear();

        void Refresh() => Render(host, shell, session, sound);

        // Chrome above, redrawn body below, and the split is not cosmetic.
        //
        // A SLIDER MUST NOT LIVE IN THE PART THAT GETS REBUILT. Both sliders
        // here update on ValueChanged, and rebuilding the tree under a dragging
        // thumb disconnects it — WPF drops mouse capture when the capturing
        // element leaves the tree, the Thumb takes LostMouseCapture and cancels
        // the drag, and the replacement slider is a new instance holding
        // nothing. The user gets one step per click and no drag at all. This is
        // the same hazard CLAUDE.md records for the manifold canvas; making the
        // renderer clear its host is what exposed it here.
        //
        // So the sliders sit in the chrome and only `body` is rebuilt. Buttons
        // may live anywhere: destroying a button that has already raised Click
        // costs nothing.
        var body = new StackPanel();

        void RedrawBody()
        {
            body.Children.Clear();
            FillBody(body, sound);
        }

        host.Children.Add(Heading(
            "Sound",
            $"{sound.A.Name}   ⇄   {sound.B.Name}"));

        host.Children.Add(DesignStrip(sound, Refresh));
        host.Children.Add(SubTabs(sound, Refresh));
        host.Children.Add(SpeedStrip(sound, RedrawBody));

        if (sound.SelectedTab == SoundTab.Silencing)
        {
            host.Children.Add(SilencerSliders(sound, RedrawBody));
        }

        if (sound.SelectedTab == SoundTab.Intake)
        {
            host.Children.Add(IntakeSliders(sound, RedrawBody));
        }

        host.Children.Add(body);
        RedrawBody();
    }

    /// <summary>
    /// The tab body — everything a speed or geometry change has to redraw, and
    /// nothing that can be holding a mouse capture while it does.
    /// </summary>
    private static void FillBody(Panel body, SoundWorkspace sound)
    {
        switch (sound.SelectedTab)
        {
            case SoundTab.Timing:
                body.Children.Add(ExplainCard(sound));
                body.Children.Add(PlotCard(sound.TimingChart()));
                body.Children.Add(PlotCard(sound.Waterfall()));
                break;

            case SoundTab.Spectrum:
                body.Children.Add(PlotCard(sound.OrderSpectrumChart()));
                body.Children.Add(PlotCard(sound.CharacterRadar()));
                break;

            case SoundTab.Audition:
                body.Children.Add(AuditionCard(sound));
                break;

            case SoundTab.Silencing:
                // The sliders themselves are chrome; only the curve they drive
                // is redrawn.
                body.Children.Add(PlotCard(sound.TransmissionLoss()));
                break;

            case SoundTab.Intake:
                body.Children.Add(PlotCard(sound.IntakeAcousticProfile()));
                break;

            case SoundTab.Compliance:
            default:
                body.Children.Add(PendingCard(sound.SelectedTab));
                break;
        }
    }

    // ---- Header strip -----------------------------------------------------

    /// <summary>
    /// The §8.4 A/B header: which design the single-design figures show, and
    /// the one-keystroke swap.
    /// </summary>
    private static UIElement DesignStrip(SoundWorkspace sound, Action refresh)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        strip.Children.Add(DesignButton(sound.A.Name, !sound.ShowingB, () =>
        {
            sound.ShowingB = false;
            refresh();
        }));

        strip.Children.Add(Styled(new TextBlock
        {
            Text = "⇄",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        }, "Text.Secondary"));

        strip.Children.Add(DesignButton(sound.B.Name, sound.ShowingB, () =>
        {
            sound.ShowingB = true;
            refresh();
        }));

        return strip;
    }

    private static UIElement DesignButton(string label, bool selected, Action click)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(16, 6, 16, 7),
            Background = (Brush)Application.Current.Resources[selected ? "Brush.Accent" : "Brush.Surface"],
            Foreground = (Brush)Application.Current.Resources[selected ? "Brush.OnAccent" : "Brush.TextSecondary"],
            BorderBrush = (Brush)Application.Current.Resources["Brush.BorderSubtle"],
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static UIElement SubTabs(SoundWorkspace sound, Action refresh)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        foreach (var tab in Enum.GetValues<SoundTab>())
        {
            var selected = tab == sound.SelectedTab;
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
                sound.SelectedTab = target;
                refresh();
            };
            strip.Children.Add(button);
        }

        return strip;
    }

    /// <summary>
    /// The rpm slider. Every figure on this screen recomputes from geometry in
    /// single-digit milliseconds, so it can redraw on the drag rather than on
    /// release — which is the interaction plan §8.4 asks for.
    /// </summary>
    private static UIElement SpeedStrip(SoundWorkspace sound, Action redraw)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        strip.Children.Add(Styled(new TextBlock
        {
            Text = "Engine speed",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        }, "Text.Secondary"));

        var slider = new Slider
        {
            Minimum = 1000,
            Maximum = 8000,
            Value = sound.Rpm,
            Width = 380,
            TickFrequency = 250,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var readout = Styled(new TextBlock
        {
            Text = $"{sound.Rpm:F0} rpm",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            MinWidth = 90,
        }, "Text.Body");

        slider.ValueChanged += (_, e) =>
        {
            sound.Rpm = e.NewValue;
            readout.Text = $"{sound.Rpm:F0} rpm";
            redraw();
        };

        strip.Children.Add(slider);
        strip.Children.Add(readout);
        return strip;
    }

    // ---- Silencing --------------------------------------------------------

    /// <summary>
    /// Live geometry sliders over the TMM (plan §8.4's
    /// interactive-TMM-then-refine pattern). A 512-point sweep is a couple of
    /// milliseconds, so these redraw on the drag.
    /// </summary>
    private static UIElement SilencerSliders(SoundWorkspace sound, Action redraw)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = "Expansion chamber" }, "Text.Body"));

        panel.Children.Add(GeometrySlider("Pipe Ø", "mm", 30, 90, sound.PipeDiameterMm, v =>
        {
            sound.PipeDiameterMm = v;
            redraw();
        }));

        panel.Children.Add(GeometrySlider("Chamber Ø", "mm", 60, 250, sound.ChamberDiameterMm, v =>
        {
            sound.ChamberDiameterMm = v;
            redraw();
        }));

        panel.Children.Add(GeometrySlider("Chamber length", "mm", 80, 800, sound.ChamberLengthMm, v =>
        {
            sound.ChamberLengthMm = v;
            redraw();
        }));

        return Card(panel);
    }

    private static UIElement IntakeSliders(SoundWorkspace sound, Action redraw)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = "Intake duct and compressor" }, "Text.Body"));

        panel.Children.Add(GeometrySlider("Duct Ø", "mm", 30, 100, sound.IntakeDuctDiameterMm, v =>
        {
            sound.IntakeDuctDiameterMm = v;
            redraw();
        }));

        panel.Children.Add(GeometrySlider("Duct length", "mm", 100, 1200, sound.IntakeDuctLengthMm, v =>
        {
            sound.IntakeDuctLengthMm = v;
            redraw();
        }));

        panel.Children.Add(GeometrySlider("Turbo speed", "rpm", 40_000, 220_000, sound.TurboRpm, v =>
        {
            sound.TurboRpm = v;
            redraw();
        }));

        return Card(panel);
    }

    private static UIElement GeometrySlider(
        string label, string unit, double min, double max, double value, Action<double> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        row.Children.Add(Styled(new TextBlock
        {
            Text = label,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
        }, "Text.Secondary"));

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 340,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var readout = Styled(new TextBlock
        {
            Text = $"{value:F0} {unit}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            MinWidth = 80,
        }, "Text.Body");

        slider.ValueChanged += (_, e) =>
        {
            readout.Text = $"{e.NewValue:F0} {unit}";
            changed(e.NewValue);
        };

        row.Children.Add(slider);
        row.Children.Add(readout);
        return row;
    }

    // ---- Explain this -----------------------------------------------------

    private static UIElement ExplainCard(SoundWorkspace sound)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = "Explain this" }, "Text.Body"));
        panel.Children.Add(Styled(new TextBlock
        {
            Text = sound.Explain(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        }, "Text.Secondary"));
        panel.Children.Add(Styled(new TextBlock
        {
            Text = sound.CompareSummary(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        }, "Text.Caption"));
        return Card(panel);
    }

    // ---- Audition ---------------------------------------------------------

    private static UIElement AuditionCard(SoundWorkspace sound)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = "Level-matched A/B" }, "Text.Body"));

        AbAudition audition;
        try
        {
            audition = sound.AuditionPreview();
        }
        catch (ArgumentException e)
        {
            panel.Children.Add(Styled(new TextBlock
            {
                Text = e.Message, TextWrapping = TextWrapping.Wrap,
            }, "Text.Secondary"));
            return Card(panel);
        }

        panel.Children.Add(Styled(new TextBlock
        {
            Text = $"Both brought to −23 LUFS: {sound.A.Name} {audition.GainDbA:+0.0;-0.0} dB, "
                 + $"{sound.B.Name} {audition.GainDbB:+0.0;-0.0} dB. "
                 + $"Before matching, {sound.A.Name} was {audition.TrueDifferenceLu:+0.0;-0.0} LU "
                 + $"{(audition.TrueDifferenceLu >= 0 ? "louder" : "quieter")}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        }, "Text.Secondary"));

        panel.Children.Add(Styled(new TextBlock
        {
            Text = "Matched because a listener asked to choose between two exhausts picks the louder one "
                 + "almost every time — an unmatched comparison measures level, not character. The real "
                 + "difference is stated above rather than thrown away.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        }, "Text.Caption"));

        panel.Children.Add(Styled(new TextBlock
        {
            Text = $"Preview at {sound.Rpm:F0} rpm from the collector pulse train: it carries the order "
                 + "structure, which is what the comparison is about, and none of the radiation, "
                 + "propagation or mechanical layers. A solved render adds those.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        }, "Text.Caption"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ActionButton($"Export {sound.A.Name} (WAV)", () =>
            Save(audition.A, $"{Stem(sound.A.Name)}-matched")));
        buttons.Children.Add(ActionButton($"Export {sound.B.Name} (WAV)", () =>
            Save(audition.B, $"{Stem(sound.B.Name)}-matched")));
        buttons.Children.Add(ActionButton("Export A/B switch (WAV)", () =>
        {
            var quarter = sound.AuditionSeconds / 4.0;
            Save(audition.Render([quarter, quarter * 2, quarter * 3]), "ab-switch");
        }));

        panel.Children.Add(buttons);
        return Card(panel);
    }

    private static void Save(AudioStem stem, string stem_name)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = stem_name,
            Filter = "WAV audio|*.wav",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        WavWriter.Write(dialog.FileName, stem);
    }

    private static string Stem(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');

    // ---- Shared -----------------------------------------------------------

    private static UIElement PendingCard(SoundTab tab)
    {
        var body = new StackPanel();
        body.Children.Add(Styled(new TextBlock
        {
            Text = "Compliance needs a radiated sound pressure level at a microphone.",
            TextWrapping = TextWrapping.Wrap,
        }, "Text.Body"));
        body.Children.Add(Styled(new TextBlock
        {
            Text = "That comes from the propagation and radiation chain, which needs a solved run — the "
                 + "instant model on the other tabs carries order structure but not absolute level, and a "
                 + "compliance verdict computed from it would be a number with nothing behind it. The rule "
                 + "sets themselves are built and verified: FSAE 2024 and SAE J1287, with the derived "
                 + "test-speed formula and an explicit uncertainty band on every verdict.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        }, "Text.Secondary"));
        _ = tab;
        return Card(body);
    }

    private static UIElement PlotCard(PlotModel model)
    {
        var body = new StackPanel();
        var view = new PlotView(model) { Height = 340 };
        body.Children.Add(view);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        row.Children.Add(ActionButton("Export PNG", () =>
        {
            var path = AskWhereToSave(model.FileStem(), "PNG image|*.png");
            if (path is not null)
            {
                File.WriteAllBytes(path, view.ToPng());
            }
        }));
        row.Children.Add(ActionButton("Export SVG", () =>
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
        return Card(body);
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

    private static UIElement Heading(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
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

    private static TextBlock Styled(TextBlock block, string styleKey)
    {
        block.Style = (Style)Application.Current.Resources[styleKey];
        return block;
    }
}
