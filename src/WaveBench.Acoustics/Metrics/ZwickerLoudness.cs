namespace WaveBench.Acoustics.Metrics;

public enum SoundField
{
    /// <summary>Free field — the listener-preset geometry of plan §3.5.</summary>
    Free,

    /// <summary>Diffuse field.</summary>
    Diffuse,
}

/// <summary>
/// Stationary loudness by the Zwicker method — ISO 532-1:2017 method B
/// (equivalently DIN 45631), from third-octave band levels.
///
/// Structure of the method, implemented from the standard's description:
///  1. The eleven lowest bands (25–250 Hz) get a level-dependent correction
///     and are combined into the first three critical bands, because the ear
///     does not resolve them separately.
///  2. Core (main) loudness per critical band:
///       N'_core = 0.0635·10^(0.025·LTQ) · [(1 − s + s·10^(0.1(L_E − LTQ)))^0.25 − 1]
///     with s = 0.25, L_E the band level corrected for transmission through
///     the outer and middle ear (a0) and, in a diffuse field, the
///     free-to-diffuse difference.
///  3. Upward spectral masking: each band's loudness decays toward higher
///     critical-band rate along level-dependent slopes, and the specific
///     loudness N'(z) is the upper envelope of all those contributions.
///     Total loudness is ∫N'(z)dz over the 24 Bark scale.
///
/// The tabulated data (RAP, DLL, LTQ, a0, DDF, DCB, ZUP, RNS, USL) are the
/// standard's own coefficients — facts, implemented rather than reproduced
/// from any particular text.
///
/// <b>Verification.</b> The sone is DEFINED by a 1 kHz tone at 40 dB, and
/// the loudness function doubles per 10 dB above that; see
/// ZwickerLoudnessTests, which pins 40/50/60 dB to 1/2/4 sone.
/// </summary>
public static class ZwickerLoudness
{
    /// <summary>The 28 third-octave band centres the method expects, Hz.</summary>
    public static IReadOnlyList<double> BandCentres { get; } =
    [
        25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630,
        800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500,
    ];

    /// <summary>Ranges of 1/3-octave band levels for the low-frequency correction, dB.</summary>
    private static readonly double[] Rap = [45, 55, 65, 71, 80, 90, 100, 120];

    /// <summary>Level corrections for the eleven lowest bands, per Rap range, dB.</summary>
    private static readonly double[,] Dll =
    {
        { -32, -24, -16, -10, -5, 0, -7, -3, 0, -2, 0 },
        { -29, -22, -15, -10, -4, 0, -7, -2, 0, -2, 0 },
        { -27, -19, -14, -9, -4, 0, -6, -2, 0, -2, 0 },
        { -25, -17, -12, -9, -3, 0, -5, -2, 0, -2, 0 },
        { -23, -16, -11, -7, -3, 0, -4, -1, 0, -1, 0 },
        { -20, -14, -10, -6, -3, 0, -4, -1, 0, -1, 0 },
        { -18, -12, -9, -6, -2, 0, -3, -1, 0, -1, 0 },
        { -15, -10, -8, -4, -2, 0, -3, -1, 0, -1, 0 },
    };

    /// <summary>Critical-band level at the absolute threshold of hearing, dB.</summary>
    private static readonly double[] Ltq =
        [30, 18, 12, 8, 7, 6, 5, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3];

