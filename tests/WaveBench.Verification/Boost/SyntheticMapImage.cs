using WaveBench.Boost.Digitiser;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Renders <see cref="SyntheticTurbo"/>'s analytic compressor map as a plot
/// image, so the digitiser can be asked to read back a map whose true values
/// are known exactly.
///
/// It draws what a datasheet draws — antialiased curves and grey gridlines on
/// white, with no numbers or frame — and nothing the tracer is told about. The
/// gridlines are there on purpose: a tracer that picked them up would produce a
/// map with a horizontal line through it, and this is where that would show.
/// </summary>
internal static class SyntheticMapImage
{
    public const int Width = 900;

    public const int Height = 700;

    // The plot rectangle, in pixels. Flow runs left to right, pressure ratio
    // bottom to top — so the pixel row DECREASES as pressure ratio rises, which
    // the axis calibration absorbs by taking the two ticks as it finds them.
    private const double Left = 100.0;

    private const double Right = 860.0;

    private const double Bottom = 640.0;

    private const double Top = 60.0;

    private const double FlowMin = 0.0;

    private const double FlowMax = 0.35;

    private const double PrMin = 1.0;

    private const double PrMax = 3.6;

    /// <summary>Half-width of the drawn curve, pixels. About what a scanned datasheet line covers.</summary>
    private const double LineRadius = 2.2;

    public static double PixelX(double flow) => Left + ((flow - FlowMin) / (FlowMax - FlowMin) * (Right - Left));

    public static double PixelY(double pressureRatio) =>
        Bottom + ((pressureRatio - PrMin) / (PrMax - PrMin) * (Top - Bottom));

    /// <summary>
    /// The calibration a user would produce by clicking two labelled gridlines
    /// on each axis. Deliberately not the plot corners — see
    /// <see cref="AxisCalibration"/>.
    /// </summary>
    public static PlotCalibration Calibration() => new(
        new AxisCalibration(PixelX(0.05), 0.05, PixelX(0.30), 0.30),
        new AxisCalibration(PixelY(1.5), 1.5, PixelY(3.0), 3.0));

    // Seven mutually distinguishable colours. Every pair is at least 255 apart
    // in RGB, so at a tracing tolerance of 110 no antialiased blend of one can
    // be mistaken for another — and the grey gridlines are further than 110
    // from all of them, so they are invisible to the tracer.
    private static readonly (byte R, byte G, byte B)[] LineColours =
    [
        (255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 0, 0),
    ];

    private static readonly (byte R, byte G, byte B)[] IslandColours =
    [
        (255, 0, 255), (0, 255, 255), (255, 255, 0),
    ];

    public static readonly double[] IslandEfficiencies = [0.78, 0.74, 0.70];

    public const double TraceTolerance = 110.0;

    public static IReadOnlyList<SpeedLineTarget> SpeedLineTargets() =>
        SyntheticTurbo.SpeedFractions.Select((f, i) => new SpeedLineTarget(
            f * SyntheticTurbo.MaxCorrectedRpm,
            new ColourKey(LineColours[i].R, LineColours[i].G, LineColours[i].B, TraceTolerance))).ToList();

    public static IReadOnlyList<EfficiencyIsland> IslandTargets() =>
        IslandEfficiencies.Select((e, i) => new EfficiencyIsland(
            e,
            new ColourKey(IslandColours[i].R, IslandColours[i].G, IslandColours[i].B, TraceTolerance))).ToList();

    public static EfficiencyPeak Peak() => new(
        SyntheticTurbo.PeakEfficiency,
        PixelX(SyntheticTurbo.PeakFlow),
        PixelY(SyntheticTurbo.PeakPressureRatio));

    /// <summary>Render the map and encode it as a PNG.</summary>
    public static byte[] RenderPng()
    {
        var canvas = new byte[Width * Height * 4];
        Array.Fill(canvas, (byte)255);

        Gridlines(canvas);

        // Islands first, speed lines over them: where a speed line crosses a
        // contour it breaks the contour, exactly as a printed map does. The
        // digitiser's radial profile has to cope with those gaps.
        for (var i = 0; i < IslandEfficiencies.Length; i++)
        {
            DrawIsland(canvas, SyntheticTurbo.ContourRadius(IslandEfficiencies[i]), IslandColours[i]);
        }

        for (var i = 0; i < SyntheticTurbo.SpeedFractions.Length; i++)
        {
            DrawSpeedLine(canvas, SyntheticTurbo.SpeedFractions[i], LineColours[i]);
        }

        return PngWriter.Encode(canvas, Width, Height);
    }

