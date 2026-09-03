using WaveBench.Core.EngineModel;

namespace WaveBench.Boost.Unsteady;

/// <summary>Whether a surging compression system oscillates gently or fully reverses flow each cycle.</summary>
public enum SurgeClassification
{
    /// <summary>B below Greitzer's critical value: bounded, roughly sinusoidal flow oscillation, no reversal.</summary>
    Mild,

    /// <summary>B above critical: large-amplitude relaxation oscillation with flow reversal (Moore &amp; Greitzer 1986).</summary>
    Deep,
}

/// <summary>
/// A compression system's Greitzer parameters at one operating point.
/// </summary>
/// <param name="BParameter">Greitzer's B (dimensionless).</param>
/// <param name="HelmholtzFrequencyHz">
/// The compression system's own natural (Helmholtz) frequency — see
/// <see cref="GreitzerSurgeModel.Evaluate"/>.
/// </param>
/// <param name="SurgeFrequencyHz">
/// The frequency the plenum pressure and compressor flow actually oscillate
/// at once surge is under way. Equal to <see cref="HelmholtzFrequencyHz"/>:
/// in Greitzer's own nondimensionalisation (time scaled by the system's
/// Helmholtz frequency) the surge-cycle period is O(1), so — to the accuracy
/// this bracket is meant to give a designer, and exactly for deep surge where
/// the limit cycle is Helmholtz-frequency-dominated — the dimensional surge
/// frequency IS the Helmholtz frequency. This is what plan §4.8 means by
/// "predicted, not sampled; it falls out of the surge model": the number
/// moves whenever the plenum volume or duct geometry moves, because it is
/// the same call as <see cref="QuickEstimate.HelmholtzFrequency"/>, not a
/// fitted constant.
/// </param>
/// <param name="Classification">Mild or deep, by <see cref="GreitzerSurgeModel.CriticalB"/>.</param>
public sealed record GreitzerSurgeResult(
    double BParameter,
    double HelmholtzFrequencyHz,
    double SurgeFrequencyHz,
    SurgeClassification Classification);

/// <summary>
/// The Greitzer compression-system model (plan §4.8, validation item #19):
/// classifies whether a compressor pushed into surge oscillates mildly or
/// deeply, and gives the frequency of that oscillation.
///
/// <b>This is new machinery, not a rename of <see cref="CompressorPointResult.SurgeMarginPercent"/>.</b>
/// <c>SurgeMarginPercent</c> is a static distance from the current operating
/// point to the map's surge line — it answers "has this point crossed into
/// surge." This class answers the different question Greitzer's theory
/// answers: once it has crossed, what does the resulting limit cycle look
/// like. The two are meant to be used together — a caller checks
/// <c>SurgeMarginPercent &lt; 0</c> (or <c>InSurge</c>) first, then asks this
/// model what character and frequency the resulting flutter has.
///
/// Sources: Greitzer, E. M. "Surge and Rotating Stall in Axial Flow
/// Compressors, Part I: Theoretical Compression System Model." ASME J. Eng.
/// Power 98(2), 190-198, 1976 — the B parameter,
/// B = U/(2·ω_H·L_c) = (U/2a)·√(V_p/(A_c·L_c)), and its critical value near
/// 0.8 separating mild, bounded oscillation from deep surge with flow
/// reversal. Moore, F. K. &amp; Greitzer, E. M. "A Theory of Post-Stall
/// Transients in Axial Compression Systems: Part I." ASME J. Eng. Gas
/// Turbines Power 108(1), 68-76, 1986 — the deep-surge limit cycle
/// oscillating near the compression system's own Helmholtz frequency.
/// Valid for a single-plenum compression system with a well-defined duct
/// area/length between the compressor and the plenum; not a rotating-stall
/// model.
/// </summary>
public static class GreitzerSurgeModel
{
    /// <summary>Greitzer's own reported mild/deep transition (1976 Part I).</summary>
    public const double CriticalB = 0.8;

    /// <summary>
    /// Evaluate the compression system's Greitzer parameters.
    /// </summary>
    /// <param name="wheelTipSpeedMPerS">Compressor wheel tip speed U, m/s.</param>
    /// <param name="soundSpeedMPerS">Local sound speed a, m/s.</param>
    /// <param name="plenumVolumeM3">Downstream plenum volume V_p, m³.</param>
    /// <param name="ductAreaM2">Compressor-to-plenum duct cross-section A_c, m².</param>
    /// <param name="effectiveDuctLengthM">Compressor-to-plenum effective duct length L_c, m.</param>
    public static GreitzerSurgeResult Evaluate(
        double wheelTipSpeedMPerS,
        double soundSpeedMPerS,
        double plenumVolumeM3,
        double ductAreaM2,
        double effectiveDuctLengthM)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wheelTipSpeedMPerS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(soundSpeedMPerS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plenumVolumeM3);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ductAreaM2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effectiveDuctLengthM);

        // Never re-derived: the surge model's natural frequency IS the plan
        // §2.10 Helmholtz-resonance estimate, called directly.
        var helmholtzHz = QuickEstimate.HelmholtzFrequency(
            soundSpeedMPerS, ductAreaM2, plenumVolumeM3, effectiveDuctLengthM);
        var omegaH = 2.0 * Math.PI * helmholtzHz;

        var b = wheelTipSpeedMPerS / (2.0 * omegaH * effectiveDuctLengthM);
        var classification = b < CriticalB ? SurgeClassification.Mild : SurgeClassification.Deep;

        return new GreitzerSurgeResult(b, helmholtzHz, helmholtzHz, classification);
    }
}
