namespace WaveBench.ViewModels.Plotting;

/// <summary>How a series is drawn. Never colour alone (plan §8.11).</summary>
public enum PlotSeriesKind
{
    Line,
    Dashed,
    Dotted,
    Scatter,
    Bar,
}

/// <summary>
/// One series. <paramref name="ColourToken"/> names a resource key rather than
/// a colour, so the same model renders correctly in light and dark and the SVG
/// export matches whatever the user is looking at.
/// </summary>
public sealed record PlotSeries(
    string Name,
    IReadOnlyList<double> X,
    IReadOnlyList<double> Y,
    string ColourToken,
    PlotSeriesKind Kind = PlotSeriesKind.Line,
    bool RightAxis = false)
{
    /// <summary>Shown in the legend alongside the colour, so colour is never the only cue.</summary>
    public string StyleDescription => Kind switch
    {
        PlotSeriesKind.Line => "solid",
        PlotSeriesKind.Dashed => "dashed",
        PlotSeriesKind.Dotted => "dotted",
        PlotSeriesKind.Scatter => "points",
        PlotSeriesKind.Bar => "bars",
        _ => "",
    };
}

/// <summary>An axis with an explicit range — never auto-scaled per frame.</summary>
public sealed record PlotAxis(
    string Label,
    double Min,
    double Max,
    string Unit = "",
    IReadOnlyList<double>? Ticks = null)
{
    public double Span => Max - Min;

    /// <summary>
    /// Ticks as given, or a round set spanning the range. Round numbers matter
    /// more than a fixed count: an axis labelled 0, 2.5, 5 is read faster than
    /// one labelled 0, 2.37, 4.74 even though both are correct.
    /// </summary>
    public IReadOnlyList<double> ResolvedTicks(int target = 5)
    {
        if (Ticks is { Count: > 0 })
        {
            return Ticks;
        }

        if (Span <= 0 || !double.IsFinite(Span))
        {
            return [Min];
        }

        var raw = Span / Math.Max(target, 1);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalised = raw / magnitude;
        var step = normalised switch
        {
            <= 1.0 => 1.0,
            <= 2.0 => 2.0,
            <= 2.5 => 2.5,
            <= 5.0 => 5.0,
            _ => 10.0,
        } * magnitude;

        // Indexed rather than accumulated, and snapped to the step's own
        // precision. Neither alone is enough: repeated addition drifts, but so
        // does a single multiply, because a step of 0.2 is not exact in binary
        // and 3 × 0.2 is 0.6000000000000001. A tick is a label, and a label
        // that reads 0.6000000000000001 is a bug the formatter should not have
        // to hide.
        var decimals = Math.Clamp((int)Math.Max(0, -Math.Floor(Math.Log10(step))) + 3, 0, 12);
        var ticks = new List<double>();
        var first = Math.Ceiling(Min / step);
        for (var k = 0; ; k++)
        {
            var t = Math.Round((first + k) * step, decimals);
            if (t > Max + (step * 1e-9))
            {
                break;
            }

            ticks.Add(Math.Abs(t) < step * 1e-9 ? 0.0 : t);
        }

        return ticks;
    }
}

/// <summary>
/// A labelled vertical rule — a valve event on a crank-angle axis, a redline,
/// the arrival the wave decomposition found.
/// </summary>
public sealed record PlotMarker(double X, string Label, string ColourToken = "Brush.TextSecondary");

/// <summary>
/// A scalar field drawn as a heat map: the x–t wave diagram (plan §8.4).
/// </summary>
/// <param name="Values">Row-major, <paramref name="Rows"/> × <paramref name="Columns"/>.</param>
/// <param name="Columns">Samples along x (cells).</param>
/// <param name="Rows">Samples along y (frames).</param>
/// <param name="Min">Colour-scale minimum, over the WHOLE field.</param>
/// <param name="Max">Colour-scale maximum.</param>
/// <param name="Label">What the colour means, with its unit.</param>
public sealed record HeatMapLayer(
    IReadOnlyList<float> Values,
    int Columns,
    int Rows,
    float Min,
    float Max,
    string Label)
{
    public float At(int row, int column) => Values[(row * Columns) + column];

    /// <summary>Value mapped to 0–1 across the scale; 0.5 is the midpoint.</summary>
    public double Normalised(float value) =>
        Max > Min ? Math.Clamp((value - Min) / (double)(Max - Min), 0.0, 1.0) : 0.5;
}

/// <summary>
/// A plot described as DATA, so it can be drawn on screen and written to SVG
/// from one source (plan §7.2: "export every plot to PNG and SVG").
///
/// The alternative — a WPF drawing routine plus a separate export routine — is
/// the classic way for an export to drift from what the user saw, and a report
/// whose figures disagree with the screen is worse than no report. Everything
/// here is plain data with no UI types, which is also what makes plots
/// testable and exportable from the headless CLI.
/// </summary>
public sealed record PlotModel
{
    public required string Title { get; init; }

    public string Subtitle { get; init; } = "";

    public required PlotAxis XAxis { get; init; }

    public required PlotAxis YAxis { get; init; }

    /// <summary>
    /// Optional second scale. Two quantities on one axis prints the second
    /// against numbers a reader takes for the first, so anything with its own
    /// unit gets its own axis.
    /// </summary>
    public PlotAxis? RightAxis { get; init; }

    public IReadOnlyList<PlotSeries> Series { get; init; } = [];

    /// <summary>Vertical rules, positioned on the X axis.</summary>
    public IReadOnlyList<PlotMarker> Markers { get; init; } = [];

    /// <summary>
    /// Horizontal rules, positioned on the Y axis.
    ///
    /// The x–t wave diagram needs these and cannot use the vertical ones: its
    /// x axis is distance and its y axis is crank angle, so a valve event —
    /// the thing plan §8.4 asks to be overlaid — is a horizontal line at a
    /// crank angle, not a vertical one at a position down the pipe.
    /// </summary>
    public IReadOnlyList<PlotMarker> YMarkers { get; init; } = [];

    public HeatMapLayer? HeatMap { get; init; }

    /// <summary>
    /// Free text under the plot — the wave-decomposition annotation, the
    /// source of a correlation, a note that a curve is an estimate.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A filename stem, safe on every platform.</summary>
    public string FileStem()
    {
        var chars = Title.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var stem = new string(chars);
        while (stem.Contains("--", StringComparison.Ordinal))
        {
            stem = stem.Replace("--", "-", StringComparison.Ordinal);
        }

        return stem.Trim('-') is { Length: > 0 } trimmed ? trimmed : "plot";
    }
}
