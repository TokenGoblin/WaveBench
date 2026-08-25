using FluentAssertions;
using WaveBench.Model.Units;
using Xunit;

namespace WaveBench.Core.Tests.Units;

public class ParsingTests
{
    [Theory]
    [InlineData("300 mm", 0.3)]
    [InlineData("300mm", 0.3)]
    [InlineData("  0.3 m  ", 0.3)]
    [InlineData("12 in", 0.3048)]
    [InlineData("12 inches", 0.3048)]
    [InlineData("1.2e3 mm", 1.2)]
    [InlineData("-5 mm", -0.005)]
    [InlineData(".5 m", 0.5)]
    public void Length_parses(string text, double expectedMetres) =>
        Length.Parse(text).Metres.Should().BeApproximately(expectedMetres, Math.Abs(expectedMetres) * 1e-12);

    [Theory]
    [InlineData("101.325 kPa", 101_325.0)]
    [InlineData("1 bar", 100_000.0)]
    [InlineData("14.7 psi", 101_352.93)]
    [InlineData("29.92 inHg", 101_320.76)]
    [InlineData("29.92 in Hg", 101_320.76)]
    [InlineData("100000 Pa", 100_000.0)]
    public void Pressure_parses(string text, double expectedPascals) =>
        Pressure.Parse(text).Pascals.Should().BeApproximately(expectedPascals, 0.01);

    [Theory]
    [InlineData("293.15 K", 293.15)]
    [InlineData("20 °C", 293.15)]
    [InlineData("20 C", 293.15)]
    [InlineData("20 degC", 293.15)]
    [InlineData("68 °F", 293.15)]
    [InlineData("68 F", 293.15)]
    [InlineData("-40 C", 233.15)]
    public void Temperature_parses(string text, double expectedKelvin) =>
        Temperature.Parse(text).Kelvin.Should().BeApproximately(expectedKelvin, 1e-9);

    [Theory]
    [InlineData("2.0 L", 0.002)]
    [InlineData("600 cc", 0.0006)]
    [InlineData("350 in³", 0.005735)]
    [InlineData("350 in^3", 0.005735)]
    public void Volume_parses(string text, double expectedCubicMetres) =>
        Volume.Parse(text).CubicMetres.Should().BeApproximately(expectedCubicMetres, 1e-6);

    [Theory]
    [InlineData("0.05 kg/s", 0.05)]
    [InlineData("50 g/s", 0.05)]
    [InlineData("180 kg/h", 0.05)]
    [InlineData("30 lb/min", 0.226796185)]
    public void MassFlow_parses(string text, double expectedKgPerS) =>
        MassFlowRate.Parse(text).KilogramsPerSecond.Should().BeApproximately(expectedKgPerS, 1e-9);

    [Theory]
    [InlineData("314.159 mm²", 3.14159e-4)]
    [InlineData("314.159 mm^2", 3.14159e-4)]
    [InlineData("1 in²", 6.4516e-4)]
    public void Area_parses(string text, double expectedSquareMetres) =>
        Area.Parse(text).SquareMetres.Should().BeApproximately(expectedSquareMetres, 1e-12);

    [Theory]
    [InlineData("110 °", 110.0)]
    [InlineData("110 deg", 110.0)]
    [InlineData("1.9198621771937625 rad", 110.0)]
    public void Angle_parses(string text, double expectedDegrees) =>
        Angle.Parse(text).Degrees.Should().BeApproximately(expectedDegrees, 1e-9);

    [Theory]
    [InlineData("8500 rpm", 8500.0)]
    [InlineData("8500 rev/min", 8500.0)]
    [InlineData("141.667 rev/s", 8500.02)]
    public void RotationalSpeed_parses(string text, double expectedRpm) =>
        RotationalSpeed.Parse(text).Rpm.Should().BeApproximately(expectedRpm, 0.01);

    [Theory]
    [InlineData("300")]        // no unit — units are mandatory
    [InlineData("mm")]         // no number
    [InlineData("300 furlongs")]
    [InlineData("12,5 mm")]    // locale decimal comma is rejected: invariant culture only
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_input_fails_TryParse_and_Parse_throws(string text)
    {
        Length.TryParse(text, out _).Should().BeFalse();
        var act = () => Length.Parse(text);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Null_fails_TryParse() => Length.TryParse(null, out _).Should().BeFalse();

    [Fact]
    public void Unit_matching_is_case_insensitive()
    {
        Pressure.Parse("1 BAR").Pascals.Should().Be(100_000.0);
        Length.Parse("25.4 MM").Metres.Should().BeApproximately(0.0254, 1e-15);
    }
}
