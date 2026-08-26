namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// Gated loudness per ITU-R BS.1770-4 / EBU R128 (plan §3.6: level-matched
/// A/B is non-negotiable, because humans reliably judge the louder of two
/// sounds as better).
///
/// Chain: K-weighting (a high-shelf stage plus a high-pass stage, the
/// standard's two biquads), 400 ms blocks at 75% overlap, absolute gate at
/// −70 LKFS then a relative gate 10 LU below the ungated mean.
/// The published 48 kHz coefficients are used directly; other sample rates
/// are rejected rather than silently mis-weighted.
/// </summary>
public static class Loudness
{
    // BS.1770-4 Table 1: stage 1 high-shelf at 48 kHz.
    private static readonly double[] ShelfB = [1.53512485958697, -2.69169618940638, 1.19839281085285];
    private static readonly double[] ShelfA = [1.0, -1.69065929318241, 0.73248077421585];

    // BS.1770-4 Table 2: stage 2 high-pass at 48 kHz.
    private static readonly double[] HighPassB = [1.0, -2.0, 1.0];
    private static readonly double[] HighPassA = [1.0, -1.99004745483398, 0.99007225036621];

    public const double SupportedSampleRate = 48_000.0;

    /// <summary>Integrated (gated) loudness, LUFS. Mono input.</summary>
    public static double IntegratedLufs(ReadOnlySpan<float> samples, double sampleRate)
    {
        if (Math.Abs(sampleRate - SupportedSampleRate) > 1e-6)
        {
            throw new NotSupportedException(
                "BS.1770 K-weighting coefficients here are the published 48 kHz set; resample first.");
        }

        var weighted = KWeight(samples);

        var blockSamples = (int)(0.4 * sampleRate);
        var step = blockSamples / 4; // 75% overlap
        if (weighted.Length < blockSamples)
        {
            throw new ArgumentException("Signal shorter than one 400 ms loudness block.", nameof(samples));
        }

        var blockPowers = new List<double>();
        for (var start = 0; start + blockSamples <= weighted.Length; start += step)
        {
            double sum = 0;
            for (var i = start; i < start + blockSamples; i++)
            {
                sum += weighted[i] * weighted[i];
            }

            blockPowers.Add(sum / blockSamples);
        }

        // Absolute gate at −70 LKFS.
        const double absoluteGate = -70.0;
        var aboveAbsolute = blockPowers.Where(p => LoudnessOf(p) > absoluteGate).ToList();
        if (aboveAbsolute.Count == 0)
        {
            return double.NegativeInfinity;
        }

        // Relative gate: 10 LU below the mean of the absolute-gated blocks.
        var relativeGate = LoudnessOf(aboveAbsolute.Average()) - 10.0;
        var gated = aboveAbsolute.Where(p => LoudnessOf(p) > relativeGate).ToList();
        if (gated.Count == 0)
        {
            return double.NegativeInfinity;
        }

        return LoudnessOf(gated.Average());
    }

    private static double LoudnessOf(double meanSquare) =>
        meanSquare <= 0 ? double.NegativeInfinity : -0.691 + 10.0 * Math.Log10(meanSquare);

    /// <summary>Apply the two K-weighting biquads.</summary>
    public static double[] KWeight(ReadOnlySpan<float> samples)
    {
        var stage1 = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            stage1[i] = samples[i];
        }

        Biquad(stage1, ShelfB, ShelfA);
        Biquad(stage1, HighPassB, HighPassA);
        return stage1;
    }

    private static void Biquad(double[] x, double[] b, double[] a)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var x0 = x[i];
            var y0 = b[0] * x0 + b[1] * x1 + b[2] * x2 - a[1] * y1 - a[2] * y2;
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
            x[i] = y0;
        }
    }

    /// <summary>
    /// Scale a stem to a target integrated loudness. The gain is reported so
    /// the UI can show BOTH the matched render and the true level difference
    /// (plan §3.6: never hide the SPL delta, just stop it biasing the ear).
    /// </summary>
    public static (AudioStem Normalised, double GainDb) NormaliseTo(AudioStem stem, double targetLufs)
    {
        var current = IntegratedLufs(stem.Samples, stem.SampleRate);
        if (double.IsNegativeInfinity(current))
        {
            return (stem, 0.0);
        }

        var gainDb = targetLufs - current;
        var gain = Math.Pow(10.0, gainDb / 20.0);
        var output = new float[stem.Samples.Length];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (float)(stem.Samples[i] * gain);
        }

        return (stem with { Samples = output }, gainDb);
    }

    /// <summary>
    /// Loudness-matched A/B pair (plan §3.6): both rendered to the same
    /// integrated loudness, with each one's true gain change reported so the
    /// real level difference stays visible.
    /// </summary>
    public static (AudioStem A, AudioStem B, double GainDbA, double GainDbB, double TrueDifferenceLu)
        MatchPair(AudioStem a, AudioStem b, double targetLufs = -23.0)
    {
        var lufsA = IntegratedLufs(a.Samples, a.SampleRate);
        var lufsB = IntegratedLufs(b.Samples, b.SampleRate);
        var (na, ga) = NormaliseTo(a, targetLufs);
        var (nb, gb) = NormaliseTo(b, targetLufs);
        return (na, nb, ga, gb, lufsA - lufsB);
    }
}
