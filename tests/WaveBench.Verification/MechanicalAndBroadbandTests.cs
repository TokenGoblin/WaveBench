using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Core.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 10 §3.6 verification of the two remaining stems: broadband flow
/// noise (physical scaling, uncalibrated level) and the mechanical layer
/// (cosmetic, but correctly timed).
/// </summary>
public class MechanicalAndBroadbandTests(ITestOutputHelper output)
{
    private const double SampleRate = 48_000.0;

    private static double Rms(IReadOnlyList<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
        }

        return samples.Count > 0 ? Math.Sqrt(sum / samples.Count) : 0.0;
    }

    private static double Rms(IReadOnlyList<double> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return samples.Count > 0 ? Math.Sqrt(sum / samples.Count) : 0.0;
    }

    [Theory]
    [InlineData(6.0, 8.0)]  // Curle dipole: power ∝ U⁶, so pressure ∝ U³ → 2³ = 8
    [InlineData(8.0, 16.0)] // Lighthill quadrupole: power ∝ U⁸, pressure ∝ U⁴ → 2⁴ = 16
    public void Gate_broadband_pressure_follows_the_cited_velocity_scaling(double exponent, double expectedRatio)
    {
        // The scaling law is the physical content of this generator — its
        // absolute level is explicitly not. Doubling the velocity must raise
        // the radiated pressure by 2^(n/2).
        var slow = new double[48_000];
        var fast = new double[48_000];
        Array.Fill(slow, 30.0);
        Array.Fill(fast, 60.0);

        var slowNoise = FlowNoise.Generate(slow, SampleRate, 0.040, 12345UL, 1.0, exponent);
        var fastNoise = FlowNoise.Generate(fast, SampleRate, 0.040, 12345UL, 1.0, exponent);

        var ratio = Rms(fastNoise) / Rms(slowNoise);
        output.WriteLine($"U^{exponent:F0} power law: doubling velocity gives {ratio:F2}× pressure (expect {expectedRatio:F0})");
        ratio.Should().BeApproximately(expectedRatio, expectedRatio * 0.02);
    }

    [Fact]
    public void Gate_broadband_peaks_at_the_strouhal_frequency()
    {
        // f_peak = 0.2·U/D. At 60 m/s through 40 mm that is 300 Hz.
        const double velocity = 60.0;
        const double diameter = 0.040;
        var expected = 0.2 * velocity / diameter;

        var track = new double[48_000];
        Array.Fill(track, velocity);
        var noise = FlowNoise.Generate(track, SampleRate, diameter, 99UL, 1.0, 6.0);

        var spectrum = Fft.MagnitudeSpectrum(noise, out var padded);
        var peakBin = 1;
        for (var k = 2; k < spectrum.Length / 4; k++)
        {
            if (spectrum[k] > spectrum[peakBin])
            {
                peakBin = k;
            }
        }

        var peak = Fft.BinFrequency(peakBin, SampleRate, padded);
        output.WriteLine($"{velocity:F0} m/s through {diameter * 1000:F0} mm: peak {peak:F0} Hz, St=0.2 predicts {expected:F0} Hz");
        peak.Should().BeApproximately(expected, expected * 0.25, "the band-pass sits on the Strouhal frequency");
    }

    [Fact]
    public void The_velocity_probe_records_alongside_pressure_and_keeps_its_mean()
    {
        // Pressure tables have their DC removed because only the fluctuation
        // radiates; velocity tables must NOT, because the broadband source
        // scales on |U| and removing the mean would delete the flow.
        var samples = new float[720];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(45.0 + 10.0 * Math.Sin(2.0 * Math.PI * i / samples.Length));
        }

        var table = new CrankWavetable(4000.0, samples, 1.0);
        table.Mean.Should().BeApproximately(45.0, 0.1);
        table.WithoutMean().Mean.Should().BeApproximately(0.0, 1e-4);
        table.WithoutMean().Load.Should().Be(1.0, "removing the mean must not lose the load line");
    }

    [Fact]
    public void Gate_mechanical_events_track_engine_speed_and_cylinder_count()
    {
        // The timing is the part that is real: a four-cylinder must produce
        // four times the valve events of a single, and twice the events at
        // twice the speed.
        // Ticks are deliberately made very short here so individual events can
        // be resolved: at four cylinders the closest pair is only 30° apart,
        // which is 1.7 ms at 3000 rpm, and the default 3.5 ms decay would
        // smear them into each other.
        int Events(int cylinders, double rpm)
        {
            var stem = MechanicalLayer.Render(
                RpmProfile.Steady(rpm, 1.0), cylinders, 1, SampleRate, 7UL,
                new MechanicalCharacter(
                    ValveTrainLevel: 0.5, TimingDriveLevel: 0.0, InjectorLevel: 0.0, TickDecayMs: 0.15));
            return CountTransients(stem.Samples, threshold: 0.05);
        }

        var single3000 = Events(1, 3000.0);
        var single6000 = Events(1, 6000.0);
        var four3000 = Events(4, 3000.0);

        output.WriteLine($"valve events per second: 1-cyl @3000 {single3000}, 1-cyl @6000 {single6000}, 4-cyl @3000 {four3000}");

        // One cylinder, two valve events per 720°, 3000 rpm = 25 cycles/s → 50.
        single3000.Should().BeInRange(45, 55);
        single6000.Should().BeInRange(90, 110, "twice the speed is twice the events");
        four3000.Should().BeInRange(180, 220, "four cylinders is four times the events");
    }

    [Fact]
    public void Gate_the_timing_drive_whine_sits_at_the_geometric_order()
    {
        // Camshaft turns at half crank speed, so meshing frequency is
        // teeth × (rpm/60) / 2. At 4000 rpm with 20 teeth that is 666.7 Hz.
        const double rpm = 4000.0;
        const int teeth = 20;
        var expected = teeth * rpm / 60.0 / 2.0;

        var stem = MechanicalLayer.Render(
            RpmProfile.Steady(rpm, 1.0), 1, 1, SampleRate, 3UL,
            new MechanicalCharacter(
                ValveTrainLevel: 0.0, TimingDriveLevel: 0.3, InjectorLevel: 0.0, TimingDriveTeeth: teeth));

        var spectrum = Fft.MagnitudeSpectrum(Array.ConvertAll(stem.Samples, s => (double)s), out var padded);
        var peakBin = 1;
        for (var k = 2; k < spectrum.Length / 2; k++)
        {
            if (spectrum[k] > spectrum[peakBin])
            {
                peakBin = k;
            }
        }

        var peak = Fft.BinFrequency(peakBin, SampleRate, padded);
        output.WriteLine($"{teeth} teeth at {rpm:F0} rpm: whine at {peak:F1} Hz, geometry predicts {expected:F1} Hz");
        peak.Should().BeApproximately(expected, expected * 0.02);
    }

    [Fact]
    public void The_mechanical_layer_is_deterministic_and_can_be_switched_off()
    {
        var profile = RpmProfile.Sweep(2000.0, 5000.0, 0.5);

        var first = MechanicalLayer.Render(profile, 4, 2, SampleRate, 42UL);
        var second = MechanicalLayer.Render(profile, 4, 2, SampleRate, 42UL);
        first.Samples.Should().Equal(second.Samples, "same seed, bit-identical (plan Part 0)");

        var silent = MechanicalLayer.Render(profile, 4, 2, SampleRate, 42UL, MechanicalCharacter.None);
        silent.Samples.Should().OnlyContain(s => s == 0f, "None means none");

        first.Name.Should().Be("mechanical", "it stays a separate stem so it can be soloed or muted");
    }

    [Fact]
    public void A_clattery_engine_is_louder_than_a_refined_one()
    {
        var profile = RpmProfile.Steady(3000.0, 0.5);
        var clattery = MechanicalLayer.Render(profile, 1, 2, SampleRate, 5UL, MechanicalCharacter.Clattery);
        var refined = MechanicalLayer.Render(profile, 1, 2, SampleRate, 5UL, MechanicalCharacter.Refined);

        output.WriteLine($"clattery {Rms(clattery.Samples):E3}, refined {Rms(refined.Samples):E3}");
        Rms(clattery.Samples).Should().BeGreaterThan(Rms(refined.Samples) * 2.0);
    }

    /// <summary>Counts rising crossings of a threshold, with a refractory gap.</summary>
    private static int CountTransients(float[] samples, double threshold)
    {
        var count = 0;
        var cooldown = 0;
        foreach (var sample in samples)
        {
            if (cooldown > 0)
            {
                cooldown--;
                continue;
            }

            if (Math.Abs(sample) > threshold)
            {
                count++;
                cooldown = (int)(SampleRate * 0.0012);
            }
        }

        return count;
    }
}
