namespace WaveBench.Analysis;

/// <summary>
/// One point of a pressure history split into the two waves superposed there.
/// </summary>
/// <param name="RightwardRatio">
/// Blair's pressure amplitude ratio X_r of the rightward-running wave.
/// 1 is undisturbed; above 1 is compression, below 1 is expansion.
/// </param>
/// <param name="LeftwardRatio">The same for the leftward-running wave, X_l.</param>
/// <param name="RightwardPressurePa">Rightward component as an absolute pressure.</param>
/// <param name="LeftwardPressurePa">Leftward component as an absolute pressure.</param>
public readonly record struct WaveComponents(
    double RightwardRatio,
    double LeftwardRatio,
    double RightwardPressurePa,
    double LeftwardPressurePa);

/// <summary>Which way a wave is running.</summary>
public enum WaveSense
{
    /// <summary>Toward +x — away from the cylinder, in the exhaust convention used here.</summary>
    Rightward,

    /// <summary>Toward −x — back toward the cylinder. The reflection.</summary>
    Leftward,
}

/// <summary>
/// A wave arriving at a probe: when, how strong, and which kind.
/// </summary>
/// <param name="AngleDeg">Cycle angle of the peak, degrees.</param>
/// <param name="AmplitudeRatio">Blair X at the peak; 1 is undisturbed.</param>
/// <param name="PressurePa">The component's absolute pressure at the peak.</param>
/// <param name="Sense">Which way it was running.</param>
public sealed record WaveArrival(double AngleDeg, double AmplitudeRatio, double PressurePa, WaveSense Sense)
{
    /// <summary>An expansion pulls pressure below the reference; a compression pushes it above.</summary>
    public bool IsExpansion => AmplitudeRatio < 1.0;

    public string Kind => IsExpansion ? "expansion" : "compression";
}

/// <summary>
/// Splits a measured pressure/velocity history into its rightward- and
/// leftward-running components (plan §8.4, Phase 19).
///
/// This is what turns a pressure trace into an answer. A raw trace at a point
/// in a pipe is the superposition of everything passing through it, so "is the
/// reflection back before the valve shuts?" cannot be read off it — the
/// outgoing blowdown and the returning expansion are added together into one
/// squiggle. Separating them is the whole reason the diagram exists.
///
/// <b>Method.</b> Blair's superposition decomposition (Blair, <i>Design and
/// Simulation of Four-Stroke Engines</i>, SAE 1999, §2.2–2.5), which is the
/// finite-amplitude form of the Riemann variables. Writing the pressure
/// amplitude ratio
///
/// <code>X = (p / p_ref)^((γ−1)/2γ)</code>
///
/// superposition of two waves gives <c>X = X_r + X_l − 1</c> and a particle
/// velocity <c>u = (2·a_ref/(γ−1))·(X_r − X_l)</c>. Two equations, two
/// unknowns, so a simultaneous (p, u) pair at ONE point separates the waves
/// with no second transducer and no time delay — which is why 1D codes report
/// it and two-microphone rigs exist to measure it.
///
/// <b>Validity.</b> Isentropic and homentropic: the derivation assumes both
/// waves ride on gas at the same reference state. Across a strong temperature
/// gradient or a contact discontinuity the split degrades, which is exactly
/// where Blair says to use the non-homentropic form. Reported per-point, so a
/// caller can see where it is being pushed.
/// </summary>
public static class WaveDecomposition
{
    /// <summary>
    /// Split one simultaneous (pressure, velocity) sample.
    /// </summary>
    /// <param name="pressurePa">Static pressure at the point, Pa.</param>
    /// <param name="velocity">Signed axial particle velocity, m/s; +x is rightward.</param>
    /// <param name="referencePressurePa">Undisturbed reference pressure, Pa.</param>
    /// <param name="referenceSoundSpeed">Sound speed at the reference state, m/s.</param>
    /// <param name="gamma">Ratio of specific heats at the reference state.</param>
    public static WaveComponents At(
        double pressurePa,
        double velocity,
        double referencePressurePa,
        double referenceSoundSpeed,
        double gamma)
    {
        if (referencePressurePa <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(referencePressurePa), referencePressurePa, "Reference pressure must be positive.");
        }

