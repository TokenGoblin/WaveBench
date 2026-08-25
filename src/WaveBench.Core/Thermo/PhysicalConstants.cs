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
    /// The named constants serve hot-path arithmetic (no dictionary lookup);
    /// the dictionary serves the CHEMKIN parser.
    /// </summary>
    public const double Hydrogen = 1.008;
    public const double Carbon = 12.011;
    public const double Nitrogen = 14.007;
    public const double Oxygen = 15.999;
    public const double Argon = 39.95;

    private static readonly Dictionary<string, double> Weights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["H"] = Hydrogen,
        ["C"] = Carbon,
        ["N"] = Nitrogen,
        ["O"] = Oxygen,
        ["AR"] = Argon,
        ["HE"] = 4.0026,
        ["S"] = 32.06,
    };

    public static double Of(string element) =>
        Weights.TryGetValue(element, out var w)
            ? w
            : throw new KeyNotFoundException($"No atomic weight for element '{element}'.");
}
