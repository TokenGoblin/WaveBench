namespace WaveBench.Boost.Engine;

/// <summary>The state of a supercharger at one operating point.</summary>
/// <param name="MassFlowKgPerS">Air delivered.</param>
/// <param name="PressureRatio">Delivered pressure ratio.</param>
/// <param name="OutletTemperatureK">Charge temperature leaving the blower.</param>
/// <param name="ShaftPowerW">Power taken off the crank. It comes straight out of engine torque.</param>
/// <param name="AdiabaticEfficiency">Isentropic efficiency implied by the outlet temperature.</param>
public readonly record struct SuperchargerPoint(
    double MassFlowKgPerS,
    double PressureRatio,
    double OutletTemperatureK,
    double ShaftPowerW,
    double AdiabaticEfficiency);

/// <summary>
/// A positive-displacement supercharger — Roots or screw (plan §4.5).
///
/// <b>The difference between them is one number, and it is the whole story.</b>
/// A Roots blower has no internal compression: it carries a fixed volume of air
/// at inlet pressure round to the outlet and it is the outlet that compresses
/// it, isochorically, by back-flow. A screw compressor has a built-in volume
/// ratio and does the compression internally, isentropically, before the port
/// opens. Same pressure ratio, same flow, and the Roots delivers hotter charge —
/// which is why a Roots car needs more intercooler than a screw car for the same
/// boost.
///
/// Roots outlet temperature follows the isochoric-then-isobaric path:
/// <code>w = v₁·(p₂ − p₁)</code> of pure displacement work with no expansion
/// against it, against the screw's isentropic <code>c_p·T₁·(PR^κ − 1)</code>.
/// </summary>
public sealed record PositiveDisplacementBlower
{
    /// <summary>Swept volume per blower revolution, m³.</summary>
    public required double DisplacementPerRevM3 { get; init; }

    /// <summary>
    /// Internal (built-in) volume ratio. 1 for a Roots — no internal
    /// compression at all — and 1.6–2.2 for a screw.
    /// </summary>
    public double InternalVolumeRatio { get; init; } = 1.0;

    /// <summary>Blower speed ÷ crank speed.</summary>
    public double DriveRatio { get; init; } = 2.0;

    /// <summary>
    /// Volumetric efficiency: how much of the swept volume actually leaves,
    /// after internal leakage past the rotors. It falls at low speed (more time
    /// to leak) and at high pressure ratio (more to leak against).
    /// </summary>
    public double VolumetricEfficiencyAt(double blowerRpm, double pressureRatio)
    {
        var slip = 0.06 * (pressureRatio - 1.0) * Math.Sqrt(6000.0 / Math.Max(blowerRpm, 500.0));
        return Math.Clamp(1.0 - slip, 0.30, 0.99);
    }

    /// <summary>
    /// Rotor efficiency: friction and leakage reheat, lumped. It is applied to
    /// the GAS as well as to the shaft, because rotor losses end up in the
    /// charge — which is why a real Roots runs nearer 65% adiabatic than the
    /// 80% the ideal isochoric cycle would give at the same pressure ratio.
    /// </summary>
    public double MechanicalEfficiency { get; init; } = 0.82;

    /// <summary>Belt losses on the way from the crank.</summary>
    public double BeltEfficiency { get; init; } = 0.95;

    /// <summary>A Roots blower: no internal compression.</summary>
    public bool IsRoots => InternalVolumeRatio <= 1.0 + 1e-9;

