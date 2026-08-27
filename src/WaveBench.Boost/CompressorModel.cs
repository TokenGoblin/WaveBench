namespace WaveBench.Boost;

/// <summary>Whether a map answer was measured, interpolated, or extrapolated.</summary>
public enum MapRegion
{
    /// <summary>Inside the measured envelope.</summary>
    Measured,

    /// <summary>Beyond the surge line — physically modelled, not spline-extended.</summary>
    BeyondSurge,

    /// <summary>Beyond the choke line.</summary>
    BeyondChoke,

    /// <summary>Below the lowest measured speed line, including windmilling.</summary>
    BelowLowestSpeed,

    /// <summary>Above the highest measured speed line.</summary>
    AboveHighestSpeed,
}

/// <summary>
/// A compressor operating point.
/// </summary>
/// <param name="PressureRatio">Total-to-total.</param>
/// <param name="Efficiency">Isentropic, 0–1.</param>
/// <param name="OutletTemperatureK">T₀₂.</param>
/// <param name="PowerW">Shaft power absorbed.</param>
/// <param name="SurgeMarginPercent">
/// How far the point sits from surge, as a percentage of the surge flow at
/// this speed. Negative means past it.
/// </param>
/// <param name="ChokeMarginPercent">The same toward choke.</param>
/// <param name="Region">Whether this came from measured data or from extrapolation.</param>
public sealed record CompressorPointResult(
    double PressureRatio,
    double Efficiency,
    double OutletTemperatureK,
    double PowerW,
    double SurgeMarginPercent,
    double ChokeMarginPercent,
    MapRegion Region)
{
    /// <summary>
    /// True where a plot must shade the point (plan §4.2: "shade extrapolated
    /// regions in every plot"). Presenting an extrapolation with the same
    /// weight as a measurement is the failure this exists to prevent.
    /// </summary>
    public bool IsExtrapolated => Region != MapRegion.Measured;

    public bool InSurge => SurgeMarginPercent < 0;
}

/// <summary>
/// The compressor side of a turbocharger, read off its map (plan §4.2).
///
/// <b>Interpolation is piecewise linear, deliberately.</b> The plan forbids "a
/// naive bicubic that overshoots near surge", and the reason is that an
/// overshoot on the surge side of a speed line invents pressure ratio the
/// compressor cannot make — which then shows up as an operating line that
/// clears surge when the real one does not. Linear cannot overshoot. The cost
/// is a small kink at each measured point, which is honest about where the
/// data actually is.
/// </summary>
public static class CompressorModel
{
    private const double Gamma = 1.4;
    private const double Cp = 1005.0;

    /// <summary>
    /// Solve the compressor at a given actual mass flow and shaft speed.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="massFlowKgPerS">Actual mass flow.</param>
    /// <param name="shaftRpm">Actual shaft speed.</param>
    /// <param name="inletTemperatureK">T₀₁.</param>
    /// <param name="inletPressureKPa">p₀₁.</param>
    public static CompressorPointResult Solve(
        CompressorMap map,
        double massFlowKgPerS,
        double shaftRpm,
        double inletTemperatureK,
        double inletPressureKPa)
    {
        ArgumentNullException.ThrowIfNull(map);

        var correctedFlow = Corrected.Flow(massFlowKgPerS, inletTemperatureK, inletPressureKPa, map.Reference);
        var correctedSpeed = Corrected.Speed(shaftRpm, inletTemperatureK, map.Reference);

        var (pressureRatio, efficiency, region, surgeFlow, chokeFlow) = Read(map, correctedFlow, correctedSpeed);

        // T₀₂ = T₀₁·[1 + (PR^((γ−1)/γ) − 1)/η_is]
        var ideal = Math.Pow(pressureRatio, (Gamma - 1.0) / Gamma) - 1.0;
        var outlet = inletTemperatureK * (1.0 + (ideal / efficiency));
        var power = massFlowKgPerS * Cp * (outlet - inletTemperatureK);

        // Margins as a percentage of the flow range's own ends, which is how
        // every turbo datasheet quotes them.
        var surgeMargin = surgeFlow > 0 ? (correctedFlow - surgeFlow) / surgeFlow * 100.0 : 0.0;
        var chokeMargin = chokeFlow > 0 ? (chokeFlow - correctedFlow) / chokeFlow * 100.0 : 0.0;

        return new CompressorPointResult(
            pressureRatio, efficiency, outlet, power, surgeMargin, chokeMargin, region);
    }

