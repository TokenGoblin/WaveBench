namespace WaveBench.Boost.Unsteady;

/// <summary>
/// The exhaust event timing a scroll-separation calculation needs, in degrees
/// after the cylinder's own firing TDC.
/// </summary>
/// <param name="ExhaustOpenDeg">EVO. Typically 130–150° ATDC — well before BDC, which is what makes blowdown possible.</param>
/// <param name="ExhaustCloseDeg">EVC, past 360° when there is overlap.</param>
/// <param name="BlowdownDurationDeg">
/// How long the blowdown lasts from EVO — until cylinder pressure has fallen to
/// manifold pressure. 50–70° is representative; it shortens as the port grows
/// and lengthens as engine speed rises.
/// </param>
public sealed record ExhaustEventTiming(
    double ExhaustOpenDeg = 140.0,
    double ExhaustCloseDeg = 380.0,
    double BlowdownDurationDeg = 60.0)
{
    /// <summary>The blowdown window, in the cylinder's own frame.</summary>
    public (double Start, double End) BlowdownWindow => (ExhaustOpenDeg, ExhaustOpenDeg + BlowdownDurationDeg);

    /// <summary>
    /// The displacement window: valve open while the piston is rising, from BDC
    /// to EVC. This is the part of the cycle a cylinder cannot tolerate a
    /// neighbour's blowdown arriving in.
    /// </summary>
    public (double Start, double End) DisplacementWindow => (180.0, ExhaustCloseDeg);
}

/// <summary>How one scroll's cylinders interact with each other.</summary>
/// <param name="Scroll">Scroll name.</param>
/// <param name="Cylinders">Cylinder numbers on it.</param>
/// <param name="SeparationIndex">
/// Worst overlap between any cylinder's blowdown and a scroll-mate's
/// displacement window, as a fraction of the blowdown. 0 is clean separation;
/// 1 means a whole blowdown lands inside a mate's exhaust stroke.
/// </param>
/// <param name="WorstPair">The two cylinders responsible.</param>
/// <param name="MinimumSpacingDeg">
/// Smallest firing-angle gap between any two cylinders on this scroll.
///
/// It exists because the overlap index saturates: once a whole blowdown lands
/// inside a mate's stroke the index reads 1 and stops distinguishing bad from
/// worse. On engines where clean separation is unreachable at all — any V8 with
/// only two scrolls — this is what ranks the options.
/// </param>
public sealed record ScrollSeparation(
    string Scroll,
    IReadOnlyList<int> Cylinders,
    double SeparationIndex,
    (int From, int Into) WorstPair,
    double MinimumSpacingDeg);

/// <summary>
/// Twin-scroll cylinder pairing and its separation index (plan §4.6.2).
///
/// The rule: cylinders sharing a scroll must be spaced so that one cylinder's
/// blowdown never lands inside its scroll-mate's exhaust stroke. On a
/// four-cylinder that means 360° apart in the firing order; on a six it falls
/// out as the two alternating groups of three.
///
/// This is arithmetic on the firing order and the cam, with no gas dynamics in
/// it at all — which is the point. The plan requires the pairing to be
/// <b>derived from firing order alone</b>, so that getting it wrong is caught
/// before anyone runs a simulation, and so the resulting pumping-work and
/// spool-time penalties have an explanation and not just a number.
/// </summary>
public static class ScrollPairing
{
    /// <summary>
    /// Firing angle of each cylinder, in degrees ATDC of the first cylinder to
    /// fire, from a firing order.
    /// </summary>
    public static IReadOnlyDictionary<int, double> FiringAngles(IReadOnlyList<int> firingOrder)
    {
        ArgumentNullException.ThrowIfNull(firingOrder);

        if (firingOrder.Count == 0)
        {
            throw new ArgumentException("A firing order needs at least one cylinder.", nameof(firingOrder));
        }

        if (firingOrder.Distinct().Count() != firingOrder.Count)
        {
            throw new ArgumentException("A firing order cannot fire the same cylinder twice.", nameof(firingOrder));
        }

        var spacing = 720.0 / firingOrder.Count;
        return firingOrder
            .Select((cylinder, position) => (cylinder, angle: position * spacing))
            .ToDictionary(x => x.cylinder, x => x.angle);
    }