        if (referenceSoundSpeed <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(referenceSoundSpeed), referenceSoundSpeed, "Reference sound speed must be positive.");
        }

        if (gamma <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "γ must exceed 1.");
        }

        var exponent = (gamma - 1.0) / (2.0 * gamma);
        var x = Math.Pow(Math.Max(pressurePa, 0.0) / referencePressurePa, exponent);

        // X_r + X_l = X + 1 and X_r − X_l = (γ−1)u / (2 a_ref).
        var half = 0.5 * (x + 1.0);
        var skew = (gamma - 1.0) * velocity / (4.0 * referenceSoundSpeed);

        var xr = half + skew;
        var xl = half - skew;

        var inverse = 2.0 * gamma / (gamma - 1.0);

        // A component ratio can only go negative under a velocity so large the
        // homentropic assumption has already failed; clamping keeps the
        // returned pressure real and finite rather than NaN, and the ratio
        // itself is returned unclamped so a caller can see it happened.
        var pr = referencePressurePa * Math.Pow(Math.Max(xr, 0.0), inverse);
        var pl = referencePressurePa * Math.Pow(Math.Max(xl, 0.0), inverse);

        return new WaveComponents(xr, xl, pr, pl);
    }

    /// <summary>
    /// Split a whole history. <paramref name="pressurePa"/> and
    /// <paramref name="velocity"/> must be simultaneous samples of the same
    /// point — this is a pointwise operation, so a one-sample offset between
    /// them shows up as a spurious wave.
    /// </summary>
    public static WaveComponents[] Series(
        ReadOnlySpan<float> pressurePa,
        ReadOnlySpan<float> velocity,
        double referencePressurePa,
        double referenceSoundSpeed,
        double gamma)
    {
        if (pressurePa.Length != velocity.Length)
        {
            throw new ArgumentException(
                "Pressure and velocity histories must be the same length and simultaneously sampled.",
                nameof(velocity));
        }

        var result = new WaveComponents[pressurePa.Length];
        for (var i = 0; i < pressurePa.Length; i++)
        {
            result[i] = At(pressurePa[i], velocity[i], referencePressurePa, referenceSoundSpeed, gamma);
        }

        return result;
    }

    /// <summary>
    /// The strongest arrival of one sense inside an angle window.
    ///
    /// "Strongest" means furthest from undisturbed in the direction asked for:
    /// an expansion search returns the deepest trough, a compression search the
    /// highest peak. Searching for the extremum rather than a threshold
    /// crossing is deliberate — a threshold answers "did something arrive",
    /// and the question the UI asks is "when did the thing arrive".
    /// </summary>
    /// <param name="components">Decomposed history, one sample per angle step.</param>
    /// <param name="anglesDeg">Cycle angle of each sample; same length.</param>
    /// <param name="sense">Which running direction to search.</param>
    /// <param name="expansion">True to find the deepest expansion, false for the strongest compression.</param>
    /// <param name="fromDeg">Window start, cycle degrees. Null searches everything.</param>
    /// <param name="toDeg">Window end, cycle degrees; may wrap past 720.</param>
    public static WaveArrival? Strongest(
        IReadOnlyList<WaveComponents> components,
        IReadOnlyList<double> anglesDeg,
        WaveSense sense,
        bool expansion = true,
        double? fromDeg = null,
        double? toDeg = null)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(anglesDeg);

        if (components.Count != anglesDeg.Count)
        {
            throw new ArgumentException("Components and angles must be the same length.", nameof(anglesDeg));
        }

        WaveArrival? best = null;
        var bestScore = 0.0;

        for (var i = 0; i < components.Count; i++)
        {
            if (!InWindow(anglesDeg[i], fromDeg, toDeg))
            {
                continue;
            }

            var ratio = sense == WaveSense.Rightward
                ? components[i].RightwardRatio
                : components[i].LeftwardRatio;

            // Distance from undisturbed, signed so that only the sought
            // direction can win.
            var score = expansion ? 1.0 - ratio : ratio - 1.0;
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            var pressure = sense == WaveSense.Rightward
                ? components[i].RightwardPressurePa
                : components[i].LeftwardPressurePa;
            best = new WaveArrival(anglesDeg[i], ratio, pressure, sense);
        }

        return best;
    }

    /// <summary>
    /// The plan's §8.4 annotation, in the words it asks for:
    /// <i>"reflected expansion arrives 12° before EVC"</i>.
    ///
    /// Phrased against the event rather than in absolute cycle degrees,
    /// because "arrives at 331°" requires the reader to hold the valve timing
    /// in their head to know whether that is good news.
    /// </summary>
    /// <param name="arrival">What was found.</param>
    /// <param name="eventDeg">Cycle angle of the event to phrase it against.</param>
    /// <param name="eventName">What that event is called, e.g. "EVC".</param>
    public static string Annotate(WaveArrival arrival, double eventDeg, string eventName)
    {
        ArgumentNullException.ThrowIfNull(arrival);

        var direction = arrival.Sense == WaveSense.Leftward ? "reflected " : "";
        var delta = SignedDelta(arrival.AngleDeg, eventDeg);
        var strength = Math.Abs(arrival.AmplitudeRatio - 1.0);

        if (Math.Abs(delta) < 0.5)
        {
            return $"{direction}{arrival.Kind} arrives at {eventName} (X {arrival.AmplitudeRatio:F3})";
        }

        var relation = delta < 0 ? "before" : "after";
        return $"{direction}{arrival.Kind} arrives {Math.Abs(delta):F0}° {relation} {eventName} "
               + $"(X {arrival.AmplitudeRatio:F3}, {strength * 100:F1}% amplitude)";
    }

    /// <summary>
    /// Shortest signed angular distance from <paramref name="eventDeg"/> to
    /// <paramref name="angleDeg"/> on the 720° cycle: negative is before.
    /// </summary>
    private static double SignedDelta(double angleDeg, double eventDeg)
    {
        var delta = (angleDeg - eventDeg) % 720.0;
        if (delta > 360.0)
        {
            delta -= 720.0;
        }
        else if (delta < -360.0)
        {
            delta += 720.0;
        }

        return delta;
    }

    private static bool InWindow(double angle, double? fromDeg, double? toDeg)
    {
        if (fromDeg is not { } from || toDeg is not { } to)
        {
            return true;
        }

        // Windows on a cycle wrap: EVO 130° to EVC 380° does not, but an
        // overlap window from 690° to 30° does, and both must work.
        var offset = Wrap(angle - from);
        return offset <= Wrap(to - from);
    }

    private static double Wrap(double angle)
    {
        var a = angle % 720.0;
        return a < 0 ? a + 720.0 : a;
    }
}
