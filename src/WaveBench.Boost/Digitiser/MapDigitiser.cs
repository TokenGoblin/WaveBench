namespace WaveBench.Boost.Digitiser;

/// <summary>Whether an axis is drawn linearly or logarithmically.</summary>
public enum AxisScale
{
    Linear,
    Logarithmic,
}

/// <summary>
/// The pixel-to-value mapping for one axis, from two points the user clicks on
/// known gridlines.
///
/// Two ticks rather than "the plot rectangle" on purpose: map images are
/// cropped, skewed by scanning and often have their axes drawn inside the frame,
/// so the frame corners are not the axis limits. Two labelled gridlines are
/// something a user can identify with certainty, and any error in them is
/// visible as a constant offset rather than a subtle scale error.
/// </summary>
/// <param name="PixelA">Pixel coordinate of the first known gridline.</param>
/// <param name="ValueA">Its value.</param>
/// <param name="PixelB">Pixel coordinate of the second.</param>
/// <param name="ValueB">Its value.</param>
/// <param name="Scale">Linear, or logarithmic as turbine flow axes sometimes are.</param>
public sealed record AxisCalibration(
    double PixelA, double ValueA, double PixelB, double ValueB, AxisScale Scale = AxisScale.Linear)
{
    /// <summary>The value at a pixel coordinate.</summary>
    public double Value(double pixel)
    {
        var t = (pixel - PixelA) / (PixelB - PixelA);
        return Scale == AxisScale.Linear
            ? ValueA + ((ValueB - ValueA) * t)
            : Math.Exp(Math.Log(ValueA) + ((Math.Log(ValueB) - Math.Log(ValueA)) * t));
    }

    /// <summary>The pixel coordinate of a value.</summary>
    public double Pixel(double value)
    {
        var t = Scale == AxisScale.Linear
            ? (value - ValueA) / (ValueB - ValueA)
            : (Math.Log(value) - Math.Log(ValueA)) / (Math.Log(ValueB) - Math.Log(ValueA));
        return PixelA + ((PixelB - PixelA) * t);
    }

    public void Validate(string what)
    {
        if (Math.Abs(PixelB - PixelA) < 1.0)
        {
            throw new InvalidDataException(
                $"{what}: the two calibration gridlines are {Math.Abs(PixelB - PixelA):F2} pixels apart. "
                + "Pick ticks near opposite ends of the axis — close ones magnify every click error.");
        }

        if (Scale == AxisScale.Logarithmic && (ValueA <= 0 || ValueB <= 0))
        {
            throw new InvalidDataException($"{what}: a logarithmic axis cannot pass through zero or a negative value.");
        }
    }
}

/// <summary>Axis calibration for a whole plot: flow across, pressure ratio up.</summary>
/// <param name="X">Horizontal axis — corrected mass flow.</param>
/// <param name="Y">Vertical axis — pressure ratio. Pixel rows increase downwards, which the calibration absorbs.</param>
public sealed record PlotCalibration(AxisCalibration X, AxisCalibration Y)
{
    public void Validate()
    {
        X.Validate("Flow axis");
        Y.Validate("Pressure-ratio axis");
    }
}

/// <summary>
/// A colour to trace, with how far a pixel may stray and still count.
///
/// Tolerance is a Euclidean distance in RGB, which is crude colorimetrically
/// but is what matters here: it lets antialiased edges contribute partially,
/// and that partial contribution is where the sub-pixel accuracy comes from.
/// </summary>
public sealed record ColourKey(byte R, byte G, byte B, double Tolerance = 90.0)
{
    /// <summary>1 at an exact match, falling to 0 at the tolerance. Used as a tracing weight.</summary>
    public double Weight(byte r, byte g, byte b)
    {
        double dr = r - R, dg = g - G, db = b - B;
        var d = Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
        return d >= Tolerance ? 0.0 : 1.0 - (d / Tolerance);
    }
}

