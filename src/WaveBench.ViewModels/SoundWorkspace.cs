using System.Globalization;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Acoustics.Metrics;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels;

/// <summary>Tabs of the Sound workspace, in shell order (plan §8.4).</summary>
public enum SoundTab
{
    Timing,
    Spectrum,
    Silencing,
    Audition,
    Compliance,
}

/// <summary>
/// Whether a figure is showing the instant estimate or the solved answer
/// (plan §8.4: <i>"clearly indicating which is showing"</i>).
/// </summary>
public enum SoundFidelity
{
    /// <summary>
    /// From geometry and firing order alone — the pulse-timing model. Runs in
    /// microseconds, so it can follow a slider.
    /// </summary>
    Instant,

    /// <summary>Refined from a converged nonlinear solve.</summary>
    Solved,
}

/// <summary>
/// The Sound workspace (plan Phase 20, §8.4): collector timing, order
/// spectrum, the order waterfall, the character radar, and the A/B comparison
/// the plan's M50 worked example is built around.
///
/// <b>The instant path is the point.</b> Plan §8.4 requires that changing a
/// header length update the timing chart and spectrum immediately, with the
/// nonlinear re-solve queued behind it. Everything here except the audition
/// runs from geometry and firing order alone — arrival phases, a Gaussian
/// pulse per cylinder, one FFT — which is microseconds, so a slider can drive
/// it directly. <see cref="Fidelity"/> says which answer a figure
/// is showing, because an estimate presented as a solve is worse than no
/// estimate.
/// </summary>
public sealed class SoundWorkspace(ExhaustSoundDesign a, ExhaustSoundDesign b, UserPreferences? preferences = null)
{
    public UserPreferences Preferences { get; } = preferences ?? new UserPreferences();

    public ExhaustSoundDesign A { get; set; } = a;

    public ExhaustSoundDesign B { get; set; } = b;

    public SoundTab SelectedTab { get; set; } = SoundTab.Timing;

    /// <summary>Speed the timing and spectrum figures are drawn at.</summary>
    public double Rpm { get; set; } = 4000.0;

    /// <summary>Which design the single-design figures and the audition use.</summary>
    public bool ShowingB { get; set; }

    public ExhaustSoundDesign Current => ShowingB ? B : A;

    /// <summary>
    /// Whether the figures are showing the instant estimate or a solved
    /// refinement. Shown on every figure, because an estimate presented as a
    /// solve is worse than no estimate.
    /// </summary>
    public SoundFidelity Fidelity { get; set; } = SoundFidelity.Instant;

    /// <summary>Pulse width used by the instant model, crank degrees.</summary>
    public double PulseWidthDeg { get; set; } = 18.0;

    // ---- The instant model -----------------------------------------------

    public CollectorTimingResult Timing(ExhaustSoundDesign design) =>
        CollectorTiming.Analyze(design.Branches, Rpm);

    /// <summary>
    /// Order spectrum of the collector superposition, from geometry alone.
    /// </summary>
    public OrderSpectrum Spectrum(ExhaustSoundDesign design, double? rpm = null) =>
        CollectorSpectrum.At(design, rpm ?? Rpm, PulseWidthDeg, SpectrumCycles);

    /// <summary>Cycles synthesised before transforming; sets the order resolution.</summary>
    public int SpectrumCycles { get; set; } = CollectorSpectrum.DefaultCycles;

    /// <summary>
    /// Fraction of the collector's energy that is NOT on the firing order or
    /// its harmonics — the single number that separates a howl from a warble.
    /// </summary>
    public double NonFiringEnergyFraction(ExhaustSoundDesign design) =>
        1.0 - CharacterMetrics.OrderPurityIndex(Spectrum(design), design.FiringOrder);

    // ---- Figures ----------------------------------------------------------