    /// <summary>
    /// The correct two-scroll grouping for a firing order: alternate cylinders
    /// down the order, which is what puts scroll-mates the maximum possible
    /// distance apart in the cycle.
    ///
    /// For an I4 firing 1-3-4-2 this returns {1,4} and {3,2} — the 360°-apart
    /// pairs. For an I6 firing 1-5-3-6-2-4 it returns {1,3,2} and {5,6,4},
    /// which is the plan's 1/2/3 versus 4/5/6 split written in firing order.
    /// </summary>
    public static (IReadOnlyList<int> ScrollA, IReadOnlyList<int> ScrollB) Recommend(IReadOnlyList<int> firingOrder)
    {
        ArgumentNullException.ThrowIfNull(firingOrder);

        if (firingOrder.Count % 2 != 0)
        {
            throw new ArgumentException(
                $"A twin-scroll pairing needs an even number of cylinders; {firingOrder.Count} were given. "
                + "An odd-cylinder engine cannot be divided into two evenly-spaced scrolls.",
                nameof(firingOrder));
        }

        var a = new List<int>();
        var b = new List<int>();
        for (var i = 0; i < firingOrder.Count; i++)
        {
            (i % 2 == 0 ? a : b).Add(firingOrder[i]);
        }

        return (a, b);
    }

    /// <summary>
    /// The separation index for one grouping (plan §4.6.2): for every ordered
    /// pair of cylinders sharing a scroll, the fraction of one's blowdown that
    /// lands inside the other's exhaust stroke. The worst pair is reported,
    /// because one bad pair is enough to spoil a scroll.
    /// </summary>
    public static IReadOnlyList<ScrollSeparation> Separation(
        IReadOnlyList<int> firingOrder,
        IReadOnlyList<(string Name, IReadOnlyList<int> Cylinders)> scrolls,
        ExhaustEventTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(scrolls);

        var angles = FiringAngles(firingOrder);
        var cam = timing ?? new ExhaustEventTiming();
        var results = new List<ScrollSeparation>();

        foreach (var (name, cylinders) in scrolls)
        {
            var worst = 0.0;
            var worstPair = (0, 0);
            var minimumSpacing = cylinders.Count > 1 ? 720.0 : double.PositiveInfinity;

            foreach (var from in cylinders)
            {
                foreach (var into in cylinders)
                {
                    if (from == into)
                    {
                        continue;
                    }

                    var overlap = BlowdownIntoDisplacement(angles[from], angles[into], cam);
                    if (overlap > worst)
                    {
                        worst = overlap;
                        worstPair = (from, into);
                    }

                    var gap = Math.Abs(angles[into] - angles[from]);
                    minimumSpacing = Math.Min(minimumSpacing, Math.Min(gap, 720.0 - gap));
                }
            }

            results.Add(new ScrollSeparation(name, cylinders, worst, worstPair, minimumSpacing));
        }

        return results;
    }

