using WaveBench.Core.EngineModel;

namespace WaveBench.Boost.Engine;

/// <summary>
/// Where the fuel goes in, which decides what blow-through costs (plan §4.6.3).
/// </summary>
public enum InjectionSystem
{
    /// <summary>
    /// Injected into the port, upstream of the valve. Blow-through carries fuel
    /// with it straight into the exhaust — the reason a port-injected engine
    /// cannot run the overlap a direct-injected one can.
    /// </summary>
    PortUpstreamOfValve,

    /// <summary>
    /// Injected into the cylinder after the exhaust valve has shut. Blow-through
    /// is pure air, so it costs nothing but pumping — which is exactly why DI
    /// turbo engines run overlap a port-injected engine cannot.
    /// </summary>
    Direct,
}

/// <summary>What one cylinder did during its overlap, over one cycle.</summary>
/// <param name="Cylinder">1-based cylinder number.</param>
/// <param name="OverlapDeg">Crank degrees with both valves off their seats.</param>
/// <param name="PositiveScavengingDeg">
/// Degrees of that overlap with intake port pressure above exhaust port
/// pressure — the window in which overlap scavenges instead of reverting.
/// </param>
/// <param name="MeanScavengingPressureRatio">Mean p_intake/p_exhaust across the overlap.</param>
/// <param name="PeakScavengingPressureRatio">Its maximum.</param>
/// <param name="DeliveredFreshKg">Fresh charge that came in through the intake valve.</param>
/// <param name="BlowThroughKg">Fresh charge that went straight out of the exhaust valve.</param>
/// <param name="TrappedFreshKg">What was left to burn.</param>
public sealed record CylinderScavenging(
    int Cylinder,
    double OverlapDeg,
    double PositiveScavengingDeg,
    double MeanScavengingPressureRatio,
    double PeakScavengingPressureRatio,
    double DeliveredFreshKg,
    double BlowThroughKg,
    double TrappedFreshKg)
{
    /// <summary>Blow-through as a fraction of what was delivered. Plan §4.6.3's headline number.</summary>
    public double BlowThroughFraction => DeliveredFreshKg > 0 ? BlowThroughKg / DeliveredFreshKg : 0.0;

    /// <summary>
    /// Trapping efficiency: the share of delivered fresh charge that stayed to
    /// burn. The complement of blow-through, and the number that says how much
    /// of the pumping work bought anything.
    /// </summary>
    public double TrappingEfficiency => DeliveredFreshKg > 0 ? TrappedFreshKg / DeliveredFreshKg : 1.0;

    /// <summary>Whether overlap was an asset at all — any window at all with positive pressure across it.</summary>
    public bool ScavengedPositively => PositiveScavengingDeg > 0.0;
}

/// <summary>What blow-through cost, given the injection system (plan §4.6.3).</summary>
/// <param name="Injection">Which system was modelled.</param>
/// <param name="FuelLostKgPerCycle">Fuel that left through the exhaust valve unburned. Zero for DI.</param>
/// <param name="FuelPenaltyFraction">That loss as a share of the fuel supplied.</param>
/// <param name="MeasuredLambdaRatio">
/// Measured exhaust lambda divided by the lambda the cylinder actually ran.
/// Above 1 the sensor reads lean while the cylinder is not — the classic
/// scavenging-engine mis-read, and it happens with DI, not with port injection.
/// </param>
/// <param name="TurbineInletRiseK">
/// Temperature rise from fuel burning in the exhaust port rather than in the
/// cylinder. It shows up as turbine inlet temperature, which is a hard material
/// limit, so it is a constraint and not just a curiosity.
/// </param>
public readonly record struct BlowThroughCost(
    InjectionSystem Injection,
    double FuelLostKgPerCycle,
    double FuelPenaltyFraction,
    double MeasuredLambdaRatio,
    double TurbineInletRiseK);

/// <summary>
/// Scavenging-pressure tracking and blow-through accounting (plan §4.6.3).
///
/// <b>The claim being made operational:</b> with intake pressure above exhaust
/// pressure during overlap, overlap scavenges residuals and cools the chamber
/// instead of causing reversion — so the optimum overlap and LCA for a boosted
/// engine are materially different from an NA one, and the optimiser has to be
/// able to find that on its own.
///
/// It also has to be stopped from cheating. Free scavenging is only free if the
/// injection system can actually have it: with port injection, every kilogram
/// of blow-through carries fuel straight out of the exhaust valve. That cost is
/// computed here so it can be charged to the objective function rather than
/// discovered on a dyno.
/// </summary>
public sealed class ScavengingAnalyser
{
    private readonly List<(ValveConnection Intake, ValveConnection Exhaust)> _cylinders = [];
    private readonly List<double[]> _pressureRatio = [];
    private readonly List<double> _overlapSamples = [];
    private readonly List<double> _positiveSamples = [];
    private readonly List<double> _ratioSum = [];
    private readonly List<double> _ratioPeak = [];
    private readonly List<double> _overlapIntakeMass = [];
    private double _startAngle = double.NaN;
    private double _lastAngle = double.NaN;

