namespace WaveBench.ViewModels.Plotting;

/// <summary>
/// Draws a <see cref="PlotModel"/> into a <see cref="PdfCanvas"/> — the third
/// renderer of the same figure, alongside <c>PlotView</c> on screen and
/// <see cref="SvgPlotWriter"/> for export.
///
/// <b>Same data, same decisions.</b> The axes come from the model rather than
/// being auto-scaled here, series name colour tokens rather than colours, and
/// every series carries a line style as well — so a report printed in black
/// and white is still readable, which is how a design-event submission is
/// usually read. A figure that differed from the screen is the bug the
/// data-first plot design exists to prevent.
///
/// A heat map is NOT drawn: 400 cells by 1440 frames is half a million
/// rectangles and a PDF of that will not open. Those figures are listed with
/// a note pointing at the SVG export, which says so rather than silently
/// omitting them.
/// </summary>
public static class PdfPlotWriter
{
    private const double PadLeft = 44;
    private const double PadRight = 46;
    private const double PadTop = 26;
    private const double PadBottom = 34;

    /// <summary>Height a figure needs, including its legend and notes.</summary>
    public static double HeightFor(PlotModel plot, double width = 480)
    {
        ArgumentNullException.ThrowIfNull(plot);

        var legendRows = Math.Max(1, (int)Math.Ceiling(plot.Series.Count / 3.0));
        return 190 + (legendRows * 11) + (plot.Notes.Count * 9.5);
    }

