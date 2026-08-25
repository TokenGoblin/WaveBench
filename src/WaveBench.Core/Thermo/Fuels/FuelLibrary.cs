namespace WaveBench.Core.Thermo.Fuels;

/// <summary>
/// Shipped fuel library (plan §2.4). Property sources: Heywood, "Internal
/// Combustion Engine Fundamentals" App. D (LHV, stoich AFR, latent heats);
/// Metghalchi &amp; Keck, Combust. Flame 48 (1982) for flame-speed coefficients
/// (RMFD-303 indolene used for gasoline surrogates). Octane numbers are
/// typical published values. All fuels are data, user-editable in the model;
/// gasoline surrogates use the C8H15 pseudo-molecule (H/C = 1.875, M ≈ 111.2)
/// with iso-octane vapour thermodynamics.
/// </summary>
public static class FuelLibrary
{
    private const string MkSource = "Metghalchi & Keck, Combust. Flame 48 (1982) 191-210";

    private static readonly FlameSpeedCoefficients GasolineFlameSpeed =
        new(0.2758, -0.7834, 1.13, IsApproximate: false, $"RMFD-303 indolene, {MkSource}");

    public static Fuel IsoOctane { get; } = new()
    {
        Name = "Iso-octane",
        Formula = new FuelFormula(8, 18, 0),
        LowerHeatingValue = 44.3e6,
        TabulatedStoichAfr = 15.13,
        LatentHeatOfVaporisation = 308e3,
        LiquidDensity = 692,
        VapourSpeciesName = "IC8H18",
        Ron = 100,
        Mon = 100,
        FlameSpeed = new FlameSpeedCoefficients(0.2632, -0.8472, 1.13, false, MkSource),
        Notes = "Primary reference fuel, ON 100 by definition.",
    };

    public static Fuel NHeptane { get; } = new()
    {
        Name = "n-Heptane",
        Formula = new FuelFormula(7, 16, 0),
        LowerHeatingValue = 44.6e6,
        TabulatedStoichAfr = 15.18,
        LatentHeatOfVaporisation = 365e3,
        LiquidDensity = 684,
        VapourSpeciesName = "NC7H16",
        Ron = 0,
        Mon = 0,
        FlameSpeed = new FlameSpeedCoefficients(0.386, -1.03, 1.10, true,
            "fit to published n-heptane S_L data (approximate)"),
        Notes = "Primary reference fuel, ON 0 by definition.",
    };

    public static Fuel Toluene { get; } = new()
    {
        Name = "Toluene",
        Formula = new FuelFormula(7, 8, 0),
        LowerHeatingValue = 40.6e6,
        TabulatedStoichAfr = 13.50,
        LatentHeatOfVaporisation = 412e3,
        LiquidDensity = 867,
        VapourSpeciesName = "C7H8",
        Ron = 121,
        Mon = 107,
        FlameSpeed = new FlameSpeedCoefficients(0.36, -1.0, 1.10, true,
            "fit to published toluene S_L data (approximate)"),
        Notes = "Toluene reference fuel component; octane values are typical published figures.",
    };

    public static Fuel GasolineRon95 { get; } = Gasoline("Gasoline RON95", 95, 85);

    public static Fuel GasolineRon98 { get; } = Gasoline("Gasoline RON98", 98, 88);

    public static Fuel GasolineRon100 { get; } = Gasoline("Gasoline RON100 (race)", 100, 90);

    public static Fuel Ethanol { get; } = new()
    {
        Name = "Ethanol E100",
        Formula = new FuelFormula(2, 6, 1),
        LowerHeatingValue = 26.9e6,
        TabulatedStoichAfr = 9.00,
        LatentHeatOfVaporisation = 840e3,
        LiquidDensity = 789,
        VapourSpeciesName = "C2H5OH",
        Ron = 109,
        Mon = 90,
        FlameSpeed = new FlameSpeedCoefficients(0.41, -1.2, 1.08, true,
            "fit to Gülder (1982) ethanol S_L data (approximate)"),
    };

    public static Fuel Methanol { get; } = new()
    {
        Name = "Methanol M100",
        Formula = new FuelFormula(1, 4, 1),
        LowerHeatingValue = 20.0e6,
        TabulatedStoichAfr = 6.47,
        LatentHeatOfVaporisation = 1100e3,
        LiquidDensity = 792,
        VapourSpeciesName = "CH3OH",
        Ron = 109,
        Mon = 89,
        FlameSpeed = new FlameSpeedCoefficients(0.3692, -1.4051, 1.11, false, MkSource),
        Notes = "Δh_vap ≈ 1100 kJ/kg — charge cooling is a first-class effect (plan §2.4).",
    };

