using WaveBench.Core.Numerics;

namespace WaveBench.Acoustics.Metrics;

/// <summary>
/// The engine-specific character metrics of plan §3.7 — the ones that
/// actually discriminate header designs, as opposed to the standardised
/// psychoacoustic metrics (see <see cref="PsychoacousticStatus"/> for what
/// is and is not implemented).
///
/// These are WaveBench definitions, not standards: each is defined here
/// precisely so a number can be reproduced and argued with, and none of them
/// claims a standard's authority.
/// </summary>
public sealed record CharacterProfile
{
    /// <summary>§3.2 — howl vs warble. 1.0 = all energy on firing-order harmonics.</summary>
    public required double OrderPurityIndex { get; init; }

    /// <summary>§3.7 — rumble / lope. Half-order energy ÷ integer-order energy.</summary>
    public required double HalfOrderRatio { get; init; }

    /// <summary>§3.7 — dB per order across the firing-order harmonics. Mellow ⇐ steep negative.</summary>
    public required double HarmonicDecaySlopeDbPerOrder { get; init; }

    /// <summary>§3.7 — evenness of the harmonic ladder, dB σ about the decay fit.</summary>
    public required double OrderToOrderVarianceDb { get; init; }

    /// <summary>§3.7 — energy-weighted mean frequency, Hz. Bright vs dark.</summary>
    public required double SpectralCentroidHz { get; init; }

    /// <summary>§3.7 — energy in 2–6 kHz ÷ total. Rasp / harshness.</summary>
    public required double RaspIndex { get; init; }

    /// <summary>§3.7 — low-frequency energy weighted by 20–100 Hz modulation depth. Crossplane signature.</summary>
    public required double RumbleIndex { get; init; }

    /// <summary>§3.7 — tonal energy ÷ broadband energy. Musical vs roaring.</summary>
    public required double TonalToNoiseRatio { get; init; }

    /// <summary>§3.7 — order energy in the 30–120 Hz band, the in-cabin drone risk.</summary>
    public required double DroneRisk { get; init; }

    /// <summary>Distance to a target profile in normalised metric space (0 = identical).</summary>
    public double DistanceTo(CharacterProfile target)
    {
        static double Term(double a, double b, double scale) => Math.Pow((a - b) / scale, 2);
        return Math.Sqrt(
            Term(OrderPurityIndex, target.OrderPurityIndex, 0.3)
            + Term(HalfOrderRatio, target.HalfOrderRatio, 1.0)
            + Term(HarmonicDecaySlopeDbPerOrder, target.HarmonicDecaySlopeDbPerOrder, 6.0)
            + Term(SpectralCentroidHz, target.SpectralCentroidHz, 1500.0)
            + Term(RaspIndex, target.RaspIndex, 0.25)
            + Term(RumbleIndex, target.RumbleIndex, 0.5)
            + Term(TonalToNoiseRatio, target.TonalToNoiseRatio, 5.0));
    }
}

/// <summary>Computes the §3.7 character metric set from a signal and its order spectrum.</summary>
public static class CharacterAnalysis
{
    public static CharacterProfile Analyse(
        ReadOnlySpan<double> signal, double sampleRate, double rpm, double firingOrder)
    {
        var spectrum = OrderAnalysis.AtConstantSpeed(signal, sampleRate, rpm, maxOrder: 12.0 * firingOrder);
        var magnitude = Fft.MagnitudeSpectrum(signal, out var padded);

        return new CharacterProfile
        {
            OrderPurityIndex = CharacterMetrics.OrderPurityIndex(spectrum, firingOrder),
            HalfOrderRatio = CharacterMetrics.HalfOrderRatio(spectrum),
            HarmonicDecaySlopeDbPerOrder = CharacterMetrics.HarmonicDecaySlope(spectrum, firingOrder),
            OrderToOrderVarianceDb = CharacterMetrics.OrderToOrderVariance(spectrum, firingOrder),
            SpectralCentroidHz = SpectralCentroid(magnitude, sampleRate, padded),
            RaspIndex = BandEnergyFraction(magnitude, sampleRate, padded, 2000.0, 6000.0),
            RumbleIndex = RumbleIndex(signal, sampleRate, magnitude, padded),
            TonalToNoiseRatio = TonalToNoiseRatio(magnitude),
            DroneRisk = BandEnergyFraction(magnitude, sampleRate, padded, 30.0, 120.0),
        };
    }

