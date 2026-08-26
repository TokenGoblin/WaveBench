namespace WaveBench.Acoustics.Metrics;

public enum Weighting
{
    /// <summary>Unweighted (linear / Z).</summary>
    Z,

    /// <summary>A-weighting — IEC 61672-1.</summary>
    A,

    /// <summary>C-weighting — IEC 61672-1 (the FSAE rules basis).</summary>
    C,
}

public enum TimeWeighting
{
    /// <summary>Fast, 125 ms exponential (the FSAE / ISO 5130 basis).</summary>
    Fast,

    /// <summary>Slow, 1 s exponential.</summary>
    Slow,

    /// <summary>Impulse, 35 ms rise.</summary>
    Impulse,
}

/// <summary>
/// IEC 61672-1 frequency weighting. The pole frequencies are the standard's
/// exact values:
///   f1 = 20.598997, f2 = 107.65265, f3 = 737.86223, f4 = 12194.217 Hz
/// with the normalisation offsets that make each curve 0 dB at 1 kHz.
///
///   A(f) = f4²f⁴ / ((f²+f1²)(f²+f4²)√((f²+f2²)(f²+f3²)))
///   C(f) = f4²f²  / ((f²+f1²)(f²+f4²))
///
/// Verified against the published nominal values of IEC 61672-1 Table 3
/// across 10 Hz – 20 kHz.
/// </summary>
public static class FrequencyWeighting
{
    private const double F1 = 20.598997;
    private const double F2 = 107.65265;
    private const double F3 = 737.86223;
    private const double F4 = 12194.217;

    // Normalisation is by division at 1 kHz, which reproduces the standard's
    // published +2.00 dB (A) and +0.06 dB (C) offsets exactly rather than
    // carrying rounded literals.

    /// <summary>Weighting in dB at a frequency.</summary>
    public static double Decibels(double frequency, Weighting weighting) => weighting switch
    {
        Weighting.Z => 0.0,
        Weighting.A => 20.0 * Math.Log10(RawA(frequency) / RawA(1000.0)),
        Weighting.C => 20.0 * Math.Log10(RawC(frequency) / RawC(1000.0)),
        _ => throw new ArgumentOutOfRangeException(nameof(weighting)),
    };

    /// <summary>Linear gain (amplitude) at a frequency.</summary>
    public static double Gain(double frequency, Weighting weighting) =>
        Math.Pow(10.0, Decibels(frequency, weighting) / 20.0);

    private static double RawA(double f)
    {
        var f2 = f * f;
        return F4 * F4 * f2 * f2
               / ((f2 + F1 * F1) * (f2 + F4 * F4)
                  * Math.Sqrt((f2 + F2 * F2) * (f2 + F3 * F3)));
    }

    private static double RawC(double f)
    {
        var f2 = f * f;
        return F4 * F4 * f2 / ((f2 + F1 * F1) * (f2 + F4 * F4));
    }

    /// <summary>Time-weighting exponential averaging constant, seconds (IEC 61672-1).</summary>
    public static double TimeConstant(TimeWeighting weighting) => weighting switch
    {
        TimeWeighting.Fast => 0.125,
        TimeWeighting.Slow => 1.0,
        TimeWeighting.Impulse => 0.035,
        _ => throw new ArgumentOutOfRangeException(nameof(weighting)),
    };
}

/// <summary>
/// Sound pressure level metering per IEC 61672: frequency weighting applied
/// in the frequency domain, then exponential time weighting on the squared
/// signal. Levels are referenced to 20 µPa.
///
/// Absolute SPL prediction from a 1D code is good to roughly ±3 dB at best
/// (plan §3.8) — see <see cref="ComplianceCheck"/>, which carries that
/// uncertainty explicitly rather than presenting a bare number.
/// </summary>
public static class SoundLevelMeter
{
    public const double ReferencePressure = 20e-6;

    /// <summary>
    /// Apply a frequency weighting to a time signal via FFT (zero-phase:
    /// magnitude only, so the time envelope is not smeared by filter phase).
    /// </summary>
    public static double[] ApplyWeighting(ReadOnlySpan<double> signal, double sampleRate, Weighting weighting)
    {
        if (weighting == Weighting.Z)
        {
            return signal.ToArray();
        }

        var n = 1;
        while (n < signal.Length)
        {
            n <<= 1;
        }

        var re = new double[n];
        var im = new double[n];
        signal.CopyTo(re);
        Core.Numerics.Fft.Transform(re, im);

        for (var bin = 0; bin <= n / 2; bin++)
        {
            var frequency = bin * sampleRate / n;
            var gain = frequency <= 0 ? 0.0 : FrequencyWeighting.Gain(frequency, weighting);
            re[bin] *= gain;
            im[bin] *= gain;
            if (bin > 0 && bin < n / 2)
            {
                re[n - bin] = re[bin];
                im[n - bin] = -im[bin];
            }
        }

        for (var i = 0; i < n; i++)
        {
            im[i] = -im[i];
        }

        Core.Numerics.Fft.Transform(re, im);

        var output = new double[signal.Length];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = re[i] / n;
        }

        return output;
    }

    /// <summary>Equivalent continuous level (Leq) of a pressure signal in Pa, dB.</summary>
    public static double EquivalentLevel(ReadOnlySpan<double> pressurePa, double sampleRate, Weighting weighting)
    {
        var weighted = ApplyWeighting(pressurePa, sampleRate, weighting);
        double sum = 0;
        foreach (var s in weighted)
        {
            sum += s * s;
        }

        var meanSquare = sum / weighted.Length;
        return 10.0 * Math.Log10(meanSquare / (ReferencePressure * ReferencePressure));
    }

    /// <summary>
    /// Maximum time-weighted level (e.g. L_Cmax with Fast) — the quantity
    /// noise rules actually specify. Exponential averaging with the
    /// standard's time constant, then the peak of that running level.
    /// </summary>
    public static double MaximumTimeWeightedLevel(
        ReadOnlySpan<double> pressurePa, double sampleRate, Weighting weighting, TimeWeighting timeWeighting)
    {
        var weighted = ApplyWeighting(pressurePa, sampleRate, weighting);
        var tau = FrequencyWeighting.TimeConstant(timeWeighting);
        var alpha = 1.0 - Math.Exp(-1.0 / (tau * sampleRate));

        var running = 0.0;
        var peak = 0.0;

        // Let the detector settle before measuring, but never discard more
        // than a quarter of the signal: a Slow (1 s) measurement on a short
        // clip would otherwise skip every sample and report −∞.
        var settle = (int)Math.Min(3 * tau * sampleRate, weighted.Length / 4);
        for (var i = 0; i < weighted.Length; i++)
        {
            running += alpha * (weighted[i] * weighted[i] - running);
            if (i >= settle && running > peak)
            {
                peak = running;
            }
        }

        return peak <= 0
            ? double.NegativeInfinity
            : 10.0 * Math.Log10(peak / (ReferencePressure * ReferencePressure));
    }

    /// <summary>Level of a pure tone of the given amplitude, dB (convenience for tests and calibration).</summary>
    public static double ToneLevel(double amplitudePa) =>
        20.0 * Math.Log10(amplitudePa / Math.Sqrt(2.0) / ReferencePressure);
}
