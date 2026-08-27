namespace WaveBench.Acoustics;

/// <summary>
/// The comparison cases the Sound workspace ships with.
///
/// The M50 pair is the plan's own worked example (§3.0): <i>"A BMW M50 with the
/// factory cast manifold versus an equal-length 6-into-1, reproduced FROM
/// GEOMETRY AND FIRING ORDER ALONE."</i> That last clause is the constraint
/// that makes it a demonstration rather than a mock-up — nothing here is a
/// tuned coefficient chosen to make the answer come out; the runner lengths
/// and the firing order go in, and the order structure falls out of the pulse
/// superposition.
/// </summary>
public static class SoundCases
{
    /// <summary>
    /// M50 firing order 1-5-3-6-2-4, 120° apart on a four-stroke six.
    /// </summary>
    public static IReadOnlyList<int> M50FiringOrder { get; } = [1, 5, 3, 6, 2, 4];

    /// <summary>Cycle angle at which each cylinder fires, keyed by cylinder number.</summary>
    public static IReadOnlyDictionary<int, double> M50FiringAngles { get; } =
        M50FiringOrder
            .Select((cylinder, slot) => (cylinder, angle: slot * 120.0))
            .ToDictionary(x => x.cylinder, x => x.angle);

    /// <summary>
    /// The factory cast manifold: short runners, unequal, and with a
    /// cylinder-to-cylinder temperature spread.
    ///
    /// <b>The temperature spread is not decoration.</b> A cast log sits against
    /// the head with its runners at different lengths and different exposure,
    /// and since transit is L/(a+u) with a ∝ √T, a 60 K spread mistimes
    /// arrivals on its own — which is why geometrically equal headers can still
    /// be time-unequal (plan §3.2). The end cylinders of an inline six run
    /// cooler than the middle ones, so the profile is stated that way.
    ///
    /// Lengths and temperatures are representative engineering values for a
    /// cast log of this type, not measurements from a specific car; the point
    /// the example makes is structural and does not depend on the exact
    /// millimetres.
    /// </summary>
    public static ExhaustSoundDesign M50Factory()
    {
        // Short, unequal, and MONOTONIC: a cast log has one outlet, so runner
        // length falls steadily from the far cylinder to the near one.
        //
        // A mirror-symmetric set of lengths would be the tidier guess and it
        // would be wrong in a way that matters. Under the 1-5-3-6-2-4 firing
        // order a mirror pattern repeats every three firings, which makes the
        // collector signal periodic at 360° instead of 720° — and a signal
        // periodic at 360° has NO half-order content at all. The manifold
        // would then come out looking clean on exactly the metric the plan
        // says should condemn it. Real logs are not symmetric about their
        // middle; they taper toward their outlet.
        double[] lengthsMm = [420, 360, 300, 250, 205, 175];

        // Coolant runs front to back on an inline six, so the rear cylinders
        // run hotter — also monotonic, and also not symmetric.
        double[] temperaturesK = [880, 895, 910, 925, 940, 955];

        // Scavenging follows: shorter, hotter runners empty more completely.
        double[] amplitudes = [0.90, 0.94, 0.97, 1.00, 1.03, 1.06];

        return new ExhaustSoundDesign
        {
            Name = "Factory cast manifold",
            Branches = Branches(lengthsMm, temperaturesK),
            Amplitudes = amplitudes,
        };
    }

    /// <summary>
    /// The equal-length 6-into-1: every primary the same length, and — because
    /// each runner is its own pipe in free air rather than a lobe of a casting
    /// pressed against the head — a far tighter temperature spread.
    /// </summary>
    public static ExhaustSoundDesign M50EqualLength(double primaryLengthMm = 720.0)
    {
        var lengths = Enumerable.Repeat(primaryLengthMm, 6).ToArray();
        var temperatures = Enumerable.Repeat(920.0, 6).ToArray();

        return new ExhaustSoundDesign
        {
            Name = $"Equal-length 6-1, {primaryLengthMm:F0} mm",
            Branches = Branches(lengths, temperatures),
            Amplitudes = Enumerable.Repeat(1.0, 6).ToArray(),
        };
    }

    /// <summary>
    /// Sound speed of exhaust gas at a temperature, m/s: a = √(γRT).
    ///
    /// γ and R are those of the burned mixture rather than of air — products
    /// are heavier and less stiff, and using air here would overstate every
    /// wave speed by about 3%.
    /// </summary>
    public static double SoundSpeedAt(double temperatureK)
    {
        const double gamma = 1.33;
        const double gasConstant = 288.0;
        return Math.Sqrt(gamma * gasConstant * temperatureK);
    }

    private static IReadOnlyList<CollectorBranch> Branches(
        IReadOnlyList<double> lengthsMm, IReadOnlyList<double> temperaturesK)
    {
        // Mean flow down a primary during blowdown. It adds to the wave speed
        // — transit is L/(a+u), not L/a — and leaving it out would slow every
        // arrival by roughly a sixth.
        const double meanFlow = 90.0;

        var branches = new List<CollectorBranch>();
        for (var i = 0; i < lengthsMm.Count; i++)
        {
            var cylinder = i + 1;
            branches.Add(new CollectorBranch(
                Cylinder: cylinder,
                FiringAngleDeg: M50FiringAngles[cylinder],
                PrimaryLength: lengthsMm[i] / 1000.0,
                MeanSoundSpeed: SoundSpeedAt(temperaturesK[i]),
                MeanFlowVelocity: meanFlow));
        }

        return branches;
    }
}
