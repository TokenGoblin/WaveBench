using WaveBench.Boost.Engine;
using WaveBench.Boost.Unsteady;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Verification.Boost;

/// <summary>What one cam timing produced.</summary>
/// <param name="LobeCentreAngleDeg">Lobe centre angle. Smaller means more overlap.</param>
/// <param name="OverlapDeg">Overlap the timing implies, at the cam's own reference lift.</param>
/// <param name="TorqueNm">Indicated torque — what the optimiser would be maximising.</param>
/// <param name="NetTorqueNm">
/// Torque after charging the fuel that blew straight through. Equal to
/// <paramref name="TorqueNm"/> under direct injection; below it under port
/// injection, which is the whole point of tracking it.
/// </param>
/// <param name="VolumetricEfficiency">Against the intake plenum's own density, so boost does not flatter it.</param>
/// <param name="Scavenging">Per-cylinder scavenging record.</param>
/// <param name="Cost">What the blow-through cost.</param>
/// <param name="MeanScavengingPressureRatio">Mean p_intake/p_exhaust across the overlap, all cylinders.</param>
/// <param name="BlowThroughFraction">Fresh charge lost out of the exhaust, all cylinders.</param>
public sealed record CamPoint(
    double LobeCentreAngleDeg,
    double OverlapDeg,
    double TorqueNm,
    double NetTorqueNm,
    double VolumetricEfficiency,
    IReadOnlyList<CylinderScavenging> Scavenging,
    BlowThroughCost Cost,
    double MeanScavengingPressureRatio,
    double BlowThroughFraction);

/// <summary>
/// The same four-cylinder engine run naturally aspirated and turbocharged, with
/// its cam timing swept — validation case 17.
///
/// Boost is imposed as an intake plenum condition rather than solved from a
/// compressor. That is deliberate and it is the right question to ask: the case
/// is about what cam a boosted engine wants at a given operating point, and
/// making the plenum pressure an input isolates the cam from every other
/// variable a matching calculation would drag in. The exhaust side is NOT
/// imposed — the turbine sets manifold pressure out of the flow it is given,
/// which is what makes the scavenging pressure ratio a result rather than an
/// assumption.
/// </summary>
internal sealed class CamSweepRig
{
    private readonly EngineSimulator _engine;
    private readonly ScavengingAnalyser _scavenging;
    private readonly TurbineStage? _turbine;
    private readonly EngineModelDocument _document;

    public CamSweepRig(
        double lobeCentreAngleDeg,
        double rpm,
        bool turbocharged,
        double boostPressurePa = 200_000.0,
        double chargeTemperatureK = 320.0)
    {
        LobeCentreAngleDeg = lobeCentreAngleDeg;
        Turbocharged = turbocharged;

        _document = FourCylinder(lobeCentreAngleDeg, turbocharged);
        _engine = EngineBuilder.Build(_document, rpm);
        _engine.WallConvergenceK = _document.PipeThermal.WallConvergenceK;

        PlenumPressurePa = turbocharged ? boostPressurePa : _document.Ambient.PressureKPa * 1000.0;
        PlenumTemperatureK = turbocharged ? chargeTemperatureK : _document.Ambient.TemperatureK;

        // The compressor, as far as the cam question is concerned: a plenum held
        // at the delivered pressure and post-intercooler temperature.
        foreach (var valve in _engine.Valves.Where(v => v.IsIntake))
        {
            if (valve.Duct.LeftEnd is ReservoirBoundary reservoir)
            {
                reservoir.StagnationPressure = PlenumPressurePa;
                reservoir.StagnationTemperature = PlenumTemperatureK;
            }
        }

        if (turbocharged)
        {
            var (outlet, atLeftEnd) = FindOutlet(_document.ExhaustManifold!, _engine);
            var gas = (PerfectGasModel)outlet.Gas;
            var shaft = new TurboShaft(3.1e-6, 130_000.0);

            _turbine = TurbineStage.Build(
                TurbineModelKind.QuasiSteady, SyntheticTurbo.Turbine(), shaft,
                [(outlet, atLeftEnd, "single")],
                new VoluteGeometry(0.150, 1.2e-3, 8.0e-4, 12), gas);
        }

        _scavenging = new ScavengingAnalyser(_engine);
    }

    public double LobeCentreAngleDeg { get; }

    public bool Turbocharged { get; }

    public double PlenumPressurePa { get; }

    public double PlenumTemperatureK { get; }

    /// <summary>Overlap the timing implies: exhaust close minus intake open.</summary>
    public double OverlapDeg => 230.0 - (2.0 * LobeCentreAngleDeg);

    private static (DuctSolver Duct, bool AtLeftEnd) FindOutlet(ManifoldSpec spec, EngineSimulator engine)
    {
        var atmosphere = spec.Nodes.Single(n => n.Kind == ManifoldNodeKind.Atmosphere);
        var neighbour = spec.Upstream(atmosphere.Id).Concat(spec.Downstream(atmosphere.Id)).Single();
        return (engine.ManifoldPipes[neighbour], !spec.Downstream(neighbour).Contains(atmosphere.Id));
    }

    /// <summary>See <see cref="ScavengingAnalyser.ShortCircuitFraction"/>: 0 is the mixed floor.</summary>
    public double ShortCircuitFraction
    {
        get => _scavenging.ShortCircuitFraction;
        set => _scavenging.ShortCircuitFraction = value;
    }

