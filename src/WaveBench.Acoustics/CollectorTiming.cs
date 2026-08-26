namespace WaveBench.Acoustics;

/// <summary>One primary pipe feeding a collector.</summary>
public sealed record CollectorBranch(
    int Cylinder,
    double FiringAngleDeg,
    double PrimaryLength,
    double MeanSoundSpeed,
    double MeanFlowVelocity = 0.0)
{
    /// <summary>Blowdown transit time to the collector, s: τ = ∫dx/(ā+ū) ≈ L/(ā+ū) (plan §3.2).</summary>
    public double TransitTime => PrimaryLength / (MeanSoundSpeed + MeanFlowVelocity);

    /// <summary>Arrival phase at the collector, deg: φ = θ_fire + 6·N·τ (plan §3.2).</summary>
    public double ArrivalAngleDeg(double rpm) => FiringAngleDeg + 6.0 * rpm * TransitTime;
}

// ArrivalDeg: arrival angle per branch (branch order), wrapped to [0,720).
// SpacingDeg: gaps between consecutive arrivals (sorted), deg — sums to 720.
// TimingErrorDeg: per-branch deviation from the ideal even grid anchored at
// the first-firing branch, deg.
public sealed record CollectorTimingResult(
    IReadOnlyList<CollectorBranch> Branches,
    double Rpm,
    IReadOnlyList<double> ArrivalDeg,
    IReadOnlyList<double> SpacingDeg,
    double IdealSpacingDeg,
    IReadOnlyList<double> TimingErrorDeg,
    double MaxAbsTimingErrorDeg);

/// <summary>
/// The collector pulse-timing calculation (plan §3.2) — explicit and
/// inspectable, never an emergent by-product of the solver. Uses the ACTUAL
/// local sound speed the caller supplies per primary (from the solved gas
/// state or the wall-thermal model), because ā ∝ √T is exactly what makes
/// geometrically equal headers time-unequal on engines with cylinder-to-
/// cylinder temperature spread (§3.2).
///
/// Consequences the numbers must show (§3.2): equal lengths ⇒ spacing
/// exactly 720/m at every rpm; a fixed transit mismatch Δτ maps to 6·N·Δτ
/// crank degrees, growing linearly with speed.
/// </summary>
public static class CollectorTiming
{
    public static CollectorTimingResult Analyze(IReadOnlyList<CollectorBranch> branches, double rpm)
    {
        var m = branches.Count;
        var ideal = 720.0 / m;

        var arrivals = branches.Select(b => Wrap(b.ArrivalAngleDeg(rpm))).ToArray();

        // Spacing between consecutive arrivals.
        var sorted = arrivals.OrderBy(a => a).ToArray();
        var spacing = new double[m];
        for (var i = 0; i < m; i++)
        {
            var next = sorted[(i + 1) % m] + (i + 1 == m ? 720.0 : 0.0);
            spacing[i] = next - sorted[i];
        }

        // Timing error: deviation from the even grid anchored at the branch
        // that fires first, with slots assigned in firing sequence.
        var firingOrder = Enumerable.Range(0, m)
            .OrderBy(i => Wrap(branches[i].FiringAngleDeg)).ToArray();
        var anchor = arrivals[firingOrder[0]];
        var errors = new double[m];
        for (var slot = 0; slot < m; slot++)
        {
            var branch = firingOrder[slot];
            var idealArrival = anchor + slot * ideal;
            errors[branch] = WrapSigned(arrivals[branch] - idealArrival);
        }

        return new CollectorTimingResult(
            branches, rpm, arrivals, spacing, ideal, errors, errors.Max(Math.Abs));
    }

    private static double Wrap(double deg)
    {
        var a = deg % 720.0;
        return a < 0 ? a + 720.0 : a;
    }

    private static double WrapSigned(double deg)
    {
        var a = Wrap(deg);
        return a > 360.0 ? a - 720.0 : a;
    }
}

/// <summary>
/// Twin-scroll pairing separation (plan §4.6.2): the overlap of one
/// cylinder's blowdown window with its scroll-mate's exhaust-stroke window,
/// computed from firing order and valve events alone. Correct 360°-apart
/// pairing ⇒ near-zero overlap. The pressure-history-weighted version is the
/// forced-induction validation (Phase 13); this timing form drives the
/// pairing display and ranking.
/// </summary>
public static class ScrollSeparation
{
    /// <summary>
    /// Overlap in crank degrees, summed over ordered mate pairs in one
    /// scroll. Blowdown window: [EVO, EVO + blowdownDeg] of each cylinder
    /// (local cycle angles by firing phase); exhaust-stroke window:
    /// [EVO, EVC] of the mate.
    /// </summary>
    public static double ScrollOverlapDeg(
        IReadOnlyList<double> scrollCylinderFiringAngles,
        double evoDeg, double evcDeg, double blowdownDeg = 90.0)
    {
        var total = 0.0;
        for (var i = 0; i < scrollCylinderFiringAngles.Count; i++)
        {
            for (var j = 0; j < scrollCylinderFiringAngles.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var blowStart = scrollCylinderFiringAngles[i] + evoDeg;
                var mateStart = scrollCylinderFiringAngles[j] + evoDeg;
                var mateEnd = scrollCylinderFiringAngles[j] + evcDeg;
                total += IntervalOverlap(blowStart, blowStart + blowdownDeg, mateStart, mateEnd);
            }
        }

        return total;
    }

    /// <summary>Normalised index: overlap ÷ total blowdown degrees in the scroll (0 = perfect pairing).</summary>
    public static double Index(
        IReadOnlyList<double> scrollCylinderFiringAngles,
        double evoDeg, double evcDeg, double blowdownDeg = 90.0) =>
        ScrollOverlapDeg(scrollCylinderFiringAngles, evoDeg, evcDeg, blowdownDeg)
        / (blowdownDeg * scrollCylinderFiringAngles.Count);

    private static double IntervalOverlap(double aStart, double aEnd, double bStart, double bEnd)
    {
        // Compare on the 720° circle: shift b by −720, 0, +720 and take the
        // best-aligned overlap contributions.
        var overlap = 0.0;
        foreach (var shift in (ReadOnlySpan<double>)[-720.0, 0.0, 720.0])
        {
            var start = Math.Max(aStart, bStart + shift);
            var end = Math.Min(aEnd, bEnd + shift);
            overlap += Math.Max(0.0, end - start);
        }

        return overlap;
    }
}