    /// <summary>Energy-weighted mean frequency, Hz.</summary>
    public static double SpectralCentroid(double[] magnitude, double sampleRate, int padded)
    {
        double weighted = 0, total = 0;
        for (var bin = 1; bin < magnitude.Length; bin++)
        {
            var energy = magnitude[bin] * magnitude[bin];
            weighted += energy * Fft.BinFrequency(bin, sampleRate, padded);
            total += energy;
        }

        return total > 0 ? weighted / total : 0.0;
    }

    /// <summary>Fraction of total energy inside a frequency band.</summary>
    public static double BandEnergyFraction(
        double[] magnitude, double sampleRate, int padded, double lowHz, double highHz)
    {
        double band = 0, total = 0;
        for (var bin = 1; bin < magnitude.Length; bin++)
        {
            var f = Fft.BinFrequency(bin, sampleRate, padded);
            var energy = magnitude[bin] * magnitude[bin];
            total += energy;
            if (f >= lowHz && f <= highHz)
            {
                band += energy;
            }
        }

        return total > 0 ? band / total : 0.0;
    }

    /// <summary>
    /// Rumble: low-frequency energy weighted by how deeply the envelope is
    /// modulated at 20–100 Hz (§3.7). A crossplane V8's uneven firing
    /// modulates the envelope at exactly those rates; a flat-plane's does not.
    /// </summary>
    public static double RumbleIndex(
        ReadOnlySpan<double> signal, double sampleRate, double[] magnitude, int padded)
    {
        var lowEnergy = BandEnergyFraction(magnitude, sampleRate, padded, 20.0, 250.0);

        // Envelope via rectify + one-pole smoothing at ~200 Hz.
        var envelope = new double[signal.Length];
        var alpha = 1.0 - Math.Exp(-2.0 * Math.PI * 200.0 / sampleRate);
        var state = 0.0;
        for (var i = 0; i < signal.Length; i++)
        {
            state += alpha * (Math.Abs(signal[i]) - state);
            envelope[i] = state;
        }

        var mean = envelope.Average();
        for (var i = 0; i < envelope.Length; i++)
        {
            envelope[i] -= mean;
        }

        var envelopeSpectrum = Fft.MagnitudeSpectrum(envelope, out var envPadded);
        var modulationDepth = mean > 0
            ? BandEnergySum(envelopeSpectrum, sampleRate, envPadded, 20.0, 100.0) / (mean * envelope.Length)
            : 0.0;

        return lowEnergy * modulationDepth;
    }

    private static double BandEnergySum(double[] magnitude, double sampleRate, int padded, double lowHz, double highHz)
    {
        double sum = 0;
        for (var bin = 1; bin < magnitude.Length; bin++)
        {
            var f = Fft.BinFrequency(bin, sampleRate, padded);
            if (f >= lowHz && f <= highHz)
            {
                sum += magnitude[bin];
            }
        }

        return sum;
    }

    /// <summary>
    /// Tonal-to-noise ratio: energy in spectral peaks that stand clear of
    /// their local neighbourhood, divided by the rest. Musical vs roaring.
    /// </summary>
    public static double TonalToNoiseRatio(double[] magnitude, int neighbourhood = 12, double peakFactor = 4.0)
    {
        double tonal = 0, noise = 0;
        for (var bin = neighbourhood; bin < magnitude.Length - neighbourhood; bin++)
        {
            double localSum = 0;
            var count = 0;
            for (var k = bin - neighbourhood; k <= bin + neighbourhood; k++)
            {
                if (Math.Abs(k - bin) > 2)
                {
                    localSum += magnitude[k];
                    count++;
                }
            }

            var local = count > 0 ? localSum / count : 0.0;
            var energy = magnitude[bin] * magnitude[bin];
            if (magnitude[bin] > peakFactor * local)
            {
                tonal += energy;
            }
            else
            {
                noise += energy;
            }
        }

        return noise > 0 ? tonal / noise : double.PositiveInfinity;
    }
}