    /// <summary>
    /// Collector arrival timing: where each cylinder's pulse lands, against
    /// the even grid it should land on.
    /// </summary>
    public PlotModel TimingChart()
    {
        var design = Current;
        var timing = Timing(design);
        var cylinders = design.Branches.Select(br => (double)br.Cylinder).ToList();

        var ideal = Enumerable.Range(0, design.Branches.Count)
            .Select(_ => 0.0)
            .ToList();

        return new PlotModel
        {
            Title = $"Collector arrival timing — {design.Name}",
            Subtitle = $"{Rpm:F0} rpm · {FidelityNote(design)}",
            XAxis = new PlotAxis("Cylinder", 0.5, design.Branches.Count + 0.5, "", cylinders),
            YAxis = new PlotAxis("Timing error", Bound(timing.TimingErrorDeg, -5), Bound(timing.TimingErrorDeg, 5), "°"),
            Series =
            [
                new PlotSeries("Arrival error", cylinders, timing.TimingErrorDeg, "Brush.Accent", PlotSeriesKind.Bar),
                new PlotSeries("Even grid", cylinders, ideal, "Brush.TextSecondary", PlotSeriesKind.Dashed),
            ],
            Notes =
            [
                $"Ideal spacing {timing.IdealSpacingDeg:F0}° between pulses; actual "
                + $"{timing.SpacingDeg.Min():F1}–{timing.SpacingDeg.Max():F1}°.",
                $"Worst error {timing.MaxAbsTimingErrorDeg:F1}° at this speed. A fixed transit mismatch is "
                + "6·N·Δτ crank degrees, so it grows with rpm — equal lengths are the only way to be even "
                + "at every speed.",
            ],
        };
    }

    /// <summary>
    /// Order spectrum with A and B overlaid, firing-order harmonics marked.
    /// </summary>
    public PlotModel OrderSpectrumChart(int maxOrder = 12)
    {
        var sa = Spectrum(A);
        var sb = Spectrum(B);

        var orders = sa.Orders.Where(o => o <= maxOrder).ToList();
        var levelA = orders.Select(o => sa.Level(o)).ToList();
        var levelB = orders.Select(o => Math.Min(sb.MaxOrder, o) is var clamped && sb.Contains(o)
            ? sb.Level(o)
            : double.NaN).ToList();

        var all = levelA.Concat(levelB).Where(double.IsFinite).ToList();
        var floor = Math.Max(-80.0, Math.Floor((all.Count > 0 ? all.Min() : -60) / 10) * 10);

        var markers = new List<PlotMarker>();
        for (var k = 1; k * A.FiringOrder <= maxOrder; k++)
        {
            markers.Add(new PlotMarker(k * A.FiringOrder, $"{k * A.FiringOrder:0.#}", "Brush.Success"));
        }

        return new PlotModel
        {
            Title = "Order spectrum",
            Subtitle = $"{Rpm:F0} rpm · dashed marks are the firing order and its harmonics",
            XAxis = new PlotAxis("Order", 0, maxOrder, ""),
            YAxis = new PlotAxis("Level", floor, 0, "dB"),
            Series =
            [
                new PlotSeries(A.Name, orders, levelA, "Brush.Accent"),
                new PlotSeries(B.Name, orders, levelB, "Brush.Info", PlotSeriesKind.Dashed),
            ],
            Markers = markers,
            Notes =
            [
                $"{A.Name}: {NonFiringEnergyFraction(A) * 100:F1}% of the energy is off the firing harmonics.",
                $"{B.Name}: {NonFiringEnergyFraction(B) * 100:F1}%.",
                "Energy between the marked orders is what a listener hears as warble or rumble rather than tone.",
            ],
        };
    }

    /// <summary>
    /// Order energy against engine speed — the waterfall. Shows the thing a
    /// single-speed spectrum cannot: whether the character HOLDS as the engine
    /// revs, or drifts because the timing error grows with speed.
    /// </summary>
    public PlotModel Waterfall(double fromRpm = 1500, double toRpm = 7200, int steps = 48, int maxOrder = 12)
    {
        var design = Current;
        var rows = Math.Max(2, steps);
        var probe = Spectrum(design, fromRpm);
        var orders = probe.Orders.Where(o => o <= maxOrder).ToList();
        var columns = orders.Count;

        var values = new float[rows * columns];
        var min = float.MaxValue;
        var max = float.MinValue;

        for (var r = 0; r < rows; r++)
        {
            var rpm = fromRpm + ((toRpm - fromRpm) * r / (rows - 1.0));
            var spectrum = Spectrum(design, rpm);
            for (var c = 0; c < columns; c++)
            {
                var level = (float)Math.Max(-80.0, spectrum.Level(orders[c]));
                values[(r * columns) + c] = level;
                min = Math.Min(min, level);
                max = Math.Max(max, level);
            }
        }

        return new PlotModel
        {
            Title = $"Order waterfall — {design.Name}",
            Subtitle = $"{FidelityNote(design)} · {fromRpm:F0}–{toRpm:F0} rpm",
            XAxis = new PlotAxis("Order", 0, maxOrder, ""),
            YAxis = new PlotAxis("Engine speed", fromRpm, toRpm, "rpm"),
            HeatMap = new HeatMapLayer(values, columns, rows, min, max, "Level (dB)"),
            Notes =
            [
                "Vertical stripes mean the character holds as the engine revs. Stripes that lean or smear "
                + "mean the arrival timing is changing with speed, which is what unequal lengths do.",
            ],
        };
    }

