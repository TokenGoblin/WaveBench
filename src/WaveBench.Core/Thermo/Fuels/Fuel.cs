namespace WaveBench.Core.Thermo.Fuels;

/// <summary>
/// Metghalchi &amp; Keck laminar-flame-speed reference coefficients (m/s):
/// S_L0(φ) = Bm + Bφ·(φ − φm)². Source: Metghalchi &amp; Keck, Combustion and
/// Flame 48 (1982) 191-210; validity roughly 0.8 ≤ φ ≤ 1.4, T_u 298–700 K,
/// p 0.4–50 atm. Entries flagged <see cref="IsApproximate"/> are fits to other
/// published data, not Metghalchi–Keck measurements.
/// </summary>
public sealed record FlameSpeedCoefficients(
    double Bm,
    double BPhi,
    double PhiM,
    bool IsApproximate,
    string Source);

/// <summary>
/// A fuel is a data record, not a hard-coded constant (plan §2.4).
/// All values SI: J/kg, kg/m³. <see cref="TabulatedStoichAfr"/> is the
/// published value used to cross-check the formula-derived stoichiometry
/// (gate: agreement within 0.5%).
/// </summary>
public sealed record Fuel
{
    public required string Name { get; init; }

    public required FuelFormula Formula { get; init; }

    /// <summary>J/kg.</summary>
    public required double LowerHeatingValue { get; init; }

    /// <summary>Published stoichiometric AFR (Heywood, App. D unless noted).</summary>
    public required double TabulatedStoichAfr { get; init; }

    /// <summary>Latent heat of vaporisation, J/kg. Zero for fuels supplied as gas.</summary>
    public required double LatentHeatOfVaporisation { get; init; }

    /// <summary>Liquid density, kg/m³ (at ~20 °C; cryogenic liquid for gases).</summary>
    public required double LiquidDensity { get; init; }

    /// <summary>Thermo-database species representing the fuel vapour.</summary>
    public required string VapourSpeciesName { get; init; }

    /// <summary>Research octane number; null when octane rating is not meaningful (H2).</summary>
    public required double? Ron { get; init; }

    public required double? Mon { get; init; }

    public FlameSpeedCoefficients? FlameSpeed { get; init; }

    public string? Notes { get; init; }

    public double? Sensitivity => Ron - Mon;

    /// <summary>Stoichiometric AFR computed from the formula and standard air.</summary>
    public double StoichAfr => Stoichiometry.StoichAirFuelRatio(Formula);

    public double OxygenMassFraction => Formula.OxygenMassFraction;

    /// <summary>
    /// Mass-fraction blend. The blended formula is the pseudo-molecule with the
    /// mole-weighted mean molar mass, so formula-derived stoichiometry is exact
    /// for the blend. LHV, latent heat: mass-weighted; density: volume-additive.
    /// RON/MON must be supplied — octane blending is non-linear and the shipped
    /// values are typical measurements, user-editable.
    /// </summary>
    public static Fuel Blend(
        string name,
        double? ron,
        double? mon,
        string vapourSpeciesName,
        FlameSpeedCoefficients? flameSpeed,
        string? notes,
        params (Fuel Fuel, double MassFraction)[] parts)
    {
        var total = parts.Sum(p => p.MassFraction);
        if (Math.Abs(total - 1.0) > 1e-9)
        {
            throw new ArgumentException($"Blend mass fractions sum to {total}, expected 1.");
        }

        // kmol of each element and of fuel molecules, per kg of blend
        var molFuel = parts.Sum(p => p.MassFraction / p.Fuel.Formula.MolarMass);
        var c = parts.Sum(p => p.MassFraction / p.Fuel.Formula.MolarMass * p.Fuel.Formula.Carbon) / molFuel;
        var h = parts.Sum(p => p.MassFraction / p.Fuel.Formula.MolarMass * p.Fuel.Formula.Hydrogen) / molFuel;
        var o = parts.Sum(p => p.MassFraction / p.Fuel.Formula.MolarMass * p.Fuel.Formula.Oxygen) / molFuel;

        var formula = new FuelFormula(c, h, o);
        return new Fuel
        {
            Name = name,
            Formula = formula,
            LowerHeatingValue = parts.Sum(p => p.MassFraction * p.Fuel.LowerHeatingValue),
            TabulatedStoichAfr = parts.Sum(p => p.MassFraction * p.Fuel.TabulatedStoichAfr),
            LatentHeatOfVaporisation = parts.Sum(p => p.MassFraction * p.Fuel.LatentHeatOfVaporisation),
            LiquidDensity = 1.0 / parts.Sum(p => p.MassFraction / p.Fuel.LiquidDensity),
            VapourSpeciesName = vapourSpeciesName,
            Ron = ron,
            Mon = mon,
            FlameSpeed = flameSpeed,
            Notes = notes,
        };
    }
}
