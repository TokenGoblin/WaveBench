using System.Numerics;
using WaveBench.Core.Numerics;

namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// Applies a <see cref="PropagationPath"/> to a rendered stem, so what you
/// hear is what reaches the listener rather than what leaves the tailpipe
/// (plan §3.5/§3.6).
///
/// This is not cosmetic. Between the outlet and a drive-by microphone the
/// signal loses its top end to air absorption and picks up a ground-reflection
/// comb whose first notch sits at 1/(2·Δt) — a few hundred Hz at typical
/// geometry, right in the middle of what makes an exhaust note recognisable.
/// Auditioning a header design on the source signal answers a question nobody
/// asked.
///
/// Implemented as whole-signal FFT convolution against the path's complex
/// response. Renders are seconds long and the impulse response is
/// milliseconds, so there is no reason to block-process; the signal is padded
/// past the reflection's excess delay so the circular convolution cannot wrap
/// the tail onto the head.
///
/// <b>Not modelled:</b> source directivity. A preset's azimuth positions the
/// microphone but does not attenuate off-axis, because that needs the outlet's
/// radiation pattern — the plan puts directivity with the cabin work in
/// Phase 20. A render is therefore on-axis in character regardless of azimuth,
/// and <see cref="Describe"/> says so rather than letting the preset name
/// imply more than was applied.
/// </summary>
public static class ListenerChain
{
    /// <summary>
    /// Filter a stem through the path. Returns a new stem; the input is not
    /// modified.
    /// </summary>
    public static AudioStem Apply(AudioStem stem, PropagationPath path, double? soundSpeedOverride = null)
    {
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(path);

        if (stem.Samples.Length == 0)
        {
            return stem with { Samples = [] };
        }

        // Pad past the reflection's excess delay, then to a power of two.
        var guard = (int)Math.Ceiling(path.GroundReflectionDelay * stem.SampleRate) + 2;
        var n = 1;
        while (n < stem.Samples.Length + guard)
        {
            n <<= 1;
        }

        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < stem.Samples.Length; i++)
        {
            re[i] = stem.Samples[i];
        }

        Fft.Transform(re, im);

        // Multiply by H(f) over the lower half, mirroring conjugate-symmetric
        // onto the upper half so the result comes back real.
        var half = n / 2;
        for (var k = 0; k <= half; k++)
        {
            var frequency = k * stem.SampleRate / n;
            var h = k == 0
                ? new Complex(RealDcGain(path, soundSpeedOverride), 0.0)
                : path.ResponseRelativeToDirect(frequency, soundSpeedOverride);

            var scaledRe = re[k] * h.Real - im[k] * h.Imaginary;
            var scaledIm = re[k] * h.Imaginary + im[k] * h.Real;
            re[k] = scaledRe;
            im[k] = scaledIm;

            if (k > 0 && k < half)
            {
                var mirror = n - k;
                var mRe = re[mirror] * h.Real + im[mirror] * h.Imaginary;
                var mIm = -re[mirror] * h.Imaginary + im[mirror] * h.Real;
                re[mirror] = mRe;
                im[mirror] = mIm;
            }
        }

        // Nyquist must stay real or the inverse transform leaks an imaginary
        // part into every sample.
        im[half] = 0.0;

        InverseInPlace(re, im);

        var output = new float[stem.Samples.Length];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (float)re[i];
        }

        return stem with { Samples = output };
    }

    /// <summary>
    /// Apply a listener preset. <paramref name="sourceHeightM"/> is the outlet
    /// height above ground, which sets the ground-reflection geometry.
    /// </summary>
    public static AudioStem Apply(
        AudioStem stem,
        ListenerPreset preset,
        double sourceHeightM = 0.35,
        double groundReflectionCoefficient = GroundSurface.Asphalt,
        double temperatureK = 293.15,
        double relativeHumidityPercent = 70.0) =>
        Apply(stem, preset.ToPath(sourceHeightM, groundReflectionCoefficient, temperatureK, relativeHumidityPercent));

    /// <summary>
    /// Broadband gain the chain applies, dB — the level change from moving the
    /// microphone out to the listener, before any loudness normalisation.
    /// </summary>
    public static double InsertionGainDb(AudioStem before, AudioStem after)
    {
        static double MeanSquare(float[] samples)
        {
            double sum = 0;
            foreach (var sample in samples)
            {
                sum += (double)sample * sample;
            }

            return samples.Length > 0 ? sum / samples.Length : 0.0;
        }

        var input = MeanSquare(before.Samples);
        var output = MeanSquare(after.Samples);
        return input > 0 && output > 0 ? 10.0 * Math.Log10(output / input) : double.NegativeInfinity;
    }

    /// <summary>
    /// What was actually applied, for render metadata. States the omission as
    /// well as the inclusions, so a listener preset in a filename cannot imply
    /// a directivity model that does not exist.
    /// </summary>
    public static string Describe(ListenerPreset preset, PropagationPath path) =>
        $"{preset.Name} — {preset.SlantDistanceM:F2} m slant, receiver {preset.ReceiverHeightM:F2} m, " +
        $"{(path.GroundReflectionCoefficient > 0 ? $"ground reflection R={path.GroundReflectionCoefficient:F2} " +
            $"(excess delay {path.GroundReflectionDelay * 1000.0:F2} ms)" : "free field")}, " +
        $"ISO 9613-1 absorption at {path.TemperatureK - 273.15:F0} °C / {path.RelativeHumidityPercent:F0}% RH; " +
        $"source directivity NOT modelled (azimuth {preset.AzimuthDeg:F0}° positions only)";

    /// <summary>
    /// DC gain. The path response is singular at f = 0 (k = 0 makes both terms
    /// pure real but the absorption coefficient is evaluated at zero), so the
    /// DC bin takes the amplitude sum directly. Renders are AC-coupled in
    /// practice; this exists so the bin is finite rather than NaN.
    /// </summary>
    private static double RealDcGain(PropagationPath path, double? soundSpeedOverride)
    {
        var response = path.ResponseRelativeToDirect(1e-3, soundSpeedOverride);
        return double.IsFinite(response.Real) ? response.Real : 0.0;
    }

    /// <summary>Inverse FFT via the conjugate identity: IFFT(x) = conj(FFT(conj(x)))/n.</summary>
    private static void InverseInPlace(double[] re, double[] im)
    {
        for (var i = 0; i < im.Length; i++)
        {
            im[i] = -im[i];
        }

        Fft.Transform(re, im);

        var scale = 1.0 / re.Length;
        for (var i = 0; i < re.Length; i++)
        {
            re[i] *= scale;
            im[i] *= -scale;
        }
    }
}
