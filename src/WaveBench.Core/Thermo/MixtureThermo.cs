namespace WaveBench.Core.Thermo;

/// <summary>
/// Thermodynamic properties of a fixed-composition ideal-gas mixture:
///   c_p,mix(T,Y) = Σ Y_k c_p,k(T)     h_mix(T,Y) = Σ Y_k h_k(T)
///   R_mix(Y)     = R_u Σ (Y_k / M_k)  γ = c_p/(c_p − R)   a = √(γ R T)
/// (plan §2.3). The local speed of sound follows from local T and local
/// composition — never a constant.
/// </summary>
public enum PropertyEvaluation
{
    /// <summary>Direct NASA-polynomial evaluation.</summary>
    Direct,

    /// <summary>Pre-tabulated cubic interpolation (solver hot path).</summary>
    Tabulated,
}

public sealed class MixtureThermo
{
    private readonly ISpeciesThermo[] _species;
    private readonly double[] _massFractions;
    private readonly double[] _moleFractions;

    public MixtureThermo(
        GasComposition composition,
        SpeciesDatabase database,
        PropertyEvaluation evaluation = PropertyEvaluation.Direct)
    {
        Composition = composition;
        _species = composition.MassFractions.Keys
            .Select(name => evaluation == PropertyEvaluation.Tabulated
                ? (ISpeciesThermo)TabulatedSpecies.For(database[name])
                : database[name])
            .ToArray();
        _massFractions = _species.Select(s => composition.MassFractionOf(s.Name)).ToArray();

        SpecificGasConstant = 0.0;
        for (var i = 0; i < _species.Length; i++)
        {
            SpecificGasConstant += _massFractions[i] * _species[i].SpecificGasConstant;
        }

        MolarMass = PhysicalConstants.UniversalGasConstant / SpecificGasConstant;

        _moleFractions = new double[_species.Length];
        for (var i = 0; i < _species.Length; i++)
        {
            _moleFractions[i] = _massFractions[i] * MolarMass / _species[i].MolarMass;
        }
    }

    public GasComposition Composition { get; }

    /// <summary>R_mix, J/(kg·K).</summary>
    public double SpecificGasConstant { get; }

    /// <summary>kg/kmol.</summary>
    public double MolarMass { get; }

    /// <summary>c_p, J/(kg·K).</summary>
    public double Cp(double t)
    {
        var cp = 0.0;
        for (var i = 0; i < _species.Length; i++)
        {
            cp += _massFractions[i] * _species[i].Cp(t);
        }

        return cp;
    }

    /// <summary>c_v, J/(kg·K).</summary>
    public double Cv(double t) => Cp(t) - SpecificGasConstant;

    /// <summary>Ratio of specific heats γ(T).</summary>
    public double Gamma(double t)
    {
        var cp = Cp(t);
        return cp / (cp - SpecificGasConstant);
    }

    /// <summary>Local speed of sound a = √(γ(T,Y)·R(Y)·T), m/s.</summary>
    public double SoundSpeed(double t) => Math.Sqrt(Gamma(t) * SpecificGasConstant * t);

    /// <summary>Specific enthalpy including formation enthalpies, J/kg.</summary>
    public double Enthalpy(double t)
    {
        var h = 0.0;
        for (var i = 0; i < _species.Length; i++)
        {
            h += _massFractions[i] * _species[i].Enthalpy(t);
        }

        return h;
    }

    /// <summary>Specific internal energy, J/kg.</summary>
    public double InternalEnergy(double t) => Enthalpy(t) - SpecificGasConstant * t;

    /// <summary>
    /// Specific entropy at pressure p, J/(kg·K):
    /// s = Σ Y_k [s°_k(T) − R_k ln(X_k p / p_ref)] (ideal mixture, partial pressures).
    /// </summary>
    public double Entropy(double t, double pressure)
    {
        var s = 0.0;
        for (var i = 0; i < _species.Length; i++)
        {
            var partial = _moleFractions[i] * pressure / PhysicalConstants.ReferencePressure;
            s += _massFractions[i] *
                 (_species[i].StandardEntropy(t) - _species[i].SpecificGasConstant * Math.Log(partial));
        }

        return s;
    }

    /// <summary>True when every species fit covers T.</summary>
    public bool IsInRange(double t) => _species.All(s => s.IsInRange(t));
}
