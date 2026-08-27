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
    /// <summary>
    /// Builds the engine at a given speed and load.
    ///
    /// <paramref name="intakeLoadFraction"/> is the intake manifold absolute
    /// pressure as a fraction of ambient: 1.0 is wide-open throttle, ~0.35 a
    /// light cruise. It is applied to the intake reservoir's stagnation
    /// pressure, which is the standard way part load is set at this modelling
    /// level — the throttle sits upstream of the runner and its effect on the
    /// runner's boundary is the reduced plenum pressure.
    ///
    /// <b>Simplification, stated because it has consequences.</b> This is a
    /// steady pressure drop, not a throttle. It does not model the plate's
    /// unsteady loss, the plenum volume's own wave dynamics, or the reflection
    /// the plate presents to a runner pulse — a real part-throttle intake is
    /// acoustically closer to a closed end than an open one, so predicted
    /// intake noise at low load is optimistic. Modelling it properly needs the
    /// orifice-plus-plenum topology that arrives with the manifold canvas
    /// (Phase 18); <see cref="Components.ThrottleValve"/> is already there for
    /// it. Fuelling needs no adjustment: the charge fuel fraction is a mass
    /// fraction at fixed lambda, so less air is already less fuel.
    /// </summary>
    public static EngineSimulator Build(
        EngineModelDocument document, double rpm, double? cellSizeScale = null, double intakeLoadFraction = 1.0)
    {
        if (intakeLoadFraction is <= 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intakeLoadFraction),
                intakeLoadFraction,
                "Load is the intake manifold pressure as a fraction of ambient, in (0, 1].");
        }

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

        // A manifold graph REPLACES the per-cylinder exhaust runner. Built
        // once, before the cylinder loop, because a collector is shared by
        // every cylinder that feeds it — which is the entire point of one.
        AssembledManifold? manifold = null;
        if (document.ExhaustManifold is { Nodes.Count: > 0 } spec)
        {
            manifold = ManifoldAssembler.Build(
                spec, engine, gas, cellSize, limiter, rho0, p0, t0, document.Solver.Cfl);

            // Notes travel on the ENGINE, not on the builder. A static would
            // be shared mutable state, and OperatingPointRunner.Sweep builds
            // engines in parallel.
            foreach (var note in manifold.Notes)
            {
                engine.Notes.Add(note);
            }

            // Keep the node-id → duct map on the engine so an analysis can
            // read the SOLVED state of a named pipe afterwards. Without it the
            // pulse-interference diagram (§2.8) has to be handed a nominal
            // sound speed, which is the one thing the plan says it must not
            // be.
            engine.ManifoldPipes = manifold.Pipes;
        }

        for (var c = 0; c < document.Engine.CylinderCount; c++)
        {
            // Start the intake runner at the plenum's pressure, not ambient:
            // at 0.35 load that is a 65 kPa initial error to converge away.
            // Density follows pressure at fixed temperature.
            var intake = MakeDuct(
                document.IntakeRunner, cellSize, limiter, gas,
                rho0 * intakeLoadFraction, p0 * intakeLoadFraction, document.Solver.Cfl);
            // With a manifold the exhaust side is already built and shared;
            // without one each cylinder gets its own runner to atmosphere.
            DuctSolver exhaust;
            bool exhaustAtLeftEnd;
            if (manifold is not null)
            {
                if (!manifold.Ports.TryGetValue(c + 1, out var attachment))
                {
                    throw new InvalidOperationException(
                        $"The exhaust manifold has no port for cylinder {c + 1}.");
                }

                exhaust = attachment.Duct;
                exhaustAtLeftEnd = attachment.AtLeftEnd;
            }
            else
            {
                exhaust = MakeDuct(document.ExhaustRunner, cellSize, limiter, gas, rho0, p0, document.Solver.Cfl);
                exhaust.RightBoundary = BoundaryKind.External;
                exhaust.RightEnd = new ReservoirBoundary { StagnationPressure = p0, StagnationTemperature = t0 };
                exhaustAtLeftEnd = true;
                engine.Ducts.Add(exhaust);
            }

            intake.LeftBoundary = BoundaryKind.External;
            intake.LeftEnd = new ReservoirBoundary
            {
                StagnationPressure = p0 * intakeLoadFraction,
                StagnationTemperature = t0,
            };

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
                cylinder.TwoZoneHeatTransfer = combustion.TwoZoneHeatTransfer;
                if (combustion.TrackKnock && fuel.Ron is { } ron)
                {
                    cylinder.KnockOctaneNumber = ron;
                }
            }

            engine.Ducts.Add(intake);
            engine.Cylinders.Add(cylinder);
            engine.Valves.Add(new ValveConnection(
                cylinder, intake, ductLeftEnd: false,
                MakeCam(document.IntakeValves),
                ToValveGeometry(document.IntakeValves)));
            engine.Valves.Add(new ValveConnection(
                cylinder, exhaust, ductLeftEnd: exhaustAtLeftEnd,
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
