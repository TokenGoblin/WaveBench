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
        var inducted = 0.0;
        for (var v = 0; v < result.NetValveMass.Length; v += 2)
        {
            inducted += result.NetValveMass[v];
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
