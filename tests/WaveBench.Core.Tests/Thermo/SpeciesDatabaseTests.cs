using FluentAssertions;
using WaveBench.Core.Thermo;
using Xunit;

namespace WaveBench.Core.Tests.Thermo;

public class SpeciesDatabaseTests
{
    private static readonly SpeciesDatabase Db = SpeciesDatabase.Default;

    [Fact]
    public void Loads_all_curated_species()
    {
        string[] expected =
        [
            "N2", "O2", "AR", "CO2", "H2O", "CO", "H2", "NO", "OH", "O", "H",
            "CH4", "C3H8", "CH3OH", "C2H5OH", "C7H8", "IC8H18", "NC7H16",
        ];
        foreach (var name in expected)
        {
            Db.Contains(name).Should().BeTrue($"species {name} must be in the curated set");
        }

        Db.All.Count.Should().Be(18);
    }

    [Theory]
    [InlineData("N2", 28.014)]
    [InlineData("O2", 31.998)]
    [InlineData("AR", 39.95)]
    [InlineData("CO2", 44.009)]
    [InlineData("H2O", 18.015)]
    [InlineData("CH4", 16.043)]
    [InlineData("CH3OH", 32.042)]
    [InlineData("C2H5OH", 46.069)]
    [InlineData("C7H8", 92.141)]
    [InlineData("IC8H18", 114.23)]
    [InlineData("NC7H16", 100.202)]
    public void Molar_masses_from_formula(string name, double expected) =>
        Db[name].MolarMass.Should().BeApproximately(expected, expected * 1e-3);

    // JANAF/NIST-JANAF molar cp values, J/(mol·K). Gate tolerance 0.2% (plan §2.3).
    [Theory]
    [InlineData("N2", 298.15, 29.12)]
    [InlineData("N2", 1000.0, 32.70)]
    [InlineData("N2", 2000.0, 35.97)]
    [InlineData("O2", 298.15, 29.38)]
    [InlineData("O2", 1000.0, 34.88)]
    [InlineData("CO2", 298.15, 37.13)]
    [InlineData("CO2", 1000.0, 54.31)]
    [InlineData("CO2", 2000.0, 60.35)]
    [InlineData("H2O", 298.15, 33.59)]
    [InlineData("H2O", 1000.0, 41.27)]
    [InlineData("H2O", 2000.0, 51.18)]
    [InlineData("CO", 298.15, 29.14)]
    [InlineData("CO", 1000.0, 33.18)]
    [InlineData("H2", 298.15, 28.84)]
    [InlineData("H2", 1000.0, 30.20)]
    public void Species_cp_matches_janaf(string name, double t, double expectedJPerMolK)
    {
        var cp = Db[name].MolarCp(t) / 1000.0; // J/(kmol·K) → J/(mol·K)
        cp.Should().BeApproximately(expectedJPerMolK, expectedJPerMolK * 0.002);
    }

    // Formation enthalpies at 298.15 K, kJ/mol (CODATA/JANAF).
    [Theory]
    [InlineData("CO2", -393.52)]
    [InlineData("H2O", -241.83)]
    [InlineData("CO", -110.53)]
    [InlineData("CH4", -74.87)]
    [InlineData("CH3OH", -201.0)]
    public void Formation_enthalpies_at_reference(string name, double expectedKJPerMol)
    {
        var h = Db[name].MolarEnthalpy(298.15) / 1e6; // J/kmol → kJ/mol
        h.Should().BeApproximately(expectedKJPerMol, Math.Abs(expectedKJPerMol) * 0.005);
    }

    [Fact]
    public void Elemental_species_have_zero_formation_enthalpy()
    {
        foreach (var name in new[] { "N2", "O2", "H2", "AR" })
        {
            (Db[name].MolarEnthalpy(298.15) / 1e6).Should().BeApproximately(0.0, 0.1,
                $"{name} is a reference element");
        }
    }

    [Fact]
    public void Cp_is_continuous_across_the_range_split()
    {
        foreach (var species in Db.All)
        {
            var below = species.Cp(species.TMid - 0.01);
            var above = species.Cp(species.TMid + 0.01);
            var jump = Math.Abs(above - below) / below;
            jump.Should().BeLessThan(0.005,
                $"{species.Name} cp must be continuous at Tmid={species.TMid} (jump {jump:P3})");
        }
    }

    [Fact]
    public void Enthalpy_derivative_equals_cp()
    {
        foreach (var species in Db.All)
        {
            foreach (var t in new[] { 400.0, 900.0, 1600.0, 2500.0 })
            {
                const double dt = 0.1;
                var numerical = (species.Enthalpy(t + dt) - species.Enthalpy(t - dt)) / (2 * dt);
                numerical.Should().BeApproximately(species.Cp(t), species.Cp(t) * 1e-4,
                    $"dh/dT must equal cp for {species.Name} at {t} K");
            }
        }
    }
}
