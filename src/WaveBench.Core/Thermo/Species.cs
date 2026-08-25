namespace WaveBench.Core.Thermo;

/// <summary>
/// A gas-phase species with two-range NASA-7 thermodynamic data.
/// All specific (per-mass) quantities are SI: J/(kg·K), J/kg.
/// </summary>
public sealed class Species : ISpeciesThermo
{
    public Species(
        string name,
        IReadOnlyDictionary<string, double> elements,
        double tLow,
        double tMid,
        double tHigh,
        Nasa7Coefficients lowerRange,
        Nasa7Coefficients upperRange)
    {
        Name = name;
        Elements = elements;
        TLow = tLow;
        TMid = tMid;
        THigh = tHigh;
        LowerRange = lowerRange;
        UpperRange = upperRange;
        MolarMass = elements.Sum(e => AtomicWeights.Of(e.Key) * e.Value);
        SpecificGasConstant = PhysicalConstants.UniversalGasConstant / MolarMass;
    }

    public string Name { get; }

    /// <summary>Element symbol → atom count.</summary>
    public IReadOnlyDictionary<string, double> Elements { get; }

    /// <summary>kg/kmol.</summary>
    public double MolarMass { get; }

    /// <summary>R_u / M, J/(kg·K).</summary>
    public double SpecificGasConstant { get; }

    public double TLow { get; }

    public double TMid { get; }

    public double THigh { get; }

    public Nasa7Coefficients LowerRange { get; }

    public Nasa7Coefficients UpperRange { get; }

    /// <summary>True when T lies inside the fit's stated validity range.</summary>
    public bool IsInRange(double t) => t >= TLow && t <= THigh;

    private Nasa7Coefficients RangeFor(double t) => t < TMid ? LowerRange : UpperRange;

    /// <summary>Molar heat capacity at constant pressure, J/(kmol·K).</summary>
    public double MolarCp(double t) => RangeFor(t).CpOverR(t) * PhysicalConstants.UniversalGasConstant;

    /// <summary>Specific heat capacity at constant pressure, J/(kg·K).</summary>
    public double Cp(double t) => RangeFor(t).CpOverR(t) * SpecificGasConstant;

    /// <summary>Molar enthalpy including formation enthalpy, J/kmol.</summary>
    public double MolarEnthalpy(double t) => RangeFor(t).HOverRT(t) * PhysicalConstants.UniversalGasConstant * t;

    /// <summary>Specific enthalpy including formation enthalpy, J/kg.</summary>
    public double Enthalpy(double t) => RangeFor(t).HOverRT(t) * SpecificGasConstant * t;

    /// <summary>Specific internal energy, J/kg.</summary>
    public double InternalEnergy(double t) => Enthalpy(t) - SpecificGasConstant * t;

    /// <summary>Standard-state specific entropy at the reference pressure, J/(kg·K).</summary>
    public double StandardEntropy(double t) => RangeFor(t).SOverR(t) * SpecificGasConstant;
}
