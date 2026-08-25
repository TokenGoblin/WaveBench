using System.Globalization;
using System.Text.RegularExpressions;

namespace WaveBench.Model.Units;

/// <summary>
/// Describes one unit of a quantity as an affine map to the canonical SI value:
/// <c>si = value * Factor + Offset</c>. Offset is zero for all units except
/// temperature scales.
/// </summary>
internal sealed class UnitDef<TUnit> where TUnit : struct, Enum
{
    public UnitDef(TUnit unit, string symbol, double factor, double offset = 0.0, params string[] aliases)
    {
        Unit = unit;
        Symbol = symbol;
        Factor = factor;
        Offset = offset;
        Aliases = aliases;
    }

    public TUnit Unit { get; }

    /// <summary>Canonical display symbol, e.g. "mm", "kPa", "°C".</summary>
    public string Symbol { get; }

    public double Factor { get; }

    public double Offset { get; }

    /// <summary>Accepted parse spellings in addition to <see cref="Symbol"/> (case-insensitive).</summary>
    public string[] Aliases { get; }
}

/// <summary>
/// Shared conversion, parsing and formatting machinery for the strongly-typed
/// quantities. All parsing and formatting uses the invariant culture so model
/// files and reports are portable across locales.
/// </summary>
internal static partial class UnitConvert
{
    // Group 1: the number. Group 2: the unit token (everything after it, trimmed).
    [GeneratedRegex(@"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(\S.*?)?\s*$")]
    private static partial Regex QuantityPattern();

    public static UnitDef<TUnit> Find<TUnit>(TUnit unit, UnitDef<TUnit>[] defs) where TUnit : struct, Enum
    {
        foreach (var def in defs)
        {
            if (EqualityComparer<TUnit>.Default.Equals(def.Unit, unit))
            {
                return def;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown unit.");
    }

    public static double ToSi<TUnit>(double value, TUnit unit, UnitDef<TUnit>[] defs) where TUnit : struct, Enum
    {
        var def = Find(unit, defs);
        return value * def.Factor + def.Offset;
    }

    public static double FromSi<TUnit>(double si, TUnit unit, UnitDef<TUnit>[] defs) where TUnit : struct, Enum
    {
        var def = Find(unit, defs);
        return (si - def.Offset) / def.Factor;
    }

    /// <summary>
    /// Parses "value unit" (whitespace optional, e.g. "300 mm", "14.7psi",
    /// "1.2e3 Pa"). The unit token is matched case-insensitively against each
    /// unit's symbol and aliases. A unit token is required.
    /// </summary>
    public static bool TryParse<TUnit>(string? text, UnitDef<TUnit>[] defs, out double si) where TUnit : struct, Enum
    {
        si = 0.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = QuantityPattern().Match(text);
        if (!match.Success || !match.Groups[2].Success)
        {
            return false;
        }

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var token = match.Groups[2].Value;
        foreach (var def in defs)
        {
            if (MatchesToken(def, token))
            {
                si = value * def.Factor + def.Offset;
                return true;
            }
        }

        return false;
    }

    private static bool MatchesToken<TUnit>(UnitDef<TUnit> def, string token) where TUnit : struct, Enum
    {
        if (string.Equals(def.Symbol, token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alias in def.Aliases)
        {
            if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats with a fixed number of decimals ("F" format), which keeps digit
    /// counts stable so columns of values align — tabular-figure-friendly.
    /// </summary>
    public static string Format<TUnit>(double si, TUnit unit, UnitDef<TUnit>[] defs, int decimals) where TUnit : struct, Enum
    {
        var def = Find(unit, defs);
        var value = (si - def.Offset) / def.Factor;
        return string.Create(CultureInfo.InvariantCulture, $"{value.ToString("F" + decimals, CultureInfo.InvariantCulture)} {def.Symbol}");
    }

    /// <summary>
    /// Numeric part only, right-aligned in a fixed-width field for tabular output.
    /// </summary>
    public static string FormatTabular<TUnit>(double si, TUnit unit, UnitDef<TUnit>[] defs, int decimals, int width) where TUnit : struct, Enum
    {
        var def = Find(unit, defs);
        var value = (si - def.Offset) / def.Factor;
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture).PadLeft(width);
    }
}
