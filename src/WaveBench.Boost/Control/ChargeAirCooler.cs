namespace WaveBench.Boost.Control;

/// <summary>Outlet state of a charge air cooler at one instant.</summary>
/// <param name="OutletTemperatureK">Charge temperature leaving the core.</param>
/// <param name="PressureDropPa">Loss across the core at this flow.</param>
/// <param name="HeatRejectedW">Heat into the core plus coolant.</param>
/// <param name="CoreTemperatureK">Core metal temperature — the state that makes a second pull differ from the first.</param>
/// <param name="EffectivenessNow">
/// Instantaneous effectiveness against the coolant. Falls below the rated value
/// while the core is soaked, which is the whole reason the core has a mass here.
/// </param>
public readonly record struct ChargeCoolerState(
    double OutletTemperatureK,
    double PressureDropPa,
    double HeatRejectedW,
    double CoreTemperatureK,
    double EffectivenessNow);

/// <summary>
/// Charge air cooler with thermal mass (plan §4.4).
///
/// <b>The mass is the point.</b> A steady-state ε-NTU cooler gives the same
/// answer on the tenth dyno pull as on the first, and that is not what happens:
/// the core soaks, its metal temperature climbs toward the charge temperature,
/// and intake air temperature climbs with it. Teams discover this as "the car
/// was slower in the afternoon". Modelling the core as a lumped mass between
/// the charge and the coolant reproduces it without a heat-exchanger mesh.
///
/// Air-to-air and air-to-water differ only in the coolant side: air-to-air sees
/// ambient at vehicle speed, air-to-water sees a circuit with its own mass and
/// its own heat exchanger, which is why it soaks more slowly and recovers less
/// quickly.
/// </summary>
public sealed class ChargeAirCooler
{
    /// <summary>
    /// Rated effectiveness, ε = (T_in − T_out)/(T_in − T_coolant), at the rated
    /// flow. Typical air-to-air cores run 0.65–0.80 at moderate flow and fall
    /// as flow rises.
    /// </summary>
    public double RatedEffectiveness { get; init; } = 0.75;

    /// <summary>Flow the rated effectiveness applies at, kg/s.</summary>
    public double RatedFlowKgPerS { get; init; } = 0.15;

    /// <summary>Pressure drop at the rated flow, Pa. Scales with flow squared.</summary>
    public double RatedPressureDropPa { get; init; } = 8_000.0;

    /// <summary>Thermal capacity of the core, J/K. A 6 kg aluminium core is about 5400.</summary>
    public double CoreCapacityJPerK { get; init; } = 5_400.0;

    /// <summary>
    /// Conductance from the core metal to the coolant stream, W/K.
    ///
    /// <b>This is where the vehicle's speed lives, and it dominates heat soak.</b>
    /// An air-to-air core with 100 km/h of ram through it rejects several
    /// hundred watts per kelvin; the same core on a chassis dyno with a fan
    /// pointed at it rejects a fraction of that, which is exactly why a car that
    /// pulls cleanly on the road heat-soaks on the rollers. Set it for the
    /// installation being modelled — the default describes a moving vehicle.
    /// </summary>
    public double CoreToCoolantConductance { get; init; } = 400.0;

    /// <summary>The same core on a chassis dyno: no ram air, one fan.</summary>
    public ChargeAirCooler OnADyno() => new()
    {
        RatedEffectiveness = RatedEffectiveness,
        RatedFlowKgPerS = RatedFlowKgPerS,
        RatedPressureDropPa = RatedPressureDropPa,
        CoreCapacityJPerK = CoreCapacityJPerK,
        CoreToCoolantConductance = 110.0,
    };

    /// <summary>Core metal temperature, K.</summary>
    public double CoreTemperatureK { get; private set; } = 298.15;

    public void Reset(double coreTemperatureK) => CoreTemperatureK = coreTemperatureK;

    /// <summary>
    /// Advance the cooler one timestep.
    /// </summary>
    /// <param name="dt">Timestep, s.</param>
    /// <param name="inletTemperatureK">Charge temperature from the compressor.</param>
    /// <param name="massFlowKgPerS">Charge flow.</param>
    /// <param name="coolantTemperatureK">Ambient for air-to-air; circuit temperature for air-to-water.</param>
    /// <param name="cp">c_p of the charge, J/kg·K.</param>
    public ChargeCoolerState Step(
        double dt,
        double inletTemperatureK,
        double massFlowKgPerS,
        double coolantTemperatureK,
        double cp = 1005.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dt);
        ArgumentOutOfRangeException.ThrowIfNegative(massFlowKgPerS);

        // Effectiveness falls with flow: the same core, less residence time.
        // A ^-0.2 exponent is the usual shape for a compact plate-fin core over
        // the range an engine actually uses; it is a fitted shape, not a
        // derivation, and RatedEffectiveness is where a measured core is entered.
        var flowRatio = RatedFlowKgPerS > 0 ? Math.Max(massFlowKgPerS, 1e-6) / RatedFlowKgPerS : 1.0;
        var effectiveness = Math.Clamp(RatedEffectiveness * Math.Pow(flowRatio, -0.2), 0.05, 0.98);

        // The charge exchanges with the CORE, not with the coolant directly.
        // That one substitution is what puts heat soak into the model: when the
        // core is hot, the charge cannot be cooled below it however cold the
        // ambient is.
        var outlet = inletTemperatureK - (effectiveness * (inletTemperatureK - CoreTemperatureK));
        var toCore = massFlowKgPerS * cp * (inletTemperatureK - outlet);
        var toCoolant = CoreToCoolantConductance * (CoreTemperatureK - coolantTemperatureK);

        CoreTemperatureK += (toCore - toCoolant) / CoreCapacityJPerK * dt;

        var drop = RatedPressureDropPa * flowRatio * flowRatio;

        return new ChargeCoolerState(outlet, drop, toCore, CoreTemperatureK, effectiveness);
    }

    /// <summary>
    /// The steady answer, for comparison: what a cooler with no thermal mass
    /// would report. The gap between this and a soaked core is what a
    /// repeated-run transient is for.
    /// </summary>
    public double SteadyOutletK(double inletTemperatureK, double massFlowKgPerS, double coolantTemperatureK)
    {
        var flowRatio = RatedFlowKgPerS > 0 ? Math.Max(massFlowKgPerS, 1e-6) / RatedFlowKgPerS : 1.0;
        var effectiveness = Math.Clamp(RatedEffectiveness * Math.Pow(flowRatio, -0.2), 0.05, 0.98);
        return inletTemperatureK - (effectiveness * (inletTemperatureK - coolantTemperatureK));
    }
}
