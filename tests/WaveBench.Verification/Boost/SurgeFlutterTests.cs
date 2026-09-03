using FluentAssertions;
using WaveBench.Boost.Acoustics;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 15 gate, third clause: <i>"surge flutter frequency physically derived,
/// not tuned by ear."</i> Plan §4.8: <i>"surge flutter — modulation at the
/// Greitzer surge cycle frequency — predicted, not sampled; it falls out of
/// the surge model."</i>
///
/// The test that actually proves the clause is
/// <see cref="Gate_surge_flutter_frequency_is_derived_from_the_greitzer_model_not_tuned"/>:
/// the flutter's modulation frequency is asserted equal to
/// <see cref="GreitzerSurgeResult.SurgeFrequencyHz"/> across several distinct
/// plenum/duct geometries, so it moves whenever the physics moves — a hand-
/// tuned constant could not do that.
/// </summary>
public class SurgeFlutterTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0.0006, 0.0025, 0.08)]
    [InlineData(0.0020, 0.0025, 0.08)]
    [InlineData(0.0006, 0.0018, 0.15)]
    public void Gate_surge_flutter_frequency_is_derived_from_the_greitzer_model_not_tuned(
        double volumeM3, double areaM2, double lengthM)
    {
        var surge = GreitzerSurgeModel.Evaluate(
            wheelTipSpeedMPerS: 350.0,
            soundSpeedMPerS: 340.0,
            plenumVolumeM3: volumeM3,
            ductAreaM2: areaM2,
            effectiveDuctLengthM: lengthM);

        var flutter = SurgeFlutterSource.Evaluate(surge);

        flutter.ModulationFrequencyHz.Should().Be(surge.SurgeFrequencyHz,
            "the flutter source must read the frequency off the surge model, never carry its own number");

        output.WriteLine($"V={volumeM3 * 1000:F2} L, A={areaM2 * 1e4:F1} cm2, L={lengthM * 1000:F0} mm "
                          + $"-> B={surge.BParameter:F2} ({surge.Classification}), flutter at {flutter.ModulationFrequencyHz:F1} Hz");
    }

    [Fact]
    public void Modulation_depth_saturates_at_full_amplitude_once_surge_is_deep()
    {
        var mild = SurgeFlutterSource.Evaluate(new GreitzerSurgeResult(0.3, 100.0, 100.0, SurgeClassification.Mild));
        var deep = SurgeFlutterSource.Evaluate(new GreitzerSurgeResult(2.5, 100.0, 100.0, SurgeClassification.Deep));

        mild.ModulationDepth.Should().BeInRange(0.0, 1.0);
        deep.ModulationDepth.Should().Be(1.0, "deep surge is a full-amplitude relaxation oscillation, not a partial one");
        deep.ModulationDepth.Should().BeGreaterThan(mild.ModulationDepth);
    }

    [Fact]
    public void The_modulation_envelope_actually_oscillates_at_the_predicted_frequency()
    {
        var surge = new GreitzerSurgeResult(1.2, 40.0, 40.0, SurgeClassification.Deep);
        var flutter = SurgeFlutterSource.Evaluate(surge);

        const double sampleRate = 4000.0;
        const double duration = 1.0;
        var envelope = SurgeFlutterSource.ModulationEnvelope(flutter, duration, sampleRate);

        envelope.Should().HaveCount((int)(duration * sampleRate));
        envelope.Should().OnlyContain(v => v >= 0.0 && v <= 1.0 + 1e-9);

        // Count zero-up-crossings of (envelope - mean) as a cheap, honest
        // frequency check that doesn't require an FFT dependency here.
        var mean = envelope.Average();
        var crossings = 0;
        for (var i = 1; i < envelope.Length; i++)
        {
            if (envelope[i - 1] - mean < 0 && envelope[i] - mean >= 0)
            {
                crossings++;
            }
        }

        var measuredHz = crossings / duration;
        output.WriteLine($"Predicted {flutter.ModulationFrequencyHz:F1} Hz, measured (crossing count) {measuredHz:F1} Hz");
        measuredHz.Should().BeApproximately(flutter.ModulationFrequencyHz, 2.0);
    }
}