    /// <summary>
    /// Character radar: A, B and the nearest named target on one set of axes.
    /// </summary>
    public PlotModel CharacterRadar(CharacterProfile? profileA = null, CharacterProfile? profileB = null)
    {
        var axes = new[] { "Purity", "Half-order", "Decay", "Evenness" };
        var index = Enumerable.Range(1, axes.Length).Select(i => (double)i).ToList();

        var pa = profileA ?? InstantProfile(A);
        var pb = profileB ?? InstantProfile(B);

        return new PlotModel
        {
            Title = "Character",
            Subtitle = $"{Rpm:F0} rpm",
            XAxis = new PlotAxis("", 0.5, axes.Length + 0.5, "", index),
            YAxis = new PlotAxis("Score", 0, 1, ""),
            Series =
            [
                new PlotSeries(A.Name, index, RadarValues(pa), "Brush.Accent", PlotSeriesKind.Bar),
                new PlotSeries(B.Name, index, RadarValues(pb), "Brush.Info", PlotSeriesKind.Scatter),
            ],
            Notes =
            [
                "Purity: energy on the firing harmonics. Half-order: energy on the half orders that make a "
                + "warble. Decay: how fast the harmonics fall away. Evenness: how alike the harmonics are.",
                "Axes are " + string.Join(", ", axes.Select((n, i) => $"{i + 1} {n}")) + ".",
            ],
        };
    }

    // ---- Silencing --------------------------------------------------------

    /// <summary>Expansion-chamber silencer geometry the TMM plot is driven by.</summary>
    public double PipeDiameterMm { get; set; } = 54.0;

    public double ChamberDiameterMm { get; set; } = 130.0;

    public double ChamberLengthMm { get; set; } = 320.0;

    /// <summary>
    /// Transmission loss of a simple expansion-chamber silencer against
    /// frequency, from the transfer-matrix model (plan §8.4's
    /// interactive-TMM-then-refine pattern).
    ///
    /// The TMM is linear-acoustic and runs a 512-point sweep in a couple of
    /// milliseconds, so this follows a geometry slider directly — which is the
    /// whole reason the plan asks for it. It is an ESTIMATE: it knows nothing
    /// about mean flow, finite amplitude or temperature gradient, all of which
    /// the nonlinear solve does, and the fidelity note says so.
    /// </summary>
    public PlotModel TransmissionLoss(double fromHz = 20, double toHz = 3000, int points = 512)
    {
        var pipeArea = Area(PipeDiameterMm);
        var chamberArea = Area(ChamberDiameterMm);

        var network = new AcousticNetwork(AcousticMedium.Air20C, pipeArea, pipeArea);
        network.Elements.Add(new AreaDiscontinuityElement(pipeArea, chamberArea));
        network.Elements.Add(new UniformDuctElement(ChamberLengthMm / 1000.0, chamberArea));
        network.Elements.Add(new AreaDiscontinuityElement(pipeArea, chamberArea));

        var frequencies = new double[points];
        for (var i = 0; i < points; i++)
        {
            frequencies[i] = fromHz + ((toHz - fromHz) * i / (points - 1.0));
        }

        var loss = network.TransmissionLossSweep(frequencies);

        // An expansion chamber passes freely wherever its length is a whole
        // number of half wavelengths — the troughs are the design's weakness
        // and the reason the length matters.
        var speed = AcousticMedium.Air20C.SoundSpeed;
        var passBands = new List<PlotMarker>();
        for (var n = 1; n * speed / (2.0 * (ChamberLengthMm / 1000.0)) <= toHz && n <= 4; n++)
        {
            passBands.Add(new PlotMarker(
                n * speed / (2.0 * (ChamberLengthMm / 1000.0)),
                n == 1 ? "pass-through" : "",
                "Brush.Warning"));
        }

        var expansionRatio = chamberArea / pipeArea;

        return new PlotModel
        {
            Title = "Transmission loss",
            Subtitle = $"Expansion chamber Ø{ChamberDiameterMm:F0} × {ChamberLengthMm:F0} mm on Ø{PipeDiameterMm:F0} pipe "
                       + "· linear-acoustic estimate",
            XAxis = new PlotAxis("Frequency", fromHz, toHz, "Hz"),
            YAxis = new PlotAxis("Transmission loss", 0, Math.Max(20, Ceil(loss, 5)), "dB"),
            Series = [new PlotSeries("TL", frequencies, loss, "Brush.Accent")],
            Markers = passBands,
            Notes =
            [
                $"Expansion ratio {expansionRatio:F1}:1. Peak loss rises with the ratio; the troughs do not "
                + "move with it, only with the chamber's length.",
                $"The chamber passes freely every {speed / (2.0 * (ChamberLengthMm / 1000.0)):F0} Hz, where its "
                + "length is a whole number of half wavelengths — which is why a single chamber is never enough.",
                "Transfer-matrix estimate: plane-wave, no mean flow, no temperature gradient. A solved run "
                + "adds all three.",
            ],
        };
    }

