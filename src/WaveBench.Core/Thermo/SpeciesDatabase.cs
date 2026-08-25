using System.Reflection;

namespace WaveBench.Core.Thermo;

/// <summary>
/// Lookup of species thermodynamic data. <see cref="Default"/> serves the
/// curated embedded dataset (see Thermo/Data/thermo.dat for provenance).
/// </summary>
public sealed class SpeciesDatabase
{
    private static readonly Lazy<SpeciesDatabase> DefaultInstance = new(LoadEmbedded);

    private readonly Dictionary<string, Species> _byName;

    public SpeciesDatabase(IEnumerable<Species> species)
    {
        _byName = species.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static SpeciesDatabase Default => DefaultInstance.Value;

    public IReadOnlyCollection<Species> All => _byName.Values;

    public Species this[string name] =>
        _byName.TryGetValue(name, out var s)
            ? s
            : throw new KeyNotFoundException($"Species '{name}' is not in the database.");

    public bool Contains(string name) => _byName.ContainsKey(name);

    private static SpeciesDatabase LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resource = "WaveBench.Core.Thermo.Data.thermo.dat";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        using var reader = new StreamReader(stream);
        return new SpeciesDatabase(ChemkinThermoParser.Parse(reader));
    }
}
