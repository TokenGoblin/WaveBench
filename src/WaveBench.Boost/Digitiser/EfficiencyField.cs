namespace WaveBench.Boost.Digitiser;

/// <summary>
/// The efficiency field reconstructed from traced islands.
///
/// A compressor map states efficiency as a handful of closed contours, so an
/// efficiency anywhere else has to be interpolated between them. This does it
/// radially about the peak: every island is reduced to a radius-versus-angle
/// profile, and a point's efficiency comes from where its radius falls between
/// the two contours bracketing it on the same ray.
///
/// Radial rather than nearest-contour because islands are nested and roughly
/// star-shaped about the peak, which is exactly the geometry a radial
/// parameterisation handles well; a nearest-boundary rule produces a visible
/// crease along the medial axis between two contours, and that crease lands on
/// the operating line often enough to matter.
///
/// It is an interpolation of the published contours and nothing more. Where a
/// point falls outside every contour the field says so rather than
/// extrapolating, because a map's outermost island is the edge of what the
/// manufacturer measured.
/// </summary>
internal sealed class EfficiencyField
{
    private const int Bins = 360;

    private readonly double _centreX;
    private readonly double _centreY;
    private readonly double _peakEfficiency;
    private readonly bool _hasPeak;

    /// <summary>Islands ordered innermost (highest efficiency) first.</summary>
    private readonly List<(double Efficiency, double[] Radius)> _islands;

    private EfficiencyField(
        double centreX, double centreY, double peakEfficiency, bool hasPeak,
        List<(double, double[])> islands)
    {
        _centreX = centreX;
        _centreY = centreY;
        _peakEfficiency = peakEfficiency;
        _hasPeak = hasPeak;
        _islands = islands;
    }

    public static EfficiencyField Build(
        RasterImage image,
        IReadOnlyList<EfficiencyIsland> islands,
        EfficiencyPeak? peak,
        List<string> warnings)
    {
        var traced = new List<(double Efficiency, List<(double X, double Y)> Pixels)>();

        foreach (var island in islands.OrderByDescending(i => i.Efficiency))
        {
            var pixels = MatchingPixels(image, island.Colour);
            if (pixels.Count < 8)
            {
                warnings.Add(
                    $"The {island.Efficiency:P0} efficiency contour matched {pixels.Count} pixel(s) and has been "
                    + "ignored. Check its colour key.");
                continue;
            }

            traced.Add((island.Efficiency, pixels));
        }

        if (traced.Count == 0)
        {
            if (peak is null)
            {
                throw new InvalidDataException(
                    "No efficiency contour could be traced and no peak efficiency was given, so there is nothing "
                    + "to build an efficiency field from. A compressor map without efficiency is not a compressor "
                    + "map — it cannot produce a compressor outlet temperature or a shaft power.");
            }

            warnings.Add(
                $"No efficiency contour was traced; every point takes the stated peak of {peak.Efficiency:P1}. "
                + "This OVERSTATES efficiency away from the peak and will overstate boost.");

            return new EfficiencyField(peak.PixelX, peak.PixelY, peak.Efficiency, true, []);
        }

        // The centre: the labelled peak if there is one, otherwise the centroid
        // of the innermost island. Everything is measured radially from here,
        // so a bad centre skews every interpolation — hence preferring the
        // user's own click over a computed one.
        var innermost = traced[0].Pixels;
        var cx = peak?.PixelX ?? innermost.Average(p => p.X);
        var cy = peak?.PixelY ?? innermost.Average(p => p.Y);

        var profiles = traced
            .Select(t => (t.Efficiency, RadialProfile(t.Pixels, cx, cy)))
            .ToList();

        // Nesting is an assumption of the interpolation, so check it rather
        // than let it fail silently as a non-monotonic efficiency field.
        for (var i = 1; i < profiles.Count; i++)
        {
            var inner = profiles[i - 1].Item2;
            var outer = profiles[i].Item2;
            var crossings = Enumerable.Range(0, Bins).Count(b => outer[b] < inner[b]);
            if (crossings > Bins / 10)
            {
                warnings.Add(
                    $"The {profiles[i].Efficiency:P0} contour lies inside the {profiles[i - 1].Efficiency:P0} one "
                    + $"over {crossings * 100 / Bins}% of directions. Either the colours are swapped or the "
                    + "islands are not nested; efficiencies between them are unreliable.");
            }
        }

        return new EfficiencyField(cx, cy, peak?.Efficiency ?? profiles[0].Efficiency, peak is not null, profiles);
    }