    /// <summary>
    /// Pressure ratio and efficiency at a corrected point, with the region it
    /// came from.
    /// </summary>
    private static (double PressureRatio, double Efficiency, MapRegion Region, double SurgeFlow, double ChokeFlow)
        Read(CompressorMap map, double correctedFlow, double correctedSpeed)
    {
        var region = MapRegion.Measured;

        // Bracket the speed. Outside the measured range the speed lines are
        // extended by the affinity laws rather than by extending a spline:
        // PR − 1 scales with N², flow with N. That is the physics of a
        // centrifugal stage and it degrades gracefully to zero speed, which a
        // spline does not.
        var lines = map.SpeedLines;
        int lower, upper;
        double scale;

        if (correctedSpeed <= lines[0].CorrectedRpm)
        {
            lower = upper = 0;
            scale = correctedSpeed / lines[0].CorrectedRpm;
            if (correctedSpeed < lines[0].CorrectedRpm * 0.999)
            {
                region = MapRegion.BelowLowestSpeed;
            }
        }
        else if (correctedSpeed >= lines[^1].CorrectedRpm)
        {
            lower = upper = lines.Count - 1;
            scale = correctedSpeed / lines[^1].CorrectedRpm;
            if (correctedSpeed > lines[^1].CorrectedRpm * 1.001)
            {
                region = MapRegion.AboveHighestSpeed;
            }
        }
        else
        {
            lower = 0;
            while (lines[lower + 1].CorrectedRpm < correctedSpeed)
            {
                lower++;
            }

            upper = lower + 1;
            scale = 1.0;
        }

        if (lower == upper)
        {
            var (pr, eta, singleEdge, surge, choke) = Affinity(lines[lower], correctedFlow, scale);
            return (pr, eta, singleEdge != MapRegion.Measured ? singleEdge : region, surge, choke);
        }

        var t = (correctedSpeed - lines[lower].CorrectedRpm)
                / (lines[upper].CorrectedRpm - lines[lower].CorrectedRpm);

        var (prLow, etaLow, edgeLow, surgeLow, chokeLow) = Affinity(lines[lower], correctedFlow, 1.0);
        var (prHigh, etaHigh, edgeHigh, surgeHigh, chokeHigh) = Affinity(lines[upper], correctedFlow, 1.0);

        var edge = edgeLow != MapRegion.Measured ? edgeLow
            : edgeHigh != MapRegion.Measured ? edgeHigh
            : MapRegion.Measured;

        return (
            prLow + ((prHigh - prLow) * t),
            etaLow + ((etaHigh - etaLow) * t),
            edge,
            surgeLow + ((surgeHigh - surgeLow) * t),
            chokeLow + ((chokeHigh - chokeLow) * t));
    }

    /// <summary>
    /// Read one speed line at a flow, optionally scaled to another speed by
    /// the affinity laws.
    ///
    /// Affinity rather than spline extension (plan §4.2, Serrano's approach):
    /// for a centrifugal stage the head rise goes as the square of tip speed
    /// and the flow linearly with it, so a line at 0.6× the measured speed is
    /// the measured line with flow × 0.6 and (PR − 1) × 0.36. That stays
    /// physical all the way to zero speed, where PR → 1 and the compressor
    /// windmills, which is precisely the region a transient needs and a spline
    /// gets wrong.
    /// </summary>
    private static (double PressureRatio, double Efficiency, MapRegion Region, double SurgeFlow, double ChokeFlow)
        Affinity(CompressorSpeedLine line, double correctedFlow, double speedScale)
    {
        var surge = line.SurgeFlow * speedScale;
        var choke = line.ChokeFlow * speedScale;
        var onLine = speedScale > 0 ? correctedFlow / speedScale : correctedFlow;

        var region = MapRegion.Measured;
        if (onLine < line.SurgeFlow)
        {
            region = MapRegion.BeyondSurge;
        }
        else if (onLine > line.ChokeFlow)
        {
            region = MapRegion.BeyondChoke;
        }

        var (pr, eta) = Interpolate(line, onLine);

        // (PR − 1) scales with speed², efficiency does not scale at all —
        // it is a ratio of like quantities and stays roughly constant along
        // corresponding points of scaled lines.
        var scaled = 1.0 + ((pr - 1.0) * speedScale * speedScale);

        return (Math.Max(1.0, scaled), eta, region, surge, choke);
    }

