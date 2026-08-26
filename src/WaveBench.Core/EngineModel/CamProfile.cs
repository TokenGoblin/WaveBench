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

    /// <summary>
    /// Polydyne (polynomial) lift profile — the family real cams are designed
    /// from, after Dudley ("A New Approach to Cam Design", Machine Design,
    /// 1948) and Thoren, Engemann &amp; Stoddart (SAE 1952). Lift is
    ///   y(x) = 1 + C₂x² + C_p x^p + C_q x^q + C_r x^r,   x = (θ − θ_nose)/(Δθ/2)
    /// with the four coefficients fixed by requiring lift, velocity,
    /// acceleration AND jerk all to vanish at the seat (x = ±1).
    ///
    /// <b>Why this rather than the harmonic profile.</b> The cosine flank
    /// reaches the seat with a finite acceleration — 0.5·L·(2π/Δθ)² — so
    /// acceleration steps discontinuously to zero at seating and jerk is
    /// infinite there. That is precisely what makes a follower bounce, and it
    /// is why no real cam is a raised cosine. A polydyne closes to zero jerk,
    /// which is the entire reason the family exists.
    ///
    /// Exponents default to the classic 2-8-10-12 set. They must be distinct
    /// and greater than 3, or the boundary conditions are not independent.
    ///
    /// Still marked generic: this is a correctly-shaped profile, not the
    /// user's cam. Measured lift data always wins.
    /// </summary>
    /// <param name="openDeg">Opening angle in cycle coordinates, degrees.</param>
    /// <param name="closeDeg">Closing angle in cycle coordinates, degrees.</param>
    /// <param name="maxLift">Peak lift at the nose, m.</param>
    /// <param name="p">Second exponent (default 8).</param>
    /// <param name="q">Third exponent (default 10).</param>
    /// <param name="r">Fourth exponent (default 12).</param>
    /// <param name="samples">Table resolution over the 720° cycle.</param>
    public static CamProfile Polydyne(
        double openDeg, double closeDeg, double maxLift,
        int p = 8, int q = 10, int r = 12, int samples = 721)
    {
        if (closeDeg <= openDeg)
        {
            closeDeg += 720.0;
        }

        var coefficients = PolydyneCoefficients(p, q, r);
        var duration = closeDeg - openDeg;
        var half = duration / 2.0;

        var angles = new double[samples];
        var lifts = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var a = 720.0 * i / (samples - 1);
            var rel = (a - openDeg + 720.0) % 720.0;
            angles[i] = a;
            lifts[i] = rel <= duration
                ? maxLift * Math.Max(0.0, PolydyneLift((rel - half) / half, p, q, r, coefficients))
                : 0.0;
        }

        return new CamProfile(angles, lifts, isGeneric: true);
    }

    /// <summary>
    /// Normalised polydyne lift at x ∈ [−1, 1], 1 at the nose and 0 at the
    /// seat. Public so the profile's derivatives can be checked directly
    /// rather than only through a sampled table.
    /// </summary>
    public static double PolydyneLift(double x, int p = 8, int q = 10, int r = 12, double[]? coefficients = null)
    {
        var c = coefficients ?? PolydyneCoefficients(p, q, r);
        var t = Math.Abs(x);
        return 1.0 + (c[0] * t * t) + (c[1] * Math.Pow(t, p)) + (c[2] * Math.Pow(t, q)) + (c[3] * Math.Pow(t, r));
    }

    /// <summary>
    /// Exact <paramref name="order"/>-th derivative of the normalised
    /// polydyne lift with respect to x, at x ∈ [−1, 1]. Order 1, 2 and 3 are
    /// the follower's velocity, acceleration and jerk in normalised units —
    /// which are what a cam is actually designed against, and what a finite
    /// difference cannot resolve near the seat where all of them vanish.
    /// </summary>
    public static double PolydyneDerivative(
        double x, int order, int p = 8, int q = 10, int r = 12, double[]? coefficients = null)
    {
        if (order is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Order must be 0–4.");
        }

        var c = coefficients ?? PolydyneCoefficients(p, q, r);
        var t = Math.Abs(x);

        // d^k/dt^k of t^n is n!/(n−k)! · t^(n−k), zero once k > n.
        static double Term(double coefficient, int n, int order, double t)
        {
            if (order > n)
            {
                return 0.0;
            }

            var factor = 1.0;
            for (var i = 0; i < order; i++)
            {
                factor *= n - i;
            }

            return coefficient * factor * Math.Pow(t, n - order);
        }

        var value = Term(c[0], 2, order, t) + Term(c[1], p, order, t)
                    + Term(c[2], q, order, t) + Term(c[3], r, order, t);

        if (order == 0)
        {
            value += 1.0;
        }

        // Odd derivatives change sign on the opening flank, because the
        // profile is |x|-symmetric about the nose.
        return order % 2 == 1 && x < 0 ? -value : value;
    }

    /// <summary>
    /// Solves for [C₂, C_p, C_q, C_r] from the seating conditions
    /// y(1) = y′(1) = y″(1) = y‴(1) = 0.
    /// </summary>
    public static double[] PolydyneCoefficients(int p = 8, int q = 10, int r = 12)
    {
        if (p <= 3 || q <= p || r <= q)
        {
            throw new ArgumentException(
                $"Polydyne exponents must satisfy 3 < p < q < r; got {p}, {q}, {r}.", nameof(p));
        }

        // Rows: value, first, second and third derivative at x = 1.
        // Columns: C₂, C_p, C_q, C_r. Right-hand side is −(the constant term).
        double D1(int n) => n;
        double D2(int n) => (double)n * (n - 1);
        double D3(int n) => (double)n * (n - 1) * (n - 2);

        var m = new[,]
        {
            { 1.0, 1.0, 1.0, 1.0, -1.0 },
            { D1(2), D1(p), D1(q), D1(r), 0.0 },
            { D2(2), D2(p), D2(q), D2(r), 0.0 },
            { D3(2), D3(p), D3(q), D3(r), 0.0 },
        };

        return SolveFourByFour(m);
    }

    /// <summary>Gaussian elimination with partial pivoting on a 4×5 augmented matrix.</summary>
    private static double[] SolveFourByFour(double[,] m)
    {
        const int n = 4;
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
            {
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-14)
            {
                throw new InvalidOperationException("Polydyne boundary conditions are singular for these exponents.");
            }

            if (pivot != col)
            {
                for (var k = col; k <= n; k++)
                {
                    (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                }
            }

            for (var row = col + 1; row < n; row++)
            {
                var factor = m[row, col] / m[col, col];
                for (var k = col; k <= n; k++)
                {
                    m[row, k] -= factor * m[col, k];
                }
            }
        }

        var x = new double[n];
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = m[row, n];
            for (var k = row + 1; k < n; k++)
            {
                sum -= m[row, k] * x[k];
            }

            x[row] = sum / m[row, row];
        }

        return x;
    }

    /// <summary>
    /// Half-sine lift over the open duration, L·sin(π·(θ−θ₀)/Δθ) — the shape
    /// used by simplified literature models (e.g. the Yin CSU thesis Eq. 4).
    /// Generic like the harmonic profile.
    /// </summary>
    public static CamProfile HalfSine(double openDeg, double closeDeg, double maxLift, int samples = 721)
    {
        if (closeDeg <= openDeg)
        {
            closeDeg += 720.0;
        }

        var duration = closeDeg - openDeg;
        var angles = new double[samples];
        var lifts = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var a = 720.0 * i / (samples - 1);
            var rel = (a - openDeg + 720.0) % 720.0;
            angles[i] = a;
            lifts[i] = rel <= duration ? maxLift * Math.Sin(Math.PI * rel / duration) : 0.0;
        }

        return new CamProfile(angles, lifts, isGeneric: true);
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
