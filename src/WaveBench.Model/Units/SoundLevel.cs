using System.Globalization;
using System.Text.RegularExpressions;

namespace WaveBench.Model.Units;

public enum SoundWeighting
{
    Unweighted,
    A,
    C,
}

/// <summary>
/// Sound pressure level in decibels re 20 µPa, tagged with its frequency
/// weighting (IEC 61672). Levels with different weightings are not comparable
/// and cannot be converted into each other without the underlying spectrum, so
/// comparison across weightings throws rather than silently misleading.
/// </summary>
public readonly partial record struct SoundLevel : IComparable<SoundLevel>
{
    public double Decibels { get; }

    public SoundWeighting Weighting { get; }

    private SoundLevel(double decibels, SoundWeighting weighting)
    {
        Decibels = decibels;
        Weighting = weighting;
    }

    public static SoundLevel FromDecibels(double value, SoundWeighting weighting = SoundWeighting.Unweighted) =>
        new(value, weighting);

    // "110 dB(C)", "110 dBC", "110dBc", "95 dB(A)", "88 dB"
    [GeneratedRegex(@"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*dB\s*(?:\(\s*([AC])\s*\)|([AC]))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SoundLevelPattern();

    public static SoundLevel Parse(string text) =>
        TryParse(text, out var quantity) ? quantity : throw new FormatException($"'{text}' is not a valid sound level.");

    public static bool TryParse(string? text, out SoundLevel quantity)
    {
        quantity = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = SoundLevelPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var value = double.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        var letter = match.Groups[2].Success ? match.Groups[2].Value :
                     match.Groups[3].Success ? match.Groups[3].Value : "";
        var weighting = letter.ToUpperInvariant() switch
        {
            "A" => SoundWeighting.A,
            "C" => SoundWeighting.C,
            _ => SoundWeighting.Unweighted,
        };

        quantity = new SoundLevel(value, weighting);
        return true;
    }

    public string ToString(int decimals) =>
        Decibels.ToString("F" + decimals, CultureInfo.InvariantCulture) + " " + UnitLabel;

    public override string ToString() => ToString(1);

    public string UnitLabel => Weighting switch
    {
        SoundWeighting.A => "dB(A)",
        SoundWeighting.C => "dB(C)",
        _ => "dB",
    };

    public int CompareTo(SoundLevel other)
    {
        if (Weighting != other.Weighting)
        {
            throw new InvalidOperationException(
                $"Cannot compare a {UnitLabel} level with a {other.UnitLabel} level; weightings differ.");
        }

        return Decibels.CompareTo(other.Decibels);
    }
}
