namespace WaveBench.Model.Units;

public enum RotationalSpeedUnit
{
    RadianPerSecond,
    RevolutionPerMinute,
    Hertz,
}

/// <summary>
/// Rotational speed with a canonical SI representation in rad/s.
/// 1 rpm = 2π/60 rad/s; 1 Hz here means one revolution per second (2π rad/s),
/// the convention used for engine cycle and firing frequencies.
/// </summary>
public readonly record struct RotationalSpeed : IComparable<RotationalSpeed>
{
    private const double RpmFactor = 2.0 * Math.PI / 60.0;
    private const double HertzFactor = 2.0 * Math.PI;

    internal static readonly UnitDef<RotationalSpeedUnit>[] Units =
    [
        new(RotationalSpeedUnit.RadianPerSecond, "rad/s", 1.0),
        new(RotationalSpeedUnit.RevolutionPerMinute, "rpm", RpmFactor, 0.0, "rev/min", "r/min", "1/min"),
        new(RotationalSpeedUnit.Hertz, "Hz", HertzFactor, 0.0, "rev/s", "rps"),
    ];

    public double RadiansPerSecond { get; }

    private RotationalSpeed(double radiansPerSecond) => RadiansPerSecond = radiansPerSecond;

    public static RotationalSpeed FromRadiansPerSecond(double value) => new(value);

    public static RotationalSpeed FromRpm(double value) => new(value * RpmFactor);

    public static RotationalSpeed FromHertz(double value) => new(value * HertzFactor);

    public static RotationalSpeed From(double value, RotationalSpeedUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double Rpm => RadiansPerSecond / RpmFactor;

    public double Hertz => RadiansPerSecond / HertzFactor;

    public double In(RotationalSpeedUnit unit) => UnitConvert.FromSi(RadiansPerSecond, unit, Units);

    public static RotationalSpeed Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid rotational speed.");

    public static bool TryParse(string? text, out RotationalSpeed quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new RotationalSpeed(si);
        return ok;
    }

    public string ToString(RotationalSpeedUnit unit, int decimals = 0) =>
        UnitConvert.Format(RadiansPerSecond, unit, Units, decimals);

    public string ToTabular(RotationalSpeedUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(RadiansPerSecond, unit, Units, decimals, width);

    public override string ToString() => ToString(RotationalSpeedUnit.RevolutionPerMinute);

    public int CompareTo(RotationalSpeed other) => RadiansPerSecond.CompareTo(other.RadiansPerSecond);

    public static RotationalSpeed operator +(RotationalSpeed a, RotationalSpeed b) => new(a.RadiansPerSecond + b.RadiansPerSecond);

    public static RotationalSpeed operator -(RotationalSpeed a, RotationalSpeed b) => new(a.RadiansPerSecond - b.RadiansPerSecond);

    public static RotationalSpeed operator *(RotationalSpeed a, double k) => new(a.RadiansPerSecond * k);

    public static RotationalSpeed operator *(double k, RotationalSpeed a) => new(k * a.RadiansPerSecond);

    public static RotationalSpeed operator /(RotationalSpeed a, double k) => new(a.RadiansPerSecond / k);

    public static bool operator <(RotationalSpeed a, RotationalSpeed b) => a.RadiansPerSecond < b.RadiansPerSecond;

    public static bool operator >(RotationalSpeed a, RotationalSpeed b) => a.RadiansPerSecond > b.RadiansPerSecond;

    public static bool operator <=(RotationalSpeed a, RotationalSpeed b) => a.RadiansPerSecond <= b.RadiansPerSecond;

    public static bool operator >=(RotationalSpeed a, RotationalSpeed b) => a.RadiansPerSecond >= b.RadiansPerSecond;
}
