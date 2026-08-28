namespace WaveBench.Boost;

/// <summary>A turbine operating point.</summary>
/// <param name="MassFlowKgPerS">Actual mass flow the turbine will pass.</param>
/// <param name="Efficiency">Total-to-static isentropic efficiency.</param>
/// <param name="OutletTemperatureK">T₄.</param>
/// <param name="PowerW">Shaft power produced.</param>
/// <param name="Region">Measured or extrapolated.</param>
public sealed record TurbinePointResult(
    double MassFlowKgPerS,
    double Efficiency,
    double OutletTemperatureK,
    double PowerW,
    MapRegion Region)
{
    public bool IsExtrapolated => Region != MapRegion.Measured;
}

/// <summary>
/// The turbine side (plan §4.3).
///
/// A turbine map is a swallowing characteristic: at a given expansion ratio it
/// will pass a certain corrected flow and no other. That makes the turbine the
/// component that SETS manifold pressure for a given exhaust flow, which is
/// the opposite of how people usually picture it — the engine does not push a
/// chosen boost through the turbine, the turbine decides what back-pressure
/// the engine has to work against.
/// </summary>
public static class TurbineModel
{
    /// <summary>Exhaust products, not air: heavier and less stiff.</summary>
    private const double Gamma = 1.33;

    private const double Cp = 1150.0;

    /// <summary>
    /// Solve the turbine at a given expansion ratio and shaft speed.
    /// </summary>
    /// <param name="map">The turbine map.</param>
    /// <param name="expansionRatio">p₀₃/p₄.</param>
    /// <param name="shaftRpm">Actual shaft speed.</param>
    /// <param name="inletTemperatureK">T₀₃.</param>
    /// <param name="outletPressureKPa">
    /// p₄, downstream of the turbine — ambient plus whatever the tailpipe adds.
    /// The turbine INLET pressure follows from it and the expansion ratio; it is
    /// not an independent input, and treating it as one is how a matching
    /// calculation ends up with corrected flows that do not correspond to any
    /// real operating point.
    /// </param>
    public static TurbinePointResult Solve(
        TurbineMap map,
        double expansionRatio,
        double shaftRpm,
        double inletTemperatureK,
        double outletPressureKPa)
    {
        ArgumentNullException.ThrowIfNull(map);

        var correctedSpeed = Corrected.Speed(shaftRpm, inletTemperatureK, map.Reference);
        var (correctedFlow, efficiency, region) = Read(map, expansionRatio, correctedSpeed);

        var inletPressureKPa = Math.Max(expansionRatio, 1.0) * outletPressureKPa;
        var massFlow = Corrected.ActualFlow(correctedFlow, inletTemperatureK, inletPressureKPa, map.Reference);

        // T₄ = T₀₃·[1 − η·(1 − ER^(−(γ−1)/γ))]
        var ideal = 1.0 - Math.Pow(Math.Max(expansionRatio, 1.0), -(Gamma - 1.0) / Gamma);
        var drop = inletTemperatureK * efficiency * ideal;
        var outlet = inletTemperatureK - drop;
        var power = massFlow * Cp * drop;

        return new TurbinePointResult(massFlow, efficiency, outlet, power, region);
    }

