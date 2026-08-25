using WaveBench.Core.Numerics;
using WaveBench.Core.Thermo;

namespace WaveBench.Core.Solver;

/// <summary>Primitive gas state recovered from conserved variables.</summary>
public readonly record struct GasState(double U, double P, double T, double SoundSpeed, double Gamma);

/// <summary>
/// Equation of state seen by the solver. Two implementations: a calorically
/// perfect gas (verification problems) and the species-resolved caloric EOS
/// of plan §2.3, where R(Y), γ(T,Y) and therefore the local sound speed
/// follow from composition — the plan's single most important modelling
/// decision (§2.2).
/// </summary>
public interface IGasModel
{
    /// <summary>Number of transported species (0 for the perfect gas).</summary>
    int SpeciesCount { get; }

    /// <summary>
    /// Recover primitives from conserved densities. <paramref name="tGuess"/>
    /// warm-starts the temperature iteration for the real-gas model.
    /// </summary>
    GasState FromConserved(double rho, double momentum, double totalEnergy, ReadOnlySpan<double> y, double tGuess);

    /// <summary>Total energy per volume from primitives.</summary>
    double TotalEnergy(double rho, double u, double p, ReadOnlySpan<double> y);

    /// <summary>γ at the state implied by (ρ, p, Y) — for wave-speed estimates.</summary>
    double Gamma(double rho, double p, ReadOnlySpan<double> y);

    /// <summary>c_p at the state implied by (ρ, p, Y) — for the Colburn correlation.</summary>
    double Cp(double rho, double p, ReadOnlySpan<double> y);
}

/// <summary>Constant-γ ideal gas.</summary>
public sealed class PerfectGasModel(PerfectGas gas) : IGasModel
{
    public PerfectGas Gas { get; } = gas;

    public int SpeciesCount => 0;

    public GasState FromConserved(double rho, double momentum, double totalEnergy, ReadOnlySpan<double> y, double tGuess)
    {
        var u = momentum / rho;
        var p = Gas.Pressure(rho, momentum, totalEnergy);
        var t = p / (rho * Gas.SpecificGasConstant);
        return new GasState(u, p, t, Gas.SoundSpeed(rho, p), Gas.Gamma);
    }

    public double TotalEnergy(double rho, double u, double p, ReadOnlySpan<double> y) => Gas.TotalEnergy(rho, u, p);

    public double Gamma(double rho, double p, ReadOnlySpan<double> y) => Gas.Gamma;

    public double Cp(double rho, double p, ReadOnlySpan<double> y) =>
        Gas.Gamma * Gas.SpecificGasConstant / (Gas.Gamma - 1.0);
}

/// <summary>
/// Species-resolved caloric EOS (plan §2.3): e(T,Y) = Σ Y_k (h_k(T) − R_k T)
/// with NASA-polynomial species data on the tabulated fast path. Temperature
/// is recovered from internal energy by Newton iteration (c_v as derivative);
/// p = ρ R(Y) T, a = √(γ(T,Y)·R(Y)·T).
/// </summary>
public sealed class MultiSpeciesGasModel : IGasModel
{
    private readonly ISpeciesThermo[] _species;
    private readonly double[] _r;

    public MultiSpeciesGasModel(SpeciesDatabase database, IReadOnlyList<string> speciesNames)
    {
        SpeciesNames = speciesNames.ToArray();
        _species = SpeciesNames.Select(n => (ISpeciesThermo)TabulatedSpecies.For(database[n])).ToArray();
        _r = _species.Select(s => s.SpecificGasConstant).ToArray();
    }

    public IReadOnlyList<string> SpeciesNames { get; }

    public int SpeciesCount => _species.Length;

    /// <summary>Mass-fraction vector for a <see cref="GasComposition"/> in this model's species order.</summary>
    public double[] MassFractionsOf(GasComposition composition)
    {
        var y = new double[SpeciesCount];
        for (var k = 0; k < SpeciesCount; k++)
        {
            y[k] = composition.MassFractionOf(SpeciesNames[k]);
        }

        var sum = y.Sum();
        if (Math.Abs(sum - 1.0) > 1e-9)
        {
            throw new ArgumentException(
                $"Composition contains species outside this gas model (captured {sum:F6} of the mass).");
        }

        return y;
    }

    public double GasConstant(ReadOnlySpan<double> y)
    {
        var r = 0.0;
        for (var k = 0; k < y.Length; k++)
        {
            r += y[k] * _r[k];
        }

        return r;
    }

    private double InternalEnergy(double t, ReadOnlySpan<double> y)
    {
        var e = 0.0;
        for (var k = 0; k < y.Length; k++)
        {
            e += y[k] * (_species[k].Enthalpy(t) - _r[k] * t);
        }

        return e;
    }

    private double CpAt(double t, ReadOnlySpan<double> y)
    {
        var cp = 0.0;
        for (var k = 0; k < y.Length; k++)
        {
            cp += y[k] * _species[k].Cp(t);
        }

        return cp;
    }

    public GasState FromConserved(double rho, double momentum, double totalEnergy, ReadOnlySpan<double> y, double tGuess)
    {
        var u = momentum / rho;
        var eTarget = totalEnergy / rho - 0.5 * u * u;
        var r = GasConstant(y);

        var t = double.IsFinite(tGuess) && tGuess > 150.0 ? tGuess : 400.0;
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var cv = CpAt(t, y) - r;
            var delta = (InternalEnergy(t, y) - eTarget) / cv;
            t -= delta;
            if (t < 150.0)
            {
                t = 150.0;
            }
            else if (t > 4500.0)
            {
                t = 4500.0;
            }

            if (Math.Abs(delta) < 1e-10 * t)
            {
                break;
            }
        }

        var p = rho * r * t;
        var cp = CpAt(t, y);
        var gamma = cp / (cp - r);
        return new GasState(u, p, t, Math.Sqrt(gamma * r * t), gamma);
    }

    public double TotalEnergy(double rho, double u, double p, ReadOnlySpan<double> y)
    {
        var t = p / (rho * GasConstant(y));
        return rho * (InternalEnergy(t, y) + 0.5 * u * u);
    }

    public double Gamma(double rho, double p, ReadOnlySpan<double> y)
    {
        var r = GasConstant(y);
        var t = p / (rho * r);
        var cp = CpAt(t, y);
        return cp / (cp - r);
    }

    public double Cp(double rho, double p, ReadOnlySpan<double> y) =>
        CpAt(p / (rho * GasConstant(y)), y);

    /// <summary>Specific enthalpy of one species at T (injector energy flux), J/kg.</summary>
    public double SpeciesEnthalpy(int speciesIndex, double t) => _species[speciesIndex].Enthalpy(t);
}
