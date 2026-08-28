using WaveBench.Boost;

namespace WaveBench.Verification.Boost;

/// <summary>
/// A synthetic turbocharger with an analytic map, in the size class of a
/// 60 mm-wheel unit for a 2-litre engine.
///
/// <b>Synthetic on purpose.</b> Plan §4.7 forbids shipping manufacturer maps
/// without written permission, and that applies to the test suite as much as to
/// the database. It also makes for a better verification anchor than a real map
/// would: the underlying surface is a closed-form function, so a test can ask
/// what the answer should be rather than comparing against a digitised reading
/// with its own error. The numbers are representative, not any real product.
/// </summary>
internal static class SyntheticTurbo
{
    // ---- Compressor, in corrected quantities against SAE J1826 -------------

    public const double MaxCorrectedRpm = 150_000.0;

    /// <summary>Surge-end corrected flow of a speed line, kg/s. Rises with speed as the stage stiffens.</summary>
    public static double SurgeFlow(double speedFraction) => 0.05 + (0.10 * speedFraction * speedFraction);

    /// <summary>Choke-end corrected flow, kg/s.</summary>
    public static double ChokeFlow(double speedFraction) => 0.11 + (0.20 * speedFraction);

    /// <summary>Pressure ratio at the surge end. Euler work goes as tip speed squared, hence N².</summary>
    public static double SurgePressureRatio(double speedFraction) => 1.0 + (2.4 * speedFraction * speedFraction);

    /// <summary>
    /// Pressure ratio along a speed line, as a function of position from surge
    /// (u = 0) to choke (u = 1). Nearly flat at the surge end and steepening
    /// toward choke, which is the shape of a real centrifugal characteristic.
    /// </summary>
    public static double PressureRatio(double speedFraction, double u)
    {
        var surge = SurgePressureRatio(speedFraction);

        // Odd extension of u^1.8 through zero. A drawn curve reads a fraction of
        // a pixel wide at its ends, so a digitised line legitimately starts a
        // hair before u = 0 — and Math.Pow of a negative base with a fractional
        // exponent is NaN, which would make the comparison unanswerable rather
        // than merely slightly extrapolated.
        var shape = Math.CopySign(Math.Pow(Math.Abs(u), 1.8), u);
        return surge - ((surge - 1.0) * 0.45 * shape);
    }

    /// <summary>Pressure ratio at a given corrected flow on a given speed line.</summary>
    public static double PressureRatioAtFlow(double speedFraction, double flow)
    {
        var lo = SurgeFlow(speedFraction);
        var hi = ChokeFlow(speedFraction);
        return PressureRatio(speedFraction, (flow - lo) / (hi - lo));
    }

    /// <summary>
    /// The efficiency field, as concentric contours in the (flow, PR) plane.
    ///
    /// η = η_peak − k·ρ², with ρ the elliptical radius about the peak. Quadratic
    /// because that is what an efficiency island looks like near its own peak,
    /// and because it guarantees the contours nest — which is the assumption
    /// the digitiser's interpolation rests on, so a test of the digitiser
    /// should not also be testing whether that assumption holds.
    /// </summary>
    public const double PeakEfficiency = 0.80;

    public const double PeakFlow = 0.19;

    public const double PeakPressureRatio = 2.40;

    public const double FlowRadius = 0.1520;      // ρ = 1 at this flow offset

    public const double PressureRatioRadius = 1.3448;

    private const double Curvature = 0.10;

    public static double EfficiencyRadius(double flow, double pressureRatio)
    {
        var a = (flow - PeakFlow) / FlowRadius;
        var b = (pressureRatio - PeakPressureRatio) / PressureRatioRadius;
        return Math.Sqrt((a * a) + (b * b));
    }

    public static double Efficiency(double flow, double pressureRatio)
    {
        var rho = EfficiencyRadius(flow, pressureRatio);
        return PeakEfficiency - (Curvature * rho * rho);
    }

    /// <summary>The elliptical radius of a given efficiency contour.</summary>
    public static double ContourRadius(double efficiency) =>
        Math.Sqrt((PeakEfficiency - efficiency) / Curvature);

    public static readonly double[] SpeedFractions = [0.60, 0.7333, 0.8667, 1.00];

    public static CompressorMap Compressor(int pointsPerLine = 9) => new()
    {
        Name = "Synthetic 60 mm compressor",
        Reference = MapReference.SaeJ1826,
        MaxSpeedRpm = 165_000,
        Provenance = "Analytic test map — not a product. See SyntheticTurbo.",
        SpeedLines = SpeedFractions.Select(f =>
        {
            var lo = SurgeFlow(f);
            var hi = ChokeFlow(f);
            var points = Enumerable.Range(0, pointsPerLine).Select(i =>
            {
                var u = i / (pointsPerLine - 1.0);
                var flow = lo + ((hi - lo) * u);
                var pr = PressureRatio(f, u);
                return new CompressorPoint(flow, pr, Efficiency(flow, pr));
            }).ToList();

            return new CompressorSpeedLine(f * MaxCorrectedRpm, points);
        }).ToList(),
    };

