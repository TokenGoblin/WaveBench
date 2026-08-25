using FluentAssertions;
using WaveBench.Core.Thermo;
using WaveBench.Core.Thermo.Fuels;
using Xunit;

namespace WaveBench.Core.Tests.Thermo;

public class FuelTests
{
    private static readonly SpeciesDatabase Db = SpeciesDatabase.Default;

    [Fact]
    public void Gate_stoich_afr_from_formula_matches_tabulated_for_every_shipped_fuel()
    {
        foreach (var fuel in FuelLibrary.All)
        {
            var computed = fuel.StoichAfr;
            var tabulated = fuel.TabulatedStoichAfr;
            var deviation = Math.Abs(computed - tabulated) / tabulated;
            deviation.Should().BeLessThan(0.005,
                $"{fuel.Name}: formula gives {computed:F3}, tabulated {tabulated:F3} ({deviation:P2})");
        }
    }

    [Fact]
    public void Gate_m100_charge_cooling_at_rich_mixture_is_30_to_50_K()
    {
        // Plan §2.4 gate: M100 at λ = 0.8, port injection defaults → 30–50 K.
        var drop = ChargeCooling.TemperatureDrop(
            FuelLibrary.Methanol, lambda: 0.8, InjectorLocation.Port, Db);
        drop.Should().BeInRange(30.0, 50.0);
    }

    [Fact]
    public void Charge_cooling_ranks_alcohols_above_gasoline()
    {
        double Drop(Fuel f) => ChargeCooling.TemperatureDrop(f, 1.0, InjectorLocation.Port, Db);
        Drop(FuelLibrary.Methanol).Should().BeGreaterThan(Drop(FuelLibrary.E85));
        Drop(FuelLibrary.E85).Should().BeGreaterThan(Drop(FuelLibrary.GasolineRon95));
        Drop(FuelLibrary.Methane).Should().Be(0.0, "gaseous fuel has no evaporative cooling");
    }

    [Fact]
    public void Direct_injection_cools_the_port_charge_far_less()
    {
        var port = ChargeCooling.TemperatureDrop(FuelLibrary.Methanol, 1.0, InjectorLocation.Port, Db);
        var di = ChargeCooling.TemperatureDrop(FuelLibrary.Methanol, 1.0, InjectorLocation.Direct, Db);
        di.Should().BeLessThan(port * 0.35);
    }

    [Fact]
    public void Oxygen_mass_fractions_are_correct()
    {
        FuelLibrary.Methanol.OxygenMassFraction.Should().BeApproximately(0.4993, 0.001);
        FuelLibrary.Ethanol.OxygenMassFraction.Should().BeApproximately(0.3473, 0.001);
        FuelLibrary.IsoOctane.OxygenMassFraction.Should().Be(0.0);
    }

    [Fact]
    public void Every_fuel_vapour_species_exists_in_the_database()
    {
        foreach (var fuel in FuelLibrary.All)
        {
            Db.Contains(fuel.VapourSpeciesName).Should().BeTrue(
                $"{fuel.Name} references vapour species {fuel.VapourSpeciesName}");
        }
    }

    [Fact]
    public void Blend_conserves_mass_and_energy()
    {
        var e85 = FuelLibrary.E85;
        // Ethanol volume fraction 0.85 → mass fraction 0.851 (densities 789/750).
        var wEth = 0.85 * 789.0 / (0.85 * 789.0 + 0.15 * 750.0);
        var expectedLhv = wEth * 26.9e6 + (1 - wEth) * 44.0e6;
        e85.LowerHeatingValue.Should().BeApproximately(expectedLhv, 1e3);
        e85.Formula.Oxygen.Should().BeGreaterThan(0.8, "E85 is mostly ethanol (1 O per molecule)");
    }

    [Fact]
    public void Sensitivity_is_ron_minus_mon()
    {
        FuelLibrary.GasolineRon95.Sensitivity.Should().Be(10.0);
        FuelLibrary.Hydrogen.Sensitivity.Should().BeNull();
    }
}
