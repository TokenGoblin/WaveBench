using FluentAssertions;
using WaveBench.Acoustics.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 11 §6.1 verification of ISO 532-1 method B (Zwicker) loudness.
///
/// The anchors are the ones that define the quantity rather than describe it:
/// the sone IS a 1 kHz tone at 40 dB in a free field, and Stevens' loudness
/// function doubles the sone value per 10 dB above that. An implementation
/// that reproduces the definition and its slope has its core loudness and
/// its slope integration both right; one that misses them is wrong no matter
/// how plausible the code reads.
/// </summary>
public class ZwickerLoudnessTests(ITestOutputHelper output)
{
    private const int OneKilohertzBand = 16;

    /// <summary>A silent 28-band spectrum with one band set.</summary>
    private static double[] SingleBand(int band, double levelDb)
    {
        var levels = new double[28];
        Array.Fill(levels, -100.0);
        levels[band] = levelDb;
        return levels;
    }

    /// <summary>All 28 bands at the same level.</summary>
    private static double[] Flat(double levelDb)
    {
        var levels = new double[28];
        Array.Fill(levels, levelDb);
        return levels;
    }

    [Fact]
    public void Gate_the_filter_bank_reproduces_the_iso_532_1_worked_example()
    {
        // ISO 532-1:2017 §4 and §5.2 both state this outright: "a 1 kHz tone
        // with a sound pressure level of 70 dB produces the following levels
        // at different centre frequencies: 50 dB at 800 Hz, 70 dB at 1 kHz
        // and 50 dB at 1,25 kHz". It is the only fully specified input/output
        // pair the standard prints in prose, so it is the one place an
        // implementation can be checked against the standard for free.
        var levels = ThirdOctaveAnalysis.ToneBandLevels(1000.0, 70.0);

        levels[OneKilohertzBand].Should().BeApproximately(70.0, 0.1, "the tone sits on the band centre");
        levels[OneKilohertzBand - 1].Should().BeApproximately(50.0, 0.5, "800 Hz skirt, 20 dB down");
        levels[OneKilohertzBand + 1].Should().BeApproximately(50.0, 0.5, "1.25 kHz skirt, 20 dB down");
    }

    [Theory]
    [InlineData(40.0, 1.0)]
    [InlineData(50.0, 2.0)]
    [InlineData(60.0, 4.0)]
    [InlineData(70.0, 8.0)]
    [InlineData(80.0, 16.0)]
    public void Gate_the_1_khz_reference_tone_reproduces_the_sone_definition(double levelDb, double expectedSone)
    {
        // A tone is NOT one band with silent neighbours. Feeding the method
        // that impossible spectrum loses the filter skirts, and with them
        // about 8% of a tone's loudness — ISO 532-1 §5.2 warns that the upper
        // slope "contributes especially to the total loudness of pure tones".
        var result = ZwickerLoudness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(1000.0, levelDb));
        output.WriteLine($"1 kHz @ {levelDb:F0} dB -> {result.Sone:F3} sone ({result.Phon:F1} phon), expected {expectedSone:F1}");

