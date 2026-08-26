using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Acoustics.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 10/§3.5 verification that the listener chain applied to a render
/// reproduces the propagation physics already verified in Phase 9: spherical
/// spreading, ISO 9613-1 air absorption, and the ground-reflection comb.
/// </summary>
public class ListenerChainTests(ITestOutputHelper output)
{
    private const double SampleRate = 48_000.0;

    private static AudioStem Tone(double frequency, double seconds = 0.5, double amplitude = 1.0)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / SampleRate));
        }

        return new AudioStem($"{frequency:F0}Hz", samples, SampleRate);
    }

    private static AudioStem WhiteNoise(int seed, double seconds = 1.0)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(2.0 * random.NextDouble() - 1.0);
        }

        return new AudioStem("noise", samples, SampleRate);
    }

    [Fact]
    public void Gate_spherical_spreading_sets_the_broadband_level()
    {
        // Free field, no ground: the only broadband effect at low frequency is
        // 1/r. At 0.5 m that is +6 dB relative to the 1 m reference the path
        // response is defined against; at 8 m it is −18 dB.
        foreach (var (distance, expectedDb) in new[] { (0.5, 6.02), (2.0, -6.02), (8.0, -18.06) })
        {
            var path = new PropagationPath(0.35, 0.35, distance, GroundReflectionCoefficient: 0.0);
            var source = Tone(200.0);
            var received = ListenerChain.Apply(source, path);
            var gain = ListenerChain.InsertionGainDb(source, received);

            output.WriteLine($"{distance:F1} m free field at 200 Hz: {gain:+0.00;-0.00} dB (1/r predicts {expectedDb:+0.00;-0.00})");

            // 0.2 dB allows for the small absorption at 200 Hz over 8 m.
            gain.Should().BeApproximately(expectedDb, 0.2);
        }
    }

    [Fact]
    public void Gate_air_absorption_costs_the_top_end_over_distance()
    {
        // ISO 9613-1: absorption is negligible at 100 Hz and severe at 10 kHz.
        // Over 50 m the high tone must lose several dB MORE than 1/r alone.
        const double distance = 50.0;
        var path = new PropagationPath(0.35, 1.2, distance, GroundReflectionCoefficient: 0.0);

        var lowGain = ListenerChain.InsertionGainDb(Tone(100.0), ListenerChain.Apply(Tone(100.0), path));
        var highGain = ListenerChain.InsertionGainDb(Tone(10_000.0), ListenerChain.Apply(Tone(10_000.0), path));

        var excess = lowGain - highGain;
        output.WriteLine($"50 m: 100 Hz {lowGain:F2} dB, 10 kHz {highGain:F2} dB — excess attenuation {excess:F2} dB");

        excess.Should().BeGreaterThan(4.0, "10 kHz is strongly absorbed over 50 m");
        lowGain.Should().BeApproximately(-20.0 * Math.Log10(distance), 0.3, "100 Hz is essentially 1/r only");
    }

    [Fact]
    public void Gate_the_ground_reflection_puts_a_notch_where_the_geometry_says()
    {
        // Two-path interference cancels at f = 1/(2·Δt) and its odd multiples,
        // Δt being the reflection's excess delay. This comb is the single most
        // audible thing the chain adds, so its position is pinned.
        var path = new PropagationPath(0.35, 1.2, 7.5, GroundReflectionCoefficient: 1.0);
        var firstNotch = 1.0 / (2.0 * path.GroundReflectionDelay);
        output.WriteLine($"excess delay {path.GroundReflectionDelay * 1e6:F1} µs → first notch {firstNotch:F0} Hz");

        var atNotch = ListenerChain.InsertionGainDb(Tone(firstNotch), ListenerChain.Apply(Tone(firstNotch), path));
        var atPeak = ListenerChain.InsertionGainDb(Tone(2.0 * firstNotch), ListenerChain.Apply(Tone(2.0 * firstNotch), path));

        output.WriteLine($"notch {atNotch:F2} dB, adjacent peak {atPeak:F2} dB");
        atPeak.Should().BeGreaterThan(atNotch + 15.0,
            "a perfectly reflecting ground gives a deep first notch and a +6 dB peak between");
    }

    [Fact]
    public void Gate_a_free_field_preset_adds_no_comb_but_a_reflecting_one_does()
    {
        // FSAE static is specified free-field; drive-by is not. Confounding
        // them would put an interference comb into a compliance render.
        var noise = WhiteNoise(seed: 7);

        var fsae = Ripple(noise, ListenerChain.Apply(noise, ListenerPreset.FsaeStatic));
        var driveBy = Ripple(noise, ListenerChain.Apply(noise, ListenerPreset.DriveBy));

        output.WriteLine($"spectral ripple added by the chain: FSAE static {fsae:F2} dB, drive-by {driveBy:F2} dB");
        fsae.Should().BeLessThan(3.0, "free field has no second path to interfere with");
        driveBy.Should().BeGreaterThan(fsae * 2.0, "the drive-by preset reflects off asphalt");

        // Ripple is what the CHAIN did, so it has to be measured as the spread
        // of the before/after difference. The spread of the output spectrum
        // alone mostly measures the source: white noise rises 3 dB per octave
        // in third-octave bands, which is ~14 dB across 200 Hz – 5 kHz and
        // swamps the effect under test.
        static double Ripple(AudioStem before, AudioStem after)
        {
            var input = ThirdOctaveAnalysis.BandLevels(
                Array.ConvertAll(before.Samples, s => (double)s), before.SampleRate);
            var output = ThirdOctaveAnalysis.BandLevels(
                Array.ConvertAll(after.Samples, s => (double)s), after.SampleRate);

            var difference = new List<double>();
            for (var b = 9; b < 24; b++) // 200 Hz – 5 kHz
            {
                difference.Add(output[b] - input[b]);
            }

            return difference.Max() - difference.Min();
        }
    }

    [Fact]
    public void Gate_no_energy_wraps_from_the_tail_onto_the_head()
    {
        // The trap in FFT convolution: without padding past the impulse
        // response, the end of the signal reappears at the start. A burst
        // confined to the last tenth of the render must leave the first tenth
        // silent.
        var samples = new float[(int)SampleRate];
        for (var i = samples.Length * 9 / 10; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(2.0 * Math.PI * 500.0 * i / SampleRate);
        }

        var received = ListenerChain.Apply(
            new AudioStem("burst", samples, SampleRate), ListenerPreset.DriveBy);

        var head = received.Samples.Take(samples.Length / 10).Max(s => Math.Abs((double)s));
        var burst = received.Samples.Max(s => Math.Abs((double)s));

        output.WriteLine($"head peak {head:E3}, burst peak {burst:E3}");
        head.Should().BeLessThan(burst * 1e-6, "nothing may wrap around onto the head of the render");
    }

    [Fact]
    public void The_chain_is_deterministic_and_shape_preserving()
    {
        var source = WhiteNoise(seed: 11);
        var first = ListenerChain.Apply(source, ListenerPreset.DriveBy);
        var second = ListenerChain.Apply(source, ListenerPreset.DriveBy);

        first.Samples.Should().HaveCount(source.Samples.Length, "a render keeps its length");
        first.Samples.Should().Equal(second.Samples, "same input, bit-identical output (plan Part 0)");
        first.Samples.Should().OnlyContain(s => float.IsFinite(s));
        first.SampleRate.Should().Be(source.SampleRate);
        first.Name.Should().Be(source.Name);
    }

    [Fact]
    public void An_empty_stem_survives_the_chain()
    {
        var empty = new AudioStem("empty", [], SampleRate);
        ListenerChain.Apply(empty, ListenerPreset.DriveBy).Samples.Should().BeEmpty();
    }

    [Fact]
    public void The_description_states_what_was_not_applied()
    {
        // A preset name in metadata must not imply a directivity model that
        // does not exist — the same honesty rule as PsychoacousticStatus.
        var preset = ListenerPreset.DriveBy;
        var description = ListenerChain.Describe(preset, preset.ToPath(0.35));

        output.WriteLine(description);
        description.Should().Contain("directivity NOT modelled");
        description.Should().Contain("9613");
        description.Should().Contain("ground reflection");
    }
}
