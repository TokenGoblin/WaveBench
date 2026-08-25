using FluentAssertions;
using WaveBench.Model.Units;
using Xunit;

namespace WaveBench.Core.Tests.Units;

public class ConversionTests
{
    [Theory]
    [InlineData(1.0, 1000.0)]
    [InlineData(0.0254, 25.4)]
    [InlineData(0.7, 700.0)]
    public void Length_metres_to_millimetres(double metres, double expectedMm) =>
        Length.FromMetres(metres).Millimetres.Should().BeApproximately(expectedMm, 1e-9);

    [Fact]
    public void Length_one_inch_is_exactly_25_4_mm() =>
        Length.FromInches(1.0).Millimetres.Should().Be(25.4);

    [Fact]
    public void Pressure_one_bar_is_100_kpa() =>
        Pressure.FromBar(1.0).Kilopascals.Should().BeApproximately(100.0, 1e-12);

    [Fact]
    public void Pressure_one_atmosphere_in_psi_and_inhg()
    {
        var atm = Pressure.FromPascals(101_325.0);
        atm.Psi.Should().BeApproximately(14.6959, 1e-4);
        atm.InchesOfMercury.Should().BeApproximately(29.9213, 1e-4);
    }

    [Fact]
    public void Temperature_zero_celsius_is_273_15_kelvin() =>
        Temperature.FromCelsius(0.0).Kelvin.Should().Be(273.15);

    [Fact]
    public void Temperature_boiling_point_fahrenheit() =>
        Temperature.FromCelsius(100.0).Fahrenheit.Should().BeApproximately(212.0, 1e-10);

    [Fact]
    public void Temperature_minus_40_is_the_same_in_c_and_f() =>
        Temperature.FromCelsius(-40.0).Fahrenheit.Should().BeApproximately(-40.0, 1e-10);

    [Fact]
    public void Volume_one_litre_is_1000_cc() =>
        Volume.FromLitres(1.0).CubicCentimetres.Should().BeApproximately(1000.0, 1e-9);

    [Fact]
    public void Volume_one_cubic_inch_is_16_387064_cc() =>
        Volume.FromCubicInches(1.0).CubicCentimetres.Should().BeApproximately(16.387064, 1e-9);

    [Fact]
    public void MassFlow_one_pound_per_minute_in_kg_per_s() =>
        MassFlowRate.FromPoundsPerMinute(1.0).KilogramsPerSecond.Should().BeApproximately(0.00755987, 1e-8);

    [Fact]
    public void Area_one_square_inch_is_645_16_square_mm() =>
        Area.FromSquareInches(1.0).SquareMillimetres.Should().BeApproximately(645.16, 1e-9);

    [Fact]
    public void Angle_180_degrees_is_pi_radians() =>
        Angle.FromDegrees(180.0).Radians.Should().BeApproximately(Math.PI, 1e-15);

    [Fact]
    public void RotationalSpeed_3000_rpm_is_50_hz() =>
        RotationalSpeed.FromRpm(3000.0).Hertz.Should().BeApproximately(50.0, 1e-10);

    [Fact]
    public void RotationalSpeed_1000_rpm_in_rad_per_s() =>
        RotationalSpeed.FromRpm(1000.0).RadiansPerSecond.Should().BeApproximately(104.71975511965977, 1e-10);

    [Fact]
    public void Arithmetic_operators_work_in_si_space()
    {
        (Length.FromMillimetres(300.0) + Length.FromInches(1.0)).Millimetres.Should().BeApproximately(325.4, 1e-9);
        (Pressure.FromBar(2.0) - Pressure.FromBar(0.5)).Bar.Should().BeApproximately(1.5, 1e-12);
        (2.0 * Angle.FromDegrees(90.0)).Degrees.Should().BeApproximately(180.0, 1e-10);
        (Length.FromMetres(1.0) < Length.FromInches(40.0)).Should().BeTrue();
    }
}
