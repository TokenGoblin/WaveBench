using WaveBench.Boost.Thermal;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Solver;

namespace WaveBench.Boost.Unsteady;

/// <summary>
/// One point in the transient's history, sampled after a driver step.
/// </summary>
/// <param name="TimeSeconds">Simulated time since the driver was built.</param>
/// <param name="ShaftRpm">Turbo shaft speed.</param>
/// <param name="BoostPressurePa">
/// The stagnation pressure just written into every intake reservoir — the
/// throttle-admitted fraction of the compressor's available boost, not the
/// compressor's own outlet pressure (see <see cref="TransientDriver"/> remarks).
/// </param>
/// <param name="IndicatedTorqueNm">
/// Windowed indicated torque (see <see cref="TransientDriver"/> remarks) — not
/// brake torque, since no friction model is coupled into this driver.
/// </param>
/// <param name="CompressorOutletK">
/// The compressor's own diabatic outlet temperature (aerodynamic rise plus
/// the current housing heat flux) — NOT throttle-blended, unlike
/// <see cref="BoostPressurePa"/>, because it is the quantity a heat-soak
/// comparison across repeat runs needs to read directly.
/// </param>
public readonly record struct TransientSample(
    double TimeSeconds,
    double ShaftRpm,
    double BoostPressurePa,
    double IndicatedTorqueNm,
    double CompressorOutletK);

/// <summary>
/// Couples the gas-dynamics solver with the already-built shaft, thermal and
/// compressor machinery under a scripted <see cref="DrivingProfile"/> — the
/// piece plan §4.7 calls "transient simulation" and Phases 13/14 stopped
/// short of. Nothing here is new physics: <see cref="TurboShaft"/>,
/// <see cref="TurbineStage"/>, <see cref="TurboThermalModel"/> and
/// <see cref="CompressorModel"/> already existed as independently steppable
/// state; this class is the orchestrator that steps them together against a
/// live gas-dynamics solve instead of a fixed operating point.
///
/// Each <see cref="Advance"/> call: writes the profile's rpm for this instant
/// onto the engine, steps the gas dynamics one CFL-limited step, solves the
/// compressor at the shaft's current speed and a smoothed intake mass flow,
/// writes the resulting boost pressure and temperature into every intake
/// <see cref="ReservoirBoundary"/> (found once, at construction, via
/// <see cref="ValveConnection.IsIntake"/>), advances the shaft against that
/// compressor load through <see cref="TurbineStage.Integrate"/>, and advances
/// the turbo housing temperatures.
///
/// <b>Why the intake mass flow is smoothed.</b> A poppet valve's instantaneous
/// port flow pulses hard between open and shut; a real compressor sees that
/// pulse damped by the plenum volume between it and the valves, not the raw
/// pulse itself. This engine topology has no explicit intake plenum (plan
/// §2.1's "v0.1 topology: per-cylinder intake runner from ambient" — the same
/// simplification <see cref="EngineBuilder"/> already states), so the
/// compressor's SEEN flow is approximated with a short exponential average of
/// the summed intake port flow rather than an unphysical instantaneous
/// reading. This is a modelling choice, not a hidden one — see
/// docs/physics.md §6.1.
///
/// <b>Heat soak reaches the boost air, not just a diagnostic temperature.</b>
/// <see cref="TurboThermalModel.Step"/>'s <c>CompressorAirHeatW</c> is added
/// onto the compressor's aerodynamic outlet temperature (Δt = Q/(ṁ·c_p),
/// the same principle <see cref="DiabaticCorrection"/> uses for a held
/// operating point) before that temperature is written into the intake
/// boundary — otherwise a second scripted pull with hotter housings would
/// integrate no differently from the first, and "a second dyno pull is not
/// the same as the first" (plan §4.7) would be undemonstrable. This is a
/// simplified transient application of that same heat-addition principle, not
/// a re-derivation of it — see docs/physics.md §6.3.
///
/// <b>Torque here is INDICATED, not brake.</b> No friction model (Chen–Flynn
/// or otherwise) is coupled into this driver, so <see cref="TransientSample.IndicatedTorqueNm"/>
/// is <c>WaveBench.Core.EngineModel.PerformanceMetrics.Torque</c> applied to a
/// windowed IMEP rather than a BMEP. It is enough to show the transient's
/// trend and to anchor a time-to-90%-torque metric; it is not a claimed
/// brake-torque figure.
/// </summary>
public sealed class TransientDriver
{
    /// <summary>c_p for air, J/kg·K — matches <see cref="CompressorModel"/>'s own constant.</summary>
    private const double Cp = 1005.0;

    private readonly EngineSimulator _engine;
    private readonly TurbineStage _stage;
    private readonly CompressorMap _compressorMap;
    private readonly TurboThermalModel _thermal;
    private readonly TurboEnvironment _environment;
    private readonly IReadOnlyList<ValveConnection> _intakeValves;
    private readonly IReadOnlyList<ReservoirBoundary> _intakeBoundaries;
    private readonly double _ambientPressurePa;
    private readonly double _ambientTemperatureK;
    private readonly double _totalDisplacementM3;
    private readonly double _massFlowSmoothingTauS;
    private readonly Queue<(double Angle, double WorkJ)> _cycleWindow = new();

    private double _smoothedIntakeMassFlow;

