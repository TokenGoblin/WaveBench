using System.Numerics;
using FluentAssertions;
using WaveBench.Acoustics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

public class PropagationTests(ITestOutputHelper output)
{
    [Fact]
    public void Iso9613_absorption_matches_published_anchor_values()
    {
        // 20 °C, 70% RH, 1 atm — the classic condition. Published ISO 9613-1
        // values: ≈ 5 dB/km at 1 kHz, ≈ 23 dB/km at 4 kHz.
        var a1k = AtmosphericAbsorption.DbPerMetre(1000.0) * 1000.0;
        var a4k = AtmosphericAbsorption.DbPerMetre(4000.0) * 1000.0;
        output.WriteLine($"absorption 20°C/70%: 1 kHz {a1k:F2} dB/km, 4 kHz {a4k:F2} dB/km");

        a1k.Should().BeInRange(4.0, 6.5);
        a4k.Should().BeInRange(18.0, 28.0);

        // Low-frequency limit is the classical f² regime.
        var a50 = AtmosphericAbsorption.DbPerMetre(50.0);
        var a100 = AtmosphericAbsorption.DbPerMetre(100.0);
        (a100 / a50).Should().BeInRange(3.0, 4.1, "≈ f² scaling well below the relaxation frequencies");

        // Dry air absorbs more at mid frequencies than humid air (the famous
        // non-monotonic humidity dependence around 10-20% RH).
        var dry = AtmosphericAbsorption.DbPerMetre(4000.0, relativeHumidityPercent: 10.0);
        dry.Should().BeGreaterThan(AtmosphericAbsorption.DbPerMetre(4000.0, relativeHumidityPercent: 70.0));
    }

    [Fact]
    public void Ground_reflection_produces_the_interference_dip_where_theory_puts_it()
    {
        // h_s 0.3 m, h_r 1.2 m, d 7.5 m: path difference 0.0956 m →
        // first dip at f = c/(2Δ) ≈ 1795 Hz (plan §3.5: the outdoor-recording
        // comb). Rigid ground (R = 1) for the clean textbook case.
        var path = new PropagationPath(0.3, 1.2, 7.5, GroundReflectionCoefficient: 1.0);
        var delta = path.ReflectedDistance - path.DirectDistance;
        var dipExpected = path.SoundSpeed / (2.0 * delta);

        double best = 0, bestMag = double.MaxValue;
        for (var f = dipExpected * 0.7; f <= dipExpected * 1.3; f += 2.0)
        {
            var magnitude = path.Response(f).Magnitude;
            if (magnitude < bestMag)
            {
                bestMag = magnitude;
                best = f;
            }
        }

        output.WriteLine($"dip expected {dipExpected:F0} Hz, found {best:F0} Hz");
        best.Should().BeApproximately(dipExpected, dipExpected * 0.02);

        // Low frequency: constructive, ≈ (1 + R)/r_direct within the small
        // path-difference limit.
        var low = path.Response(30.0).Magnitude;
        low.Should().BeApproximately(1.0 / path.DirectDistance + 1.0 / path.ReflectedDistance, 0.01);
    }

    [Fact]
    public void Monopole_source_differentiates_in_frequency_and_rolls_off()
    {
        var q = Complex.One;
        var p100 = SourceRadiation.FarFieldPressure(q, 100.0, 1.0, 1.2, resolvedBandwidthHz: 5000.0);
        var p200 = SourceRadiation.FarFieldPressure(q, 200.0, 1.0, 1.2, resolvedBandwidthHz: 5000.0);
        (p200.Magnitude / p100.Magnitude).Should().BeApproximately(2.0, 0.01, "P ∝ jωQ");

        var near = SourceRadiation.FarFieldPressure(q, 100.0, 1.0, 1.2, 5000.0);
        var far = SourceRadiation.FarFieldPressure(q, 100.0, 4.0, 1.2, 5000.0);
        (near.Magnitude / far.Magnitude).Should().BeApproximately(4.0, 1e-9, "1/r spreading");

        // Above the resolved bandwidth the derivative is rolled off, not
        // amplified (plan §3.1: never present unresolved content as physical).
        var atBand = SourceRadiation.FarFieldPressure(q, 5000.0, 1.0, 1.2, 5000.0);
        var beyond = SourceRadiation.FarFieldPressure(q, 20000.0, 1.0, 1.2, 5000.0);
        (beyond.Magnitude / atBand.Magnitude).Should().BeLessThan(1.0);
    }

