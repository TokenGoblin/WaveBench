using System.Numerics;
using WaveBench.Core.Numerics;

namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// The plan §5.6 hybrid: use the nonlinear solver where it is authoritative
/// and the TMM where it is not.
///
///   f &lt; f_hybrid  → nonlinear time-domain solution (steepening, choking,
///                    finite-amplitude wave speed, flow effects)
///   f &gt; f_hybrid  → source spectrum propagated through the linear TMM
///                    transfer function (no numerical dissipation)
///
/// The two branches are a COMPLEMENTARY pair: their gains sum to one at every
/// frequency, so the crossover neither boosts nor notches the band where they
/// meet. f_hybrid defaults to the lower of ~1.5 kHz and the mesh's measured
/// −3 dB bandwidth (docs/numerics.md §5), because above that the nonlinear
/// result is numerically dissipated rather than physical.
/// </summary>
public static class HybridSynthesis
{
    /// <summary>Crossover frequency: the plan's ≈1–2 kHz, capped by the measured bandwidth.</summary>
    public static double CrossoverFrequency(double resolvedBandwidthHz, double preferredHz = 1500.0) =>
        Math.Min(preferredHz, resolvedBandwidthHz);

    /// <summary>
    /// Complementary weight for the nonlinear branch at a frequency: a
    /// smooth first-order-style pair, w_nl + w_tmm ≡ 1.
    /// </summary>
    public static double NonlinearWeight(double frequency, double crossoverHz)
    {
        var r = frequency / crossoverHz;
        return 1.0 / (1.0 + r * r);
    }

    public static double TmmWeight(double frequency, double crossoverHz) =>
        1.0 - NonlinearWeight(frequency, crossoverHz);

    /// <summary>
    /// Combine a nonlinear time signal with a TMM-propagated version of the
    /// same source. <paramref name="tmmTransfer"/> is evaluated per frequency
    /// bin and applied to the source spectrum; the result is the
    /// complementary sum, returned in the time domain.
    ///
    /// Both branches start from the SAME source spectrum, so the crossover
    /// only chooses which propagation model carries each band — it never
    /// mixes two unrelated signals.
    /// </summary>
    public static double[] Combine(
        ReadOnlySpan<double> nonlinearSignal,
        Func<double, Complex> tmmTransfer,
        double sampleRate,
        double crossoverHz)
    {
        var n = 1;
        while (n < nonlinearSignal.Length)
        {
            n <<= 1;
        }

        var re = new double[n];
        var im = new double[n];
        nonlinearSignal.CopyTo(re);
        Fft.Transform(re, im);

        // Weight each bin, applying the TMM transfer to the high branch.
        for (var bin = 0; bin <= n / 2; bin++)
        {
            var frequency = bin * sampleRate / n;
            var wNl = NonlinearWeight(frequency, crossoverHz);
            var wTmm = 1.0 - wNl;

            var source = new Complex(re[bin], im[bin]);
            var combined = wNl * source + wTmm * (source * tmmTransfer(frequency));

            re[bin] = combined.Real;
            im[bin] = combined.Imaginary;

            // Maintain Hermitian symmetry so the inverse transform is real.
            if (bin > 0 && bin < n / 2)
            {
                re[n - bin] = combined.Real;
                im[n - bin] = -combined.Imaginary;
            }
        }

        // Inverse transform via conjugation.
        for (var i = 0; i < n; i++)
        {
            im[i] = -im[i];
        }

        Fft.Transform(re, im);

        var output = new double[nonlinearSignal.Length];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = re[i] / n;
        }

        return output;
    }

    /// <summary>
    /// Convenience: hand the high band to an <see cref="AcousticNetwork"/>'s
    /// pressure transfer function — the intended production wiring, where the
    /// TMM carries the frequencies the mesh cannot resolve.
    /// </summary>
    public static double[] Combine(
        ReadOnlySpan<double> nonlinearSignal,
        AcousticNetwork network,
        TerminationKind termination,
        double sampleRate,
        double crossoverHz) =>
        Combine(
            nonlinearSignal,
            f => f <= 0 ? Complex.One : network.PressureTransfer(f, termination),
            sampleRate,
            crossoverHz);
}
