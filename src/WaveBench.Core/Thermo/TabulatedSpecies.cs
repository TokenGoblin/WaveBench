using System.Collections.Concurrent;

namespace WaveBench.Core.Thermo;

/// <summary>
/// Fast-path property evaluation (plan §2.3): c_p, h and s° pre-tabulated on a
/// 200–3500 K grid at 5 K steps with cubic (Catmull-Rom) interpolation.
/// Outside the grid it falls back to direct polynomial evaluation.
/// </summary>
public sealed class TabulatedSpecies : ISpeciesThermo
{
    public const double GridMin = 200.0;
    public const double GridMax = 3500.0;
    public const double GridStep = 5.0;

    private static readonly ConcurrentDictionary<Species, TabulatedSpecies> Cache = new();

    private readonly Species _species;
    private readonly double[] _cp;
    private readonly double[] _h;
    private readonly double[] _s;

    private TabulatedSpecies(Species species)
    {
        _species = species;
        var n = (int)((GridMax - GridMin) / GridStep) + 1;
        _cp = new double[n];
        _h = new double[n];
        _s = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = GridMin + i * GridStep;
            _cp[i] = species.Cp(t);
            _h[i] = species.Enthalpy(t);
            _s[i] = species.StandardEntropy(t);
        }
    }

    public static TabulatedSpecies For(Species species) => Cache.GetOrAdd(species, s => new TabulatedSpecies(s));

    public string Name => _species.Name;

    public double MolarMass => _species.MolarMass;

    public double SpecificGasConstant => _species.SpecificGasConstant;

    public bool IsInRange(double t) => _species.IsInRange(t);

    public double Cp(double t) => t is < GridMin or > GridMax ? _species.Cp(t) : Interpolate(_cp, t);

    public double Enthalpy(double t) => t is < GridMin or > GridMax ? _species.Enthalpy(t) : Interpolate(_h, t);

    public double StandardEntropy(double t) =>
        t is < GridMin or > GridMax ? _species.StandardEntropy(t) : Interpolate(_s, t);

    private static double Interpolate(double[] values, double t)
    {
        var x = (t - GridMin) / GridStep;
        var i1 = Math.Min((int)x, values.Length - 2);
        var u = x - i1;
        var i0 = Math.Max(i1 - 1, 0);
        var i2 = i1 + 1;
        var i3 = Math.Min(i1 + 2, values.Length - 1);

        double p0 = values[i0], p1 = values[i1], p2 = values[i2], p3 = values[i3];
        return 0.5 * (2.0 * p1 +
                      u * (p2 - p0 +
                           u * (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3 +
                                u * (3.0 * (p1 - p2) + p3 - p0))));
    }
}
