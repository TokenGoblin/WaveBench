namespace WaveBench.Core.Thermo;

/// <summary>
/// Immutable gas composition as normalised mass fractions.
/// </summary>
public sealed class GasComposition
{
    private readonly Dictionary<string, double> _massFractions;

    private GasComposition(Dictionary<string, double> massFractions)
    {
        _massFractions = massFractions;
    }

    public IReadOnlyDictionary<string, double> MassFractions => _massFractions;

    public double MassFractionOf(string species) => _massFractions.GetValueOrDefault(species);

    public static GasComposition FromMassFractions(IEnumerable<KeyValuePair<string, double>> fractions)
    {
        var dict = Accumulate(fractions);
        var sum = dict.Values.Sum();
        if (sum <= 0)
        {
            throw new ArgumentException("Mass fractions must sum to a positive value.");
        }

        foreach (var key in dict.Keys.ToList())
        {
            dict[key] /= sum;
        }

        return new GasComposition(dict);
    }

    /// <summary>Builds from mole fractions using the database's molar masses.</summary>
    public static GasComposition FromMoleFractions(
        IEnumerable<KeyValuePair<string, double>> moleFractions, SpeciesDatabase database)
    {
        var masses = Accumulate(moleFractions)
            .Select(kv => new KeyValuePair<string, double>(kv.Key, kv.Value * database[kv.Key].MolarMass));
        return FromMassFractions(masses);
    }

    /// <summary>
    /// Standard dry air: mole fractions N2 0.78084, O2 0.20946, Ar 0.00934,
    /// CO2 0.000412 (US Standard Atmosphere 1976, CO2 updated to a modern
    /// ~412 ppm level). Gives M ≈ 28.965 kg/kmol, R ≈ 287.0 J/(kg·K).
    /// </summary>
    public static GasComposition DryAir(SpeciesDatabase database) =>
        FromMoleFractions(
        [
            new("N2", 0.78084),
            new("O2", 0.20946),
            new("AR", 0.00934),
            new("CO2", 0.000412),
        ], database);

    /// <summary>Mass-weighted mix of compositions, e.g. fresh charge + residual.</summary>
    public static GasComposition Mix(params (GasComposition Composition, double MassFraction)[] parts)
    {
        var combined = parts.SelectMany(p =>
            p.Composition.MassFractions.Select(kv =>
                new KeyValuePair<string, double>(kv.Key, kv.Value * p.MassFraction)));
        return FromMassFractions(combined);
    }

    private static Dictionary<string, double> Accumulate(IEnumerable<KeyValuePair<string, double>> pairs)
    {
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            if (value < 0)
            {
                throw new ArgumentException($"Negative fraction for '{key}'.");
            }

            if (value > 0)
            {
                dict[key] = dict.GetValueOrDefault(key) + value;
            }
        }

        return dict;
    }
}
