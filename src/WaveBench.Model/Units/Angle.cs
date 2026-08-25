namespace WaveBench.Model.Units;

public enum AngleUnit
{
    Radian,
    Degree,
}

/// <summary>
/// Angle with a canonical SI representation in radians. Crank angles are plain
/// degrees; the 0–720° cycle convention lives with the engine model, not here.
/// </summary>
public readonly record struct Angle : IComparable<Angle>
{
    private const double DegreeFactor = Math.PI / 180.0;

    internal static readonly UnitDef<AngleUnit>[] Units =
    [
        new(AngleUnit.Radian, "rad", 1.0, 0.0, "radian", "radians"),
        new(AngleUnit.Degree, "°", DegreeFactor, 0.0, "deg", "degree", "degrees"),
    ];

    public double Radians { get; }

    private Angle(double radians) => Radians = radians;

    public static Angle FromRadians(double value) => new(value);

    public static Angle FromDegrees(double value) => new(value * DegreeFactor);

    public static Angle From(double value, AngleUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double Degrees => Radians / DegreeFactor;

    public double In(AngleUnit unit) => UnitConvert.FromSi(Radians, unit, Units);

    public static Angle Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid angle.");

    public static bool TryParse(string? text, out Angle quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new Angle(si);
        return ok;
    }

    public string ToString(AngleUnit unit, int decimals = 2) => UnitConvert.Format(Radians, unit, Units, decimals);

    public string ToTabular(AngleUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(Radians, unit, Units, decimals, width);

    public override string ToString() => ToString(AngleUnit.Degree);

    public int CompareTo(Angle other) => Radians.CompareTo(other.Radians);

    public static Angle operator +(Angle a, Angle b) => new(a.Radians + b.Radians);

    public static Angle operator -(Angle a, Angle b) => new(a.Radians - b.Radians);

    public static Angle operator *(Angle a, double k) => new(a.Radians * k);

    public static Angle operator *(double k, Angle a) => new(k * a.Radians);

    public static Angle operator /(Angle a, double k) => new(a.Radians / k);

    public static bool operator <(Angle a, Angle b) => a.Radians < b.Radians;

    public static bool operator >(Angle a, Angle b) => a.Radians > b.Radians;

    public static bool operator <=(Angle a, Angle b) => a.Radians <= b.Radians;

    public static bool operator >=(Angle a, Angle b) => a.Radians >= b.Radians;
}
