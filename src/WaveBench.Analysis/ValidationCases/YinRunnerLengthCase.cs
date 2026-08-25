using WaveBench.Model;
using WaveBench.Core.Solver;

namespace WaveBench.Analysis.ValidationCases;

/// <summary>
/// Shared implementation of the Yin (CSU thesis) runner-length validation
/// case (see docs/physics.md §1.9 for provenance and the documented
/// short-runner discrepancy). Consumed by the nightly validation tests and
/// the CLI `validate` command, which also renders the comparison plot.
/// </summary>
public static class YinRunnerLengthCase
{
    /// <summary>Thesis Table 3.4, GT-Power column: runner length (m) → optimal rpm.</summary>
    public static IReadOnlyDictionary<double, double> PublishedOptimalRpm { get; } =
        new Dictionary<double, double>
        {
            [0.200] = 3750.0,
            [0.400] = 3750.0,
            [0.600] = 3750.0,
            [0.800] = 3000.0,
        };

    /// <summary>Runner lengths where our solver's regime matches the thesis's (resonance-dominated).</summary>
    public static IReadOnlyList<double> GatedRunners { get; } = [0.600, 0.800];

    public static EngineModelDocument Document(double runnerLengthM) => new()
    {
        Name = $"Yin CSU thesis engine, {runnerLengthM * 1000:F0} mm runner",
        Ambient = new AmbientSpec { PressureKPa = 100.0, TemperatureK = 300.0 },
        Engine = new EngineSpec
        {
            BoreMm = 100.0,
            StrokeMm = 100.0,
            RodLengthMm = 250.0,
            CompressionRatio = 10.0,
        },
        // Thesis timing: IVO 10 BTDC (350°), IVC 45 ABDC (585°); EVO 45 BBDC
        // (135°), EVC 10 ATDC (370°); 10 mm lift; valves 50/40 mm.
        IntakeValves = new ValveTrainSpec
        {
            HeadDiameterMm = 50.0, MaxLiftMm = 10.0, OpenDeg = 350.0, CloseDeg = 585.0, CamShape = "Sine",
        },
        ExhaustValves = new ValveTrainSpec
        {
            HeadDiameterMm = 40.0, MaxLiftMm = 10.0, OpenDeg = 135.0, CloseDeg = 370.0, CamShape = "Sine",
        },
        IntakeRunner = new DuctSpec { LengthMm = runnerLengthM * 1000.0, DiameterMm = 50.0 },
        ExhaustRunner = new DuctSpec { LengthMm = 50.0, DiameterMm = 40.0 },
        Combustion = new CombustionSpec
        {
            StartDeg = -35.0,
            DurationDeg = 60.0,
            Fuel = "RON95",
            WallTemperatureK = 450.0,
            TrackKnock = false,
        },
        Solver = new SolverSpec { CellSizeMm = 8.0, MinCycles = 5, MaxCycles = 20, ConvergenceTolerance = 2e-3 },
    };

    public sealed record CaseResult(
        double RunnerLengthM,
        IReadOnlyList<OperatingPointResult> Sweep,
        double PeakRpm,
        double PublishedRpm);

    /// <summary>Run the sweep for one runner length (2500–5500 rpm, 250 steps).</summary>
    public static CaseResult RunOne(double runnerLengthM, Action<string>? progress = null)
    {
        var document = Document(runnerLengthM);
        var rpms = new List<double>();
        for (var rpm = 2500.0; rpm <= 5500.0; rpm += 250.0)
        {
            rpms.Add(rpm);
        }

        var sweep = OperatingPointRunner.Sweep(document, rpms);
        var peak = sweep.MaxBy(p => p.VolumetricEfficiency)!;
        progress?.Invoke(
            $"runner {runnerLengthM * 1000:F0} mm: peak {peak.Rpm:F0} rpm " +
            $"(published {PublishedOptimalRpm[runnerLengthM]:F0})");
        return new CaseResult(runnerLengthM, sweep, peak.Rpm, PublishedOptimalRpm[runnerLengthM]);
    }

    public static IReadOnlyList<CaseResult> RunAll(Action<string>? progress = null) =>
        PublishedOptimalRpm.Keys.OrderBy(k => k).Select(k => RunOne(k, progress)).ToList();
}