    private static void Gridlines(byte[] canvas)
    {
        foreach (var flow in new[] { 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.35 })
        {
            var x = (int)Math.Round(PixelX(flow));
            for (var y = (int)Top; y <= (int)Bottom; y++)
            {
                Set(canvas, x, y, 200, 200, 200);
            }
        }

        foreach (var pr in new[] { 1.0, 1.5, 2.0, 2.5, 3.0, 3.5 })
        {
            var y = (int)Math.Round(PixelY(pr));
            for (var x = (int)Left; x <= (int)Right; x++)
            {
                Set(canvas, x, y, 200, 200, 200);
            }
        }
    }

    private static void DrawSpeedLine(byte[] canvas, double speedFraction, (byte R, byte G, byte B) colour)
    {
        var lo = SyntheticTurbo.SurgeFlow(speedFraction);
        var hi = SyntheticTurbo.ChokeFlow(speedFraction);

        var coverage = new double[Width * Height];
        const int samples = 4000;

        for (var i = 0; i <= samples; i++)
        {
            var u = i / (double)samples;
            var flow = lo + ((hi - lo) * u);
            Stamp(coverage, PixelX(flow), PixelY(SyntheticTurbo.PressureRatio(speedFraction, u)));
        }

        Composite(canvas, coverage, colour);
    }

    private static void DrawIsland(byte[] canvas, double rho, (byte R, byte G, byte B) colour)
    {
        var coverage = new double[Width * Height];
        const int samples = 6000;

        for (var i = 0; i < samples; i++)
        {
            var theta = 2.0 * Math.PI * i / samples;
            var flow = SyntheticTurbo.PeakFlow + (SyntheticTurbo.FlowRadius * rho * Math.Cos(theta));
            var pr = SyntheticTurbo.PeakPressureRatio
                     + (SyntheticTurbo.PressureRatioRadius * rho * Math.Sin(theta));
            Stamp(coverage, PixelX(flow), PixelY(pr));
        }

        Composite(canvas, coverage, colour);
    }

    /// <summary>
    /// A symmetric tent about the exact curve position. Symmetry is the whole
    /// point: the tracer takes a weighted centroid, so as long as the ink is
    /// laid down symmetrically about the true value it recovers a sub-pixel
    /// position from a line several pixels wide.
    /// </summary>
    private static void Stamp(double[] coverage, double cx, double cy)
    {
        var x0 = (int)Math.Floor(cx - LineRadius);
        var x1 = (int)Math.Ceiling(cx + LineRadius);
        var y0 = (int)Math.Floor(cy - LineRadius);
        var y1 = (int)Math.Ceiling(cy + LineRadius);

        for (var y = y0; y <= y1; y++)
        {
            if ((uint)y >= Height)
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                if ((uint)x >= Width)
                {
                    continue;
                }

                var dx = x - cx;
                var dy = y - cy;
                var d = Math.Sqrt((dx * dx) + (dy * dy));
                var c = Math.Max(0.0, 1.0 - (d / LineRadius));

                var index = (y * Width) + x;
                if (c > coverage[index])
                {
                    coverage[index] = c;
                }
            }
        }
    }

    private static void Composite(byte[] canvas, double[] coverage, (byte R, byte G, byte B) colour)
    {
        for (var i = 0; i < coverage.Length; i++)
        {
            var c = coverage[i];
            if (c <= 0)
            {
                continue;
            }

            var o = i * 4;
            canvas[o] = (byte)Math.Round((canvas[o] * (1 - c)) + (colour.R * c));
            canvas[o + 1] = (byte)Math.Round((canvas[o + 1] * (1 - c)) + (colour.G * c));
            canvas[o + 2] = (byte)Math.Round((canvas[o + 2] * (1 - c)) + (colour.B * c));
            canvas[o + 3] = 255;
        }
    }

    private static void Set(byte[] canvas, int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x >= Width || (uint)y >= Height)
        {
            return;
        }

        var o = (((y * Width) + x) * 4);
        canvas[o] = r;
        canvas[o + 1] = g;
        canvas[o + 2] = b;
        canvas[o + 3] = 255;
    }
}
