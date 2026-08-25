using FluentAssertions;
using WaveBench.Core.Thermo;
using Xunit;

namespace WaveBench.Core.Tests.Thermo;

/// <summary>
/// Phase 1 gate: c_p, γ and a for dry air against published tables.
/// c_p references: ideal-gas dry-air tables (Cengel &amp; Boles, "Thermodynamics",
/// Table A-2; consistent with NIST). Sound speed references: standard dry-air
/// values 331.4 m/s at 0 °C and 343.2 m/s at 20 °C.
/// Tolerances per plan §2.3: 0.2% on c_p, 0.1% on a.
/// </summary>
public class AirPropertiesTests
{
    private static readonly SpeciesDatabase Db = SpeciesDatabase.Default;
    private static readonly MixtureThermo Air = new(GasComposition.DryAir(Db), Db);

    [Fact]
    public void Air_molar_mass_and_gas_constant()
    {
        Air.MolarMass.Should().BeApproximately(28.965, 0.01);
        Air.SpecificGasConstant.Should().BeApproximately(287.0, 0.3);
    }

    [Theory]
    [InlineData(300.0, 1005.0)]
    [InlineData(500.0, 1029.0)]
    [InlineData(1000.0, 1142.0)]
    [InlineData(1500.0, 1210.0)]
    public void Air_cp_matches_published_tables(double t, double expectedJPerKgK) =>
        Air.Cp(t).Should().BeApproximately(expectedJPerKgK, expectedJPerKgK * 0.002);

    [Fact]
    public void Air_gamma_at_room_temperature_is_1_400() =>
        Air.Gamma(300.0).Should().BeApproximately(1.400, 1.400 * 0.002);

    [Theory]
    [InlineData(273.15, 331.4)]
    [InlineData(293.15, 343.2)]
    public void Air_sound_speed_matches_published_values(double t, double expectedMPerS) =>
        Air.SoundSpeed(t).Should().BeApproximately(expectedMPerS, expectedMPerS * 0.001);

    [Fact]
    public void Sound_speed_is_never_a_constant_343()
    {
        // The plan's core requirement: a = √(γRT) locally. A hot cell and a
        // cold cell must differ accordingly.
        var cold = Air.SoundSpeed(310.0);
        var hot = Air.SoundSpeed(950.0);
        cold.Should().BeApproximately(353.0, 1.0);
        hot.Should().BeGreaterThan(590.0);
    }

    [Fact]
    public void Gamma_falls_with_temperature()
    {
        Air.Gamma(300.0).Should().BeGreaterThan(Air.Gamma(1000.0));
        Air.Gamma(1000.0).Should().BeGreaterThan(Air.Gamma(2000.0));
    }
}