    /// <summary>Transmission through the outer and middle ear (free field), dB.</summary>
    private static readonly double[] A0 =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -0.5, -1.6, -3.2, -5.4, -5.6, -4, -1.5, 2, 5, 12];

    /// <summary>Free-field to diffuse-field level difference, dB.</summary>
    private static readonly double[] Ddf =
        [0, 0, 0.5, 0.9, 1.2, 1.6, 2.3, 2.8, 3, 2, 0, -1.4, -2, -1.9, -1, 0.5, 3, 4, 4.3, 4];

    /// <summary>Adaptation of the levels within each critical band, dB.</summary>
    private static readonly double[] Dcb =
    [
        -0.25, -0.6, -0.8, -0.8, -0.5, 0, 0.5, 1.1, 1.5, 1.7,
        1.8, 1.8, 1.7, 1.6, 1.4, 1.2, 0.8, 0.5, 0, -0.5,
    ];

    /// <summary>Upper limit of each approximated critical band, Bark.</summary>
    private static readonly double[] Zup =
    [
        0.9, 1.8, 2.8, 3.5, 4.4, 5.4, 6.6, 7.9, 9.2, 10.6, 12.3,
        13.8, 15.2, 16.7, 18.1, 19.3, 20.6, 21.8, 22.7, 23.6, 24.0,
    ];

    /// <summary>Specific-loudness range boundaries for the slope selection, sone/Bark.</summary>
    private static readonly double[] Rns =
    [
        21.5, 18, 15.1, 11.5, 9, 6.1, 4.4, 3.1, 2.13,
        1.36, 0.82, 0.42, 0.30, 0.22, 0.15, 0.10, 0.035, 0,
    ];

    /// <summary>Steepness of the upper slopes, per specific-loudness range × band group.</summary>
    private static readonly double[,] Usl =
    {
        { 13.0, 8.2, 6.3, 5.5, 5.5, 5.5, 5.5, 5.5 },
        { 9.0, 7.5, 6.0, 5.1, 4.5, 4.5, 4.5, 4.5 },
        { 7.8, 6.7, 5.6, 4.9, 4.4, 3.9, 3.9, 3.9 },
        { 6.2, 5.4, 4.6, 4.0, 3.5, 3.2, 3.2, 3.2 },
        { 4.5, 3.8, 3.6, 3.2, 2.9, 2.7, 2.7, 2.7 },
        { 3.7, 3.0, 2.8, 2.35, 2.2, 2.2, 2.2, 2.2 },
        { 2.9, 2.3, 2.1, 1.9, 1.8, 1.7, 1.7, 1.7 },
        { 2.4, 1.7, 1.5, 1.35, 1.3, 1.3, 1.3, 1.3 },
        { 1.95, 1.45, 1.3, 1.15, 1.1, 1.1, 1.1, 1.1 },
        { 1.5, 1.2, 0.94, 0.86, 0.82, 0.82, 0.82, 0.82 },
        { 0.72, 0.67, 0.64, 0.63, 0.62, 0.62, 0.62, 0.62 },
        { 0.59, 0.53, 0.51, 0.50, 0.42, 0.42, 0.42, 0.42 },
        { 0.40, 0.33, 0.26, 0.24, 0.22, 0.22, 0.22, 0.22 },
        { 0.27, 0.21, 0.20, 0.18, 0.17, 0.17, 0.17, 0.17 },
        { 0.16, 0.15, 0.14, 0.12, 0.11, 0.11, 0.11, 0.11 },
        { 0.12, 0.11, 0.10, 0.08, 0.08, 0.08, 0.08, 0.08 },
        { 0.09, 0.08, 0.07, 0.06, 0.06, 0.06, 0.06, 0.05 },
        { 0.06, 0.05, 0.03, 0.02, 0.02, 0.02, 0.02, 0.02 },
    };

    /// <summary>Specific loudness resolution: 0.1 Bark over 24 Bark.</summary>
    public const int SpecificLoudnessBins = 240;

    public sealed record Result(double Sone, double[] SpecificLoudness)
    {
        /// <summary>Loudness level, phon: N = 2^((L_N − 40)/10) inverted.</summary>
        public double Phon => Sone >= 1.0
            ? 40.0 + 10.0 * Math.Log2(Sone)
            : 40.0 * Math.Pow(Sone, 0.35);

        /// <summary>Bark of bin i (bin centres on the 0.1 Bark grid).</summary>
        public static double BarkOf(int bin) => (bin + 0.5) * 24.0 / SpecificLoudnessBins;
    }

    /// <summary>
    /// Loudness from 28 third-octave band levels (dB SPL, 25 Hz – 12.5 kHz).
    /// </summary>
    public static Result FromThirdOctaveBands(IReadOnlyList<double> bandLevelsDb, SoundField field = SoundField.Free)
    {
        if (bandLevelsDb.Count != 28)
        {
            throw new ArgumentException(
                $"Expected 28 third-octave band levels (25 Hz – 12.5 kHz), got {bandLevelsDb.Count}.",
                nameof(bandLevelsDb));
        }

        // --- Step 1: correct and combine the eleven lowest bands ------------
        var ti = new double[11];
        for (var i = 0; i < 11; i++)
        {
            var corrected = bandLevelsDb[i] + Dll[7, i];
            for (var j = 0; j < 8; j++)
            {
                if (bandLevelsDb[i] <= Rap[j] - Dll[j, i])
                {
                    corrected = bandLevelsDb[i] + Dll[j, i];
                    break;
                }
            }

            ti[i] = Math.Pow(10.0, 0.1 * corrected);
        }

        var lcb = new double[3];
        lcb[0] = Sum(ti, 0, 6);   // 25–80 Hz
        lcb[1] = Sum(ti, 6, 9);   // 100–160 Hz
        lcb[2] = Sum(ti, 9, 11);  // 200–250 Hz
        for (var i = 0; i < 3; i++)
        {
            lcb[i] = lcb[i] > 0 ? 10.0 * Math.Log10(lcb[i]) : -100.0;
        }

        // --- Step 2: core loudness per critical band ------------------------
        const double s = 0.25;
        var nm = new double[21]; // 20 bands + a closing zero band
        for (var i = 0; i < 20; i++)
        {
            var le = i < 3 ? lcb[i] : bandLevelsDb[i + 8];
            le -= A0[i];
            if (field == SoundField.Diffuse)
            {
                le += Ddf[i];
            }

            if (le <= Ltq[i])
            {
                continue;
            }

            le -= Dcb[i];
            var mp1 = 0.0635 * Math.Pow(10.0, 0.025 * Ltq[i]);
            var mp2 = Math.Pow(1.0 - s + s * Math.Pow(10.0, 0.1 * (le - Ltq[i])), 0.25) - 1.0;
            nm[i] = Math.Max(0.0, mp1 * mp2);
        }

        // The lowest critical band gets an extra correction, because the
        // threshold in quiet runs very steeply across it (LTQ falls 30 → 18 dB
        // between the first two bands) and a single core-loudness value taken
        // at one threshold therefore overstates it. ISO 532-1:2017 Annex A:
        //   N'₀ ← N'₀·(0.4 + 0.32·N'₀^0.2), applied only where that attenuates.
        // Worth 16% on the first band and 1.2% on the total for the Annex B.2
        // signal — small enough to hide behind a total-loudness check, which
        // is why the conformance test compares every Bark.
        var correction = 0.4 + 0.32 * Math.Pow(nm[0], 0.2);
        if (correction < 1.0)
        {
            nm[0] *= correction;
        }

        // --- Step 3: upward masking slopes and integration ------------------
        // Each band's loudness decays toward higher critical-band rate along a
        // slope selected by TWO indices, and getting either wrong produces a
        // pattern that still integrates to nearly the right total:
        //
        //  * the USL column is the MASKING band (i − 1), not the band being
        //    filled. Using the latter runs band 3's decay at 2.35 sone/Bark
        //    where it should be 2.80 — a 5.6% error in that band and 0.1% in
        //    the total, which is exactly the kind of thing a scalar check
        //    cannot see;
        //  * the USL row is a level range that PERSISTS across bands. It is
        //    re-derived only on a genuine rise, and otherwise walks downward
        //    one range at a time as the slope decays. Recomputing it per
        //    segment from the current value looks equivalent and is not.
        var specific = new double[SpecificLoudnessBins];
        const double dz = 24.0 / SpecificLoudnessBins;

        double total = 0.0;
        var z1 = 0.0;      // current position, Bark
        var n1 = 0.0;      // specific loudness carried in from the last segment
        var range = 0;     // index into Rns/Usl rows — persistent, see above
        var bin = 0;       // next output bin to fill

        for (var i = 0; i < 21; i++)
        {
            var coreLoudness = nm[i];
            var zup = Zup[i] + 1e-4;
            var maskerBand = Math.Clamp(i - 1, 0, Usl.GetLength(1) - 1);
            var nextBand = false;

            do
            {
                double n2;
                double z2;

                if (n1 > coreLoudness)
                {
                    // Decaying from a louder lower band across this one.
                    var slope = Usl[range, maskerBand];
                    n2 = Math.Max(Rns[range], coreLoudness);
                    z2 = z1 + (n1 - n2) / slope;

                    if (z2 > zup)
                    {
                        // The decay does not reach the floor before the band
                        // edge; truncate and carry the remainder onward.
                        nextBand = true;
                        z2 = zup;
                        n2 = n1 - (z2 - z1) * slope;
                    }

                    total += 0.5 * (n1 + n2) * (z2 - z1);

                    // Grid points are indexed by their upper edge: bin k holds
                    // the value at z = (k+1)·0.1 Bark.
                    while (bin < specific.Length && (bin + 1) * dz <= z2 + 1e-9)
                    {
                        specific[bin] = n1 - ((bin + 1) * dz - z1) * slope;
                        bin++;
                    }
                }
                else
                {
                    // This band is at least as loud as what reaches it, so the
                    // pattern rises to it and runs flat to the band edge.
                    // Masking spreads upward in frequency, never downward.
                    if (n1 < coreLoudness)
                    {
                        range = 0;
                        while (range < Rns.Length - 1 && Rns[range] >= coreLoudness)
                        {
                            range++;
                        }
                    }

                    nextBand = true;
                    z2 = zup;
                    n2 = coreLoudness;
                    total += n2 * (z2 - z1);

                    while (bin < specific.Length && (bin + 1) * dz <= z2 + 1e-9)
                    {
                        specific[bin] = n2;
                        bin++;
                    }
                }

                while (n2 <= Rns[range] && range < Rns.Length - 1)
                {
                    range++;
                }

                z1 = z2;
                n1 = n2;
            }
            while (!nextBand);
        }

        return new Result(Math.Max(0.0, total), specific);
    }

    /// <summary>
    /// Loudness of a time signal: third-octave analysis, then the band method.
    /// The signal is taken as stationary (ISO 532-1 method B); time-varying
    /// loudness (method C) is a separate procedure.
    /// </summary>
    public static Result FromSignal(
        ReadOnlySpan<double> pressurePa, double sampleRate, SoundField field = SoundField.Free) =>
        FromThirdOctaveBands(ThirdOctaveAnalysis.BandLevels(pressurePa, sampleRate), field);

    private static double Sum(double[] values, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    private static void Fill(double[] specific, ref int bin, double z2, double dz, double value)
    {
        while (bin < specific.Length && (bin + 1) * dz <= z2 + 1e-9)
        {
            specific[bin++] = value;
        }
    }

    private static void FillRamp(double[] specific, ref int bin, double z1, double z2, double n1, double n2, double dz)
    {
        while (bin < specific.Length && (bin + 1) * dz <= z2 + 1e-9)
        {
            var z = (bin + 0.5) * dz;
            var w = z2 > z1 ? Math.Clamp((z - z1) / (z2 - z1), 0.0, 1.0) : 0.0;
            specific[bin++] = n1 + w * (n2 - n1);
        }
    }
}

