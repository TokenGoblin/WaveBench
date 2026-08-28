using WaveBench.Boost.Thermal;

namespace WaveBench.Boost;

/// <summary>
/// A compressor outlet prediction, three ways, so the difference is visible
/// rather than asserted.
/// </summary>
/// <param name="RawMapOutletK">
/// What the map says on its own: T₀₁·[1 + (PR^κ − 1)/η_map]. This is what an
/// intercooler gets sized from when nobody corrects anything.
/// </param>
/// <param name="AdiabaticOutletK">
/// The aerodynamic answer with no heat at all, using the efficiency recovered
/// from the map by removing the gas stand's own heat flux.
/// </param>
/// <param name="DiabaticOutletK">The on-engine answer: aerodynamic work plus the on-engine heat flux.</param>
/// <param name="AerodynamicEfficiency">η recovered from the map. Always higher than the map's apparent value.</param>
/// <param name="ApparentEfficiencyOnEngine">
/// What a temperature-based measurement on the engine would report. Lower than
/// the map's, which is why on-engine "efficiency" looks worse than the datasheet.
/// </param>
/// <param name="GasStandHeatW">Heat the stand put into the air when the map was measured.</param>
/// <param name="EngineHeatW">Heat the engine puts into it.</param>
public readonly record struct CompressorOutletPrediction(
    double RawMapOutletK,
    double AdiabaticOutletK,
    double DiabaticOutletK,
    double AerodynamicEfficiency,
    double ApparentEfficiencyOnEngine,
    double GasStandHeatW,
    double EngineHeatW);

/// <summary>
/// The diabatic correction (plan §4.2).
///
/// <b>The claim, stated plainly:</b> a gas-stand map's efficiency is an
/// <i>apparent</i> efficiency. The stand measures a total temperature rise that
/// already contains heat conducted from the turbine end, so
/// <c>η_map = ΔT_ideal/(ΔT_aero + ΔT_heat,stand)</c> and not
/// <c>ΔT_ideal/ΔT_aero</c>. Two consequences follow, and they point in
/// opposite directions, which is why this is worth doing properly:
///
/// <list type="number">
/// <item>The compressor's real aerodynamic efficiency is HIGHER than the map
/// says, because part of the measured rise was not work.</item>
/// <item>The on-engine outlet temperature is HIGHER than the map predicts,
/// because a turbine inlet 400–500 K hotter than the stand's drives more heat
/// into the compressor end than the map's own condition did.</item>
/// </list>
///
/// The plan's summary of the size of it: on-engine compressor outlet routinely
/// runs 15–30 K above the adiabatic prediction, and an intercooler sized from
/// raw map numbers is under-sized.
///
/// <b>Status.</b> The machinery here is verified against a synthetic case where
/// the heat flux is known exactly, and its magnitude is checked against the
/// published range. It is NOT yet validated against a measured on-engine
/// dataset — that is validation case 21, and it is open. The thermal
/// conductances in <see cref="TurboThermalProperties"/> are calibration
/// parameters and are exposed for exactly that reason.
/// </summary>
public static class DiabaticCorrection
{
    /// <summary>
    /// The condition a compressor map was measured under. Read from the map's
    /// documentation; the defaults describe a common hot gas stand.
    /// </summary>
    /// <param name="TurbineInletK">Stand turbine inlet temperature. Cold stands run near 350 K, hot ones 850–950 K.</param>
    /// <param name="Environment">Bay, oil and coolant temperatures on the stand.</param>
    public sealed record GasStandCondition(double TurbineInletK = 873.15, TurboEnvironment? Environment = null)
    {
        public TurboEnvironment Conditions => Environment ?? TurboEnvironment.GasStand;

        /// <summary>A cold gas stand — no combustor, air heated only by the compressor loop.</summary>
        public static GasStandCondition Cold { get; } = new(353.15);

        /// <summary>A hot gas stand with a combustor, the usual way a modern map is measured.</summary>
        public static GasStandCondition Hot { get; } = new(873.15);
    }

    /// <summary>
    /// Correct one compressor operating point from map conditions to engine
    /// conditions.
    /// </summary>
    /// <param name="mapEfficiency">The apparent efficiency read off the map.</param>
    /// <param name="pressureRatio">Total-to-total pressure ratio.</param>
    /// <param name="inletTemperatureK">T₀₁ at the compressor.</param>
    /// <param name="massFlowKgPerS">Air flow through the compressor.</param>
    /// <param name="engineTurbineInletK">T₀₃ on the engine — the driver of the whole effect.</param>
    /// <param name="engineEnvironment">Engine-bay, oil and coolant temperatures.</param>
    /// <param name="stand">What the map was measured under.</param>
    /// <param name="properties">Thermal properties of this turbocharger.</param>
    /// <param name="gamma">Ratio of specific heats for air.</param>
    /// <param name="cp">c_p for air, J/kg·K.</param>
    public static CompressorOutletPrediction Correct(
        double mapEfficiency,
        double pressureRatio,
        double inletTemperatureK,
        double massFlowKgPerS,
        double engineTurbineInletK,
        TurboEnvironment engineEnvironment,
        GasStandCondition? stand = null,
        TurboThermalProperties? properties = null,
        double gamma = 1.4,
        double cp = 1005.0)
    {
        ArgumentNullException.ThrowIfNull(engineEnvironment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(massFlowKgPerS);

        var condition = stand ?? GasStandCondition.Hot;
        var thermal = properties ?? new TurboThermalProperties();

        var idealRise = inletTemperatureK * (Math.Pow(pressureRatio, (gamma - 1.0) / gamma) - 1.0);
        var mapRise = idealRise / mapEfficiency;
        var rawOutlet = inletTemperatureK + mapRise;

        // The stand's own heat flux, solved at the SAME aerodynamic duty. Mean
        // air temperature through the housing is taken across the stage, which
        // is itself a function of the answer — hence one fixed-point pass, which
        // converges immediately because the heat term is a small part of the rise.
        var standHeat = HeatFlux(
            condition.TurbineInletK, inletTemperatureK, rawOutlet, condition.Conditions, thermal);

        var aeroRise = Math.Max(1e-6, mapRise - (standHeat / (massFlowKgPerS * cp)));
        var aeroEfficiency = Math.Min(1.0, idealRise / aeroRise);
        var adiabaticOutlet = inletTemperatureK + aeroRise;

        var engineHeat = HeatFlux(
            engineTurbineInletK, inletTemperatureK, adiabaticOutlet, engineEnvironment, thermal);

        var diabaticOutlet = adiabaticOutlet + (engineHeat / (massFlowKgPerS * cp));

        return new CompressorOutletPrediction(
            rawOutlet,
            adiabaticOutlet,
            diabaticOutlet,
            aeroEfficiency,
            idealRise / (diabaticOutlet - inletTemperatureK),
            standHeat,
            engineHeat);
    }

    /// <summary>Compressor-side heat flux at a held operating point, W.</summary>
    private static double HeatFlux(
        double turbineInletK, double inletK, double outletK,
        TurboEnvironment environment, TurboThermalProperties properties)
    {
        var model = new TurboThermalModel(properties);
        var state = model.SolveSteady(turbineInletK, 0.5 * (inletK + outletK), environment);
        return state.CompressorAirHeatW;
    }
}
