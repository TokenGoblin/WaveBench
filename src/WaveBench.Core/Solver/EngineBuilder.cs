using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Thermo.Fuels;
using WaveBench.Model;

namespace WaveBench.Core.Solver;

/// <summary>
/// Builds a runnable <see cref="EngineSimulator"/> from the serialisable
/// model document — the document is the single source of truth (plan Part 0
/// rule 9); this is a pure projection of it. v0.1 topology: per-cylinder
/// intake runner from ambient and exhaust runner to ambient (collector
/// topologies join with the manifold canvas phases).
/// </summary>
public static class EngineBuilder
{
    public static EngineSimulator Build(EngineModelDocument document, double rpm, double? cellSizeScale = null)
    {
        var issues = document.Validate();
        if (issues.Any(i => i.Severity == ModelIssueSeverity.Error))
        {
            throw new InvalidOperationException(
                "Model has errors: " + string.Join("; ", issues.Where(i => i.Severity == ModelIssueSeverity.Error)
                    .Select(i => $"{i.Path}: {i.Message}")));
        }

        var gas = new PerfectGasModel(PerfectGas.Air);
        var p0 = document.Ambient.PressureKPa * 1000.0;
        var t0 = document.Ambient.TemperatureK;
        var rho0 = p0 / (PerfectGas.Air.SpecificGasConstant * t0);

        var crank = new CrankGeometry
        {
            Bore = document.Engine.BoreMm * 1e-3,
            Stroke = document.Engine.StrokeMm * 1e-3,
            RodLength = document.Engine.RodLengthMm * 1e-3,
            PinOffset = document.Engine.PinOffsetMm * 1e-3,
            CompressionRatio = document.Engine.CompressionRatio,
        };

        var cellSize = document.Solver.CellSizeMm * 1e-3 * (cellSizeScale ?? 1.0);
        var limiter = Enum.Parse<SlopeLimiterKind>(document.Solver.Limiter, ignoreCase: true);

        var engine = new EngineSimulator { Rpm = rpm };
        var phasePerCylinder = 720.0 / document.Engine.CylinderCount;

        for (var c = 0; c < document.Engine.CylinderCount; c++)
        {
            var intake = MakeDuct(document.IntakeRunner, cellSize, limiter, gas, rho0, p0, document.Solver.Cfl);
            var exhaust = MakeDuct(document.ExhaustRunner, cellSize, limiter, gas, rho0, p0, document.Solver.Cfl);

            intake.LeftBoundary = BoundaryKind.External;
            intake.LeftEnd = new ReservoirBoundary { StagnationPressure = p0, StagnationTemperature = t0 };
            exhaust.RightBoundary = BoundaryKind.External;
            exhaust.RightEnd = new ReservoirBoundary { StagnationPressure = p0, StagnationTemperature = t0 };

            var cylinder = new Cylinder(gas, crank, c * phasePerCylinder, p0, t0) { CylinderIndex = c };

            if (document.Combustion is { } combustion)
            {
                var fuel = FuelLibrary.All.FirstOrDefault(f =>
                        f.Name.Contains(combustion.Fuel, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Fuel '{combustion.Fuel}' not in the library.");

                cylinder.Combustion = new WiebeCombustion(combustion.StartDeg, combustion.DurationDeg);
                cylinder.FuelLowerHeatingValue = fuel.LowerHeatingValue;
                cylinder.FuelChargeFraction = 1.0 / (1.0 + fuel.StoichAfr * combustion.Lambda);
                cylinder.CombustionEfficiency = combustion.Efficiency;
                cylinder.HeatTransfer = Enum.Parse<HeatTransferCorrelation>(combustion.HeatTransfer, ignoreCase: true);
                cylinder.WallTemperature = combustion.WallTemperatureK;
                if (combustion.TrackKnock && fuel.Ron is { } ron)
                {
                    cylinder.KnockOctaneNumber = ron;
                }
            }

            engine.Ducts.Add(intake);
            engine.Ducts.Add(exhaust);
            engine.Cylinders.Add(cylinder);
            engine.Valves.Add(new ValveConnection(
                cylinder, intake, ductLeftEnd: false,
                MakeCam(document.IntakeValves),
                ToValveGeometry(document.IntakeValves)));
            engine.Valves.Add(new ValveConnection(
                cylinder, exhaust, ductLeftEnd: true,
                MakeCam(document.ExhaustValves),
                ToValveGeometry(document.ExhaustValves)));
        }

        return engine;
    }

    private static DuctSolver MakeDuct(
        DuctSpec spec, double cellSize, SlopeLimiterKind limiter,
        PerfectGasModel gas, double rho0, double p0, double cfl)
    {
        var length = spec.LengthMm * 1e-3;
        var cells = Math.Max(6, (int)Math.Round(length / cellSize)); // plan §5.3: ≥ 6 cells per pipe
        var duct = new DuctSolver(
            DuctGeometry.Uniform(length, cells, spec.DiameterMm * 1e-3, spec.RoughnessMm * 1e-3),
            gas)
        {
            Limiter = limiter,
            Cfl = cfl,
        };
        for (var i = 0; i < cells; i++)
        {
            duct.SetState(i, new PrimitiveState(rho0, 0.0, p0));
        }

        return duct;
    }

    private static CamProfile MakeCam(ValveTrainSpec spec) =>
        spec.CamShape.Equals("Sine", StringComparison.OrdinalIgnoreCase)
            ? CamProfile.HalfSine(spec.OpenDeg, spec.CloseDeg, spec.MaxLiftMm * 1e-3)
            : CamProfile.Harmonic(spec.OpenDeg, spec.CloseDeg, spec.MaxLiftMm * 1e-3);

    private static ValveGeometry ToValveGeometry(ValveTrainSpec spec) => new()
    {
        HeadDiameter = spec.HeadDiameterMm * 1e-3,
        ThroatDiameter = spec.ThroatDiameterMm * 1e-3,
        ValveCount = spec.Count,
    };
}
