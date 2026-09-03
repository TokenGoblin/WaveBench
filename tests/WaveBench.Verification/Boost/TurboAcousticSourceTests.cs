using FluentAssertions;
using WaveBench.Boost.Acoustics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 15, plan §4.8's "new sources" table: compressor blade-pass (with
/// splitter-blade content and shaft-order sidebands), whoosh, wastegate flow
/// noise and blow-off event level. These are regression/sanity checks on the
/// arithmetic and scaling shape, not gate-clause tests — the plan's own table
/// does not cite a source for whoosh/wastegate/BOV level scaling (they are
/// acknowledged as phenomenological), so <see cref="TurboAcousticSources"/>
/// marks those as explicit, exposed calibration constants rather than
/// invented facts.
/// </summary>
public class TurboAcousticSourceTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate_compressor_blade_pass_frequency_is_shaft_rate_times_blade_count()
    {
        // Tyler & Sofrin, "Axial Flow Compressor Noise Studies," SAE 620532,
        // 1962 -- f_BPF = (N/60)*B for a rotor with B full blades.
        var bpf = TurboAcousticSources.CompressorBladePassFrequency(turboRpm: 150_000, bladeCount: 6);

        bpf.Should().BeApproximately(150_000.0 / 60.0 * 6.0, 1e-6);
        output.WriteLine($"150,000 rpm, 6 blades -> BPF {bpf:F0} Hz");
    }

    [Fact]
    public void Blade_pass_harmonics_include_the_combined_full_plus_splitter_tone()
    {
        var shaftHz = 180_000.0 / 60.0;
        var harmonics = TurboAcousticSources.BladePassHarmonics(180_000, bladeCount: 6, splitterCount: 6, harmonics: 2);

        harmonics.Should().Contain(shaftHz * 6.0);
        harmonics.Should().Contain(shaftHz * 12.0, "2nd harmonic of the full-blade count");
        harmonics.Should().Contain(shaftHz * 12.0, "the combined full+splitter count tone coincides with the 2nd harmonic here");

        var noSplitter = TurboAcousticSources.BladePassHarmonics(180_000, bladeCount: 6, splitterCount: 0, harmonics: 2);
        noSplitter.Should().HaveCount(2, "with no splitters there is no extra combined-count tone");

        output.WriteLine($"With splitters: {string.Join(", ", harmonics.Select(h => h.ToString("F0")))} Hz");
    }

    [Fact]
    public void Whoosh_level_rises_with_tip_speed_and_with_incidence_away_from_design()
    {
        var atDesign = TurboAcousticSources.WhooshLevel(tipSpeedMPerS: 300, referenceTipSpeedMPerS: 300, incidenceDeg: 0);
        var faster = TurboAcousticSources.WhooshLevel(tipSpeedMPerS: 400, referenceTipSpeedMPerS: 300, incidenceDeg: 0);
        var offDesign = TurboAcousticSources.WhooshLevel(tipSpeedMPerS: 300, referenceTipSpeedMPerS: 300, incidenceDeg: 15);

        faster.Should().BeGreaterThan(atDesign, "faster tip speed must read louder, not just different");
        offDesign.Should().BeGreaterThan(atDesign, "incidence away from design flow adds level");

        output.WriteLine($"300 m/s, 0deg: {atDesign:F1} dB; 400 m/s, 0deg: {faster:F1} dB; 300 m/s, 15deg: {offDesign:F1} dB");
    }

    [Fact]
    public void Wastegate_and_bov_levels_are_zero_when_shut_and_rise_as_they_open()
    {
        var shutGate = TurboAcousticSources.WastegateFlowNoiseLevel(divertedFlowKgPerS: 0.0, referenceFlowKgPerS: 0.05);
        var openGate = TurboAcousticSources.WastegateFlowNoiseLevel(divertedFlowKgPerS: 0.05, referenceFlowKgPerS: 0.05);
        shutGate.Should().Be(double.NegativeInfinity, "no diverted flow means no gate noise at all");
        openGate.Should().BeGreaterThan(0.0);

        var shutBov = TurboAcousticSources.BlowOffEventLevel(pressureDifferentialPa: 0.0, crackingPressurePa: 30_000, fullOpenPressurePa: 80_000);
        var midBov = TurboAcousticSources.BlowOffEventLevel(pressureDifferentialPa: 55_000, crackingPressurePa: 30_000, fullOpenPressurePa: 80_000);
        var fullBov = TurboAcousticSources.BlowOffEventLevel(pressureDifferentialPa: 90_000, crackingPressurePa: 30_000, fullOpenPressurePa: 80_000);

        shutBov.Should().Be(double.NegativeInfinity, "below cracking pressure the valve is shut and silent");
        fullBov.Should().BeGreaterThan(midBov);
        midBov.Should().BeGreaterThan(double.NegativeInfinity);

        output.WriteLine($"Wastegate shut: {shutGate}, open: {openGate:F1} dB");
        output.WriteLine($"BOV shut: {shutBov}, mid: {midBov:F1} dB, full: {fullBov:F1} dB");
    }
}
