using FluentAssertions;
using WaveBench.Core.Thermo;
using WaveBench.Core.Thermo.Fuels;
using Xunit;

namespace WaveBench.Core.Tests.Thermo;

public class CombustionProductsTests
{
    private static readonly SpeciesDatabase Db = SpeciesDatabase.Default;
    private static readonly FuelFormula IsoOctane = new(8, 18, 0);

    private static Dictionary<string, double> MoleFractions(GasComposition c)
    {
        var mix = new MixtureThermo(c, Db);
        return c.MassFractions.ToDictionary(
            kv => kv.Key,
            kv => kv.Value * mix.MolarMass / Db[kv.Key].MolarMass,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stoichiometric_iso_octane_products_hand_calculation()
    {
        // Per kmol fuel: 12.5 O2; N2 12.5·3.72784 = 46.598; Ar 0.5574;
        // CO2 8 + 0.0246 (air CO2); H2O 9. Total 64.180 kmol.
        var products = CombustionProducts.Of(IsoOctane, 1.0, Db);
        var x = MoleFractions(products);

        x["CO2"].Should().BeApproximately(8.0246 / 64.180, 1e-4);
        x["H2O"].Should().BeApproximately(9.0 / 64.180, 1e-4);
        x["N2"].Should().BeApproximately(46.598 / 64.180, 1e-4);
        x.ContainsKey("O2").Should().BeFalse("no oxygen survives stoichiometric combustion");

        var mix = new MixtureThermo(products, Db);
        mix.MolarMass.Should().BeApproximately(28.72, 0.05);
    }

    [Fact]
    public void Burnt_gas_gamma_at_900C_is_in_the_documented_band()
    {
        // Plan §2.2: γ ≈ 1.28–1.31 at 900 °C burnt gas (vs 1.40 for cold air).
        var products = CombustionProducts.Of(IsoOctane, 1.0, Db);
        var mix = new MixtureThermo(products, Db);
        mix.Gamma(1173.15).Should().BeInRange(1.27, 1.32);
    }

    [Fact]
    public void Hot_exhaust_sound_speed_far_exceeds_cold_intake()
    {
        // Plan §2.2: a 950 K exhaust cell propagates near 600 m/s.
        var products = CombustionProducts.Of(IsoOctane, 1.0, Db);
        var mix = new MixtureThermo(products, Db);
        mix.SoundSpeed(950.0).Should().BeInRange(580.0, 640.0);
    }

    [Fact]
    public void Lean_products_contain_excess_oxygen_and_no_co()
    {
        var x = MoleFractions(CombustionProducts.Of(IsoOctane, 0.8, Db));
        x["O2"].Should().BeGreaterThan(0.03);
        x.ContainsKey("CO").Should().BeFalse();
    }

    [Fact]
    public void Rich_products_satisfy_element_balance_and_shift_equilibrium()
    {
        const double phi = 1.2;
        const double k = 3.5;
        var x = MoleFractions(CombustionProducts.Of(IsoOctane, phi, Db, k));

        x.ContainsKey("O2").Should().BeFalse("rich combustion consumes all oxygen");
        x["CO"].Should().BeGreaterThan(0.01);
        x["H2"].Should().BeGreaterThan(0.001);

        // Water-gas-shift consistency: K = (X_CO·X_H2O)/(X_CO2·X_H2).
        var shift = x["CO"] * x["H2O"] / (x["CO2"] * x["H2"]);
        shift.Should().BeApproximately(k, k * 0.02);

        // Element balances per kmol fuel (recover totals via N2, which is known exactly).
        var o2Supplied = IsoOctane.StoichiometricO2Moles / phi;
        var scale = o2Supplied * AirComposition.N2PerO2 / x["N2"]; // total kmol products
        var carbon = (x["CO2"] + x["CO"]) * scale - o2Supplied * AirComposition.Co2PerO2;
        var hydrogen = (x["H2O"] + x["H2"]) * scale * 2.0;
        var oxygen = (2 * x["CO2"] + x["CO"] + x["H2O"]) * scale
                     - o2Supplied * AirComposition.Co2PerO2 * 2.0;

        carbon.Should().BeApproximately(8.0, 1e-6);
        hydrogen.Should().BeApproximately(18.0, 1e-6);
        oxygen.Should().BeApproximately(2.0 * o2Supplied, 1e-6);
    }

    [Fact]
    public void Rich_gamma_differs_from_lean_gamma()
    {
        var lean = new MixtureThermo(CombustionProducts.Of(IsoOctane, 0.8, Db), Db);
        var rich = new MixtureThermo(CombustionProducts.Of(IsoOctane, 1.2, Db), Db);
        Math.Abs(lean.Gamma(1200.0) - rich.Gamma(1200.0)).Should().BeGreaterThan(0.001);
    }

    [Fact]
    public void Oxygenated_fuel_needs_less_air()
    {
        var ethanol = CombustionProducts.Of(new FuelFormula(2, 6, 1), 1.0, Db);
        var x = MoleFractions(ethanol);
        x["CO2"].Should().BeGreaterThan(0.0);
        x["H2O"].Should().BeGreaterThan(x["CO2"], "ethanol makes 1.5 mol H2O per mol CO2");
    }
}