    /// <summary>
    /// Fraction of the blowdown of the cylinder firing at
    /// <paramref name="fromFiringDeg"/> that falls inside the exhaust stroke of
    /// the cylinder firing at <paramref name="intoFiringDeg"/>.
    /// </summary>
    private static double BlowdownIntoDisplacement(
        double fromFiringDeg, double intoFiringDeg, ExhaustEventTiming cam)
    {
        var (blowStart, blowEnd) = cam.BlowdownWindow;
        var (dispStart, dispEnd) = cam.DisplacementWindow;

        // Put both windows in one absolute frame, then compare over the 720°
        // cycle. The windows can wrap, so the comparison is done on the
        // unwrapped copy plus one shifted by a full cycle.
        var blow = (Start: fromFiringDeg + blowStart, End: fromFiringDeg + blowEnd);
        var disp = (Start: intoFiringDeg + dispStart, End: intoFiringDeg + dispEnd);

        var length = blow.End - blow.Start;
        if (length <= 0)
        {
            return 0.0;
        }

        var covered = 0.0;
        for (var shift = -720.0; shift <= 1440.0; shift += 720.0)
        {
            covered += Overlap(blow.Start, blow.End, disp.Start + shift, disp.End + shift);
        }

        return Math.Min(1.0, covered / length);
    }

    private static double Overlap(double aStart, double aEnd, double bStart, double bEnd) =>
        Math.Max(0.0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));
}

/// <summary>
/// Partial admission at a twin-entry rotor (plan §4.3).
///
/// A twin-scroll turbine is not at half admission all the time. When both
/// scrolls are delivering equally it is at full admission and behaves like a
/// single-entry turbine; when one is pulsing and the other is near-static the
/// pulsing scroll drives most of the rotor arc on its own. Assuming a constant
/// pressure at the limb junction misses all of this, which is why the plan
/// insists the conservation equations be solved at the mixing plane.
/// </summary>
public static class TwinScrollTurbine
{
    /// <summary>
    /// Relative efficiency lost at full single-entry admission.
    ///
    /// A rotor fed over part of its arc loses to windage in the unfed sectors
    /// and to mixing where the streams meet. The penalty is applied linearly in
    /// admission imbalance: zero when the scrolls are balanced, this value when
    /// one scroll is doing everything. The <b>shape</b> follows the qualitative
    /// trend published for twin-entry turbines under unequal admission
    /// (Copeland, Martinez-Botas et al.); the <b>coefficient is not fitted to
    /// any dataset</b> and is exposed here so it can be calibrated when one is
    /// available.
    /// </summary>
    public static double PartialAdmissionPenalty { get; set; } = 0.15;

    /// <summary>
    /// Split the rotor annulus between two scrolls according to what each is
    /// currently able to deliver.
    ///
    /// Allocation is in proportion to the flow each scroll would pass at full
    /// admission under its own instantaneous total conditions. That reduces to
    /// an even split when the scrolls are in phase — full admission, no penalty
    /// — and to one scroll taking the whole rotor when its mate has gone quiet,
    /// which is the out-of-phase case the pairing rule is designed to produce.
    /// </summary>
    public static void Redistribute(RotorNozzleBoundary a, RotorNozzleBoundary b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        // Each scroll's last solved flow, scaled back up to what it would be at
        // full admission, is the measure of what it is offering the rotor.
        var offerA = a.AdmissionFraction > 0 ? a.Last.MassFlowKgPerS / a.AdmissionFraction : 0.0;
        var offerB = b.AdmissionFraction > 0 ? b.Last.MassFlowKgPerS / b.AdmissionFraction : 0.0;
        var total = offerA + offerB;

        if (total <= 0)
        {
            a.AdmissionFraction = 0.5;
            b.AdmissionFraction = 0.5;
            a.EfficiencyScale = 1.0;
            b.EfficiencyScale = 1.0;
            return;
        }

        var shareA = Math.Clamp(offerA / total, 0.02, 0.98);
        a.AdmissionFraction = shareA;
        b.AdmissionFraction = 1.0 - shareA;

        // Imbalance runs 0 (even) to 1 (one scroll doing everything). It costs
        // efficiency, not capacity — the rotor still swallows what the scrolls
        // between them can push through it.
        var imbalance = Math.Abs((2.0 * shareA) - 1.0);
        var penalty = 1.0 - (PartialAdmissionPenalty * imbalance);
        a.EfficiencyScale = penalty;
        b.EfficiencyScale = penalty;
    }
}
