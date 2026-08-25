namespace WaveBench.Core.Thermo;

/// <summary>
/// Standard dry-air mole fractions (US Standard Atmosphere 1976; CO2 at a
/// modern ~412 ppm). Single source of truth for both the air composition and
/// stoichiometric air-fuel calculations.
/// </summary>
public static class AirComposition
{
    public const double MoleFractionN2 = 0.78084;
    public const double MoleFractionO2 = 0.20946;
    public const double MoleFractionAr = 0.00934;
    public const double MoleFractionCo2 = 0.000412;

    /// <summary>Moles of N2 accompanying one mole of O2 in air.</summary>
    public const double N2PerO2 = MoleFractionN2 / MoleFractionO2;

    /// <summary>Moles of Ar accompanying one mole of O2 in air.</summary>
    public const double ArPerO2 = MoleFractionAr / MoleFractionO2;

    /// <summary>Moles of CO2 accompanying one mole of O2 in air.</summary>
    public const double Co2PerO2 = MoleFractionCo2 / MoleFractionO2;

    /// <summary>
    /// Mass of air that supplies one kmol of O2, kg (≈ 138.3 kg). Computed from
    /// the mole ratios and IUPAC atomic weights.
    /// </summary>
    public static readonly double AirMassPerKmolO2 =
        2 * AtomicWeights.Of("O")
        + N2PerO2 * 2 * AtomicWeights.Of("N")
        + ArPerO2 * AtomicWeights.Of("AR")
        + Co2PerO2 * (AtomicWeights.Of("C") + 2 * AtomicWeights.Of("O"));
}