/// <summary>
/// Named target profiles as vectors in metric space, each with the written
/// mechanism the plan §3.7 demands. There is no universal "good": these are
/// destinations a user picks, not a ranking.
/// </summary>
public sealed record SoundTarget(string Name, string Mechanism, CharacterProfile Profile)
{
    private static CharacterProfile P(
        double opi, double halfOrder, double decay, double centroid, double rasp,
        double rumble, double tonal, double drone, double variance = 2.0) => new()
    {
        OrderPurityIndex = opi,
        HalfOrderRatio = halfOrder,
        HarmonicDecaySlopeDbPerOrder = decay,
        OrderToOrderVarianceDb = variance,
        SpectralCentroidHz = centroid,
        RaspIndex = rasp,
        RumbleIndex = rumble,
        TonalToNoiseRatio = tonal,
        DroneRisk = drone,
    };

    /// <summary>Very high OPI, negligible half-order, ≈ −6 dB/order, moderate sharpness.</summary>
    public static SoundTarget StraightSixHowl { get; } = new(
        "Straight-six howl",
        "Equal-length primaries put all six pulses exactly 120° apart at every rpm, so energy "
        + "collapses onto the 3rd order and its harmonic ladder. Pure, ordered, rpm-invariant.",
        P(opi: 0.95, halfOrder: 0.02, decay: -6.0, centroid: 900.0, rasp: 0.08, rumble: 0.05, tonal: 12.0, drone: 0.10));

    /// <summary>High OPI at 4th order, strong upper harmonics, high centroid.</summary>
    public static SoundTarget FlatPlaneScream { get; } = new(
        "Flat-plane scream",
        "Even 180° bank spacing gives a clean 4th order; short primaries and a high firing "
        + "frequency push energy up the ladder, raising the spectral centroid.",
        P(opi: 0.92, halfOrder: 0.03, decay: -3.5, centroid: 2200.0, rasp: 0.28, rumble: 0.05, tonal: 9.0, drone: 0.06));

    /// <summary>Deliberately high half-order ratio and rumble, low sharpness.</summary>
    public static SoundTarget CrossplaneRumble { get; } = new(
        "Crossplane rumble",
        "90-180-270-180 bank spacing breaks the cancellation, populating half-orders and "
        + "modulating the envelope at 20–100 Hz. The lope IS the timing error.",
        P(opi: 0.45, halfOrder: 0.9, decay: -5.0, centroid: 700.0, rasp: 0.07, rumble: 0.55, tonal: 4.0, drone: 0.22));

    /// <summary>Very high firing frequency, rich stepped-header comb, high tonality.</summary>
    public static SoundTarget NaF1Scream { get; } = new(
        "NA F1 scream",
        "A V10 at 18 000 rpm fires at 1500 Hz — the fundamental sits where a road V8's 6th "
        + "harmonic does. Progressive header steps add a comb of shallow resonances.",
        P(opi: 0.90, halfOrder: 0.02, decay: -2.5, centroid: 3200.0, rasp: 0.35, rumble: 0.02, tonal: 14.0, drone: 0.02));

    /// <summary>High tonality, low roughness and sharpness, zero drone in the cruise band.</summary>
    public static SoundTarget RefinedGt { get; } = new(
        "Refined GT",
        "Ordered harmonics with a steep decay and, critically, no order energy landing in the "
        + "30–120 Hz cabin band at cruise rpm — drone is the failure mode here, not loudness.",
        P(opi: 0.88, halfOrder: 0.05, decay: -8.0, centroid: 600.0, rasp: 0.04, rumble: 0.10, tonal: 10.0, drone: 0.02));

    /// <summary>Maximise OPI and tonality subject to the dB(C) limits.</summary>
    public static SoundTarget FsaeCompliantCharismatic { get; } = new(
        "FSAE compliant + charismatic",
        "Character comes from order purity, not level: hold the 110 dB(C) limit while keeping "
        + "the harmonic ladder clean, so the car sounds deliberate rather than merely quiet.",
        P(opi: 0.90, halfOrder: 0.04, decay: -5.0, centroid: 1400.0, rasp: 0.12, rumble: 0.06, tonal: 10.0, drone: 0.08));

    public static IReadOnlyList<SoundTarget> All { get; } =
    [
        StraightSixHowl, FlatPlaneScream, CrossplaneRumble, NaF1Scream, RefinedGt, FsaeCompliantCharismatic,
    ];

    /// <summary>Rank the shipped targets by how close a design sits to each.</summary>
    public static IReadOnlyList<(SoundTarget Target, double Distance)> Rank(CharacterProfile design) =>
        All.Select(t => (t, design.DistanceTo(t.Profile))).OrderBy(x => x.Item2).ToList();
}

