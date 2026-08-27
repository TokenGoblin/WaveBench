using WaveBench.Acoustics.Metrics;

namespace WaveBench.Acoustics;

/// <summary>
/// One exhaust design as the sound model sees it: its primaries, and how hard
/// each cylinder pushes into the collector.
/// </summary>
public sealed record ExhaustSoundDesign
{
    public required string Name { get; init; }

    /// <summary>One entry per cylinder, in cylinder order.</summary>
    public required IReadOnlyList<CollectorBranch> Branches { get; init; }

    /// <summary>
    /// Relative blowdown amplitude per branch. Unequal amplitudes break the
    /// order cancellation exactly as unequal timing does (plan §3.2), so a
    /// design with perfect lengths and uneven scavenging is not a clean one.
    /// </summary>
    public IReadOnlyList<double> Amplitudes { get; init; } = [];

    /// <summary>Firing events per revolution: 3.0 for a four-stroke six, 2.0 for a four.</summary>
    public double FiringOrder => Branches.Count / 2.0;

    public double AmplitudeOf(int branch) =>
        Amplitudes.Count > branch ? Amplitudes[branch] : 1.0;

    /// <summary>Replace one branch's primary length, in metres.</summary>
    public ExhaustSoundDesign WithPrimaryLength(int branchIndex, double metres)
    {
        var branches = Branches.ToArray();
        branches[branchIndex] = branches[branchIndex] with { PrimaryLength = metres };
        return this with { Branches = branches };
    }

    /// <summary>Replace every primary length — the equal-length case.</summary>
    public ExhaustSoundDesign WithPrimaryLength(double metres) => this with
    {
        Branches = Branches.Select(b => b with { PrimaryLength = metres }).ToArray(),
    };
}

/// <summary>
/// Why one branch's pulse is mistimed, split into causes that can be acted on
/// separately.
/// </summary>
/// <param name="Cylinder">Which cylinder.</param>
/// <param name="ErrorDeg">Its arrival error against the even grid; positive is late.</param>
/// <param name="ReferenceCylinder">The cylinder the error is measured against.</param>
/// <param name="LengthDeltaM">How much longer than the reference its primary is.</param>
/// <param name="FromLengthDeg">Crank degrees of the error attributable to that length.</param>
/// <param name="TemperatureDeltaK">How much hotter than the reference its gas is.</param>
/// <param name="FromTemperatureDeg">Crank degrees attributable to that temperature.</param>
/// <param name="NonFiringEnergyFraction">Share of collector energy off the firing harmonics.</param>
public sealed record TimingAttribution(
    int Cylinder,
    double ErrorDeg,
    int ReferenceCylinder,
    double LengthDeltaM,
    double FromLengthDeg,
    double TemperatureDeltaK,
    double FromTemperatureDeg,
    double NonFiringEnergyFraction)
{
    /// <summary>
    /// What the two named causes fail to account for, crank degrees.
    /// Zero by construction — the split is exact — and asserted, because a
    /// sentence that says "15° late because of these two things" and then
    /// lists two things summing to 6° is one a careful reader will catch.
    /// </summary>
    public double UnexplainedDeg => ErrorDeg - FromLengthDeg - FromTemperatureDeg;
}

/// <summary>
/// The collector superposition in the order domain, computed from geometry and
/// firing order alone (plan §3.2).
///
/// This is the fast path the Sound workspace runs on: arrival phases, a
/// Gaussian pulse per cylinder, one FFT. Microseconds, so a slider can drive
/// it — which is what plan §8.4 requires of a header-length change, with the
/// nonlinear re-solve queued behind it.
/// </summary>
public static class CollectorSpectrum
{
    /// <summary>Cycles synthesised before transforming; sets the order resolution.</summary>
    public const int DefaultCycles = 4;

    /// <summary>Order spectrum of the collector superposition.</summary>
    public static OrderSpectrum At(
        ExhaustSoundDesign design, double rpm, double pulseWidthDeg = 18.0, int cycles = DefaultCycles)
    {
        ArgumentNullException.ThrowIfNull(design);

        var timing = CollectorTiming.Analyze(design.Branches, rpm);
        var amplitudes = Enumerable.Range(0, design.Branches.Count).Select(design.AmplitudeOf).ToArray();
        var cycle = CollectorPulseTrain.SynthesizeCycle(timing, pulseWidthDeg, 2880, amplitudes);

        // Repeated before transforming. One cycle resolves orders only in
        // whole multiples of the cycle rate, and the half orders are the whole
        // point here — they are what a warble is made of.
        var (signal, sampleRate) = CollectorPulseTrain.Repeat(cycle, cycles, rpm);
        return OrderAnalysis.AtConstantSpeed(signal, sampleRate, rpm);
    }

