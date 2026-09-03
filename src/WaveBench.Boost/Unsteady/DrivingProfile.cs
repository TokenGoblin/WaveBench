namespace WaveBench.Boost.Unsteady;

/// <summary>
/// A step in intake load at a given time, expressed the same way
/// <c>WaveBench.Core.Solver.EngineBuilder.Build</c>'s <c>intakeLoadFraction</c>
/// already does — a 0-to-1 fraction, though here of the compressor's
/// available boost rather than of ambient pressure (see
/// <see cref="TransientDriver"/> remarks) — plan §4.7: "step throttle at
/// fixed rpm (time to 90% boost)".
/// </summary>
/// <param name="StartFraction">Load fraction before the step.</param>
/// <param name="EndFraction">Load fraction after the step.</param>
/// <param name="StepAtSeconds">When the step begins, on the transient's own clock.</param>
/// <param name="RampSeconds">
/// How long the step itself takes to complete, linearly. Not an idealised
/// Heaviside jump: <see cref="TransientDriver"/> imposes this fraction on a
/// <c>ReservoirBoundary</c> the CFL-limited gas-dynamics solver reads on the
/// very next step, and a genuinely instantaneous pressure jump there can
/// demand a flux the previous step's timestep was never sized for, driving
/// that cell non-physical. A few milliseconds is still a "step" against a
/// spool transient running tens to hundreds of milliseconds — short enough to
/// be indistinguishable from instantaneous at the scale plan §4.7 cares
/// about, long enough for the solver to remain well-posed. Defaults to 3 ms.
/// </param>
public readonly record struct ThrottleStep(double StartFraction, double EndFraction, double StepAtSeconds, double RampSeconds = 0.003)
{
    public double LoadFractionAt(double timeSeconds)
    {
        if (timeSeconds <= StepAtSeconds)
        {
            return StartFraction;
        }

        if (RampSeconds <= 0.0 || timeSeconds >= StepAtSeconds + RampSeconds)
        {
            return EndFraction;
        }

        var t = (timeSeconds - StepAtSeconds) / RampSeconds;
        return StartFraction + ((EndFraction - StartFraction) * t);
    }
}

/// <summary>
/// A linear rpm ramp over a window — the "gear-limited vehicle acceleration"
/// half of plan §4.7. Holds <see cref="StartRpm"/> before the window and
/// <see cref="EndRpm"/> after it.
/// </summary>
/// <param name="StartRpm">Rpm before the ramp starts.</param>
/// <param name="EndRpm">Rpm once the ramp completes.</param>
/// <param name="DurationSeconds">How long the ramp takes.</param>
/// <param name="StartAtSeconds">When the ramp begins, on the transient's own clock.</param>
public readonly record struct RpmRamp(double StartRpm, double EndRpm, double DurationSeconds, double StartAtSeconds = 0.0)
{
    public double RpmAt(double timeSeconds)
    {
        if (timeSeconds <= StartAtSeconds || DurationSeconds <= 0.0)
        {
            return timeSeconds <= StartAtSeconds ? StartRpm : EndRpm;
        }

        var t = (timeSeconds - StartAtSeconds) / DurationSeconds;
        return t >= 1.0 ? EndRpm : StartRpm + ((EndRpm - StartRpm) * t);
    }
}

/// <summary>
/// What <see cref="TransientDriver"/> does to the throttle boundary and the
/// prescribed crank speed at a given instant (plan §4.7). Deliberately
/// smaller and more specific than <c>Auralisation.RpmProfile</c>/<c>LoadProfile</c>
/// — those interpolate between independently pre-solved steady operating
/// points for audio synthesis, which is exactly the shortcut a genuinely
/// coupled transient must not take. A repeat-run heat-soak scenario is not a
/// distinct profile shape: it is the same <see cref="ThrottleStep"/> replayed
/// against a <see cref="Thermal.TurboThermalModel"/> whose housing
/// temperatures were carried over from the previous run rather than reset.
/// </summary>
/// <param name="Throttle">The load-fraction step.</param>
/// <param name="Rpm">An optional rpm ramp; omitted for a fixed-rpm step-throttle scenario.</param>
public sealed record DrivingProfile(ThrottleStep Throttle, RpmRamp? Rpm = null)
{
    public double LoadFractionAt(double timeSeconds) => Throttle.LoadFractionAt(timeSeconds);

    public double RpmAt(double timeSeconds, double fallbackRpm) => Rpm?.RpmAt(timeSeconds) ?? fallbackRpm;
}
