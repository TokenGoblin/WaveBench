namespace WaveBench.Model.Units;

public enum MassFlowRateUnit
{
    KilogramPerSecond,
    GramPerSecond,
    KilogramPerHour,
    PoundPerMinute,
}

/// <summary>
/// Mass flow rate with a canonical SI representation in kg/s.
/// 1 lb = 0.45359237 kg exactly, so 1 lb/min = 0.45359237/60 kg/s
/// (lb/min is the customary axis of compressor maps).
/// </summary>
public readonly record struct MassFlowRate : IComparable<MassFlowRate>
{
    private const double PoundPerMinuteFactor = 0.45359237 / 60.0;

    internal static readonly UnitDef<MassFlowRateUnit>[] Units =
    [
        new(MassFlowRateUnit.KilogramPerSecond, "kg/s", 1.0),
        new(MassFlowRateUnit.GramPerSecond, "g/s", 1e-3),
        new(MassFlowRateUnit.KilogramPerHour, "kg/h", 1.0 / 3600.0, 0.0, "kg/hr"),
        new(MassFlowRateUnit.PoundPerMinute, "lb/min", PoundPerMinuteFactor, 0.0, "lbs/min", "lbm/min"),
    ];

    public double KilogramsPerSecond { get; }

    private MassFlowRate(double kilogramsPerSecond) => KilogramsPerSecond = kilogramsPerSecond;

    public static MassFlowRate FromKilogramsPerSecond(double value) => new(value);

    public static MassFlowRate FromGramsPerSecond(double value) => new(value * 1e-3);

    public static MassFlowRate FromKilogramsPerHour(double value) => new(value / 3600.0);

    public static MassFlowRate FromPoundsPerMinute(double value) => new(value * PoundPerMinuteFactor);

    public static MassFlowRate From(double value, MassFlowRateUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double GramsPerSecond => KilogramsPerSecond * 1e3;

    public double KilogramsPerHour => KilogramsPerSecond * 3600.0;

    public double PoundsPerMinute => KilogramsPerSecond / PoundPerMinuteFactor;

    public double In(MassFlowRateUnit unit) => UnitConvert.FromSi(KilogramsPerSecond, unit, Units);

    public static MassFlowRate Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid mass flow rate.");

    public static bool TryParse(string? text, out MassFlowRate quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new MassFlowRate(si);
        return ok;
    }

    public string ToString(MassFlowRateUnit unit, int decimals = 4) =>
        UnitConvert.Format(KilogramsPerSecond, unit, Units, decimals);

    public string ToTabular(MassFlowRateUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(KilogramsPerSecond, unit, Units, decimals, width);

    public override string ToString() => ToString(MassFlowRateUnit.KilogramPerSecond);

    public int CompareTo(MassFlowRate other) => KilogramsPerSecond.CompareTo(other.KilogramsPerSecond);

    public static MassFlowRate operator +(MassFlowRate a, MassFlowRate b) => new(a.KilogramsPerSecond + b.KilogramsPerSecond);

    public static MassFlowRate operator -(MassFlowRate a, MassFlowRate b) => new(a.KilogramsPerSecond - b.KilogramsPerSecond);

    public static MassFlowRate operator *(MassFlowRate a, double k) => new(a.KilogramsPerSecond * k);

    public static MassFlowRate operator *(double k, MassFlowRate a) => new(k * a.KilogramsPerSecond);

    public static MassFlowRate operator /(MassFlowRate a, double k) => new(a.KilogramsPerSecond / k);

    public static bool operator <(MassFlowRate a, MassFlowRate b) => a.KilogramsPerSecond < b.KilogramsPerSecond;

    public static bool operator >(MassFlowRate a, MassFlowRate b) => a.KilogramsPerSecond > b.KilogramsPerSecond;

    public static bool operator <=(MassFlowRate a, MassFlowRate b) => a.KilogramsPerSecond <= b.KilogramsPerSecond;

    public static bool operator >=(MassFlowRate a, MassFlowRate b) => a.KilogramsPerSecond >= b.KilogramsPerSecond;
}