/// <summary>
/// Reference Match (plan §3.7): extract a character fingerprint from the
/// user's own recording so it can be used as an optimisation target.
///
/// <b>The audio never leaves the machine, is never committed, and is
/// discarded after extraction</b> — this API takes samples and returns
/// metrics, and deliberately offers no way to persist the recording.
/// </summary>
public static class ReferenceMatch
{
    /// <summary>
    /// Fingerprint a recording whose engine speed is known (or was tracked
    /// from its firing order). Returns metrics only — the samples are not
    /// retained by anything here.
    /// </summary>
    public static CharacterProfile Extract(
        ReadOnlySpan<double> recording, double sampleRate, double rpm, double firingOrder) =>
        CharacterAnalysis.Analyse(recording, sampleRate, rpm, firingOrder);

    /// <summary>
    /// Track engine speed from the firing order: find the rpm whose firing
    /// frequency best explains the spectrum, searching a plausible range.
    /// Enables fingerprinting a recording without a tachometer channel.
    /// </summary>
    public static double TrackRpm(
        ReadOnlySpan<double> recording, double sampleRate, double firingOrder,
        double minRpm = 800.0, double maxRpm = 12_000.0)
    {
        var magnitude = Fft.MagnitudeSpectrum(recording, out var padded);

        var bestRpm = minRpm;
        var bestScore = double.NegativeInfinity;
        for (var rpm = minRpm; rpm <= maxRpm; rpm += 5.0)
        {
            // Score = summed magnitude at the firing frequency and its first
            // four harmonics; the true speed lights all of them at once.
            var fundamental = rpm / 60.0 * firingOrder;
            double score = 0;
            for (var h = 1; h <= 5; h++)
            {
                var bin = (int)Math.Round(h * fundamental * padded / sampleRate);
                if (bin > 0 && bin < magnitude.Length)
                {
                    score += magnitude[bin];
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestRpm = rpm;
            }
        }

        return bestRpm;
    }
}

/// <summary>
/// Honest status of the standardised psychoacoustic metrics (plan §3.7).
/// These are NOT implemented, and this type exists so the gap is visible in
/// code and in the UI rather than buried in a document.
///
/// The blocker is verification, not effort: the plan requires each metric to
/// match published reference verification signals within its standard's
/// tolerance, and shipping a plausible-but-unverified loudness or sharpness
/// figure would be worse than shipping none — it is a number users would
/// trust and act on.
/// </summary>
public static class PsychoacousticStatus
{
    public sealed record MetricStatus(string Metric, string Standard, string Unit, bool Implemented, string Note);

    public static IReadOnlyList<MetricStatus> All { get; } =
    [
        new("A/C-weighted SPL", "IEC 61672-1", "dB(A)/dB(C)", true,
            "Verified against the standard's published nominal table, 10 Hz–20 kHz."),
        new("Time weighting F/S/I", "IEC 61672-1", "s", true,
            "Exact time constants 125 ms / 1 s / 35 ms."),
        new("Loudness (stationary)", "ISO 532-1 (Zwicker)", "sone", false,
            "Needs the standard's exact critical-band slope algorithm plus its verification "
            + "signals. Deferred rather than approximated."),
        new("Loudness (Moore–Glasberg)", "ISO 532-3", "sone", false,
            "Deferred for the same reason as ISO 532-1: the excitation-pattern model needs its "
            + "published verification signals before a number can be trusted."),
        new("Sharpness", "DIN 45692", "acum", false,
            "Defined on the ISO 532-1 specific-loudness distribution, so it is blocked on that."),
        new("Loudness/tonality/roughness", "ECMA-418-2 (Sottek)", "sone_HMS/tuHMS/asper_HMS", false,
            "Large hearing model; requires the reference implementation's signals to verify."),
        new("Fluctuation strength", "Zwicker & Fastl", "vacil", false, "Blocked on the loudness model."),
        new("Tonality", "DIN 45681", "—", false,
            "Deferred. The engine-specific tonal-to-noise ratio here serves the same design "
            + "question without claiming the standard's authority."),
        new("Speech interference", "ANSI S3.5", "—", false, "Cabin only; needs the cabin transfer function (Phase 20+)."),
    ];

    public static IReadOnlyList<MetricStatus> Implemented => All.Where(m => m.Implemented).ToList();

    public static IReadOnlyList<MetricStatus> Outstanding => All.Where(m => !m.Implemented).ToList();
}