/// <summary>
/// One-third-octave band analysis — the input stage for
/// <see cref="ZwickerLoudness"/>.
///
/// Bands are the base-ten (IEC 61260-1) set, so the exact centre of band x
/// relative to 1 kHz is 1000·10^(x/10): the band labelled "25 Hz" is really
/// 25.119 Hz. Using the nominal labels as if they were exact is a mistake
/// this codebase has made once already, in the IEC 61672 weighting tables.
///
/// Each band is applied as a filter magnitude response rather than a
/// rectangular bin sum, because the leakage between adjacent filters is not
/// an artefact here — it is part of the specified measurement chain, and
/// ISO 532-1 depends on it. §4 of that standard requires class-1 filters
/// giving 20 dB damping at the centre frequencies of the adjacent bands, and
/// states the consequence: "a 1 kHz tone with a sound pressure level of
/// 70 dB produces the following levels at different centre frequencies:
/// 50 dB at 800 Hz, 70 dB at 1 kHz and 50 dB at 1,25 kHz". Those skirts
/// carry a tone's loudness into the neighbouring critical bands, and
/// ISO 532-1 §5.2 notes the resulting upper slope "contributes especially to
/// the total loudness of pure tones" — about 8% of it at 1 kHz. An ideal
/// rectangular filter bank silently loses that.
///
/// The response is the IEC 61260-1 idealised form
///   |H(f)|² = 1 / (1 + [((f/f_m) − (f_m/f)) / Ω]^p),  Ω = G^(1/2b) − G^(−1/2b)
/// with G = 10^(3/10), b = 3, and the order exponent p solved so the
/// attenuation at the adjacent band centre is exactly the 20 dB ISO 532-1
/// specifies (p ≈ 6.57; a nominal 6th-order bandpass would give 18.3 dB).
/// </summary>
public static class ThirdOctaveAnalysis
{
    private const double G = 1.9952623149688795; // 10^(3/10), base-ten octave ratio
    private const int BandsPerOctave = 3;

