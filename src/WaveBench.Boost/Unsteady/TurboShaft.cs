namespace WaveBench.Boost.Unsteady;

/// <summary>
/// The turbocharger shaft as a dynamic state (plan §4.1).
///
/// <code>J·dω/dt = (P_turbine·η_mech − P_compressor − P_friction)/ω</code>
///
/// The plan is blunt about why this exists: <i>"turbo lag is a solvable
/// transient — never treat boost as a boundary condition."</i> A steady match
/// says where the shaft ends up; only this says how long it takes to get there,
/// and time-to-torque is the number that correlates with lap time.
///
/// Integration is explicit but on ENERGY rather than speed. Torque goes as
/// P/ω, which blows up as ω → 0; kinetic energy ½Jω² does not, and its
/// derivative is just the net power. That makes a stationary shaft a starting
/// condition rather than a singularity.
/// </summary>
public sealed class TurboShaft
{
    private double _kineticEnergyJ;

    public TurboShaft(double inertiaKgM2, double initialRpm, BearingFriction? friction = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inertiaKgM2);
        ArgumentOutOfRangeException.ThrowIfNegative(initialRpm);

        Inertia = inertiaKgM2;
        Friction = friction ?? new BearingFriction();
        Rpm = initialRpm;
    }

    public double Inertia { get; }

    public BearingFriction Friction { get; set; }

    /// <summary>Mechanical efficiency of the bearing system, applied to turbine power.</summary>
    public double MechanicalEfficiency { get; set; } = 0.97;

    /// <summary>
    /// Electrical assist at the shaft, W (plan §4.5). Positive motors, negative
    /// generates. An e-turbo lives entirely in this one term.
    /// </summary>
    public double AssistPowerW { get; set; }

    /// <summary>Shaft speed, rpm.</summary>
    public double Rpm
    {
        get => Math.Sqrt(Math.Max(0.0, 2.0 * _kineticEnergyJ / Inertia)) * 60.0 / (2.0 * Math.PI);
        set
        {
            var omega = value * 2.0 * Math.PI / 60.0;
            _kineticEnergyJ = 0.5 * Inertia * omega * omega;
        }
    }

    public double Omega => Rpm * 2.0 * Math.PI / 60.0;

    /// <summary>Net accelerating power at the last step, W. Zero at a balanced point.</summary>
    public double NetPowerW { get; private set; }

    /// <summary>Bearing loss at the last step, W.</summary>
    public double FrictionPowerW { get; private set; }

    /// <summary>
    /// Advance the shaft by one timestep against the turbine and compressor
    /// powers currently acting on it.
    /// </summary>
    public void Step(double dt, double turbinePowerW, double compressorPowerW)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dt);

        FrictionPowerW = Friction.PowerW(Rpm);
        NetPowerW = (turbinePowerW * MechanicalEfficiency) - compressorPowerW - FrictionPowerW + AssistPowerW;

        _kineticEnergyJ = Math.Max(0.0, _kineticEnergyJ + (NetPowerW * dt));
    }

    /// <summary>
    /// Seconds to reach a target speed under a constant net power — the closed
    /// form the energy formulation makes available, used to sanity-check the
    /// integration and to size a timestep.
    /// </summary>
    public double TimeToReach(double targetRpm, double netPowerW)
    {
        if (netPowerW <= 0)
        {
            return double.PositiveInfinity;
        }

        var omega = targetRpm * 2.0 * Math.PI / 60.0;
        var target = 0.5 * Inertia * omega * omega;
        return Math.Max(0.0, (target - _kineticEnergyJ) / netPowerW);
    }
}