    /// <summary>
    /// The same analytic surface scaled down to an FSAE-sized unit: a
    /// restricted engine draws about 0.07 kg/s, and the 60 mm map above is
    /// sized for three times that. Putting a restricted engine on the wrong map
    /// makes every operating point read as surge, which is true of the real
    /// mismatch and useless for showing anything else.
    ///
    /// Geometric scaling: flow by the area ratio, speed by 1/√(flow scale) so
    /// tip speed and therefore pressure ratio are preserved.
    /// </summary>
    public static CompressorMap FsaeCompressor(int pointsPerLine = 9)
    {
        const double flowScale = 0.34;
        var speedScale = 1.0 / Math.Sqrt(flowScale);
        var baseline = Compressor(pointsPerLine);

        return new CompressorMap
        {
            Name = "Synthetic 40 mm compressor (FSAE class)",
            Reference = MapReference.SaeJ1826,
            MaxSpeedRpm = baseline.MaxSpeedRpm * speedScale,
            Provenance = "Analytic test map — not a product. See SyntheticTurbo.",
            SpeedLines = baseline.SpeedLines.Select(l => new CompressorSpeedLine(
                l.CorrectedRpm * speedScale,
                l.Points.Select(p => p with { CorrectedFlowKgPerS = p.CorrectedFlowKgPerS * flowScale })
                    .ToList())).ToList(),
        };
    }

    // ---- Turbine ----------------------------------------------------------

    /// <summary>Corrected flow at ER = 3, kg/s: the choked capacity of the nozzle.</summary>
    private const double TurbineChokedFlow = 0.235;

    public static readonly double[] TurbineSpeeds = [40_000.0, 60_000.0, 80_000.0];

    /// <summary>
    /// The swallowing characteristic: orifice-like in expansion ratio, with a
    /// mild fall-off as wheel speed rises and the relative flow angle worsens.
    /// </summary>
    public static double TurbineCorrectedFlow(double correctedRpm, double expansionRatio)
    {
        var shape = Math.Sqrt(Math.Max(0.0, 1.0 - (1.0 / (expansionRatio * expansionRatio))))
                    / Math.Sqrt(1.0 - (1.0 / 9.0));
        return TurbineChokedFlow * shape * (1.0 - (0.06 * ((correctedRpm / 80_000.0) - 0.5)));
    }

    /// <summary>
    /// Total-to-static efficiency, peaking near ER 2 at the design blade-speed
    /// ratio and falling either side.
    /// </summary>
    public static double TurbineEfficiency(double correctedRpm, double expansionRatio)
    {
        var n = correctedRpm / 80_000.0;
        var eta = 0.72 - (0.10 * Math.Pow(expansionRatio - 2.0, 2.0)) - (0.15 * Math.Pow(n - 0.75, 2.0));
        return Math.Clamp(eta, 0.25, 0.78);
    }

    private static readonly double[] TurbineExpansionRatios = [1.2, 1.5, 1.8, 2.2, 2.6, 3.0];

    public static TurbineMap Turbine() => new()
    {
        Name = "Synthetic 0.64 A/R turbine",
        Reference = MapReference.SaeJ1826,
        AreaRatio = 0.64,
        RotorDiameterM = 0.055,
        Provenance = "Analytic test map — not a product. See SyntheticTurbo.",
        SpeedLines = TurbineSpeeds.Select(n => new TurbineSpeedLine(
            n,
            TurbineExpansionRatios.Select(er => new TurbinePoint(
                er, TurbineCorrectedFlow(n, er), TurbineEfficiency(n, er))).ToList())).ToList(),
    };

    public static Turbocharger Turbo() => new()
    {
        Name = "Synthetic 60 mm unit",
        Compressor = Compressor(),
        Turbine = Turbine(),
        ShaftInertia = 3.1e-6,
        MechanicalEfficiency = 0.97,
        MaxTurbineInletK = 1223.15,
        Provenance = "Analytic test unit — not a product.",
    };

    public static TurboEntry Entry() => new()
    {
        Turbo = Turbo(),
        Source = "Generated analytically by SyntheticTurbo",
        Licence = "Part of the WaveBench test suite",
        Tags = ["synthetic", "60 mm", "A/R 0.64"],
    };
}