    private static double Area(double diameterMm) => Math.PI / 4.0 * Math.Pow(diameterMm / 1000.0, 2);

    private static double Ceil(IReadOnlyList<double> values, double step) =>
        Math.Ceiling(values.Where(double.IsFinite).DefaultIfEmpty(0).Max() / step) * step;

    // ---- Audition ---------------------------------------------------------

    /// <summary>Seconds of audio the preview audition renders.</summary>
    public double AuditionSeconds { get; set; } = 3.0;

    /// <summary>
    /// A level-matched, gapless A/B of the two designs at the current speed
    /// (plan §8.4).
    ///
    /// This is the INSTANT preview — the collector pulse train rendered
    /// straight to audio, so it follows a slider like everything else on this
    /// screen. It carries the order structure, which is what the comparison is
    /// about, and none of the radiation, propagation or mechanical layers that
    /// make a full auralisation sound like a car. Those come from the solved
    /// path; this is for deciding which design to solve.
    /// </summary>
    public AbAudition AuditionPreview(double targetLufs = -23.0)
    {
        return new AbAudition(Stem(A), Stem(B), targetLufs);

        AudioStem Stem(ExhaustSoundDesign design)
        {
            var timing = CollectorTiming.Analyze(design.Branches, Rpm);
            var amplitudes = Enumerable.Range(0, design.Branches.Count).Select(design.AmplitudeOf).ToArray();
            var samples = CollectorPulseTrain.Render(
                timing, Rpm, AuditionSeconds, Loudness.SupportedSampleRate, PulseWidthDeg, amplitudes);
            return new AudioStem(design.Name, samples, Loudness.SupportedSampleRate);
        }
    }

    // ---- "Explain this" ---------------------------------------------------

