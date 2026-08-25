namespace WaveBench.Model.Units;

public enum LengthUnit
{
    Metre,
    Millimetre,
    Inch,
}

/// <summary>
/// Length with a canonical SI representation in metres. 1 in = 25.4 mm exactly
/// (international inch, 1959).
/// </summary>
public readonly record struct Length : IComparable<Length>
{
    internal static readonly UnitDef<LengthUnit>[] Units =
    [
        new(LengthUnit.Metre, "m", 1.0, 0.0, "metre", "meter", "metres", "meters"),
        new(LengthUnit.Millimetre, "mm", 1e-3, 0.0, "millimetre", "millimeter", "millimetres", "millimeters"),
        new(LengthUnit.Inch, "in", 0.0254, 0.0, "inch", "inches", "\""),
    ];

    public double Metres { get; }

    private Length(double metres) => Metres = metres;

    public static Length FromMetres(double value) => new(value);

    public static Length FromMillimetres(double value) => new(value * 1e-3);

    public static Length FromInches(double value) => new(value * 0.0254);

    public static Length From(double value, LengthUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double Millimetres => Metres * 1e3;

    public double Inches => Metres / 0.0254;

    public double In(LengthUnit unit) => UnitConvert.FromSi(Metres, unit, Units);

    public static Length Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid length.");

    public static bool TryParse(string? text, out Length quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new Length(si);
        return ok;
    }

    public string ToString(LengthUnit unit, int decimals = 3) => UnitConvert.Format(Metres, unit, Units, decimals);

    public string ToTabular(LengthUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(Metres, unit, Units, decimals, width);

    public override string ToString() => ToString(LengthUnit.Metre);

    public int CompareTo(Length other) => Metres.CompareTo(other.Metres);

    public static Length operator +(Length a, Length b) => new(a.Metres + b.Metres);

    public static Length operator -(Length a, Length b) => new(a.Metres - b.Metres);

    public static Length operator *(Length a, double k) => new(a.Metres * k);

    public static Length operator *(double k, Length a) => new(k * a.Metres);

    public static Length operator /(Length a, double k) => new(a.Metres / k);

    public static bool operator <(Length a, Length b) => a.Metres < b.Metres;

    public static bool operator >(Length a, Length b) => a.Metres > b.Metres;

    public static bool operator <=(Length a, Length b) => a.Metres <= b.Metres;

    public static bool operator >=(Length a, Length b) => a.Metres >= b.Metres;
}
