using System.Globalization;
using System.Text;

namespace WaveBench.ViewModels.Plotting;

/// <summary>
/// Resolved colours for one theme, keyed by the token names a
/// <see cref="PlotModel"/> refers to.
///
/// The model names tokens rather than colours so a plot renders correctly in
/// either theme; the renderer supplies the values. For the app that comes from
/// the live resource dictionary, so an SVG matches what the user was looking
/// at when they pressed export.
/// </summary>
public sealed class PlotPalette
{
    private readonly Dictionary<string, string> _colours;

    public PlotPalette(IReadOnlyDictionary<string, string>? colours = null)
    {
        _colours = colours is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(colours, StringComparer.Ordinal);
    }

    /// <summary>
    /// A light-theme fallback so a plot exported from the CLI — where no
    /// resource dictionary exists — still comes out readable rather than
    /// black-on-black. These are the only literal colours outside Tokens.xaml
    /// and they exist for exactly that case.
    /// </summary>
    public static PlotPalette Default { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Brush.Canvas"] = "#FFFFFF",
        ["Brush.Surface"] = "#FFFFFF",
        ["Brush.SurfaceAlt"] = "#F4F5F7",
        ["Brush.BorderSubtle"] = "#E1E4E8",
        ["Brush.BorderStrong"] = "#9AA0A6",
        ["Brush.TextPrimary"] = "#1A1C1E",
        ["Brush.TextSecondary"] = "#5F6368",
        ["Brush.Accent"] = "#0B6BCB",
        ["Brush.Success"] = "#1E8E3E",
        ["Brush.Warning"] = "#C77700",
        ["Brush.Danger"] = "#C5221F",
        ["Brush.Info"] = "#7B5CD6",
    });

    public string Resolve(string token) =>
        _colours.TryGetValue(token, out var value) ? value
        : Default._colours.TryGetValue(token, out var fallback) ? fallback
        : "#5F6368";
}

/// <summary>
/// Renders a <see cref="PlotModel"/> to SVG (plan §7.2: every plot exports to
/// PNG and SVG).
///
/// Axes, gridlines, series, markers, legend and notes are true vector geometry.
/// A heat-map layer is embedded as a PNG data URI, because a field of 400
/// cells by 1440 frames is 576 000 rectangles and no SVG reader will open
/// that; the axes around it stay vector, so the figure still scales and its
/// text is still selectable and searchable.
/// </summary>
public static class SvgPlotWriter
{
    private const double PadLeft = 64;
    private const double PadRight = 68;
    private const double PadTop = 54;
    private const double PadBottomBase = 88;

