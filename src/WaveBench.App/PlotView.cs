using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.App;

/// <summary>
/// Draws a <see cref="PlotModel"/> on a WPF canvas.
///
/// The same model the SVG writer renders, so a figure exported to file is the
/// figure that was on screen. Anything this class decides — a scale, a
/// rounding, which points to skip — the SVG would not know about, so it
/// decides nothing: the model carries the axes, the ticks and the colours, and
/// this only maps them to pixels.
/// </summary>
public sealed class PlotView : Canvas
{
    private const double PadLeft = 64;
    private const double PadRightWithAxis = 68;
    private const double PadRightPlain = 24;
    private const double PadTop = 54;
    private const double PadBottomBase = 88;

    /// <summary>
    /// Notes sit under the legend, so the plot area has to give way for them.
    /// A fixed bottom padding put a three-line note straight through the x-axis
    /// title.
    /// </summary>
    private double PadBottom => PadBottomBase + (_model.Notes.Count * 14);

    private PlotModel _model;

    /// <summary>
    /// Set while series are being drawn. Its children are positioned relative
    /// to the plot's top-left rather than the canvas's, so they inherit the
    /// clip.
    /// </summary>
    private Canvas? _seriesLayer;

    public PlotView(PlotModel model)
    {
        _model = model;
        Background = (Brush)Application.Current.Resources["Brush.Surface"];
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    public PlotModel Model
    {
        get => _model;
        set
        {
            _model = value;
            Redraw();
        }
    }

    /// <summary>
    /// Resolve the live theme's brushes into the palette the SVG writer wants,
    /// so an export matches what the user is looking at rather than reverting
    /// to a built-in light scheme.
    /// </summary>
    public static PlotPalette CurrentPalette()
    {
        var tokens = new[]
        {
            "Brush.Canvas", "Brush.Surface", "Brush.SurfaceAlt", "Brush.BorderSubtle", "Brush.BorderStrong",
            "Brush.TextPrimary", "Brush.TextSecondary", "Brush.Accent", "Brush.Success", "Brush.Warning",
            "Brush.Danger", "Brush.Info",
        };

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (Application.Current.Resources[token] is SolidColorBrush brush)
            {
                var c = brush.Color;
                map[token] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }

        return new PlotPalette(map);
    }

    /// <summary>
    /// Render to PNG at a chosen scale. Uses the visual tree rather than
    /// re-drawing, so the file is pixel-for-pixel what was on screen.
    /// </summary>
    public byte[] ToPng(double scale = 2.0)
    {
        var w = (int)Math.Max(1, ActualWidth * scale);
        var h = (int)Math.Max(1, ActualHeight * scale);
        var target = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        target.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private Brush Token(string key) =>
        Application.Current.Resources[key] is Brush brush
            ? brush
            : (Brush)Application.Current.Resources["Brush.TextSecondary"];

    private void Redraw()
    {
        Children.Clear();

        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 80 || h < 80)
        {
            return;
        }

        var padRight = _model.RightAxis is null ? PadRightPlain : PadRightWithAxis;
        var plotW = w - PadLeft - padRight;
        var plotH = h - PadTop - PadBottom;
        if (plotW < 20 || plotH < 20)
        {
            return;
        }

        double X(double v) => PadLeft + (plotW * (v - _model.XAxis.Min) / NonZero(_model.XAxis.Span));
        double Y(double v) => PadTop + (plotH * (1.0 - ((v - _model.YAxis.Min) / NonZero(_model.YAxis.Span))));
        double YRight(double v) => _model.RightAxis is { } r
            ? PadTop + (plotH * (1.0 - ((v - r.Min) / NonZero(r.Span))))
            : Y(v);

        DrawTitle();
        DrawHeatMap(plotW, plotH);
        DrawGrid(plotW, plotH, X, Y);

        // Series are drawn into a clipped layer. A value far outside the axis
        // range is not a reason to redraw the axis — an order spectrum's floor
        // is −300 dB and an axis showing that would render everything else
        // flat — but it is also not licence to draw over the legend, which is
        // what an unclipped polyline does.
        _seriesLayer = new Canvas
        {
            Width = plotW,
            Height = plotH,
            Clip = new RectangleGeometry(new Rect(0, 0, plotW, plotH)),
        };
        Add(_seriesLayer, PadLeft, PadTop);

        foreach (var series in _model.Series)
        {
            DrawSeries(series, plotH, X, series.RightAxis ? YRight : Y);
        }

        _seriesLayer = null;
        DrawMarkers(plotH, X);
        DrawYMarkers(plotW, Y);
        DrawAxisLabels(plotW, plotH, X, Y, YRight);
        DrawLegend(plotH);
        DrawNotes(h);
    }

    private void DrawTitle()
    {
        Add(new TextBlock
        {
            Text = _model.Title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("Brush.TextPrimary"),
        }, PadLeft, 12);

        if (_model.Subtitle.Length > 0)
        {
            Add(new TextBlock
            {
                Text = _model.Subtitle,
                FontSize = 11.5,
                Foreground = Token("Brush.TextSecondary"),
            }, PadLeft, 33);
        }
    }

    private void DrawHeatMap(double plotW, double plotH)
    {
        if (_model.HeatMap is not { } heat || heat.Columns == 0 || heat.Rows == 0)
        {
            return;
        }

        // Built as a bitmap rather than as rectangles: a wave field is
        // hundreds of cells by hundreds of frames, and WPF will not keep 60 fps
        // with a hundred thousand Rectangles in a Canvas.
        var stride = heat.Columns * 4;
        var pixels = new byte[stride * heat.Rows];
        for (var row = 0; row < heat.Rows; row++)
        {
            // Top of the image is the LAST frame: the y axis grows upward and
            // the bitmap grows downward.
            var source = heat.Rows - 1 - row;
            for (var col = 0; col < heat.Columns; col++)
            {
                var (r, g, b) = SvgPlotWriter.HeatColour(heat.Normalised(heat.At(source, col)));
                var o = (row * stride) + (col * 4);
                pixels[o] = b;      // Bgra32
                pixels[o + 1] = g;
                pixels[o + 2] = r;
                pixels[o + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            heat.Columns, heat.Rows, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

        var image = new Image
        {
            Source = bitmap,
            Width = plotW,
            Height = plotH,
            Stretch = Stretch.Fill,
        };

        // Nearest-neighbour: a wave front is a real discontinuity and
        // smoothing it across cells invents gradient that is not in the
        // solution.
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Add(image, PadLeft, PadTop);
    }

    private void DrawGrid(double plotW, double plotH, Func<double, double> x, Func<double, double> y)
    {
        var faint = Token("Brush.BorderSubtle");

        foreach (var tick in _model.YAxis.ResolvedTicks())
        {
            if (tick < _model.YAxis.Min || tick > _model.YAxis.Max)
            {
                continue;
            }

            Children.Add(new Line
            {
                X1 = PadLeft, Y1 = y(tick), X2 = PadLeft + plotW, Y2 = y(tick),
                Stroke = faint, StrokeThickness = 1,
            });
        }

        foreach (var tick in _model.XAxis.ResolvedTicks())
        {
            if (tick < _model.XAxis.Min || tick > _model.XAxis.Max)
            {
                continue;
            }

            Children.Add(new Line
            {
                X1 = x(tick), Y1 = PadTop, X2 = x(tick), Y2 = PadTop + plotH,
                Stroke = faint, StrokeThickness = 1,
            });
        }

        var frame = new Rectangle
        {
            Width = plotW, Height = plotH,
            Stroke = Token("Brush.BorderStrong"), StrokeThickness = 1,
            Fill = Brushes.Transparent,
        };
        Add(frame, PadLeft, PadTop);
    }

    private void DrawSeries(PlotSeries series, double plotH, Func<double, double> x, Func<double, double> y)
    {
        var count = Math.Min(series.X.Count, series.Y.Count);
        if (count == 0)
        {
            return;
        }

        var brush = Token(series.ColourToken);

        switch (series.Kind)
        {
            case PlotSeriesKind.Bar:
            {
                var barWidth = count > 1 ? Math.Abs(x(series.X[1]) - x(series.X[0])) * 0.62 : 28.0;
                var baseline = PadTop + plotH;
                for (var i = 0; i < count; i++)
                {
                    if (!double.IsFinite(series.Y[i]))
                    {
                        continue;
                    }

                    var top = y(series.Y[i]);
                    AddSeries(new Rectangle
                    {
                        Width = barWidth,
                        Height = Math.Abs(baseline - top),
                        Fill = brush,
                        Opacity = 0.85,
                    }, x(series.X[i]) - (barWidth / 2), Math.Min(top, baseline));
                }

                return;
            }

            case PlotSeriesKind.Scatter:
            {
                for (var i = 0; i < count; i++)
                {
                    if (!double.IsFinite(series.Y[i]))
                    {
                        continue;
                    }

                    AddSeries(new Ellipse { Width = 6, Height = 6, Fill = brush },
                        x(series.X[i]) - 3, y(series.Y[i]) - 3);
                }

                return;
            }

            default:
            {
                var line = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeDashArray = series.Kind switch
                    {
                        PlotSeriesKind.Dashed => [3, 2],
                        PlotSeriesKind.Dotted => [0.8, 1.6],
                        _ => null,
                    },
                };

                for (var i = 0; i < count; i++)
                {
                    if (!double.IsFinite(series.Y[i]))
                    {
                        continue;
                    }

                    line.Points.Add(new Point(x(series.X[i]), y(series.Y[i])));
                }

                AddSeriesGeometry(line);
                return;
            }
        }
    }

    private void DrawMarkers(double plotH, Func<double, double> x)
    {
        foreach (var marker in _model.Markers)
        {
            if (marker.X < _model.XAxis.Min || marker.X > _model.XAxis.Max || marker.Label.Length == 0)
            {
                continue;
            }

            var brush = Token(marker.ColourToken);
            Children.Add(new Line
            {
                X1 = x(marker.X), Y1 = PadTop, X2 = x(marker.X), Y2 = PadTop + plotH,
                Stroke = brush, StrokeThickness = 1.2, StrokeDashArray = [4, 3],
            });

            Add(new TextBlock { Text = marker.Label, FontSize = 10.5, Foreground = brush },
                x(marker.X) + 4, PadTop + 4);
        }
    }

    /// <summary>
    /// Horizontal rules — valve events on the x–t diagram, where the y axis is
    /// crank angle.
    /// </summary>
    private void DrawYMarkers(double plotW, Func<double, double> y)
    {
        foreach (var marker in _model.YMarkers)
        {
            if (marker.X < _model.YAxis.Min || marker.X > _model.YAxis.Max || marker.Label.Length == 0)
            {
                continue;
            }

            var brush = Token(marker.ColourToken);
            var py = y(marker.X);
            Children.Add(new Line
            {
                X1 = PadLeft, Y1 = py, X2 = PadLeft + plotW, Y2 = py,
                Stroke = brush, StrokeThickness = 1.2, StrokeDashArray = [4, 3],
            });

            Add(new TextBlock
            {
                Text = marker.Label, FontSize = 10.5, Foreground = brush,
                TextAlignment = TextAlignment.Right, Width = 60,
            }, PadLeft + plotW - 64, py - 15);
        }
    }

    private void DrawAxisLabels(
        double plotW, double plotH, Func<double, double> x, Func<double, double> y, Func<double, double> yRight)
    {
        var text = Token("Brush.TextSecondary");

        foreach (var tick in _model.YAxis.ResolvedTicks())
        {
            if (tick < _model.YAxis.Min || tick > _model.YAxis.Max)
            {
                continue;
            }

            var label = Tabular(new TextBlock
            {
                Text = Tick(tick), FontSize = 11, Foreground = text,
                TextAlignment = TextAlignment.Right, Width = 48,
            });
            Add(label, PadLeft - 54, y(tick) - 8);
        }

        foreach (var tick in _model.XAxis.ResolvedTicks())
        {
            if (tick < _model.XAxis.Min || tick > _model.XAxis.Max)
            {
                continue;
            }

            var label = Tabular(new TextBlock
            {
                Text = Tick(tick), FontSize = 11, Foreground = text,
                TextAlignment = TextAlignment.Center, Width = 60,
            });
            Add(label, x(tick) - 30, PadTop + plotH + 6);
        }

        Add(new TextBlock
        {
            Text = AxisTitle(_model.XAxis), FontSize = 12, Foreground = text,
            TextAlignment = TextAlignment.Center, Width = plotW,
        }, PadLeft, PadTop + plotH + 26);

        var yTitle = new TextBlock
        {
            Text = AxisTitle(_model.YAxis), FontSize = 12, Foreground = text,
            RenderTransform = new RotateTransform(-90),
            RenderTransformOrigin = new Point(0, 0),
        };
        Add(yTitle, 18, PadTop + (plotH / 2) + 40);

        if (_model.RightAxis is { } right)
        {
            foreach (var tick in right.ResolvedTicks())
            {
                if (tick < right.Min || tick > right.Max)
                {
                    continue;
                }

                Add(Tabular(new TextBlock { Text = Tick(tick), FontSize = 11, Foreground = text }),
                    PadLeft + plotW + 8, yRight(tick) - 8);
            }

            var rightTitle = new TextBlock
            {
                Text = AxisTitle(right), FontSize = 12, Foreground = text,
                RenderTransform = new RotateTransform(90),
                RenderTransformOrigin = new Point(0, 0),
            };
            Add(rightTitle, PadLeft + plotW + 58, PadTop + (plotH / 2) - 40);
        }
    }

    private void DrawLegend(double plotH)
    {
        if (_model.Series.Count == 0)
        {
            return;
        }

        var panel = new WrapPanel { Width = Math.Max(100, ActualWidth - PadLeft - 20) };
        foreach (var series in _model.Series)
        {
            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 18, 2),
            };
            entry.Children.Add(new Border
            {
                Width = 16, Height = 3, CornerRadius = new CornerRadius(1.5),
                Background = Token(series.ColourToken),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });

            // Style named as well as shown: a legend that separates series by
            // colour alone is unreadable in greyscale (plan §8.11).
            entry.Children.Add(new TextBlock
            {
                Text = $"{series.Name} — {series.StyleDescription}",
                FontSize = 11,
                Foreground = Token("Brush.TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            panel.Children.Add(entry);
        }

        Add(panel, PadLeft, PadTop + plotH + 44);
    }

    private void DrawNotes(double height)
    {
        if (_model.Notes.Count == 0)
        {
            return;
        }

        var panel = new StackPanel { Width = Math.Max(100, ActualWidth - PadLeft - 20) };
        foreach (var note in _model.Notes)
        {
            panel.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Token("Brush.TextSecondary"),
            });
        }

        Add(panel, PadLeft, height - 12 - (_model.Notes.Count * 14));
    }

    private void Add(UIElement element, double left, double top)
    {
        SetLeft(element, left);
        SetTop(element, top);
        Children.Add(element);
    }

    /// <summary>
    /// Add a POSITIONED series element — a bar or a marker dot, whose shape
    /// carries no coordinates of its own. Left/top are in canvas space and are
    /// rebased onto the clipped layer.
    /// </summary>
    private void AddSeries(UIElement element, double left, double top)
    {
        if (_seriesLayer is null)
        {
            Add(element, left, top);
            return;
        }

        SetLeft(element, left - PadLeft);
        SetTop(element, top - PadTop);
        _seriesLayer.Children.Add(element);
    }

    /// <summary>
    /// Add series geometry whose points are ALREADY in canvas coordinates — a
    /// polyline. It is placed at minus the layer's own origin so the two
    /// offsets cancel; rebasing it like a positioned element instead applies
    /// the padding twice and slides the whole curve down and right, which is
    /// subtle enough on a plot with no reference marks to go unnoticed.
    /// </summary>
    private void AddSeriesGeometry(UIElement element)
    {
        if (_seriesLayer is null)
        {
            Children.Add(element);
            return;
        }

        SetLeft(element, -PadLeft);
        SetTop(element, -PadTop);
        _seriesLayer.Children.Add(element);
    }

    private static TextBlock Tabular(TextBlock block)
    {
        block.SetValue(
            System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        return block;
    }

    private static string AxisTitle(PlotAxis axis) =>
        axis.Unit.Length > 0 ? $"{axis.Label} ({axis.Unit})" : axis.Label;

    private static string Tick(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double NonZero(double span) => Math.Abs(span) < 1e-12 ? 1.0 : span;
}
