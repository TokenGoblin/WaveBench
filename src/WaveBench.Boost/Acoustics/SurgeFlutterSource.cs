using WaveBench.Boost.Unsteady;

namespace WaveBench.Boost.Acoustics;

/// <summary>
/// The audible signature of a surging compressor: an amplitude modulation at
/// the compression system's own surge frequency (plan §4.8).
/// </summary>
/// <param name="ModulationFrequencyHz">
/// Read directly off <see cref="GreitzerSurgeResult.SurgeFrequencyHz"/> —
/// never a separate, independently-tunable number.
/// </param>
/// <param name="ModulationDepth">
/// 0-1 engineering severity indicator, not a validated sound-pressure
/// figure: 1.0 for deep surge (a full flow-reversal relaxation oscillation,
/// Moore &amp; Greitzer 1986), scaling with B/CriticalB below that. Exposed
/// for calibration once a real recording is available to fit against.
/// </param>
/// <param name="Classification">Carried through from the surge model.</param>
public sealed record SurgeFlutterResult(
    double ModulationFrequencyHz, double ModulationDepth, SurgeClassification Classification);

/// <summary>
/// Turns a <see cref="GreitzerSurgeResult"/> into the modulation signature a
/// listener hears as surge flutter (plan §4.8: "predicted, not sampled; it
/// falls out of the surge model"). Deliberately thin: the physics — the
/// frequency itself — lives entirely in <see cref="GreitzerSurgeModel"/>;
/// this class only shapes it into an audible envelope.
/// </summary>
public static class SurgeFlutterSource
{
    public static SurgeFlutterResult Evaluate(GreitzerSurgeResult surge)
    {
        ArgumentNullException.ThrowIfNull(surge);

        var depth = surge.Classification == SurgeClassification.Deep
            ? 1.0
            : Math.Clamp(surge.BParameter / GreitzerSurgeModel.CriticalB, 0.0, 1.0);

        return new SurgeFlutterResult(surge.SurgeFrequencyHz, depth, surge.Classification);
    }

    /// <summary>
    /// A 0-1 amplitude envelope at the flutter frequency, for shaping a
    /// broadband or tonal carrier in the auralisation layer.
    ///
    /// <b>The frequency is the physically-derived quantity gate clause 3
    /// tests; the exact waveform shape below is an engineering proxy.</b> Mild
    /// surge is modelled as the roughly sinusoidal oscillation Greitzer's
    /// linearised theory predicts near the mild/deep boundary; deep surge is
    /// modelled as a compressed (higher-harmonic-content) version of the same
    /// sinusoid, standing in for the sharper relaxation-oscillation shape
    /// Moore &amp; Greitzer (1986) describe, pending a recording to fit the
    /// exact waveform against.
    /// </summary>
    public static double[] ModulationEnvelope(SurgeFlutterResult flutter, double durationS, double sampleRate)
    {
        ArgumentNullException.ThrowIfNull(flutter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var count = (int)(durationS * sampleRate);
        var envelope = new double[count];

        // Shape exponent >1 sharpens the trough of the cycle toward the
        // choked/reversed-flow portion as depth rises, without introducing a
        // second, independently-tunable frequency.
        var shape = 1.0 + (2.0 * flutter.ModulationDepth);
        var omega = 2.0 * Math.PI * flutter.ModulationFrequencyHz;

        for (var i = 0; i < count; i++)
        {
            var t = i / sampleRate;
            var raw = 0.5 * (1.0 - Math.Cos(omega * t));
            envelope[i] = 1.0 - (flutter.ModulationDepth * Math.Pow(1.0 - raw, shape));
        }

        return envelope;
    }
}