    /// <summary>Normalised half-bandwidth, Ω = G^(1/2b) − G^(−1/2b).</summary>
    private static readonly double Bandwidth =
        Math.Pow(G, 1.0 / (2.0 * BandsPerOctave)) - Math.Pow(G, -1.0 / (2.0 * BandsPerOctave));

    /// <summary>
    /// Order exponent giving 20 dB attenuation at the adjacent band centre,
    /// per ISO 532-1 §4. Solved rather than hard-coded so the intent survives.
    /// </summary>
    private static readonly double Order = Math.Log(Math.Pow(10.0, 20.0 / 10.0) - 1.0) / Math.Log(
        (Math.Pow(G, 1.0 / BandsPerOctave) - Math.Pow(G, -1.0 / BandsPerOctave)) / Bandwidth);

    /// <summary>
    /// Exact centre frequency of band <paramref name="index"/> (0 = "25 Hz",
    /// 16 = 1 kHz, 27 = "12.5 kHz"), Hz. Not the nominal label.
    /// </summary>
    public static double ExactCentre(int index) => 1000.0 * Math.Pow(10.0, (index - 16) / 10.0);

    /// <summary>Power transfer function |H(f)|² of the band's filter.</summary>
    public static double FilterPowerResponse(double frequency, int bandIndex)
    {
        if (frequency <= 0.0)
        {
            return 0.0;
        }

        var centre = ExactCentre(bandIndex);
        var detuning = (frequency / centre - centre / frequency) / Bandwidth;
        return 1.0 / (1.0 + Math.Pow(Math.Abs(detuning), Order));
    }

