using FluentAssertions;
using WaveBench.Model.Units;
using Xunit;

namespace WaveBench.Core.Tests.Units;

/// <summary>
/// Phase 0 gate: every unit round-trips value → SI → value to full double
/// precision across many orders of magnitude (relative 1e-12 for purely
/// multiplicative units). Temperature is affine, so tiny values sit on a large
/// offset and are checked with an absolute tolerance instead.
/// </summary>
public class RoundTripTests
{
    public static readonly double[] Magnitudes = [1e-12, 1e-6, 1e-3, 1.0, 123.456789, 1e3, 1e6, 1e12];

    private static void AssertRoundTrip(double original, double roundTripped)
    {
        var tolerance = Math.Abs(original) * 1e-12;
        roundTripped.Should().BeApproximately(original, tolerance);
    }

    [Theory]
    [MemberData(nameof(AllMagnitudes))]
    public void Length_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<LengthUnit>())
        {
            AssertRoundTrip(value, Length.From(value, unit).In(unit));
        }
    }

    [Theory]
    [MemberData(nameof(AllMagnitudes))]
    public void Pressure_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<PressureUnit>())
        {
            AssertRoundTrip(value, Pressure.From(value, unit).In(unit));
        }
    }

    [Theory]
    [MemberData(nameof(AllMagnitudes))]
    public void Volume_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<VolumeUnit>())
        {
            AssertRoundTrip(value, Volume.From(value, unit).In(unit));
        }
    }

    [Theory]
    [MemberData(nameof(AllMagnitudes))]
    public void MassFlow_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<MassFlowRateUnit>())
        {
            AssertRoundTrip(value, MassFlowRate.From(value, unit).In(unit));
        }
    }

    [Theory]
    [MemberData(nameof(AllMagnitudes))]
    public void Area_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<AreaUnit>())
        {
            AssertRoundTrip(value, Area.From(value, unit).In(unit));
        }
    }

    [Theory]
    [MemberData(nameof(AllMagnitudes))]
    public void Angle_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<AngleUnit>())
        {
            AssertRoundTrip(value, Angle.From(value, unit).In(unit));
        }
    }

    [Theory]
    [MemberData(nameof(AllMagnitudes))]
    public void RotationalSpeed_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<RotationalSpeedUnit>())
        {
            AssertRoundTrip(value, RotationalSpeed.From(value, unit).In(unit));
        }
    }

    [Theory]
    [InlineData(-459.66)]
    [InlineData(-40.0)]
    [InlineData(0.0)]
    [InlineData(20.0)]
    [InlineData(1234.5678)]
    [InlineData(3500.0)]
    public void Temperature_round_trips_every_unit(double value)
    {
        foreach (var unit in Enum.GetValues<TemperatureUnit>())
        {
            Temperature.From(value, unit).In(unit).Should().BeApproximately(value, 1e-9);
        }
    }

    [Fact]
    public void Chained_conversions_do_not_drift()
    {
        // mm → in → m → mm, a realistic UI unit-toggle path.
        var original = Length.FromMillimetres(457.2);
        var chained = Length.FromMetres(Length.FromInches(original.Inches).Metres);
        chained.Millimetres.Should().BeApproximately(457.2, 457.2 * 1e-12);
    }

    [Fact]
    public void Boundary_zero_round_trips_exactly()
    {
        Length.From(0.0, LengthUnit.Inch).In(LengthUnit.Inch).Should().Be(0.0);
        Pressure.From(0.0, PressureUnit.Psi).In(PressureUnit.Psi).Should().Be(0.0);
        MassFlowRate.From(0.0, MassFlowRateUnit.PoundPerMinute).In(MassFlowRateUnit.PoundPerMinute).Should().Be(0.0);
    }

    [Fact]
    public void Boundary_negative_values_round_trip()
    {
        // Gauge-negative pressures (vacuum) and negative angles are legitimate.
        AssertRoundTrip(-14.7, Pressure.From(-14.7, PressureUnit.Psi).In(PressureUnit.Psi));
        AssertRoundTrip(-720.0, Angle.From(-720.0, AngleUnit.Degree).In(AngleUnit.Degree));
    }

    public static TheoryData<double> AllMagnitudes()
    {
        var data = new TheoryData<double>();
        foreach (var m in Magnitudes)
        {
            data.Add(m);
            data.Add(-m);
        }

        return data;
    }
}