    public static string Write(PlotModel plot, int width = 900, int height = 520, PlotPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(plot);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 120);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 120);

        var p = palette ?? PlotPalette.Default;

        // Notes sit under the legend, so the plot area gives way for them. A
        // fixed bottom padding ran a three-line note through the axis title.
        var padBottom = PadBottomBase + (plot.Notes.Count * 14);
        var plotW = width - PadLeft - (plot.RightAxis is null ? 24 : PadRight);
        var plotH = height - PadTop - padBottom;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{width}" height="{height}" viewBox="0 0 {width} {height}" font-family="Segoe UI, Inter, system-ui, sans-serif">""");
        svg.Append('\n');

        svg.Append(CultureInfo.InvariantCulture,
            $"""<rect width="{width}" height="{height}" fill="{p.Resolve("Brush.Canvas")}"/>""");
        svg.Append('\n');

        // Title block.
        svg.Append(CultureInfo.InvariantCulture,
            $"""<text x="{F(PadLeft)}" y="26" font-size="17" font-weight="600" fill="{p.Resolve("Brush.TextPrimary")}">{Escape(plot.Title)}</text>""");
        svg.Append('\n');
        if (plot.Subtitle.Length > 0)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(PadLeft)}" y="44" font-size="12" fill="{p.Resolve("Brush.TextSecondary")}">{Escape(plot.Subtitle)}</text>""");
            svg.Append('\n');
        }

        double X(double v) => PadLeft + (plotW * (v - plot.XAxis.Min) / NonZero(plot.XAxis.Span));
        double Y(double v) => PadTop + (plotH * (1.0 - ((v - plot.YAxis.Min) / NonZero(plot.YAxis.Span))));
        double YRight(double v) => plot.RightAxis is { } r
            ? PadTop + (plotH * (1.0 - ((v - r.Min) / NonZero(r.Span))))
            : Y(v);

        // Heat map first: everything else is drawn over it.
        if (plot.HeatMap is { } heat)
        {
            AppendHeatMap(svg, heat, PadLeft, PadTop, plotW, plotH);
        }

        AppendGrid(svg, plot, p, plotW, plotH, X, Y);

        // Series are clipped to the plot rectangle. A value far outside the
        // axis range is not a reason to redraw the axis — an order spectrum's
        // floor is −300 dB and an axis showing that would render everything
        // else flat — but it is also not licence to draw over the legend.
        svg.Append(CultureInfo.InvariantCulture,
            $"""<clipPath id="plot"><rect x="{F(PadLeft)}" y="{F(PadTop)}" width="{F(plotW)}" height="{F(plotH)}"/></clipPath>""");
        svg.Append('\n');
        svg.Append("""<g clip-path="url(#plot)">""");
        svg.Append('\n');

        foreach (var series in plot.Series)
        {
            AppendSeries(svg, series, p, plotH, X, series.RightAxis ? YRight : Y);
        }

        svg.Append("</g>\n");

        AppendMarkers(svg, plot, p, plotH, X);
        AppendYMarkers(svg, plot, p, plotW, Y);
        AppendAxisLabels(svg, plot, p, plotW, plotH, X, Y, YRight);
        AppendLegend(svg, plot, p, plotH, height);
        AppendNotes(svg, plot, p, height);

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void AppendGrid(
        StringBuilder svg, PlotModel plot, PlotPalette p, double plotW, double plotH,
        Func<double, double> x, Func<double, double> y)
    {
        var faint = p.Resolve("Brush.BorderSubtle");
        var strong = p.Resolve("Brush.BorderStrong");

        foreach (var tick in plot.YAxis.ResolvedTicks())
        {
            if (tick < plot.YAxis.Min || tick > plot.YAxis.Max)
            {
                continue;
            }

            var py = y(tick);
            svg.Append(CultureInfo.InvariantCulture,
                $"""<line x1="{F(PadLeft)}" y1="{F(py)}" x2="{F(PadLeft + plotW)}" y2="{F(py)}" stroke="{faint}" stroke-width="1"/>""");
            svg.Append('\n');
        }

        foreach (var tick in plot.XAxis.ResolvedTicks())
        {
            if (tick < plot.XAxis.Min || tick > plot.XAxis.Max)
            {
                continue;
            }

            var px = x(tick);
            svg.Append(CultureInfo.InvariantCulture,
                $"""<line x1="{F(px)}" y1="{F(PadTop)}" x2="{F(px)}" y2="{F(PadTop + plotH)}" stroke="{faint}" stroke-width="1"/>""");
            svg.Append('\n');
        }

        svg.Append(CultureInfo.InvariantCulture,
            $"""<rect x="{F(PadLeft)}" y="{F(PadTop)}" width="{F(plotW)}" height="{F(plotH)}" fill="none" stroke="{strong}" stroke-width="1"/>""");
        svg.Append('\n');
    }

    private static void AppendSeries(
        StringBuilder svg, PlotSeries series, PlotPalette p, double plotH,
        Func<double, double> x, Func<double, double> y)
    {
        if (series.X.Count == 0)
        {
            return;
        }

        var colour = p.Resolve(series.ColourToken);
        var count = Math.Min(series.X.Count, series.Y.Count);

        if (series.Kind == PlotSeriesKind.Bar)
        {
            // Width from the spacing, so a five-cylinder chart and a
            // thirteen-point sweep both look deliberate.
            var barWidth = count > 1
                ? Math.Abs(x(series.X[1]) - x(series.X[0])) * 0.62
                : 28.0;

            for (var i = 0; i < count; i++)
            {
                var top = y(series.Y[i]);
                var baseline = PadTop + plotH;
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<rect x="{F(x(series.X[i]) - (barWidth / 2))}" y="{F(Math.Min(top, baseline))}" width="{F(barWidth)}" height="{F(Math.Abs(baseline - top))}" fill="{colour}" fill-opacity="0.85"/>""");
                svg.Append('\n');
            }

            return;
        }

        if (series.Kind == PlotSeriesKind.Scatter)
        {
            for (var i = 0; i < count; i++)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<circle cx="{F(x(series.X[i]))}" cy="{F(y(series.Y[i]))}" r="2.6" fill="{colour}"/>""");
                svg.Append('\n');
            }

            return;
        }

        var dash = series.Kind switch
        {
            PlotSeriesKind.Dashed => """ stroke-dasharray="6 4""" + "\"",
            PlotSeriesKind.Dotted => """ stroke-dasharray="1.5 3""" + "\"",
            _ => "",
        };

        var points = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (!double.IsFinite(series.Y[i]))
            {
                continue;
            }

            points.Append(CultureInfo.InvariantCulture, $"{F(x(series.X[i]))},{F(y(series.Y[i]))} ");
        }

        svg.Append(CultureInfo.InvariantCulture,
            $"""<polyline points="{points.ToString().TrimEnd()}" fill="none" stroke="{colour}" stroke-width="2" stroke-linejoin="round"{dash}/>""");
        svg.Append('\n');
    }

    private static void AppendMarkers(
        StringBuilder svg, PlotModel plot, PlotPalette p, double plotH, Func<double, double> x)
    {
        foreach (var marker in plot.Markers)
        {
            if (marker.X < plot.XAxis.Min || marker.X > plot.XAxis.Max)
            {
                continue;
            }

            var px = x(marker.X);
            var colour = p.Resolve(marker.ColourToken);
            svg.Append(CultureInfo.InvariantCulture,
                $"""<line x1="{F(px)}" y1="{F(PadTop)}" x2="{F(px)}" y2="{F(PadTop + plotH)}" stroke="{colour}" stroke-width="1.2" stroke-dasharray="4 3"/>""");
            svg.Append('\n');
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(px + 4)}" y="{F(PadTop + 13)}" font-size="10.5" fill="{colour}">{Escape(marker.Label)}</text>""");
            svg.Append('\n');
        }
    }

    private static void AppendYMarkers(
        StringBuilder svg, PlotModel plot, PlotPalette p, double plotW, Func<double, double> y)
    {
        foreach (var marker in plot.YMarkers)
        {
            if (marker.X < plot.YAxis.Min || marker.X > plot.YAxis.Max)
            {
                continue;
            }

            var py = y(marker.X);
            var colour = p.Resolve(marker.ColourToken);
            svg.Append(CultureInfo.InvariantCulture,
                $"""<line x1="{F(PadLeft)}" y1="{F(py)}" x2="{F(PadLeft + plotW)}" y2="{F(py)}" stroke="{colour}" stroke-width="1.2" stroke-dasharray="4 3"/>""");
            svg.Append('\n');
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(PadLeft + plotW - 4)}" y="{F(py - 4)}" font-size="10.5" text-anchor="end" fill="{colour}">{Escape(marker.Label)}</text>""");
            svg.Append('\n');
        }
    }

    private static void AppendAxisLabels(
        StringBuilder svg, PlotModel plot, PlotPalette p, double plotW, double plotH,
        Func<double, double> x, Func<double, double> y, Func<double, double> yRight)
    {
        var text = p.Resolve("Brush.TextSecondary");

        foreach (var tick in plot.YAxis.ResolvedTicks())
        {
            if (tick < plot.YAxis.Min || tick > plot.YAxis.Max)
            {
                continue;
            }

            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(PadLeft - 8)}" y="{F(y(tick) + 4)}" font-size="11" text-anchor="end" fill="{text}">{Tick(tick)}</text>""");
            svg.Append('\n');
        }

        foreach (var tick in plot.XAxis.ResolvedTicks())
        {
            if (tick < plot.XAxis.Min || tick > plot.XAxis.Max)
            {
                continue;
            }

            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(x(tick))}" y="{F(PadTop + plotH + 18)}" font-size="11" text-anchor="middle" fill="{text}">{Tick(tick)}</text>""");
            svg.Append('\n');
        }

        svg.Append(CultureInfo.InvariantCulture,
            $"""<text x="{F(PadLeft + (plotW / 2))}" y="{F(PadTop + plotH + 38)}" font-size="12" text-anchor="middle" fill="{text}">{Escape(AxisTitle(plot.XAxis))}</text>""");
        svg.Append('\n');

        var yTitleX = 16.0;
        var yTitleY = PadTop + (plotH / 2);
        svg.Append(CultureInfo.InvariantCulture,
            $"""<text x="{F(yTitleX)}" y="{F(yTitleY)}" font-size="12" text-anchor="middle" fill="{text}" transform="rotate(-90 {F(yTitleX)} {F(yTitleY)})">{Escape(AxisTitle(plot.YAxis))}</text>""");
        svg.Append('\n');

        if (plot.RightAxis is { } right)
        {
            foreach (var tick in right.ResolvedTicks())
            {
                if (tick < right.Min || tick > right.Max)
                {
                    continue;
                }

                svg.Append(CultureInfo.InvariantCulture,
                    $"""<text x="{F(PadLeft + plotW + 8)}" y="{F(yRight(tick) + 4)}" font-size="11" fill="{text}">{Tick(tick)}</text>""");
                svg.Append('\n');
            }

            var rx = PadLeft + plotW + 52;
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(rx)}" y="{F(yTitleY)}" font-size="12" text-anchor="middle" fill="{text}" transform="rotate(90 {F(rx)} {F(yTitleY)})">{Escape(AxisTitle(right))}</text>""");
            svg.Append('\n');
        }
    }

    private static void AppendLegend(
        StringBuilder svg, PlotModel plot, PlotPalette p, double plotH, int height)
    {
        if (plot.Series.Count == 0)
        {
            return;
        }

        var yPos = PadTop + plotH + 52;
        var xPos = PadLeft;
        var text = p.Resolve("Brush.TextSecondary");

        foreach (var series in plot.Series)
        {
            var colour = p.Resolve(series.ColourToken);
            svg.Append(CultureInfo.InvariantCulture,
                $"""<rect x="{F(xPos)}" y="{F(yPos - 7)}" width="16" height="3" rx="1.5" fill="{colour}"/>""");
            svg.Append('\n');

            // Style named as well as shown: a legend that distinguishes series
            // by colour alone is unreadable in greyscale (plan §8.11).
            var label = $"{series.Name} — {series.StyleDescription}";
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(xPos + 22)}" y="{F(yPos)}" font-size="11" fill="{text}">{Escape(label)}</text>""");
            svg.Append('\n');

            xPos += 34 + (label.Length * 6.1);
            if (xPos > 700)
            {
                xPos = PadLeft;
                yPos += 16;
            }
        }

        _ = height;
    }

    private static void AppendNotes(StringBuilder svg, PlotModel plot, PlotPalette p, int height)
    {
        if (plot.Notes.Count == 0)
        {
            return;
        }

        var text = p.Resolve("Brush.TextSecondary");
        var y = height - 10.0 - ((plot.Notes.Count - 1) * 14.0);
        foreach (var note in plot.Notes)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{F(PadLeft)}" y="{F(y)}" font-size="10.5" fill="{text}">{Escape(note)}</text>""");
            svg.Append('\n');
            y += 14;
        }
    }

    private static void AppendHeatMap(
        StringBuilder svg, HeatMapLayer heat, double left, double top, double plotW, double plotH)
    {
        var rgba = new byte[heat.Columns * heat.Rows * 4];
        for (var row = 0; row < heat.Rows; row++)
        {
            // SVG y grows downward and the plot's y axis grows upward, so the
            // raster is written bottom-up. Getting this backwards mirrors the
            // wave diagram in time, which reverses every diagonal.
            var sourceRow = heat.Rows - 1 - row;
            for (var col = 0; col < heat.Columns; col++)
            {
                var (r, g, b) = HeatColour(heat.Normalised(heat.At(sourceRow, col)));
                var o = ((row * heat.Columns) + col) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = 255;
            }
        }

        var png = Convert.ToBase64String(PngWriter.Encode(rgba, heat.Columns, heat.Rows));
        svg.Append(CultureInfo.InvariantCulture,
            $"""<image x="{F(left)}" y="{F(top)}" width="{F(plotW)}" height="{F(plotH)}" preserveAspectRatio="none" image-rendering="auto" xlink:href="data:image/png;base64,{png}"/>""");
        svg.Append('\n');
    }

    /// <summary>
    /// Diverging blue–white–red about the midpoint. A wave field is signed
    /// about its undisturbed state, so a diverging scale puts the neutral
    /// value at a neutral colour and makes compressions and expansions
    /// distinguishable at a glance; a sequential scale would render both as
    /// "some amount of colour".
    /// </summary>
    public static (byte R, byte G, byte B) HeatColour(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        if (t < 0.5)
        {
            var k = t / 0.5;
            return ((byte)(35 + (220 * k)), (byte)(85 + (170 * k)), (byte)(180 + (75 * k)));
        }

        var j = (t - 0.5) / 0.5;
        return ((byte)(255 - (30 * j)), (byte)(255 - (200 * j)), (byte)(255 - (215 * j)));
    }

    private static string AxisTitle(PlotAxis axis) =>
        axis.Unit.Length > 0 ? $"{axis.Label} ({axis.Unit})" : axis.Label;

    private static double NonZero(double span) => Math.Abs(span) < 1e-12 ? 1.0 : span;

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Tick(double v) =>
        Math.Abs(v) >= 1000
            ? v.ToString("0.###", CultureInfo.InvariantCulture)
            : v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