    public TransientDriver(
        EngineSimulator engine,
        TurbineStage stage,
        CompressorMap compressorMap,
        TurboThermalModel thermal,
        double ambientPressurePa,
        double ambientTemperatureK,
        TurboEnvironment? environment = null,
        double massFlowSmoothingTauS = 0.01)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(compressorMap);
        ArgumentNullException.ThrowIfNull(thermal);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ambientPressurePa);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ambientTemperatureK);
        ArgumentOutOfRangeException.ThrowIfNegative(massFlowSmoothingTauS);

        _engine = engine;
        _stage = stage;
        _compressorMap = compressorMap;
        _thermal = thermal;
        _environment = environment ?? TurboEnvironment.GasStand;
        _ambientPressurePa = ambientPressurePa;
        _ambientTemperatureK = ambientTemperatureK;
        _massFlowSmoothingTauS = massFlowSmoothingTauS;

        _intakeValves = [.. engine.Valves.Where(v => v.IsIntake)];
        if (_intakeValves.Count == 0)
        {
            throw new ArgumentException("The engine has no intake valves to drive.", nameof(engine));
        }

        _intakeBoundaries = [.. _intakeValves.Select(v =>
            v.Duct.LeftEnd as ReservoirBoundary
            ?? throw new ArgumentException(
                "Every intake duct's left end must be a ReservoirBoundary for a transient driver to control its "
                + "load — this is how EngineBuilder.Build always wires the v0.1 per-cylinder intake topology.",
                nameof(engine)))];

        _totalDisplacementM3 = engine.Cylinders.Sum(c => c.Geometry.DisplacedVolume);

        LatestCompressor = CompressorModel.Solve(
            compressorMap, 1e-4, Math.Max(stage.Shaft.Rpm, 1.0), ambientTemperatureK, ambientPressurePa / 1000.0);
    }

    /// <summary>The compressor solve from the most recent <see cref="Advance"/> call.</summary>
    public CompressorPointResult LatestCompressor { get; private set; }

    /// <summary>Advance everything by one gas-dynamics step under the given driving profile.</summary>
    public TransientSample Advance(DrivingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _engine.Rpm = profile.RpmAt(_engine.Time, _engine.Rpm);

        var before = _engine.Time;
        _engine.Step();
        var dt = _engine.Time - before;

        var instantFlow = _intakeValves.Sum(v => v.MassFlow);
        var alpha = _massFlowSmoothingTauS > 0 ? Math.Min(1.0, dt / _massFlowSmoothingTauS) : 1.0;
        _smoothedIntakeMassFlow += (instantFlow - _smoothedIntakeMassFlow) * alpha;
        var flow = Math.Max(_smoothedIntakeMassFlow, 1e-6);

        var compressor = CompressorModel.Solve(
            _compressorMap, flow, _stage.Shaft.Rpm, _ambientTemperatureK, _ambientPressurePa / 1000.0);
        LatestCompressor = compressor;

        // Rotor.Last is written during the duct-end evaluation inside
        // _engine.Step() above, so it already reflects THIS step — read it
        // before Integrate(), which only accumulates and advances the shaft.
        var turbineInletK = _stage.Entries.Count > 0 ? _stage.Entries[0].Rotor.Last.TotalTemperatureK : _ambientTemperatureK;
        var meanCompressorAirK = 0.5 * (_ambientTemperatureK + compressor.OutletTemperatureK);
        var thermalState = _thermal.Step(dt, turbineInletK, meanCompressorAirK, _environment);

        var diabaticOutletK = compressor.OutletTemperatureK + (thermalState.CompressorAirHeatW / (flow * Cp));

        // The throttle sits downstream of the compressor and upstream of the
        // intake valves in a real engine; this per-cylinder-reservoir
        // topology has no separate throttle-plate/plenum component to put it
        // in (the same simplification EngineBuilder.Build's own doc comment
        // states for the steady NA case), so the driving profile's load
        // fraction is applied here as how much of the compressor's available
        // boost is actually admitted — 0 leaves the reservoir at ambient
        // (closed throttle), 1 admits the full compressor delivery (WOT).
        var boostPa = _ambientPressurePa * compressor.PressureRatio;
        var loadFraction = profile.LoadFractionAt(_engine.Time);
        var admittedPa = _ambientPressurePa + (loadFraction * (boostPa - _ambientPressurePa));
        var admittedK = _ambientTemperatureK + (loadFraction * (diabaticOutletK - _ambientTemperatureK));

        foreach (var boundary in _intakeBoundaries)
        {
            boundary.StagnationPressure = admittedPa;
            boundary.StagnationTemperature = admittedK;
        }

        _stage.Integrate(dt, compressor.PowerW);

        var torque = WindowedIndicatedTorqueNm();

        return new TransientSample(_engine.Time, _stage.Shaft.Rpm, admittedPa, torque, diabaticOutletK);
    }

    /// <summary>
    /// Indicated torque from a sliding 720°-crank-angle window of piston work.
    /// Before a full window has elapsed the partial window's MEP is scaled by
    /// 720°/window — an extrapolation, not a measurement, but one that lets
    /// the very first samples of a transient carry a torque estimate rather
    /// than a meaningless zero.
    /// </summary>
    private double WindowedIndicatedTorqueNm()
    {
        _cycleWindow.Enqueue((_engine.Angle, _engine.CumulativePistonWork));
        while (_cycleWindow.Count > 1 && _engine.Angle - _cycleWindow.Peek().Angle > 720.0)
        {
            _cycleWindow.Dequeue();
        }

        var (frontAngle, frontWork) = _cycleWindow.Peek();
        var windowAngle = _engine.Angle - frontAngle;
        if (windowAngle < 1e-6)
        {
            return 0.0;
        }

        var workJ = _engine.CumulativePistonWork - frontWork;
        var imepPa = workJ / _totalDisplacementM3 * (720.0 / windowAngle);
        return PerformanceMetrics.Torque(imepPa, _totalDisplacementM3);
    }
}