        // ±5% is the conformance tolerance ISO 532-1 §5.1 sets for any
        // implementation measured against its reference implementation.
        result.Sone.Should().BeApproximately(expectedSone, expectedSone * 0.05);
    }

    [Fact]
    public void Gate_loudness_level_in_phon_tracks_the_band_level_at_1_khz()
    {
        // By construction L_N(phon) == L_p(dB) for a 1 kHz tone.
        foreach (var level in new[] { 40.0, 50.0, 60.0, 70.0, 80.0 })
        {
            var phon = ZwickerLoudness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(1000.0, level)).Phon;
            phon.Should().BeApproximately(level, 2.0, $"1 kHz at {level:F0} dB is {level:F0} phon by definition");
        }
    }

    [Fact]
    public void Loudness_is_strictly_monotonic_in_level()
    {
        var previous = 0.0;
        for (var level = 20.0; level <= 100.0; level += 2.5)
        {
            var sone = ZwickerLoudness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(1000.0, level)).Sone;
            sone.Should().BeGreaterThan(previous, $"loudness must rise with level (at {level:F1} dB)");
            previous = sone;
        }
    }

    [Fact]
    public void The_filter_skirts_are_a_measurable_part_of_a_tones_loudness()
    {
        // Pins the size of the effect that made the first version of this
        // suite fail, so a future "optimisation" back to rectangular bands
        // cannot pass quietly.
        var withSkirts = ZwickerLoudness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(1000.0, 40.0));
        var withoutSkirts = ZwickerLoudness.FromThirdOctaveBands(SingleBand(OneKilohertzBand, 40.0));

        output.WriteLine($"1 kHz @ 40 dB: with skirts {withSkirts.Sone:F3} sone, single band {withoutSkirts.Sone:F3} sone");
        withoutSkirts.Sone.Should().BeLessThan(withSkirts.Sone * 0.96,
            "dropping the skirts loses several percent of the loudness, which is why the anchor needs them");
    }

    [Fact]
    public void Silence_is_inaudible_and_sub_threshold_tones_are_too()
    {
        ZwickerLoudness.FromThirdOctaveBands(Flat(-100.0)).Sone.Should().Be(0.0);

        // 20 Hz needs roughly 75 dB to be heard at all; at 20 dB it is far
        // below the threshold of hearing and must contribute nothing.
        ZwickerLoudness.FromThirdOctaveBands(SingleBand(0, 20.0)).Sone.Should().Be(0.0);
    }

    [Fact]
    public void Gate_spectral_spreading_makes_broadband_noise_louder_than_a_tone_of_equal_spl()
    {
        // The central claim of the method: energy spread across many critical
        // bands is louder than the same energy in one. A 28-band flat spectrum
        // at 45 dB/band is ~59.5 dB overall; put that same 59.5 dB in one
        // 1 kHz band and it must come out quieter.
        var broadband = ZwickerLoudness.FromThirdOctaveBands(Flat(45.0));

        var total = 10.0 * Math.Log10(28.0 * Math.Pow(10.0, 4.5));
        var tone = ZwickerLoudness.FromThirdOctaveBands(SingleBand(OneKilohertzBand, total));

        output.WriteLine($"{total:F1} dB total: broadband {broadband.Sone:F2} sone vs single band {tone.Sone:F2} sone");
        broadband.Sone.Should().BeGreaterThan(tone.Sone * 1.5,
            "spreading energy over the Bark scale recruits far more critical bands");
    }

    [Fact]
    public void Gate_specific_loudness_integrates_to_the_reported_total()
    {
        var result = ZwickerLoudness.FromThirdOctaveBands(Flat(60.0));
        const double dz = 24.0 / ZwickerLoudness.SpecificLoudnessBins;
        var integral = result.SpecificLoudness.Sum() * dz;

        output.WriteLine($"reported {result.Sone:F3} sone, ∫N'(z)dz = {integral:F3} sone");
        integral.Should().BeApproximately(result.Sone, result.Sone * 0.02,
            "the pattern and the scalar must be the same number, or the two lenses disagree");
    }

    [Fact]
    public void Specific_loudness_spreads_upward_in_frequency_never_downward()
    {
        // Masking is asymmetric: a 1 kHz tone (≈8.5 Bark) excites bands above
        // itself far more than below. This is what the upper-slope machinery
        // exists to produce, so it is worth pinning directly.
        var result = ZwickerLoudness.FromThirdOctaveBands(SingleBand(OneKilohertzBand, 70.0));

        var below = 0.0;
        var above = 0.0;
        for (var i = 0; i < ZwickerLoudness.SpecificLoudnessBins; i++)
        {
            var bark = ZwickerLoudness.Result.BarkOf(i);
            if (bark < 8.0)
            {
                below += result.SpecificLoudness[i];
            }
            else if (bark > 9.5)
            {
                above += result.SpecificLoudness[i];
            }
        }

        output.WriteLine($"1 kHz @ 70 dB: below 8 Bark {below:F3}, above 9.5 Bark {above:F3}");
        above.Should().BeGreaterThan(below * 5.0, "upward masking dominates");
    }

    [Theory]
    [InlineData(3150.0, false)] // DDF = −1.9 dB: frontal incidence is favoured here
    [InlineData(8000.0, true)]  // DDF = +4.0 dB: a diffuse field excites the pinna more
    public void The_diffuse_field_correction_moves_loudness_in_the_tabulated_direction(
        double frequency, bool diffuseIsLouder)
    {
        // The free-to-diffuse difference changes sign across the spectrum, so
        // "diffuse is louder" is only true where DDF is positive. Testing one
        // band would pass on a table that had the sign wrong everywhere else.
        var levels = ThirdOctaveAnalysis.ToneBandLevels(frequency, 60.0);
        var free = ZwickerLoudness.FromThirdOctaveBands(levels, SoundField.Free).Sone;
        var diffuse = ZwickerLoudness.FromThirdOctaveBands(levels, SoundField.Diffuse).Sone;

        output.WriteLine($"{frequency:F0} Hz @ 60 dB: free {free:F3} sone, diffuse {diffuse:F3} sone");
        if (diffuseIsLouder)
        {
            diffuse.Should().BeGreaterThan(free * 1.02);
        }
        else
        {
            diffuse.Should().BeLessThan(free * 0.98);
        }
    }

    [Fact]
    public void A_1_khz_sine_signal_agrees_with_its_own_band_spectrum()
    {
        // Closes the loop from the time domain: synthesise a 1 kHz tone at a
        // known SPL, run the third-octave analyser, and the loudness must land
        // on the same anchor as the hand-built band spectrum.
        const double sampleRate = 48_000.0;
        const double targetSpl = 60.0;
        var amplitude = Math.Sqrt(2.0) * SoundLevelMeter.ReferencePressure * Math.Pow(10.0, targetSpl / 20.0);

        var samples = new double[(int)sampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
        }

        var fromSignal = ZwickerLoudness.FromSignal(samples, sampleRate);
        output.WriteLine($"1 kHz sine at {targetSpl:F0} dB SPL -> {fromSignal.Sone:F3} sone");
        fromSignal.Sone.Should().BeApproximately(4.0, 4.0 * 0.05,
            "the analyser plus the method must still land on the definition, inside the ISO 532-1 §5.1 tolerance");
    }

    [Fact]
    public void Band_levels_recover_the_spl_of_a_synthesised_tone()
    {
        const double sampleRate = 48_000.0;
        var amplitude = Math.Sqrt(2.0) * SoundLevelMeter.ReferencePressure * Math.Pow(10.0, 70.0 / 20.0);
        var samples = new double[(int)sampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
        }

        var levels = ThirdOctaveAnalysis.BandLevels(samples, sampleRate);
        levels[OneKilohertzBand].Should().BeApproximately(70.0, 0.5, "the tone sits squarely in the 1 kHz band");
    }

    [Fact]
    public void Rejects_a_spectrum_that_is_not_28_bands()
    {
        var act = () => ZwickerLoudness.FromThirdOctaveBands(new double[24]);
        act.Should().Throw<ArgumentException>();
    }
}