    /// <summary>
    /// The plan's §8.4 affordance: a plain sentence saying which cylinder is
    /// mistimed, by how much, WHY, and what it costs.
    ///
    /// The "why" is the part that makes it worth writing. A timing error has
    /// two independent causes — a longer pipe and cooler gas — and they are
    /// separable because transit is L/(a+u): holding one fixed and varying the
    /// other gives each contribution directly. Reporting only the error tells
    /// a user something is wrong; reporting the split tells them which one to
    /// go and change.
    /// </summary>
    public string Explain(ExhaustSoundDesign? design = null)
    {
        var d = design ?? Current;
        var timing = Timing(d);

        if (CollectorSpectrum.Attribute(d, Rpm, PulseWidthDeg) is not { } why)
        {
            return "No primaries to analyse.";
        }

        if (Math.Abs(why.ErrorDeg) < 0.5)
        {
            return $"Every pulse arrives within {timing.MaxAbsTimingErrorDeg:F1}° of the even "
                   + $"{timing.IdealSpacingDeg:F0}° grid at {Rpm:F0} rpm, so "
                   + $"{(1.0 - why.NonFiringEnergyFraction) * 100:F0}% of the exhaust energy sits on the firing "
                   + "order and its harmonics. That is what an equal-length header is for.";
        }

        // Named against the anchor cylinder, because "110 mm shorter" on its
        // own invites the reader to supply their own reference — and the only
        // one the arithmetic supports is the branch the even grid is anchored
        // on.
        var reasons = new List<string>();
        if (Math.Abs(why.LengthDeltaM) >= 0.001)
        {
            reasons.Add($"its primary is {Math.Abs(why.LengthDeltaM) * 1000:F0} mm "
                        + $"{(why.LengthDeltaM > 0 ? "longer" : "shorter")} than cylinder "
                        + $"{why.ReferenceCylinder}'s ({why.FromLengthDeg:+0.0;-0.0}°)");
        }

        if (Math.Abs(why.TemperatureDeltaK) >= 5.0)
        {
            reasons.Add($"it runs {Math.Abs(why.TemperatureDeltaK):F0} K "
                        + $"{(why.TemperatureDeltaK < 0 ? "cooler" : "hotter")} "
                        + $"({why.FromTemperatureDeg:+0.0;-0.0}°)");
        }

        var because = reasons.Count > 0 ? " because " + string.Join(" and ", reasons) : "";

        return $"Cylinder {why.Cylinder}'s pulse arrives {Math.Abs(why.ErrorDeg):F0}° "
               + $"{(why.ErrorDeg > 0 ? "late" : "early")} at {Rpm:F0} rpm{because}. "
               + $"This puts {why.NonFiringEnergyFraction * 100:F0}% of the exhaust energy into "
               + "non-firing orders.";
    }
    /// <summary>
    /// How the comparison reads: the sentence the M50 story ends on.
    /// </summary>
    public string CompareSummary()
    {
        var ta = Timing(A);
        var tb = Timing(B);
        var na = NonFiringEnergyFraction(A);
        var nb = NonFiringEnergyFraction(B);

        var better = nb < na ? B : A;
        var worse = nb < na ? A : B;
        var betterFraction = Math.Min(na, nb);
        var worseFraction = Math.Max(na, nb);

        return $"{better.Name} puts {betterFraction * 100:F0}% of its exhaust energy off the firing "
               + $"harmonics against {worseFraction * 100:F0}% for {worse.Name}, and its worst arrival error "
               + $"is {(better == A ? ta : tb).MaxAbsTimingErrorDeg:F1}° against "
               + $"{(worse == A ? ta : tb).MaxAbsTimingErrorDeg:F1}°. "
               + "Off-harmonic energy is what a listener hears as warble rather than tone.";
    }

    // ---- Internals --------------------------------------------------------

    /// <summary>
    /// The order-domain half of a character profile, from the instant model.
    /// The spectral metrics — centroid, rasp, tonal-to-noise — need real
    /// radiated audio and are left to the solved path rather than guessed at
    /// from a pulse train.
    /// </summary>
    private CharacterProfile InstantProfile(ExhaustSoundDesign design)
    {
        var spectrum = Spectrum(design);
        return new CharacterProfile
        {
            OrderPurityIndex = CharacterMetrics.OrderPurityIndex(spectrum, design.FiringOrder),
            HalfOrderRatio = CharacterMetrics.HalfOrderRatio(spectrum),
            HarmonicDecaySlopeDbPerOrder = CharacterMetrics.HarmonicDecaySlope(spectrum, design.FiringOrder),
            OrderToOrderVarianceDb = CharacterMetrics.OrderToOrderVariance(spectrum, design.FiringOrder),
            SpectralCentroidHz = double.NaN,
            RaspIndex = double.NaN,
            RumbleIndex = double.NaN,
            TonalToNoiseRatio = double.NaN,
            DroneRisk = double.NaN,
        };
    }

    private static IReadOnlyList<double> RadarValues(CharacterProfile p) =>
    [
        Math.Clamp(p.OrderPurityIndex, 0, 1),
        Math.Clamp(1.0 - p.HalfOrderRatio, 0, 1),
        Math.Clamp(Math.Abs(p.HarmonicDecaySlopeDbPerOrder) / 12.0, 0, 1),
        Math.Clamp(1.0 - (p.OrderToOrderVarianceDb / 20.0), 0, 1),
    ];

    private string FidelityNote(ExhaustSoundDesign design)
    {
        _ = design;
        return Fidelity switch
        {
            SoundFidelity.Solved => "from the solved gas state",
            _ => "instant estimate from geometry — a solve will refine it",
        };
    }

    private static double Bound(IReadOnlyList<double> values, double pad)
    {
        var extreme = pad < 0
            ? Math.Min(values.DefaultIfEmpty(0).Min(), 0)
            : Math.Max(values.DefaultIfEmpty(0).Max(), 0);
        var magnitude = Math.Max(Math.Abs(extreme) * 1.3, Math.Abs(pad));
        return pad < 0 ? -magnitude : magnitude;
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
