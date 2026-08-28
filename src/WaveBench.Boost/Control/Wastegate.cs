using WaveBench.Boost.Unsteady;

namespace WaveBench.Boost.Control;

/// <summary>Where the wastegate sits, which decides what it costs beyond the flow it diverts.</summary>
public enum WastegatePlacement
{
    /// <summary>
    /// A port in the turbine housing, upstream of the rotor. Cheap and compact —
    /// and on a twin-scroll housing it is usually a single port bridging both
    /// scrolls, which is why it partly defeats the pairing at high load.
    /// </summary>
    Internal,

    /// <summary>A separate valve on its own take-off, with independent ducting per scroll if fitted.</summary>
    External,
}

/// <summary>
/// The wastegate (plan §4.3): a parallel path around the rotor.
///
/// <b>The scroll-division loss is modelled, and that matters.</b> An internally
/// gated twin-scroll housing has one port opening into both scrolls, so cracking
/// it open connects them. From that moment the pulse separation the firing-order
/// pairing was designed to preserve starts to leak away — at exactly the high-load
/// condition where the gate is open. Omitting this overstates the twin-scroll
/// benefit precisely where the user is looking at it.
/// </summary>
public sealed class Wastegate
{
    /// <summary>Effective flow area with the valve fully open, m².</summary>
    public required double FullOpenAreaM2 { get; init; }

    public WastegatePlacement Placement { get; init; } = WastegatePlacement.Internal;

    /// <summary>Discharge coefficient of the port. 0.7–0.85 for a poppet in a housing.</summary>
    public double DischargeCoefficient { get; init; } = 0.78;

    /// <summary>
    /// How much of the scroll division survives with the gate fully open, for an
    /// internal gate. 0 would mean the scrolls are completely merged; 1 would
    /// mean the gate does not connect them at all, which an internal gate on a
    /// shared port cannot manage. Exposed because it depends entirely on how the
    /// port and divider wall are cast.
    /// </summary>
    public double DivisionRetainedWhenOpen { get; init; } = 0.35;

    /// <summary>Valve position, 0 shut to 1 fully open.</summary>
    public double Position { get; set; }

    /// <summary>Open area at the current position, m².</summary>
    public double OpenAreaM2 => DischargeCoefficient * FullOpenAreaM2 * Math.Clamp(Position, 0.0, 1.0);

    /// <summary>
    /// How much of the twin-scroll separation survives at the current position.
    /// 1 shut; falling toward <see cref="DivisionRetainedWhenOpen"/> as an
    /// internal gate opens. An external gate leaves it alone.
    /// </summary>
    public double ScrollDivisionRetained => Placement == WastegatePlacement.External
        ? 1.0
        : 1.0 - (Math.Clamp(Position, 0.0, 1.0) * (1.0 - DivisionRetainedWhenOpen));

    /// <summary>
    /// Apply the gate to a turbine stage: the diverted flow reduces what reaches
    /// the rotor, and on an internal twin-scroll gate the scrolls partly merge.
    /// </summary>
    /// <param name="stage">The stage to act on.</param>
    /// <param name="rotorEffectiveAreaM2">
    /// The rotor's own effective throat area, needed to split the flow between
    /// the two parallel paths. Both see the same pressure ratio, so the split is
    /// by area.
    /// </param>
    public void Apply(TurbineStage stage, double rotorEffectiveAreaM2)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rotorEffectiveAreaM2);

        // Rotor and gate are two nozzles across the same pressure difference, so
        // the rotor keeps the share of the total flow its area represents.
        var rotorShare = rotorEffectiveAreaM2 / (rotorEffectiveAreaM2 + OpenAreaM2);

        foreach (var entry in stage.Entries)
        {
            entry.Rotor.CapacityScale = rotorShare;
        }
    }

    /// <summary>
    /// Flow through the gate itself, kg/s — wasted energy, and the source of the
    /// gate's own noise in Phase 15.
    /// </summary>
    public double DivertedFlow(double totalFlowKgPerS, double rotorEffectiveAreaM2)
    {
        var gateArea = OpenAreaM2;
        return gateArea <= 0 ? 0.0 : totalFlowKgPerS * gateArea / (rotorEffectiveAreaM2 + gateArea);
    }
}

/// <summary>
/// A blow-off or recirculation valve (plan §4.4).
///
/// It exists for one event: a shut throttle in front of a spinning compressor.
/// Without it the compressor is pushed left across its map into surge, and the
/// characteristic flutter that follows is a surge cycle, not a design feature.
/// </summary>
public sealed class BlowOffValve
{
    /// <summary>Effective area when fully open, m².</summary>
    public required double FullOpenAreaM2 { get; init; }

    /// <summary>Pressure difference across the valve at which it starts to open, Pa.</summary>
    public double CrackingPressurePa { get; init; } = 30_000.0;

    /// <summary>Pressure difference at which it is fully open, Pa.</summary>
    public double FullOpenPressurePa { get; init; } = 80_000.0;

    /// <summary>
    /// Recirculating returns the air to the compressor inlet; venting dumps it.
    /// Recirculating keeps the compressor's own mass flow up, which is what
    /// actually holds the operating point away from surge — venting only helps
    /// the plenum.
    /// </summary>
    public bool Recirculates { get; init; } = true;

    /// <summary>Open fraction at a given pressure difference across the valve.</summary>
    public double Position(double differentialPa)
    {
        if (differentialPa <= CrackingPressurePa)
        {
            return 0.0;
        }

        return Math.Clamp(
            (differentialPa - CrackingPressurePa) / (FullOpenPressurePa - CrackingPressurePa), 0.0, 1.0);
    }

    public double OpenAreaM2(double differentialPa) => FullOpenAreaM2 * Position(differentialPa);
}