/// <summary>A constant-speed line to trace, and the corrected speed it is labelled with.</summary>
public sealed record SpeedLineTarget(double CorrectedRpm, ColourKey Colour);

/// <summary>
/// One efficiency contour, traced as a closed island.
///
/// Islands are nested — the highest efficiency innermost — and that nesting is
/// what makes interpolation between them meaningful.
/// </summary>
public sealed record EfficiencyIsland(double Efficiency, ColourKey Colour);

/// <summary>The peak the map labels, if it labels one: an island of zero radius.</summary>
/// <param name="Efficiency">Peak isentropic efficiency.</param>
/// <param name="PixelX">Where the peak sits, in pixels.</param>
/// <param name="PixelY">Likewise.</param>
public sealed record EfficiencyPeak(double Efficiency, double PixelX, double PixelY);

/// <summary>What a digitisation produced, and everything about it worth doubting.</summary>
/// <param name="Map">The map, ready to save or solve against.</param>
/// <param name="Warnings">Anything the tracer had to guess at. Never empty-by-design — read them.</param>
/// <param name="TracedPixelsPerLine">How many image columns carried each speed line. Thin evidence is visible here.</param>
public sealed record DigitiseResult(
    CompressorMap Map,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<(double CorrectedRpm, int Columns)> TracedPixelsPerLine);

/// <summary>
/// The map digitiser (plan §4.7): turn a compressor-map image into map data.
///
/// "Every builder has JPEGs of compressor maps and no way to use them." The
/// job is mechanical — calibrate two gridlines per axis, name the colour of
/// each speed line and each efficiency contour, and read the curves back out —
/// but it has to be honest about what it could not see, because a digitised
/// map that is quietly 5% wrong produces an operating line that looks entirely
/// reasonable and is not.
/// </summary>
public static class MapDigitiser
{
    /// <summary>
    /// Trace a map image into a <see cref="CompressorMap"/>.
    /// </summary>
    /// <param name="image">The decoded map image.</param>
    /// <param name="calibration">Pixel-to-value mapping for both axes.</param>
    /// <param name="reference">
    /// The conditions the printed map is referred to. Required, and read off the
    /// datasheet — never guessed. See <see cref="MapReference"/>.
    /// </param>
    /// <param name="speedLines">The constant-speed curves to trace, by colour.</param>
    /// <param name="islands">Efficiency contours, by colour. May be empty if <paramref name="peak"/> is given.</param>
    /// <param name="peak">The labelled peak efficiency, if the map states one.</param>
    /// <param name="name">Map name.</param>
    /// <param name="pointsPerLine">How many points to sample each traced curve down to.</param>
    /// <param name="provenance">Where the image came from. Recorded in the map.</param>
    public static DigitiseResult Digitise(
        RasterImage image,
        PlotCalibration calibration,
        MapReference reference,
        IReadOnlyList<SpeedLineTarget> speedLines,
        IReadOnlyList<EfficiencyIsland> islands,
        EfficiencyPeak? peak = null,
        string name = "Digitised map",
        int pointsPerLine = 12,
        string provenance = "")
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(speedLines);
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentOutOfRangeException.ThrowIfLessThan(pointsPerLine, 2);

        calibration.Validate();
        reference.Validate(name);

        var warnings = new List<string>();
        var field = EfficiencyField.Build(image, islands, peak, warnings);

        var lines = new List<CompressorSpeedLine>();
        var counts = new List<(double, int)>();

        foreach (var target in speedLines.OrderBy(s => s.CorrectedRpm))
        {
            var traced = TraceByColumn(image, target.Colour);
            counts.Add((target.CorrectedRpm, traced.Count));

            if (traced.Count < 2)
            {
                warnings.Add(
                    $"The {target.CorrectedRpm:F0} rpm line was found in {traced.Count} image column(s) and has "
                    + "been dropped. Check the colour and its tolerance.");
                continue;
            }

            lines.Add(Resample(traced, calibration, target, field, pointsPerLine, warnings));
        }

