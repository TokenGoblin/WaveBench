using System.Numerics;
using WaveBench.Acoustics;

namespace WaveBench.Boost.Acoustics;

/// <summary>
/// The turbine as a two-port in the exhaust TMM (plan §4.8, §3.3): an area
/// restriction from the last duct into the rotor throat, plus a resistive
/// term for the acoustic energy the rotor's work extraction removes from the
/// wave — the "partly-anechoic termination character" the plan asks for.
///
/// <b>The reactive (inertial) part</b> is the same piston-in-baffle end
/// correction <see cref="WaveBench.Acoustics.AreaDiscontinuityElement"/> uses
/// (Pierce, "Acoustics", the classical low-ka approximation cited there):
/// L_a = ρ·δ/S_throat, δ ≈ 0.6·r_throat·(1 − S_throat/S_duct).
///
/// <b>The resistive part</b> is the standard quasi-steady flow resistance of
/// an orifice/restriction under a mean-flow bias (see e.g. Munjal, "Acoustics
/// of Ducts and Mufflers," 2nd ed., Wiley 2014, ch. 2 — the linearised
/// pressure-drop-vs-velocity-perturbation relation used throughout muffler
/// TMM work), R₀ = ρ·Ū/S_throat with Ū the mean throat velocity. A rotor that
/// is also extracting shaft work is a materially more dissipative restriction
/// than a bare nozzle of the same throat area — real unsteady turbine
/// behaviour under pulsating flow is well outside a quasi-steady quasi-linear
/// model (Costall &amp; Martinez-Botas, ASME GT2007-28317; Chiong, Rajoo,
/// Martinez-Botas &amp; Costall, "Unsteady Performance of a Nozzled and
/// Nozzleless Twin-Entry Turbine," Energy Conversion and Management 57,
/// 2012). <see cref="DissipationFactor"/> scales R₀ up to stand in for that
/// extra loss; it is NOT derived from a quantified extraction correlation —
/// it is an explicit, exposed calibration constant (the same honesty
/// discipline as <c>ScrollPairing</c>'s twin-scroll admission coefficient),
/// pending a measured turbine transmission-loss dataset to fit it against.
/// </summary>
/// <param name="UpstreamAreaM2">Cross-section of the duct feeding the rotor, m².</param>
/// <param name="RotorThroatAreaM2">Effective rotor throat/nozzle area, m².</param>
/// <param name="MeanMassFlowKgPerS">Mean mass flow through the rotor, kg/s.</param>
/// <param name="DissipationFactor">
/// Multiplier on the bare quasi-steady orifice resistance standing in for
/// work-extraction loss. Default 2.0 (roughly double a plain nozzle's
/// quasi-steady loss) — an engineering placeholder, exposed for calibration.
/// </param>
public sealed record TurbineFourPoleElement(
    double UpstreamAreaM2, double RotorThroatAreaM2, double MeanMassFlowKgPerS, double DissipationFactor = 2.0)
    : IAcousticElement
{
    public FourPole Matrix(double frequency, AcousticMedium medium)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UpstreamAreaM2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RotorThroatAreaM2);
        ArgumentOutOfRangeException.ThrowIfNegative(MeanMassFlowKgPerS);
        ArgumentOutOfRangeException.ThrowIfNegative(DissipationFactor);

        var rThroat = Math.Sqrt(RotorThroatAreaM2 / Math.PI);
        var delta = 0.6 * rThroat * (1.0 - Math.Min(1.0, RotorThroatAreaM2 / UpstreamAreaM2));
        var inertance = medium.Density * delta / RotorThroatAreaM2;
        var omega = 2.0 * Math.PI * frequency;

        var meanThroatVelocity = MeanMassFlowKgPerS / (medium.Density * RotorThroatAreaM2);
        var resistance = DissipationFactor * medium.Density * meanThroatVelocity / RotorThroatAreaM2;

        var z = new Complex(resistance, omega * inertance);
        return FourPole.SeriesImpedance(z);
    }
}