    /// <summary>
    /// How much of the charge entering during overlap short-circuits straight to
    /// the exhaust without mixing, 0–1.
    ///
    /// <b>This is a bracket, not a prediction, and it is worth being blunt about
    /// why.</b> The cylinder model is single-zone: gas leaving takes fresh charge
    /// in proportion to the cylinder's mean fresh fraction, as though the
    /// contents were uniform. That is <i>perfect mixing</i>, and it is the
    /// LOWER bound on blow-through — on this engine it reports under 1% where a
    /// measured DI turbo with the same overlap and scavenging pressure shows
    /// several. The upper bound is <i>perfect displacement</i>: every kilogram
    /// entering during overlap crosses to the exhaust valve untouched, which is
    /// this parameter at 1.
    ///
    /// Where a real engine sits between them depends on port angle, valve
    /// shrouding and chamber shape — none of which a 1D solver can resolve, and
    /// none of which should be guessed at inside a physics model. Set it from a
    /// CFD result or a measured trapping efficiency; leave it at zero and read
    /// the answer as the floor it is.
    /// </summary>
    public double ShortCircuitFraction { get; set; }

    /// <summary>
    /// Attach to an engine's valves. The builder emits them in
    /// (intake, exhaust) pairs per cylinder, which is the order relied on here
    /// and asserted rather than assumed.
    /// </summary>
    public ScavengingAnalyser(EngineSimulator engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        if (engine.Valves.Count != engine.Cylinders.Count * 2)
        {
            throw new ArgumentException(
                $"Expected two valves per cylinder; found {engine.Valves.Count} for "
                + $"{engine.Cylinders.Count} cylinders.", nameof(engine));
        }

        for (var c = 0; c < engine.Cylinders.Count; c++)
        {
            var intake = engine.Valves[2 * c];
            var exhaust = engine.Valves[(2 * c) + 1];

            if (!intake.IsIntake || exhaust.IsIntake)
            {
                throw new ArgumentException(
                    $"Cylinder {c + 1}'s valves are not in (intake, exhaust) order.", nameof(engine));
            }

            _cylinders.Add((intake, exhaust));
            _overlapSamples.Add(0.0);
            _positiveSamples.Add(0.0);
            _ratioSum.Add(0.0);
            _ratioPeak.Add(0.0);
            _overlapIntakeMass.Add(0.0);
            _pressureRatio.Add([]);
        }
    }

    public int CylinderCount => _cylinders.Count;

    public void Clear()
    {
        _startAngle = double.NaN;
        _lastAngle = double.NaN;
        for (var c = 0; c < _cylinders.Count; c++)
        {
            _overlapSamples[c] = 0.0;
            _positiveSamples[c] = 0.0;
            _ratioSum[c] = 0.0;
            _ratioPeak[c] = 0.0;
            _overlapIntakeMass[c] = 0.0;
        }
    }

    /// <summary>
    /// Sample every cylinder at the current crank angle. Called once per solver
    /// step, after the valves have been updated.
    /// </summary>
    /// <param name="engineAngleDeg">Crank angle after the step.</param>
    /// <param name="dt">The step, s — needed to integrate the overlap-window flow.</param>
    public void Record(double engineAngleDeg, double dt)
    {
        if (double.IsNaN(_startAngle))
        {
            _startAngle = engineAngleDeg;
            _lastAngle = engineAngleDeg;
            return;
        }

        var dTheta = engineAngleDeg - _lastAngle;
        _lastAngle = engineAngleDeg;
        if (dTheta <= 0)
        {
            return;
        }

        for (var c = 0; c < _cylinders.Count; c++)
        {
            var (intake, exhaust) = _cylinders[c];

            // Overlap is both valves off their seats. A lift threshold rather
            // than a cam angle, because what matters is whether gas can
            // actually cross — a valve 0.05 mm off its seat is not an overlap
            // window in any useful sense.
            if (intake.CurrentLift < 1e-4 || exhaust.CurrentLift < 1e-4)
            {
                continue;
            }

            var pIntake = PortPressure(intake);
            var pExhaust = PortPressure(exhaust);
            var ratio = pExhaust > 0 ? pIntake / pExhaust : double.NaN;
            if (!double.IsFinite(ratio))
            {
                continue;
            }

            _overlapSamples[c] += dTheta;
            _ratioSum[c] += ratio * dTheta;
            _ratioPeak[c] = Math.Max(_ratioPeak[c], ratio);

            if (intake.MassFlow > 0)
            {
                _overlapIntakeMass[c] += intake.MassFlow * dt;
            }

            if (ratio > 1.0)
            {
                _positiveSamples[c] += dTheta;
            }
        }
    }