        if (lines.Count < 2)
        {
            throw new InvalidDataException(
                $"Only {lines.Count} speed line(s) could be traced. A map needs at least two to interpolate "
                + "between; check the colour keys against the image.");
        }

        var map = new CompressorMap
        {
            Name = name,
            Reference = reference,
            SpeedLines = lines,
            Provenance = string.IsNullOrWhiteSpace(provenance) ? "Digitised from an image" : provenance,
        };

        map.Validate();
        return new DigitiseResult(map, warnings, counts);
    }

    /// <summary>
    /// Trace one curve: for each image column, the intensity-weighted centre of
    /// the matching pixels.
    ///
    /// Weighting by colour closeness is what recovers sub-pixel position from a
    /// 2-pixel-wide antialiased line. Taking the topmost matching pixel instead
    /// — the obvious approach — biases every reading by half the line width,
    /// which on a typical map is a whole percent of pressure ratio.
    /// </summary>
    public static List<(double X, double Y)> TraceByColumn(RasterImage image, ColourKey colour)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(colour);

        var points = new List<(double, double)>();

        for (var x = 0; x < image.Width; x++)
        {
            double sum = 0, weighted = 0;

            for (var y = 0; y < image.Height; y++)
            {
                var (r, g, b, a) = image.At(x, y);
                if (a < 128)
                {
                    continue;
                }

                var w = colour.Weight(r, g, b);
                if (w <= 0)
                {
                    continue;
                }

                sum += w;
                weighted += w * y;
            }

            if (sum > 0)
            {
                points.Add((x, weighted / sum));
            }
        }

        return points;
    }

    /// <summary>
    /// Resample a traced pixel polyline onto evenly spaced flows, and read an
    /// efficiency for each point.
    /// </summary>
    private static CompressorSpeedLine Resample(
        List<(double X, double Y)> traced,
        PlotCalibration calibration,
        SpeedLineTarget target,
        EfficiencyField field,
        int pointsPerLine,
        List<string> warnings)
    {
        var flowLo = calibration.X.Value(traced[0].X);
        var flowHi = calibration.X.Value(traced[^1].X);

        if (flowHi < flowLo)
        {
            (flowLo, flowHi) = (flowHi, flowLo);
        }

        var points = new List<CompressorPoint>(pointsPerLine);
        var offMap = 0;

        for (var i = 0; i < pointsPerLine; i++)
        {
            var flow = flowLo + ((flowHi - flowLo) * i / (pointsPerLine - 1.0));
            var px = calibration.X.Pixel(flow);
            var py = InterpolateAtColumn(traced, px);
            var pr = calibration.Y.Value(py);

            var (efficiency, inside) = field.At(px, py);
            if (!inside)
            {
                offMap++;
            }

            points.Add(new CompressorPoint(flow, pr, efficiency));
        }

        if (offMap > 0)
        {
            warnings.Add(
                $"{offMap} of {pointsPerLine} points on the {target.CorrectedRpm:F0} rpm line fall outside every "
                + "efficiency contour; they carry the outermost contour's value, which OVERSTATES efficiency "
                + "there. Trim the line to the mapped region or trace a lower contour.");
        }

        return new CompressorSpeedLine(target.CorrectedRpm, points);
    }

    /// <summary>Linear interpolation of the traced polyline at an image column.</summary>
    private static double InterpolateAtColumn(List<(double X, double Y)> traced, double px)
    {
        if (px <= traced[0].X)
        {
            return traced[0].Y;
        }

        if (px >= traced[^1].X)
        {
            return traced[^1].Y;
        }

        var lo = 0;
        var hi = traced.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (traced[mid].X <= px)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var span = traced[hi].X - traced[lo].X;
        var t = span > 0 ? (px - traced[lo].X) / span : 0.0;
        return traced[lo].Y + ((traced[hi].Y - traced[lo].Y) * t);
    }
}
