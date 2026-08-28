using WaveBench.Boost;
using WaveBench.Boost.Unsteady;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Verification.Boost;

/// <summary>What one primary diameter produced.</summary>
/// <param name="PrimaryDiameterMm">Swept variable.</param>
/// <param name="Metrics">Delivery metrics at the turbine inlet.</param>
/// <param name="MeanTurbinePowerW">Mean shaft power over the measured cycle.</param>
/// <param name="VolumetricEfficiency">What the engine actually breathed.</param>
/// <param name="ImepPa">Indicated mean effective pressure, the load the pumping loss comes out of.</param>
public sealed record PrimarySweepPoint(
    double PrimaryDiameterMm,
    TurbineDeliveryMetrics Metrics,
    double MeanTurbinePowerW,
    double VolumetricEfficiency,
    double ImepPa);

/// <summary>
/// A four-cylinder engine with a 4-into-1 exhaust manifold feeding a real
/// turbine, assembled out of the parts that already exist: the document model
/// builds the engine, the collector library builds the header, and the turbine
/// stage replaces the atmosphere at the end of it.
///
/// Nothing here reaches into <c>WaveBench.Core</c>. The turbine attaches at the
/// same duct end the open termination used, the volute (when resolved) is just
/// another duct and junction added to the simulator's own lists, and the shaft
/// is integrated on the timestep the simulator advanced by. That the coupling
/// needs no changes to the gas dynamics is the point of having the rotor be a
/// boundary condition.
/// </summary>
internal sealed class TurbochargedEngineRig
{
    private readonly EngineSimulator _engine;
    private readonly TurbineStage _stage;
    private readonly TurbineDeliveryRecorder _recorder;

    public TurbochargedEngineRig(
        double primaryDiameterMm,
        double rpm,
        TurbineModelKind kind = TurbineModelKind.QuasiSteady,
        double shaftRpm = 110_000.0,
        double primaryLengthMm = 300.0,
        double cellSizeMm = 12.0)
    {
        PrimaryDiameterMm = primaryDiameterMm;
        Document = FourCylinder(primaryDiameterMm, primaryLengthMm, cellSizeMm);
        _engine = EngineBuilder.Build(Document, rpm);
        _engine.WallConvergenceK = Document.PipeThermal.WallConvergenceK;

        var (outlet, atLeftEnd) = FindOutlet(Document.ExhaustManifold!, _engine);

        var gas = (PerfectGasModel)outlet.Gas;
        var shaft = new TurboShaft(3.1e-6, shaftRpm);

        var voluteGeometry = new VoluteGeometry(
            LengthM: 0.150,
            InletAreaM2: atLeftEnd ? outlet.Geometry.FaceArea[0] : outlet.Geometry.FaceArea[^1],
            RotorAreaM2: 8.0e-4,
            Cells: 12);

        _stage = TurbineStage.Build(
            kind, SyntheticTurbo.Turbine(), shaft,
            [(outlet, atLeftEnd, "single")],
            voluteGeometry, gas);

        // A resolved volute is another duct and another junction; the simulator
        // owns both from here, and steps them with everything else.
        foreach (var duct in _stage.OwnedDucts)
        {
            var p0 = Document.Ambient.PressureKPa * 1000.0;
            var rho = p0 / (PerfectGas.Air.SpecificGasConstant * Document.Ambient.TemperatureK);
            for (var i = 0; i < duct.CellCount; i++)
            {
                duct.SetState(i, new PrimitiveState(rho, 0.0, p0));
            }

            _engine.Ducts.Add(duct);
        }

        foreach (var junction in _stage.OwnedJunctions)
        {
            _engine.Junctions.Add(junction);
        }

        // Manifold volume: every exhaust pipe from the valve to the rotor face,
        // plus the volute when it is resolved. Displacement per exhaust event is
        // one cylinder's swept volume — a four-cylinder empties one of them per
        // 180°, not all four per cycle.
        var manifoldVolume = _engine.ManifoldPipes.Values.Sum(DuctVolume)
                             + (kind == TurbineModelKind.VoluteResolved ? voluteGeometry.VolumeM3 : 0.0);

        // γ and c_p of the gas the ENGINE is running, not of exhaust products:
        // EngineBuilder uses a perfect-air model throughout, and the delivery
        // metric's constant-pressure reference has to be computed on the same
        // gas as the measurement it is compared against.
        _recorder = new TurbineDeliveryRecorder(
            manifoldVolume,
            _engine.Cylinders[0].Geometry.DisplacedVolume,
            gamma: PerfectGas.Air.Gamma,
            cp: PerfectGas.Air.Gamma * PerfectGas.Air.SpecificGasConstant / (PerfectGas.Air.Gamma - 1.0));
    }

    public EngineModelDocument Document { get; }

    public EngineSimulator Engine => _engine;

    public TurbineStage Stage => _stage;

    public double PrimaryDiameterMm { get; }

