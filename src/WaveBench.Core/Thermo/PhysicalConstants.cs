namespace WaveBench.Core.Thermo;

public static class PhysicalConstants
{
    /// <summary>Universal gas constant, J/(kmol·K). CODATA 2018 exact value.</summary>
    public const double UniversalGasConstant = 8314.462618;

    /// <summary>Standard reference pressure for entropy, Pa (1 atm).</summary>
    public const double ReferencePressure = 101_325.0;

    /// <summary>Standard reference temperature, K (25 °C).</summary>
    public const double ReferenceTemperature = 298.15;
}

internal static class AtomicWeights
{
    /// <summary>
    /// Standard atomic weights, kg/kmol (IUPAC 2021 abridged values).
    /// </summary>
    private static readonly Dictionary<string, double> Weights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["H"] = 1.008,
        ["C"] = 12.011,
        ["N"] = 14.007,
        ["O"] = 15.999,
        ["AR"] = 39.95,
        ["HE"] = 4.0026,
        ["S"] = 32.06,
    };

    public static double Of(string element) =>
        Weights.TryGetValue(element, out var w)
            ? w
            : throw new KeyNotFoundException($"No atomic weight for element '{element}'.");
}
