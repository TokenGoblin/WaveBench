namespace WaveBench.Boost.Acoustics;

/// <summary>
/// The forced-induction noise sources of plan §4.8's "new sources" table,
/// other than the turbine four-pole (<see cref="TurbineFourPoleElement"/>)
/// and surge flutter (<see cref="SurgeFlutterSource"/>).
///
/// Turbo wheel geometry (blade/splitter count, tip speed) is taken as an
/// explicit parameter to every method here rather than stored on
/// <c>Turbocharger</c> — that schema carries no wheel geometry today, and
/// adding it for these few callers would be a bigger, riskier change (it
/// touches the map round-trip file format) than the acoustic estimate needs.
///
/// <b>Honesty about what is and is not cited.</b> Blade-pass frequency is a
/// textbook result (Tyler &amp; Sofrin, cited below) and is exact arithmetic.
/// The broadband sources — whoosh, wastegate flow noise, blow-off level — are
/// NOT: plan §4.8 itself lists them without a citation, acknowledging they
/// are more phenomenological than the tonal sources. Rather than invent a
/// paper number for them, each is built on the one broadband-noise scaling
/// law that IS real and general (Curle's dipole extension of Lighthill's
/// aeroacoustic theory, both cited below), with every level-calibration
/// constant marked in its own doc comment as exposed and unfitted — the same
/// discipline <c>ScrollPairing</c>'s admission coefficient already uses.
/// </summary>
public static class TurboAcousticSources
{
    /// <summary>
    /// Compressor blade-pass frequency, Hz: f = (N/60)·B, N in rpm, B the
    /// full blade count (Tyler, J. M. &amp; Sofrin, T. G., "Axial Flow
    /// Compressor Noise Studies," SAE Technical Paper 620532, 1962 — the
    /// standard reference for rotor blade-passing tones in turbomachinery).
    /// </summary>
    public static double CompressorBladePassFrequency(double turboRpm, int bladeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(turboRpm);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bladeCount);
        return turboRpm / 60.0 * bladeCount;
    }

    /// <summary>
    /// The blade-pass tonal family, Hz: harmonics of the full-blade count
    /// plus, when the wheel carries splitter blades, the extra tone at the
    /// combined (full + splitter) count that a mixed-pitch rotor produces
    /// (Tyler &amp; Sofrin's spinning-mode counting — a rotor with unequally
    /// spaced blade sets radiates at both the fundamental blade count and the
    /// total blade-passing count).
    /// </summary>
    public static IReadOnlyList<double> BladePassHarmonics(
        double turboRpm, int bladeCount, int splitterCount, int harmonics = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(harmonics);
        var shaftHz = turboRpm / 60.0;

        var tones = new List<double>();
        for (var k = 1; k <= harmonics; k++)
        {
            tones.Add(k * bladeCount * shaftHz);
        }

        if (splitterCount > 0)
        {
            tones.Add((bladeCount + splitterCount) * shaftHz);
        }

        return tones;
    }

    /// <summary>
    /// Whoosh: broadband intake-side noise from turbulent flow over the
    /// compressor wheel and inducer, dB relative to <paramref name="referenceLevelDb"/>
    /// at <paramref name="referenceTipSpeedMPerS"/> and zero incidence.
    ///
    /// Scaled with tip speed to the sixth power — Curle, N. "The Influence of
    /// Solid Boundaries upon Aerodynamic Sound." Proc. R. Soc. Lond. A 231,
    /// 505-514, 1955, extending Lighthill's U⁸ free-quadrupole scaling
    /// (Lighthill, M. J. "On Sound Generated Aerodynamically I: General
    /// Theory." Proc. R. Soc. Lond. A 211, 564-587, 1952) to the dipole
    /// character a solid blade/wall boundary adds, which is the closer
    /// analogy for flow past a compressor wheel than a free jet — plus an
    /// incidence penalty away from the design flow angle.
    ///
    /// <b>The incidence-penalty coefficient (2.5 dB per degree²/100) is an
    /// exposed, unfitted engineering placeholder</b>, not derived from the
    /// scaling law above; only the velocity exponent carries a citation.
    /// </summary>
    public static double WhooshLevel(
        double tipSpeedMPerS, double referenceTipSpeedMPerS, double incidenceDeg,
        double referenceLevelDb = 70.0, double velocityExponent = 6.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tipSpeedMPerS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceTipSpeedMPerS);

        var velocityTerm = velocityExponent * 10.0 * Math.Log10(tipSpeedMPerS / referenceTipSpeedMPerS);
        var incidencePenalty = 2.5 * (incidenceDeg * incidenceDeg) / 100.0;

        return referenceLevelDb + velocityTerm + incidencePenalty;
    }

    /// <summary>
    /// Wastegate flow noise, dB relative to <paramref name="referenceLevelDb"/>
    /// at <paramref name="referenceFlowKgPerS"/>: silent when shut
    /// (<see cref="double.NegativeInfinity"/>, no diverted flow at all), then
    /// the same Curle-dipole velocity-scaling shape as
    /// <see cref="WhooshLevel"/> applied to the diverted mass flow as a proxy
    /// for gap velocity. Exposed, unfitted calibration constant: the default
    /// exponent and reference level.
    /// </summary>
    public static double WastegateFlowNoiseLevel(
        double divertedFlowKgPerS, double referenceFlowKgPerS,
        double referenceLevelDb = 65.0, double flowExponent = 6.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(divertedFlowKgPerS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceFlowKgPerS);

        if (divertedFlowKgPerS <= 0.0)
        {
            return double.NegativeInfinity;
        }

        return referenceLevelDb + (flowExponent * 10.0 * Math.Log10(divertedFlowKgPerS / referenceFlowKgPerS));
    }

    /// <summary>
    /// Blow-off/recirculation valve event level, dB relative to
    /// <paramref name="referenceLevelDb"/> at fully open: silent below
    /// <paramref name="crackingPressurePa"/> (the valve has not opened),
    /// rising with valve position between cracking and fully-open pressure —
    /// a phenomenological, transient event model (plan §4.8), not a steady
    /// scaling law. Exposed, unfitted calibration constant: the level rise
    /// across the position range.
    /// </summary>
    public static double BlowOffEventLevel(
        double pressureDifferentialPa, double crackingPressurePa, double fullOpenPressurePa,
        double referenceLevelDb = 75.0, double rangeDb = 15.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pressureDifferentialPa);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fullOpenPressurePa - crackingPressurePa);

        if (pressureDifferentialPa <= crackingPressurePa)
        {
            return double.NegativeInfinity;
        }

        var position = Math.Clamp(
            (pressureDifferentialPa - crackingPressurePa) / (fullOpenPressurePa - crackingPressurePa), 0.0, 1.0);

        return referenceLevelDb - rangeDb + (rangeDb * position);
    }
}
