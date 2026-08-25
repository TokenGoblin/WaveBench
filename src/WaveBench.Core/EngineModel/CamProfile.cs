using System.Globalization;

namespace WaveBench.Core.EngineModel;

/// <summary>
/// Valve lift versus crank angle over the 720° cycle. Sources: an analytic
/// generic profile (smooth C¹ cosine ramps — a placeholder until the polydyne
/// generator lands; flagged generic per plan §2.6), or an imported measured
/// table (CSV "crankAngleDeg,liftMetres" or "deg,mm" — the unit is inferred
/// from magnitude). Angles are cycle-periodic.
/// </summary>
public sealed class CamProfile
{
    private readonly double[] _angles; // ascending, degrees in [0,720)
    private readonly double[] _lift;   // metres

    private CamProfile(double[] angles, double[] lift, bool isGeneric)
    {
        _angles = angles;
        _lift = lift;
        IsGeneric = isGeneric;
        MaxLift = lift.Max();
    }

    public bool IsGeneric { get; }

    public double MaxLift { get; }

    /// <summary>Lift at crank angle (deg, any value; wrapped to the 720° cycle), m.</summary>
    public double Lift(double crankAngleDeg)
    {
        var a = crankAngleDeg % 720.0;
        if (a < 0)
        {
            a += 720.0;
        }

        // Binary search + linear interpolation.
        var idx = Array.BinarySearch(_angles, a);
        if (idx >= 0)
        {
            return _lift[idx];
        }

        var hi = ~idx;
        if (hi == 0 || hi == _angles.Length)
        {
            // Between last and first sample through the wrap: lift is zero
            // there for any sane cam; return the boundary value.
            return 0.0;
        }

        var lo = hi - 1;
        var w = (a - _angles[lo]) / (_angles[hi] - _angles[lo]);
        return _lift[lo] + w * (_lift[hi] - _lift[lo]);
    }

    /// <summary>Opening angle at a lift threshold (first up-crossing), deg.</summary>
    public double OpeningAngle(double threshold = 1e-4) =>
        _angles[Array.FindIndex(_lift, l => l > threshold)];

    public double ClosingAngle(double threshold = 1e-4) =>
        _angles[Array.FindLastIndex(_lift, l => l > threshold)];

    /// <summary>
    /// Angle on the closing flank where lift falls to the given fraction of
    /// maximum — the "effective closing" for wave-tuning estimates, since the
    /// valve stops dominating the flow well before nominal closure.
    /// </summary>
    public double ClosingAngleAtFraction(double fraction)
    {
        var threshold = fraction * MaxLift;
        return _angles[Array.FindLastIndex(_lift, l => l > threshold)];
    }

