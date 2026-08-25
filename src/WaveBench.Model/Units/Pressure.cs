namespace WaveBench.Model.Units;

public enum PressureUnit
{
    Pascal,
    Kilopascal,
    Bar,
    Psi,
    InchOfMercury,
}

/// <summary>
/// Pressure with a canonical SI representation in pascals.
/// 1 psi = 6894.757293168361 Pa (from 1 lbf = 4.4482216152605 N over 1 in²);
/// 1 inHg = 3386.389 Pa (conventional, at 0 °C, per ISO 80000-4).
/// </summary>
public readonly record struct Pressure : IComparable<Pressure>
{
    internal static readonly UnitDef<PressureUnit>[] Units =
    [
        new(PressureUnit.Pascal, "Pa", 1.0, 0.0, "pascal", "pascals"),
        new(PressureUnit.Kilopascal, "kPa", 1e3, 0.0, "kilopascal", "kilopascals"),
        new(PressureUnit.Bar, "bar", 1e5, 0.0, "bars"),
        new(PressureUnit.Psi, "psi", 6894.757293168361, 0.0, "lb/in2", "lbf/in2"),
        new(PressureUnit.InchOfMercury, "inHg", 3386.389, 0.0, "in Hg", "\"Hg"),
    ];

    public double Pascals { get; }

    private Pressure(double pascals) => Pascals = pascals;

    public static Pressure FromPascals(double value) => new(value);

    public static Pressure FromKilopascals(double value) => new(value * 1e3);

    public static Pressure FromBar(double value) => new(value * 1e5);

    public static Pressure FromPsi(double value) => new(value * 6894.757293168361);

    public static Pressure FromInchesOfMercury(double value) => new(value * 3386.389);

    public static Pressure From(double value, PressureUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double Kilopascals => Pascals * 1e-3;

    public double Bar => Pascals * 1e-5;

    public double Psi => Pascals / 6894.757293168361;

    public double InchesOfMercury => Pascals / 3386.389;

    public double In(PressureUnit unit) => UnitConvert.FromSi(Pascals, unit, Units);

    public static Pressure Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid pressure.");

    public static bool TryParse(string? text, out Pressure quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new Pressure(si);
        return ok;
    }

    public string ToString(PressureUnit unit, int decimals = 3) => UnitConvert.Format(Pascals, unit, Units, decimals);

    public string ToTabular(PressureUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(Pascals, unit, Units, decimals, width);

    public override string ToString() => ToString(PressureUnit.Kilopascal);

    public int CompareTo(Pressure other) => Pascals.CompareTo(other.Pascals);

    public static Pressure operator +(Pressure a, Pressure b) => new(a.Pascals + b.Pascals);

    public static Pressure operator -(Pressure a, Pressure b) => new(a.Pascals - b.Pascals);

    public static Pressure operator *(Pressure a, double k) => new(a.Pascals * k);

    public static Pressure operator *(double k, Pressure a) => new(k * a.Pascals);

    public static Pressure operator /(Pressure a, double k) => new(a.Pascals / k);

    public static bool operator <(Pressure a, Pressure b) => a.Pascals < b.Pascals;

    public static bool operator >(Pressure a, Pressure b) => a.Pascals > b.Pascals;

    public static bool operator <=(Pressure a, Pressure b) => a.Pascals <= b.Pascals;

    public static bool operator >=(Pressure a, Pressure b) => a.Pascals >= b.Pascals;
}
