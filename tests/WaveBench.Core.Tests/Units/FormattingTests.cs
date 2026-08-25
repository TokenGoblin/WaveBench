using FluentAssertions;
using WaveBench.Model.Units;
using Xunit;

namespace WaveBench.Core.Tests.Units;

public class FormattingTests
{
    [Fact]
    public void Formats_with_requested_unit_and_decimals()
    {
        Length.FromMetres(0.3).ToString(LengthUnit.Millimetre, 1).Should().Be("300.0 mm");
        Length.FromMetres(0.3).ToString(LengthUnit.Inch, 3).Should().Be("11.811 in");
        Pressure.FromPascals(101_325.0).ToString(PressureUnit.Kilopascal, 3).Should().Be("101.325 kPa");
        Temperature.FromCelsius(20.0).ToString(TemperatureUnit.Celsius, 1).Should().Be("20.0 °C");
        RotationalSpeed.FromRpm(8500.0).ToString(RotationalSpeedUnit.RevolutionPerMinute, 0).Should().Be("8500 rpm");
    }

    [Fact]
    public void Formatting_is_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A decimal-comma locale must not change the output.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Length.FromMetres(0.3).ToString(LengthUnit.Millimetre, 1).Should().Be("300.0 mm");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Tabular_output_right_aligns_to_fixed_width()
    {
        // Fixed decimals + fixed width → decimal points align down a column.
        Length.FromMillimetres(7.5).ToTabular(LengthUnit.Millimetre, 2, 10).Should().Be("      7.50");
        Length.FromMillimetres(300.0).ToTabular(LengthUnit.Millimetre, 2, 10).Should().Be("    300.00");
        Length.FromMillimetres(-12.25).ToTabular(LengthUnit.Millimetre, 2, 10).Should().Be("    -12.25");

        var column = new[]
        {
            Length.FromMillimetres(7.5).ToTabular(LengthUnit.Millimetre, 2, 10),
            Length.FromMillimetres(300.0).ToTabular(LengthUnit.Millimetre, 2, 10),
        };
        column.Should().OnlyContain(s => s.Length == 10);
    }

    [Fact]
    public void Round_trip_through_format_and_parse()
    {
        var original = Pressure.FromPsi(14.7);
        var reparsed = Pressure.Parse(original.ToString(PressureUnit.Psi, 6));
        reparsed.Pascals.Should().BeApproximately(original.Pascals, 0.01);
    }
}
