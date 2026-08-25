namespace WaveBench.Core.Thermo.Fuels;

/// <summary>
/// Elemental composition CxHyOz of a fuel (per molecule; fractional atom
/// counts are allowed for surrogate blends).
/// </summary>
public readonly record struct FuelFormula(double Carbon, double Hydrogen, double Oxygen)
{
    /// <summary>kg/kmol.</summary>
    public double MolarMass =>
        Carbon * AtomicWeights.Carbon + Hydrogen * AtomicWeights.Hydrogen + Oxygen * AtomicWeights.Oxygen;

    /// <summary>kmol O2 required per kmol fuel for complete combustion.</summary>
    public double StoichiometricO2Moles => Carbon + Hydrogen / 4.0 - Oxygen / 2.0;

    /// <summary>Oxygen content by mass (shifts stoich AFR and charge cooling, plan §2.4).</summary>
    public double OxygenMassFraction => Oxygen * AtomicWeights.Oxygen / MolarMass;

    public override string ToString() =>
        $"C{Carbon:0.###}H{Hydrogen:0.###}" + (Oxygen > 0 ? $"O{Oxygen:0.###}" : "");
}

/// <summary>
/// Stoichiometry from the elemental formula and standard dry air
/// (single source of truth: <see cref="AirComposition"/>).
/// </summary>
public static class Stoichiometry
{
    /// <summary>Stoichiometric air-fuel ratio by mass.</summary>
    public static double StoichAirFuelRatio(FuelFormula fuel) =>
        fuel.StoichiometricO2Moles * AirComposition.AirMassPerKmolO2 / fuel.MolarMass;

    /// <summary>Air-fuel ratio at excess-air ratio λ (= 1/φ).</summary>
    public static double AirFuelRatio(FuelFormula fuel, double lambda) =>
        StoichAirFuelRatio(fuel) * lambda;
}