    private static double PortPressure(ValveConnection valve) =>
        valve.Duct.GetPressure(valve.DuctLeftEnd ? 0 : valve.Duct.CellCount - 1);

    /// <summary>
    /// Reduce the recorded cycle. Fresh-charge masses come from the valves'
    /// own accumulators, so they must be reset on the same cycle boundary this
    /// analyser was cleared on.
    /// </summary>
    public IReadOnlyList<CylinderScavenging> Reduce()
    {
        var results = new List<CylinderScavenging>(_cylinders.Count);

        for (var c = 0; c < _cylinders.Count; c++)
        {
            var (intake, exhaust) = _cylinders[c];
            var delivered = intake.ImportedFreshMass;

            // The mixed answer from the gas dynamics, plus whatever share of the
            // overlap-window flow is declared to short-circuit past the mixing.
            var shortCircuit = Math.Clamp(ShortCircuitFraction, 0.0, 1.0) * _overlapIntakeMass[c];
            var blowThrough = Math.Min(delivered, exhaust.ExportedFreshMass + shortCircuit);

            results.Add(new CylinderScavenging(
                c + 1,
                _overlapSamples[c],
                _positiveSamples[c],
                _overlapSamples[c] > 0 ? _ratioSum[c] / _overlapSamples[c] : double.NaN,
                _ratioPeak[c],
                delivered,
                blowThrough,
                Math.Max(0.0, delivered - blowThrough)));
        }

        return results;
    }

    /// <summary>
    /// What the blow-through cost, given the injection system.
    /// </summary>
    /// <param name="scavenging">The reduced cycle.</param>
    /// <param name="injection">Which injection system is modelled.</param>
    /// <param name="fuelChargeFraction">Fuel mass fraction of the fresh charge.</param>
    /// <param name="fuelSuppliedKgPerCycle">Total fuel put in over the cycle, across all cylinders.</param>
    /// <param name="exhaustMassKgPerCycle">Total exhaust mass, for the temperature rise.</param>
    /// <param name="lowerHeatingValue">Fuel LHV, J/kg.</param>
    /// <param name="exhaustCp">c_p of the exhaust, J/kg·K.</param>
    /// <param name="portBurnFraction">
    /// How much of the blown-through fuel actually burns in the exhaust port.
    /// It needs oxygen, residence time and temperature, and gets a partial
    /// amount of all three. Exposed because it is a fitted number, not a derived
    /// one; the default is a middling assumption and the sensitivity to it is
    /// linear in the reported temperature rise.
    /// </param>
    public static BlowThroughCost Cost(
        IReadOnlyList<CylinderScavenging> scavenging,
        InjectionSystem injection,
        double fuelChargeFraction,
        double fuelSuppliedKgPerCycle,
        double exhaustMassKgPerCycle,
        double lowerHeatingValue,
        double exhaustCp = 1150.0,
        double portBurnFraction = 0.5)
    {
        ArgumentNullException.ThrowIfNull(scavenging);

        var blowThrough = scavenging.Sum(s => s.BlowThroughKg);
        var trapped = scavenging.Sum(s => s.TrappedFreshKg);

        if (injection == InjectionSystem.Direct)
        {
            // Nothing but air goes out. It costs no fuel — but it does reach the
            // exhaust sensor, which then reads leaner than the cylinder ran. A
            // closed loop that trusts it will richen an engine that did not need
            // it, which is the practical consequence worth reporting.
            var extraAir = trapped > 0 ? blowThrough / trapped : 0.0;
            return new BlowThroughCost(injection, 0.0, 0.0, 1.0 + extraAir, 0.0);
        }

        var fuelLost = blowThrough * fuelChargeFraction;
        var burnedInPort = fuelLost * Math.Clamp(portBurnFraction, 0.0, 1.0);
        var rise = exhaustMassKgPerCycle > 0
            ? burnedInPort * lowerHeatingValue / (exhaustMassKgPerCycle * exhaustCp)
            : 0.0;

        return new BlowThroughCost(
            injection,
            fuelLost,
            fuelSuppliedKgPerCycle > 0 ? fuelLost / fuelSuppliedKgPerCycle : 0.0,

            // Port injection sends fuel out WITH the air, so the mixture reaching
            // the sensor is at roughly the same lambda the cylinder ran. The
            // sensor is not fooled; the fuel bill is.
            1.0,
            rise);
    }
}