    public static Fuel E10 { get; } = EthanolBlend("E10 (10% vol ethanol)", 0.10, 96, 86);

    public static Fuel E30 { get; } = EthanolBlend("E30 (30% vol ethanol)", 0.30, 100, 88);

    public static Fuel E85 { get; } = EthanolBlend("E85 (85% vol ethanol)", 0.85, 106, 89);

    public static Fuel Methane { get; } = new()
    {
        Name = "CNG / Methane",
        Formula = new FuelFormula(1, 4, 0),
        LowerHeatingValue = 50.0e6,
        TabulatedStoichAfr = 17.23,
        LatentHeatOfVaporisation = 0,
        LiquidDensity = 422,
        VapourSpeciesName = "CH4",
        Ron = 120,
        Mon = 120,
        FlameSpeed = new FlameSpeedCoefficients(0.36, -1.4, 1.06, true,
            "fit to published methane S_L data (approximate)"),
        Notes = "Supplied as gas: latent heat set to zero (no evaporative charge cooling).",
    };

    public static Fuel Propane { get; } = new()
    {
        Name = "Propane / LPG",
        Formula = new FuelFormula(3, 8, 0),
        LowerHeatingValue = 46.4e6,
        TabulatedStoichAfr = 15.67,
        LatentHeatOfVaporisation = 426e3,
        LiquidDensity = 493,
        VapourSpeciesName = "C3H8",
        Ron = 112,
        Mon = 97,
        FlameSpeed = new FlameSpeedCoefficients(0.3422, -1.3865, 1.08, false, MkSource),
        Notes = "Latent heat applies only to liquid-injection systems; vapour systems get no cooling.",
    };

    public static Fuel Hydrogen { get; } = new()
    {
        Name = "Hydrogen",
        Formula = new FuelFormula(0, 2, 0),
        LowerHeatingValue = 120.0e6,
        TabulatedStoichAfr = 34.3,
        LatentHeatOfVaporisation = 0,
        LiquidDensity = 71,
        VapourSpeciesName = "H2",
        Ron = null,
        Mon = null,
        FlameSpeed = null,
        Notes = "Octane rating and Metghalchi-Keck coefficients are not meaningful for H2; " +
                "the Douaud-Eyzat knock model must not be applied.",
    };

    /// <summary>Every shipped fuel (for iteration, UI listing and gate tests).</summary>
    public static IReadOnlyList<Fuel> All { get; } =
    [
        IsoOctane, NHeptane, Toluene,
        GasolineRon95, GasolineRon98, GasolineRon100,
        Ethanol, Methanol, E10, E30, E85,
        Methane, Propane, Hydrogen,
    ];

    private static Fuel Gasoline(string name, double ron, double mon) => new()
    {
        Name = name,
        // C8H15 pseudo-molecule: H/C = 1.875, M = 111.2 — Heywood's typical gasoline.
        Formula = new FuelFormula(8, 15, 0),
        LowerHeatingValue = 44.0e6,
        TabulatedStoichAfr = 14.6,
        LatentHeatOfVaporisation = 350e3,
        LiquidDensity = 750,
        VapourSpeciesName = "IC8H18",
        Ron = ron,
        Mon = mon,
        FlameSpeed = GasolineFlameSpeed,
        Notes = "Surrogate: C8H15 pseudo-molecule with iso-octane vapour thermodynamics.",
    };

    private static Fuel EthanolBlend(string name, double ethanolVolumeFraction, double ron, double mon)
    {
        var mEth = ethanolVolumeFraction * Ethanol.LiquidDensity;
        var mGas = (1 - ethanolVolumeFraction) * GasolineRon95.LiquidDensity;
        var wEth = mEth / (mEth + mGas);
        var majority = wEth >= 0.5 ? Ethanol : GasolineRon95;
        return Fuel.Blend(
            name, ron, mon,
            majority.VapourSpeciesName,
            majority.FlameSpeed,
            $"Volume blend; ethanol mass fraction {wEth:0.000}. Octane values are typical " +
            "splash-blend measurements (octane blending is non-linear) — user-editable.",
            (GasolineRon95, 1 - wEth), (Ethanol, wEth));
    }
}
