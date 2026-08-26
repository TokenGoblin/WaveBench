namespace WaveBench.Acoustics.Metrics;

/// <summary>
/// Sharpness — the perceived weighting of a sound toward high critical-band
/// rate, in acum. A sharp exhaust note is not necessarily a loud one: two
/// systems can measure the same dB(A) and differ entirely in how "hard" or
/// "raspy" they read, which is what this metric separates out.
///
/// DIN 45692:2009:
///   S = k · ∫₀²⁴ N'(z)·g(z)·z dz ⁄ ∫₀²⁴ N'(z) dz     [acum]
/// with the weighting
///   g(z) = 1                                for z &lt; 15.8 Bark
///   g(z) = 0.15·e^(0.42·(z − 15.8)) + 0.85  for z ≥ 15.8 Bark
/// and k = 0.11, which normalises the reference signal — narrowband noise one
/// critical band wide, centred at 1 kHz, at 60 dB — to exactly 1 acum.
///
/// The specific loudness N'(z) comes from <see cref="ZwickerLoudness"/>, so
/// sharpness inherits that method's ±5% conformance band. The weighting is
/// flat below 15.8 Bark (≈ 3.4 kHz) and rises steeply above it: the metric is
/// dominated by content above about 3 kHz.
///
/// Valid for stationary sounds. Level dependence is weak but not zero — the
/// pattern broadens with level, so the same spectrum measures slightly
/// sharper as it gets louder.
/// </summary>
public static class Sharpness
{
    /// <summary>Normalisation constant, acum (DIN 45692:2009).</summary>
    public const double K = 0.11;

    /// <summary>Bark above which the weighting rises.</summary>
    public const double WeightingKnee = 15.8;

    /// <summary>The DIN 45692 weighting function g(z).</summary>
    public static double Weighting(double bark) => bark < WeightingKnee
        ? 1.0
        : 0.15 * Math.Exp(0.42 * (bark - WeightingKnee)) + 0.85;

    /// <summary>
    /// Sharpness in acum from a specific-loudness pattern on the 0.1 Bark
    /// grid produced by <see cref="ZwickerLoudness"/>.
    /// </summary>
    public static double FromSpecificLoudness(IReadOnlyList<double> specificLoudness)
    {
        var dz = 24.0 / specificLoudness.Count;
        double weighted = 0.0;
        double total = 0.0;

        for (var i = 0; i < specificLoudness.Count; i++)
        {
            var bark = (i + 0.5) * dz;
            var n = specificLoudness[i];
            weighted += n * Weighting(bark) * bark * dz;
            total += n * dz;
        }

        // A silent signal has no spectral centroid to report; 0 acum is the
        // only honest answer, and dividing would produce NaN.
        return total > 0.0 ? K * weighted / total : 0.0;
    }

    /// <summary>Sharpness in acum from one-third-octave band levels.</summary>
    public static double FromThirdOctaveBands(
        IReadOnlyList<double> bandLevelsDb, SoundField field = SoundField.Free) =>
        FromSpecificLoudness(ZwickerLoudness.FromThirdOctaveBands(bandLevelsDb, field).SpecificLoudness);

    /// <summary>Sharpness in acum from a time signal.</summary>
    public static double FromSignal(
        ReadOnlySpan<double> pressurePa, double sampleRate, SoundField field = SoundField.Free) =>
        FromSpecificLoudness(ZwickerLoudness.FromSignal(pressurePa, sampleRate, field).SpecificLoudness);
}