    /// <summary>
    /// Fraction of the collector's energy that is NOT on the firing order or
    /// its harmonics — the single number that separates a howl from a warble.
    /// </summary>
    public static double NonFiringEnergyFraction(
        ExhaustSoundDesign design, double rpm, double pulseWidthDeg = 18.0) =>
        1.0 - CharacterMetrics.OrderPurityIndex(At(design, rpm, pulseWidthDeg), design.FiringOrder);

    /// <summary>
    /// Attribute the worst arrival error to its causes.
    ///
    /// The split is the part worth having. A timing error has two independent
    /// causes — a longer pipe and cooler gas — and reporting only the error
    /// tells a builder something is wrong where reporting the split tells them
    /// which one to go and change.
    ///
    /// <b>Measured against the anchor branch, not the design mean.</b> The
    /// arrival error itself is defined against the even grid anchored on the
    /// first-firing branch, and because firing is evenly spaced that reduces
    /// exactly to
    ///
    /// <code>error_i = 6·N·(τ_i − τ_anchor)</code>
    ///
    /// so the anchor is the only reference against which the parts can sum to
    /// the whole. Splitting about the design mean instead — the obvious first
    /// choice — produced a sentence reading <i>"arrives 15° early because its
    /// primary is 110 mm shorter (−6.3°) and it runs 38 K hotter (−0.3°)"</i>,
    /// where the two causes account for six of the fifteen degrees and nothing
    /// explains the rest.
    ///
    /// The split within that difference is exact rather than a linearisation:
    /// <code>
    ///   τ_i − τ_a = [L_i/(a_a+u_a) − L_a/(a_a+u_a)]   ← length, at the reference wave speed
    ///             + [L_i/(a_i+u_i) − L_i/(a_a+u_a)]   ← wave speed, at this pipe's length
    /// </code>
    /// </summary>
    public static TimingAttribution? Attribute(
        ExhaustSoundDesign design, double rpm, double pulseWidthDeg = 18.0)
    {
        ArgumentNullException.ThrowIfNull(design);
        if (design.Branches.Count == 0)
        {
            return null;
        }

        var timing = CollectorTiming.Analyze(design.Branches, rpm);

        var worst = 0;
        for (var i = 1; i < timing.TimingErrorDeg.Count; i++)
        {
            if (Math.Abs(timing.TimingErrorDeg[i]) > Math.Abs(timing.TimingErrorDeg[worst]))
            {
                worst = i;
            }
        }

        // The branch that fires first is the one the even grid is anchored on.
        var anchor = design.Branches
            .Select((b, i) => (Branch: b, Index: i))
            .MinBy(x => Wrap(x.Branch.FiringAngleDeg))
            .Index;

        var branch = design.Branches[worst];
        var reference = design.Branches[anchor];
        var toDeg = 6.0 * rpm;

        var referenceWaveSpeed = reference.MeanSoundSpeed + reference.MeanFlowVelocity;
        var branchWaveSpeed = branch.MeanSoundSpeed + branch.MeanFlowVelocity;

        var fromLength = (branch.PrimaryLength - reference.PrimaryLength) / referenceWaveSpeed * toDeg;
        var fromSpeed = branch.PrimaryLength * ((1.0 / branchWaveSpeed) - (1.0 / referenceWaveSpeed)) * toDeg;

        // a = √(γRT), so T = a²/(γR): a sound-speed difference IS a
        // temperature difference, and kelvin is what a builder can act on.
        var temperatureDelta =
            ((branch.MeanSoundSpeed * branch.MeanSoundSpeed)
             - (reference.MeanSoundSpeed * reference.MeanSoundSpeed)) / GammaR;

        return new TimingAttribution(
            branch.Cylinder,
            timing.TimingErrorDeg[worst],
            reference.Cylinder,
            branch.PrimaryLength - reference.PrimaryLength,
            fromLength,
            temperatureDelta,
            fromSpeed,
            NonFiringEnergyFraction(design, rpm, pulseWidthDeg));
    }

    private static double Wrap(double deg)
    {
        var a = deg % 720.0;
        return a < 0 ? a + 720.0 : a;
    }

    /// <summary>γ·R for burned products — see <see cref="SoundCases.SoundSpeedAt"/>.</summary>
    internal const double GammaR = 1.33 * 288.0;
}
