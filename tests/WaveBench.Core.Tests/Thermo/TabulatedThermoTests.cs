using FluentAssertions;
using WaveBench.Core.Thermo;
using Xunit;

namespace WaveBench.Core.Tests.Thermo;

public class TabulatedThermoTests
{
    private static readonly SpeciesDatabase Db = SpeciesDatabase.Default;

    [Theory]
    [InlineData("N2")]
    [InlineData("CO2")]
    [InlineData("H2O")]
    [InlineData("IC8H18")]
    public void Tabulated_matches_direct_evaluation(string name)
    {
        var direct = Db[name];
        var tabulated = TabulatedSpecies.For(direct);

        for (var t = 250.0; t <= 3400.0; t += 37.0)
        {
            tabulated.Cp(t).Should().BeApproximately(direct.Cp(t), Math.Abs(direct.Cp(t)) * 2e-4);
            tabulated.Enthalpy(t).Should().BeApproximately(direct.Enthalpy(t), Math.Abs(direct.Enthalpy(t)) * 2e-4 + 50.0);
            tabulated.StandardEntropy(t).Should().BeApproximately(
                direct.StandardEntropy(t), Math.Abs(direct.StandardEntropy(t)) * 2e-4);
        }
    }

    [Fact]
    public void Tabulated_mixture_sound_speed_matches_direct()
    {
        var air = GasComposition.DryAir(Db);
        var direct = new MixtureThermo(air, Db);
        var tabulated = new MixtureThermo(air, Db, PropertyEvaluation.Tabulated);

        for (var t = 250.0; t <= 3000.0; t += 250.0)
        {
            tabulated.SoundSpeed(t).Should().BeApproximately(direct.SoundSpeed(t), direct.SoundSpeed(t) * 5e-4);
        }
    }

    [Fact]
    public void Outside_the_grid_falls_back_to_direct()
    {
        var direct = Db["N2"];
        var tabulated = TabulatedSpecies.For(direct);
        tabulated.Cp(4000.0).Should().Be(direct.Cp(4000.0));
    }
}
