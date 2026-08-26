namespace WaveBench.Core.Numerics;

/// <summary>
/// In-place iterative radix-2 Cooley–Tukey FFT. Deterministic, dependency-free;
/// sized for post-processing (order analysis, transfer functions), not for
/// audio-rate streaming.
/// </summary>
public static class Fft
{
    /// <summary>In-place complex FFT; lengths must be a power of two.</summary>
    public static void Transform(double[] real, double[] imaginary)
    {
        var n = real.Length;
        if (n != imaginary.Length || (n & (n - 1)) != 0 || n == 0)
        {
            throw new ArgumentException("FFT length must be a power of two and arrays equal length.");
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j &= ~bit;
            }

            j |= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                double curRe = 1.0, curIm = 0.0;
                for (var k = 0; k < length / 2; k++)
                {
                    var evenRe = real[i + k];
                    var evenIm = imaginary[i + k];
                    var oddRe = real[i + k + length / 2] * curRe - imaginary[i + k + length / 2] * curIm;
                    var oddIm = real[i + k + length / 2] * curIm + imaginary[i + k + length / 2] * curRe;

                    real[i + k] = evenRe + oddRe;
                    imaginary[i + k] = evenIm + oddIm;
                    real[i + k + length / 2] = evenRe - oddRe;
                    imaginary[i + k + length / 2] = evenIm - oddIm;

                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }
    }

    /// <summary>
    /// Magnitude spectrum of a real signal, zero-padded to the next power of
    /// two. Returns bins 0..N/2 (inclusive); bin spacing = sampleRate / N via
    /// <see cref="BinFrequency"/>.
    /// </summary>
    public static double[] MagnitudeSpectrum(ReadOnlySpan<double> signal, out int paddedLength)
    {
        paddedLength = 1;
        while (paddedLength < signal.Length)
        {
            paddedLength <<= 1;
        }

        var re = new double[paddedLength];
        var im = new double[paddedLength];
        signal.CopyTo(re);
        Transform(re, im);

        var half = paddedLength / 2;
        var magnitude = new double[half + 1];
        for (var k = 0; k <= half; k++)
        {
            magnitude[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
        }

        return magnitude;
    }

    public static double BinFrequency(int bin, double sampleRate, int paddedLength) =>
        bin * sampleRate / paddedLength;
}