    public static void Draw(PdfCanvas canvas, PlotModel plot, PlotPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(plot);

        var p = palette ?? PlotPalette.Default;
        var ink = p.Resolve("Brush.TextPrimary");
        var muted = p.Resolve("Brush.TextSecondary");
        var grid = p.Resolve("Brush.BorderSubtle");

        // Notes sit at the bottom, the legend above them, and the plot frame
        // takes whatever is left — so a figure with four notes shrinks its
        // frame rather than overprinting them.
        var noteHeight = plot.Notes.Count * 9.5;
        var legendRows = Math.Max(1, (int)Math.Ceiling(plot.Series.Count / 3.0));
        var legendHeight = plot.Series.Count == 0 ? 0 : legendRows * 11;

        var frameBottom = noteHeight + legendHeight + PadBottom;
        var frameTop = canvas.Height - PadTop;
        var frameLeft = PadLeft;
        var frameRight = canvas.Width - (plot.RightAxis is null ? 12 : PadRight);
        var frameWidth = Math.Max(10, frameRight - frameLeft);
        var frameHeight = Math.Max(10, frameTop - frameBottom);

        canvas.Fill(ink);
        canvas.Text(0, canvas.Height - 10, plot.Title, 10.5, PdfFont.Bold);

        if (plot.Subtitle.Length > 0)
        {
            canvas.Fill(muted);
            canvas.Text(0, canvas.Height - 20, plot.Subtitle, 7.5);
        }

        double MapX(double v) => frameLeft + ((v - plot.XAxis.Min) / Span(plot.XAxis) * frameWidth);
        double MapY(PlotAxis axis, double v) => frameBottom + ((v - axis.Min) / Span(axis) * frameHeight);

        // ---- Grid and frame ------------------------------------------------
        canvas.LineWidth(0.4);
        canvas.Dash();
        canvas.Stroke(grid);

        foreach (var tick in plot.XAxis.ResolvedTicks())
        {
            var x = MapX(tick);
            canvas.Line(x, frameBottom, x, frameTop);
        }

        foreach (var tick in plot.YAxis.ResolvedTicks())
        {
            var y = MapY(plot.YAxis, tick);
            canvas.Line(frameLeft, y, frameRight, y);
        }

        canvas.Stroke(p.Resolve("Brush.BorderStrong"));
        canvas.LineWidth(0.6);
        canvas.Rect(frameLeft, frameBottom, frameWidth, frameHeight);

        // ---- Tick labels and axis titles -----------------------------------
        canvas.Fill(muted);

        foreach (var tick in plot.XAxis.ResolvedTicks())
        {
            canvas.Text(MapX(tick), frameBottom - 10, Tick(tick), 7, PdfFont.Regular, PdfAlign.Centre);
        }

        foreach (var tick in plot.YAxis.ResolvedTicks())
        {
            canvas.Text(frameLeft - 4, MapY(plot.YAxis, tick) - 2.5, Tick(tick), 7, PdfFont.Regular, PdfAlign.Right);
        }

        if (plot.RightAxis is { } right)
        {
            foreach (var tick in right.ResolvedTicks())
            {
                canvas.Text(frameRight + 4, MapY(right, tick) - 2.5, Tick(tick), 7);
            }

            canvas.TextVertical(frameRight + 34, frameBottom + (frameHeight / 2), AxisTitle(right), 7.5);
        }

        canvas.Text(frameLeft + (frameWidth / 2), frameBottom - 21, AxisTitle(plot.XAxis), 7.5,
            PdfFont.Regular, PdfAlign.Centre);
        canvas.TextVertical(8, frameBottom + (frameHeight / 2), AxisTitle(plot.YAxis), 7.5);

        // ---- Series ---------------------------------------------------------
        foreach (var series in plot.Series)
        {
            var axis = series.RightAxis && plot.RightAxis is not null ? plot.RightAxis : plot.YAxis;
            var colour = p.Resolve(series.ColourToken);
            var count = Math.Min(series.X.Count, series.Y.Count);

            var points = new List<(double X, double Y)>(count);
            for (var i = 0; i < count; i++)
            {
                points.Add((MapX(series.X[i]), MapY(axis, series.Y[i])));
            }

            canvas.Stroke(colour);
            canvas.Fill(colour);
            canvas.LineWidth(1.0);
            ApplyDash(canvas, series.Kind);

            switch (series.Kind)
            {
                case PlotSeriesKind.Scatter:
                    foreach (var (x, y) in points)
                    {
                        canvas.Dot(x, y, 1.6);
                    }

                    break;

                case PlotSeriesKind.Bar:
                    var barWidth = Math.Max(1.5, frameWidth / Math.Max(1, count) * 0.6);
                    foreach (var (x, y) in points.Where(pt => double.IsFinite(pt.X) && double.IsFinite(pt.Y)))
                    {
                        canvas.Rect(x - (barWidth / 2), frameBottom, barWidth, Math.Max(0, y - frameBottom), fill: true);
                    }

                    break;

                default:
                    canvas.Polyline(points);
                    break;
            }
        }

        canvas.Dash();

        // ---- Markers ---------------------------------------------------------
        canvas.LineWidth(0.6);
        foreach (var marker in plot.Markers)
        {
            var x = MapX(marker.X);
            if (x < frameLeft || x > frameRight)
            {
                continue;
            }

            canvas.Stroke(p.Resolve(marker.ColourToken));
            canvas.Dash(2, 2);
            canvas.Line(x, frameBottom, x, frameTop);

            if (marker.Label.Length > 0)
            {
                canvas.Fill(p.Resolve(marker.ColourToken));
                canvas.Text(x + 2, frameTop - 8, marker.Label, 6.5);
            }
        }

        foreach (var marker in plot.YMarkers)
        {
            var y = MapY(plot.YAxis, marker.X);
            if (y < frameBottom || y > frameTop)
            {
                continue;
            }

            canvas.Stroke(p.Resolve(marker.ColourToken));
            canvas.Dash(2, 2);
            canvas.Line(frameLeft, y, frameRight, y);

            if (marker.Label.Length > 0)
            {
                canvas.Fill(p.Resolve(marker.ColourToken));
                canvas.Text(frameLeft + 3, y + 2, marker.Label, 6.5);
            }
        }

        canvas.Dash();

        // ---- Legend ----------------------------------------------------------
        // The style name goes in the legend text, not only in the swatch: a
        // report is often read printed, where a colour swatch says nothing.
        var column = frameWidth / 3.0;
        for (var i = 0; i < plot.Series.Count; i++)
        {
            var series = plot.Series[i];
            var x = frameLeft + (i % 3 * column);
            var y = frameBottom - 30 - (i / 3 * 11);

            canvas.Stroke(p.Resolve(series.ColourToken));
            canvas.LineWidth(1.2);
            ApplyDash(canvas, series.Kind);
            canvas.Line(x, y + 2.5, x + 12, y + 2.5);
            canvas.Dash();

            canvas.Fill(ink);
            canvas.Text(x + 16, y, $"{series.Name} — {series.StyleDescription}", 7);
        }

        // ---- Notes ------------------------------------------------------------
        canvas.Fill(muted);
        for (var i = 0; i < plot.Notes.Count; i++)
        {
            canvas.Text(0, noteHeight - 9.5 - (i * 9.5), plot.Notes[i], 6.8);
        }
    }

    private static void ApplyDash(PdfCanvas canvas, PlotSeriesKind kind)
    {
        switch (kind)
        {
            case PlotSeriesKind.Dashed:
                canvas.Dash(4, 2.5);
                break;
            case PlotSeriesKind.Dotted:
                canvas.Dash(1, 2);
                break;
            default:
                canvas.Dash();
                break;
        }
    }

    private static double Span(PlotAxis axis) => Math.Abs(axis.Span) < 1e-12 ? 1.0 : axis.Span;

    private static string AxisTitle(PlotAxis axis) =>
        axis.Unit.Length == 0 ? axis.Label : $"{axis.Label} ({axis.Unit})";

    private static string Tick(double v) =>
        Math.Abs(v) >= 1000 ? v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        : v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
