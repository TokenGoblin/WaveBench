using FluentAssertions;
using WaveBench.Core.Thermo.Fuels;
using Xunit;

namespace WaveBench.Core.Tests.Thermo;

public class FlameSpeedAndKnockTests
{
    private const double Atm = 101_325.0;

    [Fact]
    public void Reference_flame_speed_at_reference_conditions_is_bm()
    {
        // At φ = φm, Tu = 298 K, p = 1 atm: S_L = Bm exactly.
        FlameSpeed.Laminar(FuelLibrary.Methanol, 1.11, 298.0, Atm)
            .Should().BeApproximately(0.3692, 1e-6);
        FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.13, 298.0, Atm)
            .Should().BeApproximately(0.2758, 1e-6);
        FlameSpeed.Laminar(FuelLibrary.IsoOctane, 1.13, 298.0, Atm)
            .Should().BeApproximately(0.2632, 1e-6);
    }

    [Fact]
    public void Flame_speed_rises_with_unburned_temperature_and_falls_with_pressure()
    {
        var baseline = FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.0, 298.0, Atm);
        FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.0, 500.0, Atm).Should().BeGreaterThan(baseline * 2);
        FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.0, 298.0, 10 * Atm).Should().BeLessThan(baseline);
    }

    [Fact]
    public void Dilution_slows_the_flame()
    {
        var clean = FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.0, 400.0, Atm);
        var diluted = FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.0, 400.0, Atm, diluentMassFraction: 0.1);
        (diluted / clean).Should().BeApproximately(1.0 - 2.1 * 0.1, 1e-9);
    }

    [Fact]
    public void Hydrogen_has_no_metghalchi_keck_coefficients()
    {
        var act = () => FlameSpeed.Laminar(FuelLibrary.Hydrogen, 1.0, 298.0, Atm);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validity_range_flags_out_of_range_inputs()
    {
        FlameSpeed.IsWithinValidity(1.0, 400.0, Atm).Should().BeTrue();
        FlameSpeed.IsWithinValidity(0.5, 400.0, Atm).Should().BeFalse();
        FlameSpeed.IsWithinValidity(1.0, 900.0, Atm).Should().BeFalse();
        FlameSpeed.IsWithinValidity(1.0, 400.0, 100 * Atm).Should().BeFalse();
    }

    [Fact]
    public void Typed_overloads_agree_with_si_double_apis()
    {
        var t = WaveBench.Model.Units.Temperature.FromCelsius(120.0);
        var p = WaveBench.Model.Units.Pressure.FromBar(30.0);

        FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.0, t, p)
            .Should().Be(FlameSpeed.Laminar(FuelLibrary.GasolineRon95, 1.0, t.Kelvin, p.Pascals));
        KnockModel.InductionTime(95, p, WaveBench.Model.Units.Temperature.FromKelvin(850.0))
            .Should().Be(KnockModel.InductionTime(95, p.Pascals, 850.0));
    }

    [Fact]
    public void Induction_time_shortens_with_pressure_and_temperature()
    {
        var baseline = KnockModel.InductionTime(95, 30 * Atm, 800.0);
        KnockModel.InductionTime(95, 60 * Atm, 800.0).Should().BeLessThan(baseline);
        KnockModel.InductionTime(95, 30 * Atm, 900.0).Should().BeLessThan(baseline);
    }

    [Fact]
    public void Higher_octane_resists_longer()
    {
        var ron95 = KnockModel.InductionTime(95, 40 * Atm, 850.0);
        var e85 = KnockModel.InductionTime(106, 40 * Atm, 850.0);
        var m100 = KnockModel.InductionTime(109, 40 * Atm, 850.0);
        ron95.Should().BeLessThan(e85);
        e85.Should().BeLessThan(m100);
    }

    [Fact]
    public void Livengood_wu_integral_for_constant_conditions_is_time_over_tau()
    {
        const double on = 95;
        const double p = 40 * Atm;
        const double t = 850.0;
        var tau = KnockModel.InductionTime(on, p, t);

        // A constant-condition trace lasting exactly τ must knock at τ.
        var trace = Enumerable.Range(0, 101)
            .Select(i => (Time: i * tau / 100.0 * 1.5, Pressure: p, Temperature: t))
            .ToList();
        var result = KnockModel.LivengoodWu(trace, on);

        result.KnockPredicted.Should().BeTrue();
        result.KnockTimeSeconds.Should().NotBeNull();
        result.KnockTimeSeconds!.Value.Should().BeApproximately(tau, tau * 0.01);
    }

    [Fact]
    public void Livengood_wu_ranks_fuels_on_the_same_trace()
    {
        const double p = 45 * Atm;
        const double t = 900.0;
        var duration = KnockModel.InductionTime(95, p, t) * 0.8; // sub-critical for RON95
        var trace = Enumerable.Range(0, 51)
            .Select(i => (Time: i * duration / 50.0, Pressure: p, Temperature: t))
            .ToList();

        var ron95 = KnockModel.LivengoodWu(trace, 95).Integral;
        var e85 = KnockModel.LivengoodWu(trace, 106).Integral;
        var m100 = KnockModel.LivengoodWu(trace, 109).Integral;

        // Correct qualitative ranking: RON95 closest to knock, M100 furthest.
        ron95.Should().BeGreaterThan(e85);
        e85.Should().BeGreaterThan(m100);
    }
}