    /// <summary>
    /// Solve the blower at an engine speed and a demanded pressure ratio.
    /// </summary>
    public SuperchargerPoint Solve(
        double engineRpm,
        double pressureRatio,
        double inletTemperatureK = 298.15,
        double inletPressurePa = 101_325.0,
        double gamma = 1.4,
        double gasConstant = 287.05)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pressureRatio, 1.0);

        var blowerRpm = engineRpm * DriveRatio;
        var volumetric = VolumetricEfficiencyAt(blowerRpm, pressureRatio);
        var inletDensity = inletPressurePa / (gasConstant * inletTemperatureK);

        var flow = DisplacementPerRevM3 * (blowerRpm / 60.0) * volumetric * inletDensity;
        var cp = gamma * gasConstant / (gamma - 1.0);
        var kappa = (gamma - 1.0) / gamma;

        // Specific work. Both machines pay the isentropic work up to their own
        // built-in volume ratio; the Roots pays the rest as displacement against
        // the outlet, which is where the extra heat comes from.
        var internalPressureRatio = Math.Pow(InternalVolumeRatio, gamma);
        var internalWork = cp * inletTemperatureK * (Math.Pow(internalPressureRatio, kappa) - 1.0);

        // What is left of the pressure rise after the internal compression is
        // taken isochorically at the port: w = v·Δp, with v the specific volume
        // the gas has reached inside the machine (v₁/V_i).
        //
        // The term is deliberately NOT clamped at zero. A screw whose built-in
        // ratio exceeds the demanded one over-compresses and then blows down
        // through the port, and the negative displacement work is exactly that
        // partial recovery. Clamping it would charge the full over-compression
        // and make every screw look worse than a Roots below its design point.
        var internalSpecificVolume = 1.0 / (inletDensity * InternalVolumeRatio);
        var displacementWork = internalSpecificVolume * (pressureRatio - internalPressureRatio) * inletPressurePa;

        // Rotor losses end up in the gas, so the charge is heated by the actual
        // work and not by the ideal work.
        var specificWork = (internalWork + displacementWork) / MechanicalEfficiency;

        var outlet = inletTemperatureK + (specificWork / cp);
        var isentropicWork = cp * inletTemperatureK * (Math.Pow(pressureRatio, kappa) - 1.0);
        var efficiency = specificWork > 0 ? isentropicWork / specificWork : 1.0;

        // Parasitic power comes off crank torque directly. That is not a
        // bookkeeping detail: it is why a supercharged engine's torque curve has
        // a different shape from a turbocharged one at the same boost.
        var shaftPower = flow * specificWork / BeltEfficiency;

        return new SuperchargerPoint(flow, pressureRatio, outlet, shaftPower, Math.Min(1.0, efficiency));
    }
}

/// <summary>
/// A centrifugal supercharger (plan §4.5): a compressor map on a fixed drive
/// ratio.
///
/// The consequence is a fundamentally different torque curve. Head goes as tip
/// speed squared and tip speed is proportional to engine speed, so boost tracks
/// rpm² — almost nothing at 2000 rpm, everything at redline. Placing that
/// against a turbo at the same peak boost is the comparison worth making, and
/// it is why the two feel nothing alike.
/// </summary>
public sealed record CentrifugalSupercharger
{
    public required CompressorMap Map { get; init; }

    /// <summary>Impeller speed ÷ crank speed. Step-up drives run 3–5, plus a step-up gearbox inside.</summary>
    public required double DriveRatio { get; init; }

    /// <summary>Drive losses from crank to impeller.</summary>
    public double DriveEfficiency { get; init; } = 0.93;

    /// <summary>
    /// Solve at an engine speed and a demanded air flow. Unlike a turbo there is
    /// no shaft balance to find: the crank sets the speed.
    /// </summary>
    public SuperchargerPoint Solve(
        double engineRpm,
        double massFlowKgPerS,
        double inletTemperatureK = 298.15,
        double inletPressureKPa = 101.325)
    {
        var impellerRpm = engineRpm * DriveRatio;
        var point = CompressorModel.Solve(Map, massFlowKgPerS, impellerRpm, inletTemperatureK, inletPressureKPa);

        return new SuperchargerPoint(
            massFlowKgPerS,
            point.PressureRatio,
            point.OutletTemperatureK,
            point.PowerW / DriveEfficiency,
            point.Efficiency);
    }
}
