using FluentAssertions;
using WaveBench.Acoustics.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 11 §6.1 verification of DIN 45692:2009 sharpness.
///
/// The anchor is the standard's own reference signal: narrowband noise one
/// critical band wide at 1 kHz, 60 dB, is 1 acum by definition. The constant
/// k = 0.11 exists precisely to make that true, so hitting it confirms both
/// the weighting function and the specific-loudness pattern feeding it.
/// </summary>
public class SharpnessTests(ITestOutputHelper output)
{
    /// <summary>
    /// The DIN 45692 reference: one critical band (≈160 Hz at 1 kHz) of noise,
    /// which at one-third-octave resolution is contained in the 1 kHz band.
    /// </summary>
    private static double[] ReferenceSignal(double levelDb) =>
        ThirdOctaveAnalysis.ToneBandLevels(1000.0, levelDb);

    [Fact]
    public void Gate_the_din_45692_reference_signal_is_one_acum()
    {
        var sharpness = Sharpness.FromThirdOctaveBands(ReferenceSignal(60.0));
        output.WriteLine($"1 kHz narrowband noise @ 60 dB -> {sharpness:F3} acum");
        sharpness.Should().BeApproximately(1.0, 0.05,
            "k = 0.11 is defined to normalise exactly this signal to 1 acum");
    }

    [Fact]
    public void Gate_sharpness_rises_monotonically_with_centre_frequency()
    {
        // The whole point of the metric: the same loudness placed higher up
        // the Bark scale is sharper. A weighting with the wrong sign or a
        // mis-scaled Bark axis fails here even if the reference point passes.
        var previous = 0.0;
        foreach (var frequency in new[] { 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0 })
        {
            var sharpness = Sharpness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(frequency, 60.0));
            output.WriteLine($"{frequency,6:F0} Hz @ 60 dB -> {sharpness:F3} acum");
            sharpness.Should().BeGreaterThan(previous, $"{frequency:F0} Hz must be sharper than the band below it");
            previous = sharpness;
        }
    }

    [Fact]
    public void Gate_high_frequency_content_is_weighted_far_above_low()
    {
        // g(z) is flat to 15.8 Bark then rises exponentially, so an 8 kHz
        // narrowband is several times sharper than the 1 kHz reference even
        // at equal loudness.
        var low = Sharpness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(1000.0, 60.0));
        var high = Sharpness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(8000.0, 60.0));

        output.WriteLine($"1 kHz {low:F3} acum vs 8 kHz {high:F3} acum (ratio {high / low:F2})");
        high.Should().BeGreaterThan(low * 2.5, "the weighting above 15.8 Bark is the metric's whole character");
    }

    [Fact]
    public void The_weighting_function_is_continuous_at_the_knee()
    {
        // g(15.8) = 0.15·e⁰ + 0.85 = 1.0, so the two branches must meet. An
        // off-by-one in the comparison would put a step here.
        Sharpness.Weighting(Sharpness.WeightingKnee - 1e-9).Should().BeApproximately(1.0, 1e-9);
        Sharpness.Weighting(Sharpness.WeightingKnee).Should().BeApproximately(1.0, 1e-9);
        Sharpness.Weighting(Sharpness.WeightingKnee + 1e-9).Should().BeApproximately(1.0, 1e-6);

        Sharpness.Weighting(10.0).Should().Be(1.0, "the weighting is flat below the knee");
        Sharpness.Weighting(24.0).Should().BeGreaterThan(4.0, "and steep at the top of the scale");
    }

    [Fact]
    public void Sharpness_is_only_weakly_dependent_on_level()
    {
        // Unlike loudness, sharpness is meant to describe timbre, so the same
        // spectrum at 50 and 80 dB must stay in the same neighbourhood. It is
        // not perfectly invariant — the loudness pattern broadens with level.
        var quiet = Sharpness.FromThirdOctaveBands(ReferenceSignal(50.0));
        var loud = Sharpness.FromThirdOctaveBands(ReferenceSignal(80.0));

        output.WriteLine($"same spectrum: 50 dB -> {quiet:F3} acum, 80 dB -> {loud:F3} acum");
        (loud / quiet).Should().BeInRange(0.75, 1.35, "a 30 dB change must not reshape the timbre metric");
    }

    [Fact]
    public void Silence_reports_zero_rather_than_nan()
    {
        var silent = new double[28];
        Array.Fill(silent, -100.0);
        Sharpness.FromThirdOctaveBands(silent).Should().Be(0.0);
    }

    [Fact]
    public void Broadband_noise_sits_between_the_low_and_high_narrowbands()
    {
        var flat = new double[28];
        Array.Fill(flat, 50.0);
        var broadband = Sharpness.FromThirdOctaveBands(flat);
        var low = Sharpness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(250.0, 60.0));
        var high = Sharpness.FromThirdOctaveBands(ThirdOctaveAnalysis.ToneBandLevels(8000.0, 60.0));

        output.WriteLine($"broadband {broadband:F3} acum, between {low:F3} and {high:F3}");
        broadband.Should().BeInRange(low, high);
    }
}
