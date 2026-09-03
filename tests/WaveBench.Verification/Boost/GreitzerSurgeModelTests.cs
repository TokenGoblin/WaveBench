using FluentAssertions;
using WaveBench.Boost.Unsteady;
using WaveBench.Core.EngineModel;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 15 gate, third clause: <i>"surge flutter frequency physically derived,
/// not tuned by ear."</i> Plan §4.8: <i>"surge flutter — modulation at the
/// Greitzer surge cycle frequency — predicted, not sampled; it falls out of
/// the surge model."</i> Also closes plan validation item #19 (§6.2): the
/// Greitzer B-parameter mild/deep classification, which needs no measured
/// data — it is checked against Greitzer's own published qualitative trend,
/// the same shape as the twin-scroll and cam-optimum self-consistency checks.
///
/// Sources: Greitzer, E. M. "Surge and Rotating Stall in Axial Flow
/// Compressors, Part I: Theoretical Compression System Model." ASME J. Eng.
/// Power 98(2), 1976 — the B parameter and its critical value near 0.8.
/// Moore, F. K. &amp; Greitzer, E. M. "A Theory of Post-Stall Transients in
/// Axial Compression Systems." ASME J. Eng. Gas Turbines Power 108(1), 1986 —
/// deep surge oscillating close to the system's own Helmholtz frequency.
/// </summary>
public class GreitzerSurgeModelTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate_the_surge_frequency_is_the_same_number_quick_estimate_would_give_never_re_derived()
    {
        const double soundSpeed = 340.0;
        const double neckArea = 0.0025;
        const double volume = 0.004;
        const double effectiveLength = 0.12;

        var expected = QuickEstimate.HelmholtzFrequency(soundSpeed, neckArea, volume, effectiveLength);

        var result = GreitzerSurgeModel.Evaluate(
            wheelTipSpeedMPerS: 300.0,
            soundSpeedMPerS: soundSpeed,
            plenumVolumeM3: volume,
            ductAreaM2: neckArea,
            effectiveDuctLengthM: effectiveLength);

        result.SurgeFrequencyHz.Should().Be(expected,
            "the surge model must delegate to QuickEstimate.HelmholtzFrequency, never re-derive it");
        result.HelmholtzFrequencyHz.Should().Be(expected);

        output.WriteLine($"Helmholtz/surge frequency: {result.SurgeFrequencyHz:F1} Hz");
    }

    [Fact]
    public void Gate_surge_frequency_moves_when_the_physical_inputs_move_which_a_tuned_constant_could_not_do()
    {
        var small = GreitzerSurgeModel.Evaluate(300.0, 340.0, plenumVolumeM3: 0.002, ductAreaM2: 0.0025, effectiveDuctLengthM: 0.12);
        var large = GreitzerSurgeModel.Evaluate(300.0, 340.0, plenumVolumeM3: 0.020, ductAreaM2: 0.0025, effectiveDuctLengthM: 0.12);

        // f_H = (a/2pi)*sqrt(A/(V*L)) falls as plenum volume rises -- a ten-
        // times bigger plenum must give a materially lower surge frequency.
        large.SurgeFrequencyHz.Should().BeLessThan(small.SurgeFrequencyHz);
        (small.SurgeFrequencyHz / large.SurgeFrequencyHz).Should().BeApproximately(Math.Sqrt(10.0), 1e-6);

        output.WriteLine($"2 L plenum: {small.SurgeFrequencyHz:F1} Hz, 20 L plenum: {large.SurgeFrequencyHz:F1} Hz");
    }

    [Theory]
    [InlineData(150.0, SurgeClassification.Mild)]
    [InlineData(600.0, SurgeClassification.Deep)]
    public void Gate_b_parameter_classification_matches_greitzers_published_mild_deep_trend(
        double tipSpeed, SurgeClassification expected)
    {
        // Fixed, small compression-system geometry: a short, small-area duct
        // keeps the Helmholtz frequency high, so only a genuinely high tip
        // speed pushes B over Greitzer's critical value -- exactly the
        // qualitative trend the source reports (higher U, or a longer/smaller
        // duct, drives the system from mild into deep surge).
        var result = GreitzerSurgeModel.Evaluate(
            wheelTipSpeedMPerS: tipSpeed,
            soundSpeedMPerS: 340.0,
            plenumVolumeM3: 0.0008,
            ductAreaM2: 0.0025,
            effectiveDuctLengthM: 0.10);

        output.WriteLine($"U={tipSpeed:F0} m/s -> B={result.BParameter:F3} ({result.Classification})");

        result.Classification.Should().Be(expected);
    }

    [Fact]
    public void Gate_the_critical_b_parameter_is_greitzers_own_published_value()
    {
        GreitzerSurgeModel.CriticalB.Should().BeApproximately(0.8, 1e-9,
            "Greitzer 1976 Part I reports mild-to-deep transition near B = 0.8");
    }

    [Fact]
    public void B_parameter_rises_with_tip_speed_and_falls_with_duct_length_as_the_formula_requires()
    {
        var baseline = GreitzerSurgeModel.Evaluate(250.0, 340.0, 0.006, 0.0025, 0.10);
        var fasterWheel = GreitzerSurgeModel.Evaluate(400.0, 340.0, 0.006, 0.0025, 0.10);
        var longerDuct = GreitzerSurgeModel.Evaluate(250.0, 340.0, 0.006, 0.0025, 0.30);

        fasterWheel.BParameter.Should().BeGreaterThan(baseline.BParameter);
        longerDuct.BParameter.Should().BeLessThan(baseline.BParameter);
    }
}
