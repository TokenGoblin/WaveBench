namespace WaveBench.Model.Units;

public enum AreaUnit
{
    SquareMetre,
    SquareCentimetre,
    SquareMillimetre,
    SquareInch,
}

/// <summary>
/// Area with a canonical SI representation in square metres.
/// 1 in² = 0.00064516 m² exactly.
/// </summary>
public readonly record struct Area : IComparable<Area>
{
    internal static readonly UnitDef<AreaUnit>[] Units =
    [
        new(AreaUnit.SquareMetre, "m²", 1.0, 0.0, "m^2", "m2"),
        new(AreaUnit.SquareCentimetre, "cm²", 1e-4, 0.0, "cm^2", "cm2"),
        new(AreaUnit.SquareMillimetre, "mm²", 1e-6, 0.0, "mm^2", "mm2"),
        new(AreaUnit.SquareInch, "in²", 0.00064516, 0.0, "in^2", "in2", "sq in"),
    ];

    public double SquareMetres { get; }

    private Area(double squareMetres) => SquareMetres = squareMetres;

    public static Area FromSquareMetres(double value) => new(value);

    public static Area FromSquareCentimetres(double value) => new(value * 1e-4);

    public static Area FromSquareMillimetres(double value) => new(value * 1e-6);

    public static Area FromSquareInches(double value) => new(value * 0.00064516);

    public static Area From(double value, AreaUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double SquareCentimetres => SquareMetres * 1e4;

    public double SquareMillimetres => SquareMetres * 1e6;

    public double SquareInches => SquareMetres / 0.00064516;

    public double In(AreaUnit unit) => UnitConvert.FromSi(SquareMetres, unit, Units);

    public static Area Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid area.");

    public static bool TryParse(string? text, out Area quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new Area(si);
        return ok;
    }

    public string ToString(AreaUnit unit, int decimals = 3) => UnitConvert.Format(SquareMetres, unit, Units, decimals);

    public string ToTabular(AreaUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(SquareMetres, unit, Units, decimals, width);

    public override string ToString() => ToString(AreaUnit.SquareMetre);

    public int CompareTo(Area other) => SquareMetres.CompareTo(other.SquareMetres);

    public static Area operator +(Area a, Area b) => new(a.SquareMetres + b.SquareMetres);

    public static Area operator -(Area a, Area b) => new(a.SquareMetres - b.SquareMetres);

    public static Area operator *(Area a, double k) => new(a.SquareMetres * k);

    public static Area operator *(double k, Area a) => new(k * a.SquareMetres);

    public static Area operator /(Area a, double k) => new(a.SquareMetres / k);

    public static bool operator <(Area a, Area b) => a.SquareMetres < b.SquareMetres;

    public static bool operator >(Area a, Area b) => a.SquareMetres > b.SquareMetres;

    public static bool operator <=(Area a, Area b) => a.SquareMetres <= b.SquareMetres;

    public static bool operator >=(Area a, Area b) => a.SquareMetres >= b.SquareMetres;
}
