using System.Collections.Concurrent;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Model;

namespace WaveBench.Core.Solver;

/// <summary>Converged steady-state result at one engine speed.</summary>
public sealed record OperatingPointResult
{
    public required double Rpm { get; init; }

    public required double VolumetricEfficiency { get; init; }

    public required double ImepPa { get; init; }

    public required double BmepPa { get; init; }

    public required double TorqueNm { get; init; }

    public required double PowerW { get; init; }

    /// <summary>g/kWh; NaN for motored runs.</summary>
    public required double BsfcGPerKwh { get; init; }

    public required double PeakPressurePa { get; init; }

    public required double KnockIntegral { get; init; }

    public required int CyclesToConvergence { get; init; }

    // ---- Per cylinder (plan §8.4: the VE bar chart with numeric spread,
    //      knock margin and EGT per cylinder) ------------------------------
    //
    // A four-cylinder mean hides the thing a header is designed to fix. Two
    // cylinders at 1.05 and two at 0.85 average to the same 0.95 as four even
    // ones, and only one of those is a manifold worth building.

    /// <summary>Volumetric efficiency of each cylinder, in cylinder order.</summary>
    public double[] PerCylinderVolumetricEfficiency { get; init; } = [];

    /// <summary>Indicated mean effective pressure of each cylinder, Pa.</summary>
    public double[] PerCylinderImepPa { get; init; } = [];

    /// <summary>Peak cylinder pressure of each cylinder, Pa.</summary>
    public double[] PerCylinderPeakPressurePa { get; init; } = [];

    /// <summary>Livengood–Wu knock integral of each cylinder; 1.0 is onset.</summary>
    public double[] PerCylinderKnockIntegral { get; init; } = [];

    /// <summary>Mass-weighted exhaust gas temperature of each cylinder, K.</summary>
    public double[] PerCylinderExhaustTemperatureK { get; init; } = [];

    /// <summary>
    /// Spread of per-cylinder VE as a fraction of the mean — the single number
    /// that says whether the cylinders are being fed alike. Zero for a single.
    /// </summary>
    public double VolumetricEfficiencySpread =>
        PerCylinderVolumetricEfficiency.Length < 2
            ? 0.0
            : (PerCylinderVolumetricEfficiency.Max() - PerCylinderVolumetricEfficiency.Min())
              / Math.Max(PerCylinderVolumetricEfficiency.Average(), 1e-12);
}

/// <summary>
/// Headless execution engine (plan Phase 7): single operating points, rpm
/// sweeps (embarrassingly parallel, deterministic per point — each point is
/// an independent engine instance, so parallel scheduling cannot change any
/// result), and the one-click mesh-sensitivity study of plan §5.3.
/// </summary>
public static class OperatingPointRunner
{
    public static OperatingPointResult Run(EngineModelDocument document, double rpm, double? cellSizeScale = null)
    {
        var engine = EngineBuilder.Build(document, rpm, cellSizeScale);
        engine.WallConvergenceK = document.PipeThermal.WallConvergenceK;
        var (result, cycles) = engine.RunToConvergence(
            r => r.NetValveMass.Length > 0 ? r.NetValveMass[0] : 0.0,
            document.Solver.ConvergenceTolerance,
            document.Solver.MinCycles,
            document.Solver.MaxCycles);

        var p0 = document.Ambient.PressureKPa * 1000.0;
        var rhoRef = p0 / (PerfectGas.Air.SpecificGasConstant * document.Ambient.TemperatureK);
        var crank = engine.Cylinders[0].Geometry;
        var totalDisplacement = crank.DisplacedVolume * engine.Cylinders.Count;

        // Intake valves are even indices (builder order: intake, exhaust per cylinder).
        var cylinders = engine.Cylinders.Count;
        var perCylinderVe = new double[cylinders];
        var inducted = 0.0;
        for (var c = 0; c < cylinders; c++)
        {
            var intakeValve = 2 * c;
            var mass = intakeValve < result.NetValveMass.Length ? result.NetValveMass[intakeValve] : 0.0;
            inducted += mass;
            perCylinderVe[c] = mass / (rhoRef * crank.DisplacedVolume);
        }

        var ve = inducted / (rhoRef * totalDisplacement);
        var imep = result.Imep.Average();
        var peak = result.PeakPressure.Max();
        var friction = new ChenFlynnFriction();
        var bmep = PerformanceMetrics.Bmep(imep, friction, peak, crank.MeanPistonSpeed(rpm));
        var torque = PerformanceMetrics.Torque(bmep, totalDisplacement);
        var power = PerformanceMetrics.Power(torque, rpm);
        var fuel = result.FuelMass.Sum();
        var bsfc = fuel > 0 && power > 0
            ? PerformanceMetrics.Bsfc(fuel, result.CycleDuration, power) * 3.6e9
            : double.NaN;

        return new OperatingPointResult
        {
            Rpm = rpm,
            VolumetricEfficiency = ve,
            ImepPa = imep,
            BmepPa = bmep,
            TorqueNm = torque,
            PowerW = power,
            BsfcGPerKwh = bsfc,
            PeakPressurePa = peak,
            KnockIntegral = result.KnockIntegral.DefaultIfEmpty(0.0).Max(),
            CyclesToConvergence = cycles,
            PerCylinderVolumetricEfficiency = perCylinderVe,
            PerCylinderImepPa = result.Imep,
            PerCylinderPeakPressurePa = result.PeakPressure,
            PerCylinderKnockIntegral = result.KnockIntegral,
            PerCylinderExhaustTemperatureK = result.ExhaustTemperature,
        };
    }

    /// <summary>
    /// Parallel sweep across operating points with a bounded scheduler
    /// (plan §5.7). Results are returned in rpm order regardless of
    /// completion order.
    /// </summary>
    public static IReadOnlyList<OperatingPointResult> Sweep(
        EngineModelDocument document, IReadOnlyList<double> rpms, int? maxParallel = null)
    {
        var results = new OperatingPointResult[rpms.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallel ?? Math.Max(1, Environment.ProcessorCount - 1),
        };
        Parallel.For(0, rpms.Count, options, i => results[i] = Run(document, rpms[i]));
        return results;
    }

    /// <summary>
    /// Plan §5.3 mesh-sensitivity study: 0.5×, 1× and 2× cell size at one
    /// operating point, reporting the relative change of the chosen headline
    /// metric (torque by default). Warn above 1%.
    /// </summary>
    public static MeshSensitivityResult MeshSensitivity(EngineModelDocument document, double rpm)
    {
        var fine = Run(document, rpm, cellSizeScale: 0.5);
        var baseline = Run(document, rpm, cellSizeScale: 1.0);
        var coarse = Run(document, rpm, cellSizeScale: 2.0);

        double Metric(OperatingPointResult r) => r.TorqueNm != 0 ? r.TorqueNm : r.VolumetricEfficiency;
        var fineDelta = Math.Abs(Metric(fine) - Metric(baseline)) / Math.Abs(Metric(baseline));
        var coarseDelta = Math.Abs(Metric(coarse) - Metric(baseline)) / Math.Abs(Metric(baseline));

        return new MeshSensitivityResult(fine, baseline, coarse, fineDelta, coarseDelta,
            Warning: Math.Max(fineDelta, coarseDelta) > 0.01);
    }
}

public sealed record MeshSensitivityResult(
    OperatingPointResult Fine,
    OperatingPointResult Baseline,
    OperatingPointResult Coarse,
    double FineRelativeChange,
    double CoarseRelativeChange,
    bool Warning);