    [Fact]
    public void Flow_noise_is_deterministic_and_scales_with_velocity()
    {
        var slow = new double[9600];
        var fast = new double[9600];
        Array.Fill(slow, 50.0);
        Array.Fill(fast, 100.0);

        var a = FlowNoise.Generate(slow, 48_000.0, 0.03, seed: 7);
        var b = FlowNoise.Generate(slow, 48_000.0, 0.03, seed: 7);
        b.Should().Equal(a, "same seed → bit-identical noise (plan §3.4 reproducibility)");

        var c = FlowNoise.Generate(slow, 48_000.0, 0.03, seed: 8);
        c.Should().NotEqual(a);

        double Rms(double[] x) => Math.Sqrt(x.Skip(500).Sum(v => v * v) / (x.Length - 500));
        var ratio = Rms(FlowNoise.Generate(fast, 48_000.0, 0.03, seed: 7)) / Rms(a);
        output.WriteLine($"U-doubling RMS ratio: {ratio:F2} (U³ pressure scaling → 8 expected)");
        ratio.Should().BeInRange(5.5, 11.0, "confined-dipole U⁶ power → U³ pressure amplitude");

        var silent = FlowNoise.Generate(new double[1000], 48_000.0, 0.03, seed: 7);
        silent.Should().OnlyContain(v => v == 0.0, "no flow, no flow noise");
    }

    [Fact]
    public void Listener_presets_carry_the_rules_geometry_and_map_to_a_path()
    {
        var fsae = ListenerPreset.FsaeStatic;
        fsae.SlantDistanceM.Should().Be(0.5);
        fsae.AzimuthDeg.Should().Be(45.0);
        fsae.GroundReflection.Should().BeFalse("free-field per the rules");
        fsae.SlantDistance.Millimetres.Should().Be(500.0, "typed accessor at the UI boundary");
        ListenerPreset.All.Should().HaveCountGreaterThanOrEqualTo(4);

        // ToPath is the single defined preset → geometry mapping.
        var path = fsae.ToPath(sourceHeightM: 0.4);
        path.ReceiverHeight.Should().Be(0.0);
        // Slant 0.5 m with a 0.4 m height drop ⇒ 0.3 m horizontal (3-4-5).
        path.HorizontalDistance.Should().BeApproximately(0.3, 1e-9);
        path.DirectDistance.Should().BeApproximately(0.5, 1e-9, "slant distance is preserved");
        path.GroundReflectionCoefficient.Should().Be(0.0, "free-field presets suppress the ground path");

        var driveBy = ListenerPreset.DriveBy.ToPath(sourceHeightM: 0.4);
        driveBy.GroundReflectionCoefficient.Should().BeGreaterThan(0.5, "ISO 362 is measured over a hard pad");
    }

    [Fact]
    public void Propagation_derives_sound_speed_from_its_own_air_temperature()
    {
        // The repo's central rule (plan §2.2): never a hardcoded 343. A hot-day
        // path must move its interference comb, not just its absorption.
        var cool = new PropagationPath(0.3, 1.2, 7.5, GroundSurface.Asphalt, TemperatureK: 273.15);
        var hot = new PropagationPath(0.3, 1.2, 7.5, GroundSurface.Asphalt, TemperatureK: 313.15);

        cool.SoundSpeed.Should().BeApproximately(331.3, 0.5);
        hot.SoundSpeed.Should().BeApproximately(354.7, 0.5);

        double Dip(PropagationPath path)
        {
            var delta = path.ReflectedDistance - path.DirectDistance;
            var expected = path.SoundSpeed / (2.0 * delta);
            double best = 0, bestMag = double.MaxValue;
            for (var f = expected * 0.85; f <= expected * 1.15; f += 1.0)
            {
                var magnitude = path.Response(f).Magnitude;
                if (magnitude < bestMag)
                {
                    bestMag = magnitude;
                    best = f;
                }
            }

            return best;
        }

        var dipCool = Dip(cool);
        var dipHot = Dip(hot);
        output.WriteLine($"ground dip: 0 °C → {dipCool:F0} Hz, 40 °C → {dipHot:F0} Hz");
        (dipHot / dipCool).Should().BeApproximately(hot.SoundSpeed / cool.SoundSpeed, 0.01,
            "the comb scales with the local sound speed");
    }
}