    /// <summary>
    /// Piecewise-linear along a speed line, with physical behaviour outside
    /// its ends rather than a linear continuation.
    /// </summary>
    private static (double PressureRatio, double Efficiency) Interpolate(
        CompressorSpeedLine line, double correctedFlow)
    {
        var points = line.Points;

        if (correctedFlow <= points[0].CorrectedFlowKgPerS)
        {
            // Left of surge. A real compressor's characteristic turns over and
            // falls away toward zero flow; continuing the measured slope would
            // keep raising pressure ratio into a region where the machine
            // cannot hold it, which flatters every surge margin near the line.
            //
            // The parabola through (0, PR_surge·0.5) and the surge point,
            // flat-topped at surge, is the standard closure and is what makes
            // a Moore–Greitzer surge cycle possible at all.
            var surgePr = points[0].PressureRatio;
            var surgeFlow = points[0].CorrectedFlowKgPerS;
            var f = surgeFlow > 0 ? Math.Max(0.0, correctedFlow) / surgeFlow : 0.0;
            var pr = 1.0 + ((surgePr - 1.0) * (0.5 + (0.5 * f * f)));
            return (pr, points[0].Efficiency * Math.Max(0.2, f));
        }

        if (correctedFlow >= points[^1].CorrectedFlowKgPerS)
        {
            // Right of choke: the speed line goes vertical. Pressure ratio
            // collapses steeply for a flow the machine cannot actually pass,
            // so this is reported as a steep fall rather than an extrapolated
            // flow — and the caller sees BeyondChoke.
            var chokePr = points[^1].PressureRatio;
            var chokeFlow = points[^1].CorrectedFlowKgPerS;
            var over = (correctedFlow - chokeFlow) / chokeFlow;
            return (Math.Max(1.0, chokePr * (1.0 - (3.0 * over))), points[^1].Efficiency * Math.Max(0.2, 1.0 - over));
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (correctedFlow <= points[i].CorrectedFlowKgPerS)
            {
                var a = points[i - 1];
                var b = points[i];
                var t = (correctedFlow - a.CorrectedFlowKgPerS)
                        / (b.CorrectedFlowKgPerS - a.CorrectedFlowKgPerS);
                return (
                    a.PressureRatio + ((b.PressureRatio - a.PressureRatio) * t),
                    a.Efficiency + ((b.Efficiency - a.Efficiency) * t));
            }
        }

        return (points[^1].PressureRatio, points[^1].Efficiency);
    }

    /// <summary>
    /// The shaft speed at which the compressor makes a target pressure ratio
    /// at a given flow — the inverse problem a matching calculation needs.
    /// Returns null when the map cannot reach it at any speed.
    /// </summary>
    public static double? SpeedFor(
        CompressorMap map,
        double targetPressureRatio,
        double massFlowKgPerS,
        double inletTemperatureK,
        double inletPressureKPa)
    {
        ArgumentNullException.ThrowIfNull(map);

        // Pressure ratio rises monotonically with speed at fixed flow, so a
        // bisection is safe and needs no derivative.
        var lo = map.LowestSpeed * 0.2;
        var hi = map.HighestSpeed * 1.5;

        double At(double rpm) =>
            Solve(map, massFlowKgPerS, Corrected.ActualSpeed(rpm, inletTemperatureK, map.Reference),
                inletTemperatureK, inletPressureKPa).PressureRatio;

        if (At(hi) < targetPressureRatio)
        {
            return null;
        }

        for (var i = 0; i < 60 && hi - lo > 1.0; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (At(mid) < targetPressureRatio)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Corrected.ActualSpeed(0.5 * (lo + hi), inletTemperatureK, map.Reference);
    }
}