    /// <summary>
    /// Efficiency at a pixel, and whether it was inside the mapped region.
    /// </summary>
    public (double Efficiency, bool Inside) At(double px, double py)
    {
        if (_islands.Count == 0)
        {
            return (_peakEfficiency, true);
        }

        var dx = px - _centreX;
        var dy = py - _centreY;
        var r = Math.Sqrt((dx * dx) + (dy * dy));
        var bin = AngleBin(dx, dy);

        // Inside the innermost contour: interpolate down from the labelled
        // peak, which sits at r = 0. Without a labelled peak the innermost
        // contour's value is all the map states, so it holds flat.
        var rInner = _islands[0].Radius[bin];
        if (r <= rInner)
        {
            if (!_hasPeak)
            {
                return (_islands[0].Efficiency, true);
            }

            var f = rInner > 0 ? r / rInner : 0.0;
            return (_peakEfficiency + ((_islands[0].Efficiency - _peakEfficiency) * f), true);
        }

        for (var i = 1; i < _islands.Count; i++)
        {
            var rOuter = _islands[i].Radius[bin];
            if (r <= rOuter)
            {
                var rPrev = _islands[i - 1].Radius[bin];
                var span = rOuter - rPrev;
                var f = span > 1e-9 ? (r - rPrev) / span : 0.0;
                return (_islands[i - 1].Efficiency
                        + ((_islands[i].Efficiency - _islands[i - 1].Efficiency) * f), true);
            }
        }

        return (_islands[^1].Efficiency, false);
    }

    /// <summary>Every pixel matching a colour, with sub-pixel weighting discarded — position is what matters here.</summary>
    private static List<(double X, double Y)> MatchingPixels(RasterImage image, ColourKey colour)
    {
        var pixels = new List<(double, double)>();

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (r, g, b, a) = image.At(x, y);
                if (a >= 128 && colour.Weight(r, g, b) > 0.5)
                {
                    pixels.Add((x, y));
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// Reduce a traced contour to a mean radius per one-degree bin about the
    /// centre. Empty bins — a contour is only ever a thin curve, so gaps happen
    /// where it runs nearly radially — are filled by interpolating around the
    /// circle from the nearest occupied bins on each side.
    /// </summary>
    private static double[] RadialProfile(List<(double X, double Y)> pixels, double cx, double cy)
    {
        var sum = new double[Bins];
        var count = new int[Bins];

        foreach (var (x, y) in pixels)
        {
            var dx = x - cx;
            var dy = y - cy;
            var bin = AngleBin(dx, dy);
            sum[bin] += Math.Sqrt((dx * dx) + (dy * dy));
            count[bin]++;
        }

        var radius = new double[Bins];
        for (var b = 0; b < Bins; b++)
        {
            radius[b] = count[b] > 0 ? sum[b] / count[b] : double.NaN;
        }

        // Fill from a snapshot, not in place: filling bin b and then using that
        // filled value to fill bin b+1 propagates one interpolation into the
        // next and drags a long gap toward whichever end was filled first.
        var measured = (double[])radius.Clone();

        for (var b = 0; b < Bins; b++)
        {
            if (!double.IsNaN(measured[b]))
            {
                continue;
            }

            int before = 0, after = 0;
            while (before < Bins && double.IsNaN(measured[(((b - before - 1) % Bins) + Bins) % Bins]))
            {
                before++;
            }

            while (after < Bins && double.IsNaN(measured[(b + after + 1) % Bins]))
            {
                after++;
            }

            var lo = measured[(((b - before - 1) % Bins) + Bins) % Bins];
            var hi = measured[(b + after + 1) % Bins];
            var t = (before + 1.0) / (before + after + 2.0);
            radius[b] = lo + ((hi - lo) * t);
        }

        return radius;
    }

    private static int AngleBin(double dx, double dy)
    {
        var degrees = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        var bin = (int)Math.Floor(degrees) + 180;
        return Math.Clamp(bin, 0, Bins - 1);
    }
}