    /// <summary>
    /// Read corrected flow and efficiency off the map, with the physical
    /// closures of this class outside the measured range. Public because the
    /// unsteady rotor boundary reads the same map thousands of times per cycle
    /// and must get exactly the same numbers the steady matcher does.
    /// </summary>
    public static (double CorrectedFlow, double Efficiency, MapRegion Region) ReadMap(
        TurbineMap map, double expansionRatio, double correctedSpeed)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Read(map, expansionRatio, correctedSpeed);
    }

    private static (double CorrectedFlow, double Efficiency, MapRegion Region) Read(
        TurbineMap map, double expansionRatio, double correctedSpeed)
    {
        var lines = map.SpeedLines;
        var region = MapRegion.Measured;

        int lower, upper;
        double t;

        if (lines.Count == 1 || correctedSpeed <= lines[0].CorrectedRpm)
        {
            lower = upper = 0;
            t = 0;
            if (lines.Count > 1 && correctedSpeed < lines[0].CorrectedRpm * 0.999)
            {
                region = MapRegion.BelowLowestSpeed;
            }
        }
        else if (correctedSpeed >= lines[^1].CorrectedRpm)
        {
            lower = upper = lines.Count - 1;
            t = 0;
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
            t = (correctedSpeed - lines[lower].CorrectedRpm)
                / (lines[upper].CorrectedRpm - lines[lower].CorrectedRpm);
        }

        var (flowLow, etaLow, edgeLow) = OnLine(lines[lower], expansionRatio);
        var (flowHigh, etaHigh, edgeHigh) = OnLine(lines[upper], expansionRatio);

        var edge = edgeLow != MapRegion.Measured ? edgeLow
            : edgeHigh != MapRegion.Measured ? edgeHigh
            : region;

        return (flowLow + ((flowHigh - flowLow) * t), etaLow + ((etaHigh - etaLow) * t), edge);
    }

    /// <summary>
    /// Read one turbine speed line, with a physical closure outside its ends.
    /// </summary>
    private static (double CorrectedFlow, double Efficiency, MapRegion Region) OnLine(
        TurbineSpeedLine line, double expansionRatio)
    {
        var points = line.Points;

        if (expansionRatio <= points[0].ExpansionRatio)
        {
            // Below the measured range, down to ER = 1 where the flow must be
            // exactly zero — there is no pressure difference to drive it.
            // Published turbine maps almost never reach down here, and a
            // linear extension of the first two points crosses zero at some
            // arbitrary ER > 1 and then goes NEGATIVE, which is a turbine
            // pumping backwards.
            //
            // The swallowing characteristic is close to an orifice, so
            // ṁ ∝ √(1 − ER^(−2)) anchored on the first measured point holds
            // its shape and reaches zero in the right place.
            var anchor = points[0];
            var shape = Swallow(expansionRatio) / Swallow(anchor.ExpansionRatio);
            return (
                anchor.CorrectedFlowKgPerS * Math.Clamp(shape, 0.0, 1.0),
                anchor.Efficiency * Math.Clamp(shape, 0.1, 1.0),
                expansionRatio < points[0].ExpansionRatio * 0.999 ? MapRegion.BelowLowestSpeed : MapRegion.Measured);
        }

        if (expansionRatio >= points[^1].ExpansionRatio)
        {
            // Above it the nozzle chokes and the corrected flow stops rising.
            // A linear extension would keep adding flow a choked throat cannot
            // pass, which overstates turbine power exactly where a wastegate
            // decision is being made.
            return (points[^1].CorrectedFlowKgPerS, points[^1].Efficiency,
                expansionRatio > points[^1].ExpansionRatio * 1.001
                    ? MapRegion.BeyondChoke
                    : MapRegion.Measured);
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (expansionRatio <= points[i].ExpansionRatio)
            {
                var a = points[i - 1];
                var b = points[i];
                var f = (expansionRatio - a.ExpansionRatio) / (b.ExpansionRatio - a.ExpansionRatio);
                return (
                    a.CorrectedFlowKgPerS + ((b.CorrectedFlowKgPerS - a.CorrectedFlowKgPerS) * f),
                    a.Efficiency + ((b.Efficiency - a.Efficiency) * f),
                    MapRegion.Measured);
            }
        }

        return (points[^1].CorrectedFlowKgPerS, points[^1].Efficiency, MapRegion.Measured);
    }

    /// <summary>Orifice-like swallowing shape: √(1 − ER⁻²), zero at ER = 1.</summary>
    private static double Swallow(double expansionRatio)
    {
        var er = Math.Max(1.0, expansionRatio);
        return Math.Sqrt(Math.Max(0.0, 1.0 - (1.0 / (er * er))));
    }

    /// <summary>
    /// The expansion ratio at which the turbine swallows a given mass flow —
    /// the inverse problem, and the one that actually sets exhaust manifold
    /// pressure for an engine.
    /// </summary>
    public static double ExpansionRatioFor(
        TurbineMap map,
        double massFlowKgPerS,
        double shaftRpm,
        double inletTemperatureK,
        double outletPressureKPa)
    {
        ArgumentNullException.ThrowIfNull(map);

        // Flow rises monotonically with expansion ratio — the corrected flow
        // levels off at choke, but the actual flow keeps climbing because the
        // inlet density does — so bisection is safe.
        var lo = 1.0;
        var hi = 6.0;

        double FlowAt(double er) =>
            Solve(map, er, shaftRpm, inletTemperatureK, outletPressureKPa).MassFlowKgPerS;

        if (FlowAt(hi) < massFlowKgPerS)
        {
            // Choked: the turbine cannot pass this much however hard it is
            // pushed. Returning the ceiling rather than throwing lets the
            // caller report a flow-capacity limit, which is the useful answer.
            return hi;
        }

        for (var i = 0; i < 60 && hi - lo > 1e-6; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (FlowAt(mid) < massFlowKgPerS)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5 * (lo + hi);
    }
}