    public CamPoint Run(InjectionSystem injection, int warmupCycles = 6)
    {
        for (var c = 0; c < warmupCycles; c++)
        {
            RunCycle();
        }

        _scavenging.Clear();
        foreach (var valve in _engine.Valves)
        {
            valve.ResetFlowStatistics();
        }

        var workBefore = _engine.Cylinders.Select(c => c.CumulativeWork).ToArray();
        var fuelBefore = _engine.Cylinders.Select(c => c.CumulativeFuelBurned).ToArray();
        var cycleTime = RunCycle();

        var scavenging = _scavenging.Reduce();

        var displacement = _engine.Cylinders[0].Geometry.DisplacedVolume;
        var work = _engine.Cylinders.Select((c, i) => c.CumulativeWork - workBefore[i]).Sum();
        var fuel = _engine.Cylinders.Select((c, i) => c.CumulativeFuelBurned - fuelBefore[i]).Sum();

        // Torque from indicated work over the 720° cycle.
        var totalDisplacement = displacement * _engine.Cylinders.Count;
        var imep = work / totalDisplacement;
        var torque = imep * totalDisplacement / (4.0 * Math.PI);

        // VE against the PLENUM's density, not ambient: measured against ambient
        // a boosted engine shows 180% and the cam's effect disappears into the
        // boost. What the cam changes is how well the engine fills from what it
        // is given.
        var plenumDensity = PlenumPressurePa / (PerfectGas.Air.SpecificGasConstant * PlenumTemperatureK);
        var trapped = scavenging.Sum(s => s.TrappedFreshKg);
        var ve = trapped / (plenumDensity * totalDisplacement);

        var exhaustMass = _engine.Valves.Where(v => !v.IsIntake).Sum(v => v.ExportedMass);
        var cost = ScavengingAnalyser.Cost(
            scavenging, injection,
            _engine.Cylinders[0].FuelChargeFraction,
            fuel + scavenging.Sum(s => s.BlowThroughKg) * _engine.Cylinders[0].FuelChargeFraction,
            exhaustMass,
            _engine.Cylinders[0].FuelLowerHeatingValue);

        // Charge the lost fuel against the torque it did not produce. Without
        // this the optimiser is free to buy scavenging with fuel it never
        // accounts for.
        var lostWork = cost.FuelLostKgPerCycle * _engine.Cylinders[0].FuelLowerHeatingValue
                       * IndicatedEfficiency(work, fuel, _engine.Cylinders[0].FuelLowerHeatingValue);
        var netTorque = (work - lostWork) / (4.0 * Math.PI);

        var delivered = scavenging.Sum(s => s.DeliveredFreshKg);
        var overlapWeighted = scavenging.Sum(s => s.MeanScavengingPressureRatio * s.OverlapDeg);
        var overlapTotal = scavenging.Sum(s => s.OverlapDeg);

        _ = cycleTime;

        return new CamPoint(
            LobeCentreAngleDeg,
            OverlapDeg,
            torque,
            netTorque,
            ve,
            scavenging,
            cost,
            overlapTotal > 0 ? overlapWeighted / overlapTotal : double.NaN,
            delivered > 0 ? scavenging.Sum(s => s.BlowThroughKg) / delivered : 0.0);
    }

    private static double IndicatedEfficiency(double work, double fuelKg, double lhv) =>
        fuelKg > 0 && lhv > 0 ? Math.Clamp(work / (fuelKg * lhv), 0.0, 0.6) : 0.35;

    private double RunCycle()
    {
        var startAngle = _engine.Angle;
        var startTime = _engine.Time;

        while (_engine.Angle - startAngle < 720.0)
        {
            var before = _engine.Time;
            _engine.Step();
            var dt = _engine.Time - before;

            _turbine?.IntegrateAtFixedSpeed(dt);
            _scavenging.Record(_engine.Angle, dt);
        }

        return _engine.Time - startTime;
    }

    /// <summary>
    /// The engine, with cam timing set by lobe centre angle at fixed duration.
    ///
    /// Both cams keep their 230° duration and move their centres symmetrically
    /// about TDC overlap, so LCA is the only variable and overlap follows as
    /// 230 − 2·LCA. Sweeping overlap by changing DURATION instead would confound
    /// the scavenging question with a breathing question.
    /// </summary>
    private static EngineModelDocument FourCylinder(double lca, bool turbocharged) => new()
    {
        Name = turbocharged ? "cam sweep, boosted" : "cam sweep, naturally aspirated",
        Engine = new EngineSpec
        {
            BoreMm = 86, StrokeMm = 86, RodLengthMm = 144,
            CompressionRatio = turbocharged ? 9.5 : 11.0,
            CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec
        {
            HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10,
            OpenDeg = 360.0 + lca - 115.0,
            CloseDeg = 360.0 + lca + 115.0,
        },
        ExhaustValves = new ValveTrainSpec
        {
            HeadDiameterMm = 28, Count = 2, MaxLiftMm = 9.5,
            OpenDeg = 360.0 - lca - 115.0,
            CloseDeg = 360.0 - lca + 115.0,
        },
        IntakeRunner = new DuctSpec { LengthMm = 280, DiameterMm = 42 },
        ExhaustRunner = new DuctSpec { LengthMm = 400, DiameterMm = 34 },
        Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
        Solver = new SolverSpec { CellSizeMm = 12.0, MinCycles = 4, MaxCycles = 8 },
        ExhaustManifold = CollectorLibrary.Build("4-1", new CollectorGeometry(
            Cylinders: 4,
            PrimaryLengthMm: 300,
            PrimaryDiameterMm: 34,
            CollectorLengthMm: 120,
            CollectorDiameterMm: 50,
            MergeAngleDeg: 15,
            TailLengthMm: 80,
            TailDiameterMm: 52)),
    };
}