    /// <summary>Band levels, dB re 20 µPa, for the 28 bands 25 Hz – 12.5 kHz.</summary>
    public static double[] BandLevels(ReadOnlySpan<double> pressurePa, double sampleRate)
    {
        var spectrum = Core.Numerics.Fft.MagnitudeSpectrum(pressurePa, out var padded);
        var levels = new double[ZwickerLoudness.BandCentres.Count];

        // Parseval over a zero-padded transform: the mean square is taken over
        // the SIGNAL's length, not the padded length, so the normalisation
        // carries both. Using 1/padded² here understates every band by
        // 10·log₁₀(padded/signal) — 1.35 dB for one second at 48 kHz.
        var norm = 2.0 / ((double)pressurePa.Length * padded);

        for (var b = 0; b < levels.Length; b++)
        {
            double power = 0;
            for (var k = 1; k < spectrum.Length; k++)
            {
                var response = FilterPowerResponse(Core.Numerics.Fft.BinFrequency(k, sampleRate, padded), b);
                if (response > 1e-12)
                {
                    power += response * spectrum[k] * spectrum[k];
                }
            }

            power *= norm;
            levels[b] = power > 0
                ? 10.0 * Math.Log10(power / (SoundLevelMeter.ReferencePressure * SoundLevelMeter.ReferencePressure))
                : -100.0;
        }

        return levels;
    }

    /// <summary>
    /// Band levels produced by a pure tone of the given SPL, as the specified
    /// filter bank would report them — the tone plus its skirts. Lets a
    /// caller state "a 1 kHz tone at 40 dB" without hand-building a spectrum
    /// that no real filter bank could produce.
    /// </summary>
    public static double[] ToneBandLevels(double frequencyHz, double levelDb)
    {
        var levels = new double[ZwickerLoudness.BandCentres.Count];
        for (var b = 0; b < levels.Length; b++)
        {
            var response = FilterPowerResponse(frequencyHz, b);
            levels[b] = response > 1e-12 ? levelDb + 10.0 * Math.Log10(response) : -100.0;
        }

        return levels;
    }
}
