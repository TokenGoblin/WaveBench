using WaveBench.Boost;
using WaveBench.Boost.Thermal;
using WaveBench.Boost.Unsteady;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Verification.Boost;

/// <summary>
/// A single-cylinder engine with a real turbine on its exhaust runner and a
/// <see cref="TransientDriver"/> wired to a synthetic compressor, assembled
/// the same way <see cref="TurbochargedEngineRig"/> assembles its
/// four-cylinder rig: the document model builds the engine, and the turbine
/// stage replaces the atmosphere the exhaust runner would otherwise see.
///
/// One cylinder rather than four, and the quasi-steady turbine model rather
/// than the volute-resolved one: Stage B is testing the ORCHESTRATION of
/// already-verified physics (shaft, thermal, compressor, gas dynamics)
/// against a scripted boundary, not the turbine's own pulsating hysteresis —
/// that is Phase 13's concern, and <c>TurbineHysteresisTests</c>/
/// <c>RotorMeshProbeTests</c> already cover it. Keeping the mesh small keeps a
/// multi-hundred-millisecond transient inside a reasonable CI wall-clock
/// budget.
/// </summary>
internal sealed class TransientSpoolRig
{
    public TransientSpoolRig(
        double rpm = 3000.0,
        double initialShaftRpm = 40_000.0,
        double initialLoadFraction = 0.35,
        double? shaftInertia = null,
        BearingFriction? friction = null,
        double cellSizeMm = 20.0,
        TurboThermalProperties? thermalProperties = null,
        TurboThermalModel? sharedThermal = null)
    {
        Document = SingleCylinder(cellSizeMm);
        Engine = EngineBuilder.Build(Document, rpm, intakeLoadFraction: initialLoadFraction);
        Engine.WallConvergenceK = Document.PipeThermal.WallConvergenceK;

        var exhaustValve = Engine.Valves.First(v => !v.IsIntake);
        var exhaustDuct = exhaustValve.Duct;

        // The turbine attaches at the end OPPOSITE the valve — the same
        // convention TurbochargedEngineRig.FindOutlet uses for a manifold
        // graph, specialised to the no-manifold single-cylinder topology
        // EngineBuilder.Build gives a duct with no ExhaustManifold set.
        var turbineAtLeftEnd = !exhaustValve.DuctLeftEnd;

        var gas = (PerfectGasModel)exhaustDuct.Gas;
        var shaft = new TurboShaft(shaftInertia ?? 3.1e-6, initialShaftRpm, friction);

        Stage = TurbineStage.Build(
            TurbineModelKind.QuasiSteady,
            SyntheticTurbo.Turbine(),
            shaft,
            [(exhaustDuct, turbineAtLeftEnd, "single")],
            new VoluteGeometry(0.150, exhaustDuct.Geometry.FaceArea[0], 8.0e-4, 12),
            gas);

        Thermal = sharedThermal ?? new TurboThermalModel(thermalProperties);

        AmbientPressurePa = Document.Ambient.PressureKPa * 1000.0;
        AmbientTemperatureK = Document.Ambient.TemperatureK;

        Driver = new TransientDriver(
            Engine, Stage, SyntheticTurbo.Compressor(), Thermal, AmbientPressurePa, AmbientTemperatureK);
    }

    public EngineModelDocument Document { get; }

    public EngineSimulator Engine { get; }

    public TurbineStage Stage { get; }

    public TurboThermalModel Thermal { get; }

    public TransientDriver Driver { get; }

    public double AmbientPressurePa { get; }

    public double AmbientTemperatureK { get; }

    private static EngineModelDocument SingleCylinder(double cellSizeMm) => new()
    {
        Name = "transient spool rig",
        Engine = new EngineSpec
        {
            BoreMm = 86, StrokeMm = 86, RodLengthMm = 144, CompressionRatio = 9.5, CylinderCount = 1,
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
        ExhaustRunner = new DuctSpec { LengthMm = 200, DiameterMm = 38 },
        Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
        Solver = new SolverSpec { CellSizeMm = cellSizeMm, MinCycles = 2, MaxCycles = 4 },
    };
}
