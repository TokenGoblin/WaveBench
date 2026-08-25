namespace WaveBench.Core.Thermo;

/// <summary>
/// Per-species thermodynamic property evaluation. Implemented directly by
/// <see cref="Species"/> (NASA polynomial evaluation) and by
/// <see cref="TabulatedSpecies"/> (pre-tabulated fast path, plan §2.3).
/// </summary>
public interface ISpeciesThermo
{
    string Name { get; }

    /// <summary>kg/kmol.</summary>
    double MolarMass { get; }

    /// <summary>J/(kg·K).</summary>
    double SpecificGasConstant { get; }

    /// <summary>J/(kg·K).</summary>
    double Cp(double t);

    /// <summary>J/kg, including formation enthalpy.</summary>
    double Enthalpy(double t);

    /// <summary>J/(kg·K) at the reference pressure.</summary>
    double StandardEntropy(double t);

    bool IsInRange(double t);
}