    private static double DuctVolume(DuctSolver duct)
    {
        var volume = 0.0;
        for (var i = 0; i < duct.CellCount; i++)
        {
            volume += duct.Geometry.CellArea[i] * duct.CellSize;
        }

        return volume;
    }

    /// <summary>
    /// The pipe the atmosphere node was attached to, and which of its ends
    /// faced outward. Located from the graph rather than by name so it keeps
    /// working if the collector library changes its node ids.
    /// </summary>
    private static (DuctSolver Duct, bool AtLeftEnd) FindOutlet(ManifoldSpec spec, EngineSimulator engine)
    {
        var atmosphere = spec.Nodes.Single(n => n.Kind == ManifoldNodeKind.Atmosphere);
        var neighbour = spec.Upstream(atmosphere.Id).Concat(spec.Downstream(atmosphere.Id)).Single();
        var duct = engine.ManifoldPipes[neighbour];

        // If the atmosphere is downstream of the pipe, the pipe's RIGHT end
        // faced it, and that is where the turbine goes.
        var atLeftEnd = !spec.Downstream(neighbour).Contains(atmosphere.Id);
        return (duct, atLeftEnd);
    }

    /// <summary>
    /// Run to a periodic state and then record one cycle at the turbine inlet.
    /// </summary>
    public PrimarySweepPoint Run(int warmupCycles = 5)
    {
        for (var c = 0; c < warmupCycles; c++)
        {
            RunCycle(record: false);
        }

        _stage.ResetAccumulators();
        _recorder.Clear();
        var result = RunCycle(record: true);

        var rhoRef = Document.Ambient.PressureKPa * 1000.0
                     / (PerfectGas.Air.SpecificGasConstant * Document.Ambient.TemperatureK);
        var displacement = _engine.Cylinders[0].Geometry.DisplacedVolume * _engine.Cylinders.Count;

        var inducted = 0.0;
        for (var c = 0; c < _engine.Cylinders.Count; c++)
        {
            inducted += result.NetValveMass[2 * c];
        }

        return new PrimarySweepPoint(
            PrimaryDiameterMm,
            _recorder.Reduce(),
            _recorder.MeanPowerW(),
            inducted / (rhoRef * displacement),
            result.Imep.Average());
    }

    private CycleResult RunCycle(bool record)
    {
        var startAngle = _engine.Angle;
        var startTime = _engine.Time;
        var intakeMass = new double[_engine.Valves.Count];
        var workBefore = _engine.Cylinders.Select(c => c.CumulativeWork).ToArray();

        while (_engine.Angle - startAngle < 720.0)
        {
            var before = _engine.Time;
            _engine.Step();
            var dt = _engine.Time - before;

            for (var v = 0; v < _engine.Valves.Count; v++)
            {
                intakeMass[v] += _engine.Valves[v].MassFlow * dt;
            }

            _stage.IntegrateAtFixedSpeed(dt);

            if (record)
            {
                _recorder.Record(_engine.Time, _stage.Entries[0].Rotor.Last);
            }
        }

        var imep = new double[_engine.Cylinders.Count];
        for (var c = 0; c < _engine.Cylinders.Count; c++)
        {
            imep[c] = (_engine.Cylinders[c].CumulativeWork - workBefore[c])
                      / _engine.Cylinders[c].Geometry.DisplacedVolume;
        }

        return new CycleResult
        {
            NetValveMass = intakeMass,
            EndAngle = _engine.Angle,
            CycleDuration = _engine.Time - startTime,
            Imep = imep,
        };
    }

    private static EngineModelDocument FourCylinder(
        double primaryDiameterMm, double primaryLengthMm, double cellSizeMm) => new()
    {
        Name = "turbocharged four",
        Engine = new EngineSpec
        {
            BoreMm = 86, StrokeMm = 86, RodLengthMm = 144, CompressionRatio = 9.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec
        {
            HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580,
        },
        ExhaustValves = new ValveTrainSpec
        {
            HeadDiameterMm = 28, Count = 2, MaxLiftMm = 9.5, OpenDeg = 140, CloseDeg = 370,
        },
        IntakeRunner = new DuctSpec { LengthMm = 280, DiameterMm = 42 },
        ExhaustRunner = new DuctSpec { LengthMm = 400, DiameterMm = primaryDiameterMm },
        Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
        Solver = new SolverSpec { CellSizeMm = cellSizeMm, MinCycles = 4, MaxCycles = 8 },

        // A turbo header, not an NA one (plan §4.6.1): short, small primaries
        // into the smallest collector that will carry the flow. The primary
        // diameter is the swept variable.
        ExhaustManifold = CollectorLibrary.Build("4-1", new CollectorGeometry(
            Cylinders: 4,
            PrimaryLengthMm: primaryLengthMm,
            PrimaryDiameterMm: primaryDiameterMm,
            CollectorLengthMm: 120,
            CollectorDiameterMm: 50,
            MergeAngleDeg: 15,
            TailLengthMm: 80,
            TailDiameterMm: 52)),
    };
}
