namespace WaveBench.Boost.Control;

/// <summary>
/// A pneumatic wastegate actuator (plan §4.4): spring against boost pressure,
/// with an optional dome fed by a solenoid.
///
/// The reason a real car's wastegate is not simply "open at the target" is
/// here: the spring sets a floor the solenoid can only add to, and the actuator
/// has a stroke rate. Both show up as overshoot on a step, and a boost
/// controller tuned without them is tuned against the wrong plant.
/// </summary>
public sealed class PneumaticActuator
{
    /// <summary>Pressure difference at which the valve starts to lift, Pa gauge. The "spring pressure".</summary>
    public double SpringCrackingPa { get; init; } = 70_000.0;

    /// <summary>Additional pressure to reach full stroke, Pa.</summary>
    public double SpringRangePa { get; init; } = 45_000.0;

    /// <summary>Full stroke per second — the actuator cannot slam.</summary>
    public double SlewRatePerSecond { get; init; } = 8.0;

    /// <summary>Current valve position, 0–1.</summary>
    public double Position { get; private set; }

    /// <summary>
    /// Advance the actuator against the manifold pressure and whatever the
    /// solenoid is bleeding off.
    /// </summary>
    /// <param name="dt">Timestep, s.</param>
    /// <param name="manifoldGaugePa">Boost pressure acting on the diaphragm.</param>
    /// <param name="dutyCycle">
    /// Solenoid duty, 0–1. It bleeds pressure away from the actuator, so higher
    /// duty means the gate opens LATER and boost runs higher — the sign that
    /// catches everyone out the first time.
    /// </param>
    public double Step(double dt, double manifoldGaugePa, double dutyCycle)
    {
        var duty = Math.Clamp(dutyCycle, 0.0, 1.0);
        var effective = manifoldGaugePa * (1.0 - (0.6 * duty));

        var demand = Math.Clamp((effective - SpringCrackingPa) / SpringRangePa, 0.0, 1.0);
        var maxMove = SlewRatePerSecond * dt;

        Position += Math.Clamp(demand - Position, -maxMove, maxMove);
        return Position;
    }

    public void Reset(double position) => Position = Math.Clamp(position, 0.0, 1.0);
}

/// <summary>
/// Boost control (plan §4.4): a PID on boost error with feed-forward, driving
/// a wastegate actuator.
///
/// The feed-forward term is what makes it behave: boost demand is a known
/// function of rpm and target, so the controller starts from roughly the right
/// duty and the integrator only has to trim. Without it, the integrator has to
/// wind up from zero on every transient and the overshoot the plan asks to be
/// reported is an artefact of the controller rather than of the turbo.
/// </summary>
public sealed class BoostController
{
    private double _integral;
    private double _previousError;
    private bool _hasPrevious;

    public double ProportionalGain { get; init; } = 1.2e-5;

    public double IntegralGain { get; init; } = 4.0e-5;

    public double DerivativeGain { get; init; } = 1.5e-7;

    /// <summary>Integrator clamp, in duty units. Anti-windup, and the reason a long spool does not overshoot.</summary>
    public double IntegralLimit { get; init; } = 0.6;

    /// <summary>Feed-forward duty as a function of the target. Null means no feed-forward.</summary>
    public Func<double, double>? FeedForward { get; init; }

    /// <summary>Last computed duty, 0–1.</summary>
    public double Duty { get; private set; }

    public void Reset()
    {
        _integral = 0;
        _previousError = 0;
        _hasPrevious = false;
        Duty = 0;
    }

    /// <summary>
    /// One control update.
    /// </summary>
    /// <param name="dt">Time since the last update, s.</param>
    /// <param name="targetPa">Target manifold absolute pressure.</param>
    /// <param name="measuredPa">Measured manifold absolute pressure.</param>
    public double Update(double dt, double targetPa, double measuredPa)
    {
        var error = targetPa - measuredPa;

        var derivative = _hasPrevious && dt > 0 ? (error - _previousError) / dt : 0.0;
        _previousError = error;
        _hasPrevious = true;

        _integral = Math.Clamp(
            _integral + (IntegralGain * error * dt), -IntegralLimit, IntegralLimit);

        var feedForward = FeedForward?.Invoke(targetPa) ?? 0.0;
        var raw = feedForward + (ProportionalGain * error) + _integral + (DerivativeGain * derivative);

        // Conditional integration: stop winding while the output is saturated,
        // or the integrator stores a demand the actuator was never able to act
        // on and pays it back as overshoot.
        Duty = Math.Clamp(raw, 0.0, 1.0);
        if (raw is > 1.0 or < 0.0)
        {
            _integral = Math.Clamp(_integral - (IntegralGain * error * dt), -IntegralLimit, IntegralLimit);
        }

        return Duty;
    }
}

/// <summary>
/// A variable-geometry turbine's vane actuator (plan §4.3).
///
/// Vane position is a third map axis. Rather than require a map per vane
/// position — which almost no public data provides — the effect is expressed as
/// a capacity scale on a single reference map, so a VGT can be modelled from
/// what a user actually has. When per-position maps ARE available they should
/// be interpolated instead, and this class is then the actuator only.
/// </summary>
public sealed class VariableGeometryActuator
{
    /// <summary>Swallowing capacity relative to the reference map with the vanes fully closed.</summary>
    public double ClosedCapacityRatio { get; init; } = 0.45;

    /// <summary>Capacity relative to the reference map with the vanes fully open.</summary>
    public double OpenCapacityRatio { get; init; } = 1.35;

    /// <summary>Full travel per second. VGT actuators are not instant, and spool strategy depends on it.</summary>
    public double SlewRatePerSecond { get; init; } = 4.0;

    /// <summary>0 fully closed (small effective A/R, fast spool), 1 fully open.</summary>
    public double Position { get; private set; } = 1.0;

    /// <summary>Capacity scale to hand to the rotor boundary at the current position.</summary>
    public double CapacityScale =>
        ClosedCapacityRatio + ((OpenCapacityRatio - ClosedCapacityRatio) * Math.Clamp(Position, 0.0, 1.0));

    public double Step(double dt, double demandedPosition)
    {
        var maxMove = SlewRatePerSecond * dt;
        Position += Math.Clamp(Math.Clamp(demandedPosition, 0.0, 1.0) - Position, -maxMove, maxMove);
        return Position;
    }

    public void Reset(double position) => Position = Math.Clamp(position, 0.0, 1.0);
}
