namespace WaveBench.Model.Units;

public enum VolumeUnit
{
    CubicMetre,
    Litre,
    CubicCentimetre,
    CubicInch,
}

/// <summary>
/// Volume with a canonical SI representation in cubic metres.
/// 1 in³ = 0.0254³ m³ = 1.6387064e-5 m³ exactly.
/// </summary>
public readonly record struct Volume : IComparable<Volume>
{
    internal static readonly UnitDef<VolumeUnit>[] Units =
    [
        new(VolumeUnit.CubicMetre, "m³", 1.0, 0.0, "m^3", "m3"),
        new(VolumeUnit.Litre, "L", 1e-3, 0.0, "l", "litre", "liter", "litres", "liters"),
        new(VolumeUnit.CubicCentimetre, "cc", 1e-6, 0.0, "cm³", "cm^3", "cm3"),
        new(VolumeUnit.CubicInch, "in³", 1.6387064e-5, 0.0, "in^3", "in3", "cu in"),
    ];

    public double CubicMetres { get; }

    private Volume(double cubicMetres) => CubicMetres = cubicMetres;

    public static Volume FromCubicMetres(double value) => new(value);

    public static Volume FromLitres(double value) => new(value * 1e-3);

    public static Volume FromCubicCentimetres(double value) => new(value * 1e-6);

    public static Volume FromCubicInches(double value) => new(value * 1.6387064e-5);

    public static Volume From(double value, VolumeUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double Litres => CubicMetres * 1e3;

    public double CubicCentimetres => CubicMetres * 1e6;

    public double CubicInches => CubicMetres / 1.6387064e-5;

    public double In(VolumeUnit unit) => UnitConvert.FromSi(CubicMetres, unit, Units);

    public static Volume Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid volume.");

    public static bool TryParse(string? text, out Volume quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new Volume(si);
        return ok;
    }

    public string ToString(VolumeUnit unit, int decimals = 3) => UnitConvert.Format(CubicMetres, unit, Units, decimals);

    public string ToTabular(VolumeUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(CubicMetres, unit, Units, decimals, width);

    public override string ToString() => ToString(VolumeUnit.Litre);

    public int CompareTo(Volume other) => CubicMetres.CompareTo(other.CubicMetres);

    public static Volume operator +(Volume a, Volume b) => new(a.CubicMetres + b.CubicMetres);

    public static Volume operator -(Volume a, Volume b) => new(a.CubicMetres - b.CubicMetres);

    public static Volume operator *(Volume a, double k) => new(a.CubicMetres * k);

    public static Volume operator *(double k, Volume a) => new(k * a.CubicMetres);

    public static Volume operator /(Volume a, double k) => new(a.CubicMetres / k);

    public static bool operator <(Volume a, Volume b) => a.CubicMetres < b.CubicMetres;

    public static bool operator >(Volume a, Volume b) => a.CubicMetres > b.CubicMetres;

    public static bool operator <=(Volume a, Volume b) => a.CubicMetres <= b.CubicMetres;

    public static bool operator >=(Volume a, Volume b) => a.CubicMetres >= b.CubicMetres;
}