    /// <summary>
    /// Generic analytic profile: cosine flanks between opening and closing
    /// (degrees in cycle coordinates), peak lift at the centre.
    /// </summary>
    public static CamProfile Harmonic(double openDeg, double closeDeg, double maxLift, int samples = 721)
    {
        if (closeDeg <= openDeg)
        {
            closeDeg += 720.0;
        }

        var duration = closeDeg - openDeg;
        var angles = new List<double>();
        var lifts = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            var a = 720.0 * i / (samples - 1);
            var rel = (a - openDeg + 720.0) % 720.0;
            var lift = rel <= duration
                ? maxLift * 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * rel / duration))
                : 0.0;
            angles.Add(a);
            lifts.Add(lift);
        }

        return new CamProfile(angles.ToArray(), lifts.ToArray(), isGeneric: true);
    }

    /// <summary>Import "angleDeg,lift" CSV (lift in m or mm, inferred; comments with #).</summary>
    public static CamProfile FromCsv(TextReader reader)
    {
        var angles = new List<double>();
        var lifts = new List<double>();
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split([',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new FormatException($"Cam CSV line '{line}' needs angle and lift.");
            }

            angles.Add(double.Parse(parts[0], CultureInfo.InvariantCulture));
            lifts.Add(double.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        if (angles.Count < 4)
        {
            throw new FormatException("Cam table needs at least 4 points.");
        }

        // Millimetres if the peak is implausibly large for metres.
        var max = lifts.Max();
        if (max > 0.1)
        {
            for (var i = 0; i < lifts.Count; i++)
            {
                lifts[i] *= 1e-3;
            }
        }

        var order = Enumerable.Range(0, angles.Count).OrderBy(i => angles[i]).ToArray();
        return new CamProfile(
            order.Select(i => angles[i] % 720.0).ToArray(),
            order.Select(i => lifts[i]).ToArray(),
            isGeneric: false);
    }
}

/// <summary>
/// Poppet-valve geometry and effective flow area (plan §2.6, Blair's
/// convention): reference area is the valve curtain π·D_v·L, and the
/// effective area is the minimum of curtain and throat (inner-seat) area.
/// </summary>
public sealed record ValveGeometry
{
    public required double HeadDiameter { get; init; }

    /// <summary>Inner seat / throat diameter (defaults to 0.85·D_v).</summary>
    public double ThroatDiameter { get; init; }

    public int ValveCount { get; init; } = 1;

    public double EffectiveThroat => ThroatDiameter > 0 ? ThroatDiameter : 0.85 * HeadDiameter;

    public double ThroatArea => ValveCount * Math.PI / 4.0 * EffectiveThroat * EffectiveThroat;

    public double CurtainArea(double lift) => ValveCount * Math.PI * HeadDiameter * lift;

    public double EffectiveArea(double lift) => Math.Min(CurtainArea(lift), ThroatArea);
}

/// <summary>
/// Discharge-coefficient map C_d(L/D, pressure ratio) per flow direction
/// (plan §2.6, after Blair, Callender &amp; Mackey SAE 2001-01-1798: the C_d is
/// a 2D map, not a single curve). The shipped default is a GENERIC 4-valve
/// pent-roof-style map — replace with measured flow-bench data.
/// </summary>
public sealed class ValveCdMap
{
    private readonly double[] _liftRatio;
    private readonly double[] _cd;

    private ValveCdMap(double[] liftRatio, double[] cd, bool isGeneric)
    {
        _liftRatio = liftRatio;
        _cd = cd;
        IsGeneric = isGeneric;
    }

    public bool IsGeneric { get; }

    /// <summary>
    /// C_d at lift/diameter ratio and pressure ratio. The generic map's
    /// pressure-ratio dependence is a documented mild linear correction
    /// (measured maps supersede it).
    /// </summary>
    public double Cd(double liftOverDiameter, double pressureRatio)
    {
        var cd = Interpolate(liftOverDiameter);
        // Mild increase toward choking observed in properly-reduced data;
        // generic and clearly approximate.
        var pr = Math.Clamp(pressureRatio, 0.3, 1.0);
        return cd * (1.0 + 0.05 * (1.0 - pr));
    }

    private double Interpolate(double x)
    {
        if (x <= _liftRatio[0])
        {
            return _cd[0];
        }

        if (x >= _liftRatio[^1])
        {
            return _cd[^1];
        }

        var hi = Array.FindIndex(_liftRatio, v => v > x);
        var lo = hi - 1;
        var w = (x - _liftRatio[lo]) / (_liftRatio[hi] - _liftRatio[lo]);
        return _cd[lo] + w * (_cd[hi] - _cd[lo]);
    }

    /// <summary>
    /// Generic poppet-valve curve: high C_d at low lift (thin annular jet
    /// stays attached), falling as the jet separates at high lift — the
    /// characteristic shape of flow-bench data for pent-roof heads.
    /// </summary>
    public static ValveCdMap Generic { get; } = new(
        [0.00, 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.40],
        [0.95, 0.90, 0.80, 0.70, 0.62, 0.58, 0.55, 0.52],
        isGeneric: true);

    public static ValveCdMap FromCurve(double[] liftRatio, double[] cd) =>
        new((double[])liftRatio.Clone(), (double[])cd.Clone(), isGeneric: false);
}
