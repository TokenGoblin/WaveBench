namespace WaveBench.Model.Units;

public enum TemperatureUnit
{
    Kelvin,
    Celsius,
    Fahrenheit,
}

/// <summary>
/// Absolute temperature with a canonical SI representation in kelvin.
/// K = °C + 273.15; K = (°F − 32) · 5/9 + 273.15. Arithmetic operators are
/// deliberately not provided — adding two absolute temperatures is meaningless;
/// a temperature-difference type can be added when the physics needs it.
/// </summary>
public readonly record struct Temperature : IComparable<Temperature>
{
    internal static readonly UnitDef<TemperatureUnit>[] Units =
    [
        new(TemperatureUnit.Kelvin, "K", 1.0, 0.0, "kelvin"),
        new(TemperatureUnit.Celsius, "°C", 1.0, 273.15, "C", "degC", "celsius"),
        new(TemperatureUnit.Fahrenheit, "°F", 5.0 / 9.0, 273.15 - 32.0 * 5.0 / 9.0, "F", "degF", "fahrenheit"),
    ];

    public double Kelvin { get; }

    private Temperature(double kelvin) => Kelvin = kelvin;

    public static Temperature FromKelvin(double value) => new(value);

    public static Temperature FromCelsius(double value) => new(value + 273.15);

    public static Temperature FromFahrenheit(double value) => new((value - 32.0) * 5.0 / 9.0 + 273.15);

    public static Temperature From(double value, TemperatureUnit unit) => new(UnitConvert.ToSi(value, unit, Units));

    public double Celsius => Kelvin - 273.15;

    public double Fahrenheit => (Kelvin - 273.15) * 9.0 / 5.0 + 32.0;

    public double In(TemperatureUnit unit) => UnitConvert.FromSi(Kelvin, unit, Units);

    public static Temperature Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid temperature.");

    public static bool TryParse(string? text, out Temperature quantity)
    {
        var ok = UnitConvert.TryParse(text, Units, out var si);
        quantity = new Temperature(si);
        return ok;
    }

    public string ToString(TemperatureUnit unit, int decimals = 2) => UnitConvert.Format(Kelvin, unit, Units, decimals);

    public string ToTabular(TemperatureUnit unit, int decimals, int width) =>
        UnitConvert.FormatTabular(Kelvin, unit, Units, decimals, width);

    public override string ToString() => ToString(TemperatureUnit.Kelvin);

    public int CompareTo(Temperature other) => Kelvin.CompareTo(other.Kelvin);

    public static bool operator <(Temperature a, Temperature b) => a.Kelvin < b.Kelvin;

    public static bool operator >(Temperature a, Temperature b) => a.Kelvin > b.Kelvin;

    public static bool operator <=(Temperature a, Temperature b) => a.Kelvin <= b.Kelvin;

    public static bool operator >=(Temperature a, Temperature b) => a.Kelvin >= b.Kelvin;
}
