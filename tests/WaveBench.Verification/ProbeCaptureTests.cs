using FluentAssertions;
using WaveBench.Analysis;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// The §3.4 capture chain end to end: converge → capture k cycles → resample
/// to the crank-angle grid → order analysis / results store. This is the
/// input path Phase 10 auralisation consumes, so it is tested as a path, not
/// as isolated methods.
/// </summary>
public class ProbeCaptureTests(ITestOutputHelper output)
{
    private static EngineModelDocument Model() => new()
    {
        Name = "capture test",
        Engine = new EngineSpec { BoreMm = 86, StrokeMm = 62, RodLengthMm = 107, CompressionRatio = 11 },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 31, Count = 2, MaxLiftMm = 10, OpenDeg = 340, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 26, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 380 },
        IntakeRunner = new DuctSpec { LengthMm = 600, DiameterMm = 38 },
        ExhaustRunner = new DuctSpec { LengthMm = 200, DiameterMm = 35 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
        Solver = new SolverSpec { CellSizeMm = 10.0, MinCycles = 3, MaxCycles = 6 },
    };

    [Fact]
    public void Capture_is_off_by_default_and_records_only_the_requested_cycles()
    {
        var engine = EngineBuilder.Build(Model(), 5000.0);
        var probe = engine.AddProbe(engine.Ducts[1], cell: 15, "exhaust");

        engine.RunCycle();
        engine.Capture.SampleCount.Should().Be(0, "capture must be opt-in");
        probe.Pressure.Should().BeEmpty();

        engine.CaptureCycles(2);

        engine.Capture.SampleCount.Should().BeGreaterThan(100);
        probe.Pressure.Count.Should().Be(engine.Capture.SampleCount,
            "probe samples ride the shared timeline exactly");

        // Exactly two cycles of crank angle were spanned.
        var span = engine.Capture.AnglesDeg[^1] - engine.Capture.AnglesDeg[0];
        span.Should().BeApproximately(1440.0, 2.0);

        // A second capture replaces the first (the "last k cycles" workflow).
        engine.CaptureCycles(1);
        (engine.Capture.AnglesDeg[^1] - engine.Capture.AnglesDeg[0]).Should().BeApproximately(720.0, 2.0);

        // And capture switches itself back off.
        var after = engine.Capture.SampleCount;
        engine.RunCycle();
        engine.Capture.SampleCount.Should().Be(after);
    }

    [Fact]
    public void Probe_records_the_pressure_the_solver_reports()
    {
        var engine = EngineBuilder.Build(Model(), 5000.0);
        var duct = engine.Ducts[1];
        var probe = engine.AddProbe(duct, cell: 10, "exhaust");

        engine.CaptureCycles(1);

        // Fast path and full state recovery must agree exactly.
        duct.GetPressure(10).Should().Be(duct.GetPrimitive(10).P);

        // The last recorded sample is the post-step pressure (float32 storage).
        probe.Pressure[^1].Should().BeApproximately((float)duct.GetPressure(10), 1.0f);
        probe.Pressure.Should().OnlyContain(p => p > 1000.0f && p < 5.0e6f, "physical exhaust pressures");
    }

    [Fact]
    public void Resampling_to_the_crank_angle_grid_preserves_the_signal_and_feeds_order_analysis()
    {
        const double rpm = 5000.0;
        var engine = EngineBuilder.Build(Model(), rpm);
        var probe = engine.AddProbe(engine.Ducts[1], cell: 15, "exhaust");

        engine.RunToConvergence(r => r.Imep[0], 5e-3, 3, 6);
        engine.CaptureCycles(4);

        const int perCycle = 1440;
        var resampled = probe.ResampleToCrankAngle(engine.Capture, perCycle);
        resampled.Length.Should().Be(4 * perCycle, "whole cycles only");

        // Resampling is interpolation: it cannot invent range.
        var rawMin = probe.Pressure.Min();
        var rawMax = probe.Pressure.Max();
        resampled.Min().Should().BeGreaterThanOrEqualTo(rawMin - 1.0f);
        resampled.Max().Should().BeLessThanOrEqualTo(rawMax + 1.0f);

        // The resampled grid is crank-locked, so a converged engine repeats
        // cycle for cycle — the property auralisation depends on for
        // seamless looping (plan §3.6).
        var cycle2 = resampled.Skip(perCycle).Take(perCycle).ToArray();
        var cycle4 = resampled.Skip(3 * perCycle).Take(perCycle).ToArray();
        var span = resampled.Max() - resampled.Min();
        var worst = cycle2.Zip(cycle4, (a, b) => Math.Abs(a - b)).Max();
        output.WriteLine($"cycle-to-cycle worst difference: {worst:F0} Pa of {span:F0} Pa span");
        worst.Should().BeLessThan(span * 0.05f, "a converged engine's captured cycles overlay");

        // Order content: a single-cylinder four-stroke fires once per 720°, so
        // ALL energy sits on multiples of the 0.5 firing order — OPI ≈ 1. The
        // narrow blowdown spike spreads that energy up the harmonic ladder,
        // which is why the peak is not at 0.5 itself.
        var rate = ProbeCapture.ResampledSampleRate(rpm, perCycle);
        var signal = resampled.Select(v => (double)v).ToArray();
        var mean = signal.Average();
        for (var i = 0; i < signal.Length; i++)
        {
            signal[i] -= mean;
        }

        var spectrum = OrderAnalysis.AtConstantSpeed(signal, rate, rpm, maxOrder: 8.0);
        output.WriteLine($"exhaust orders: 0.5 → {spectrum.AmplitudeAt(0.5):F0} Pa, " +
                         $"1.0 → {spectrum.AmplitudeAt(1.0):F0}, 1.5 → {spectrum.AmplitudeAt(1.5):F0}");
        CharacterMetrics.OrderPurityIndex(spectrum, firingOrder: 0.5).Should().BeGreaterThan(0.99,
            "one firing event per 720° puts every component on a multiple of the 0.5 order");
        spectrum.Amplitude.Max().Should().BeGreaterThan(500.0, "the blowdown pulse is a strong signal");
    }

    [Fact]
    public void Capture_round_trips_through_the_results_store_as_float32()
    {
        const double rpm = 5000.0;
        var engine = EngineBuilder.Build(Model(), rpm);
        var probe = engine.AddProbe(engine.Ducts[1], cell: 12, "exhaust");
        engine.CaptureCycles(2);
        var samples = probe.ResampleToCrankAngle(engine.Capture, 720);

        var path = Path.Combine(Path.GetTempPath(), $"wavebench-capture-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new ResultsStore(path))
            {
                var runId = store.BeginRun("capture", "0.1", "{}");
                var pointId = store.AddPoint(runId, new OperatingPointResult
                {
                    Rpm = rpm,
                    VolumetricEfficiency = 1.0,
                    ImepPa = 1e6,
                    BmepPa = 9e5,
                    TorqueNm = 30,
                    PowerW = 15000,
                    BsfcGPerKwh = 280,
                    PeakPressurePa = 6e6,
                    KnockIntegral = 0,
                    CyclesToConvergence = 5,
                });

                store.AddCapture(pointId, probe.Name, ProbeCapture.ResampledSampleRate(rpm, 720), samples);
                store.ReadCapture(pointId, probe.Name).Should().Equal(samples,
                    "float32 in, identical float32 out — no precision surprise");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Probes_are_validated_and_clearable()
    {
        var engine = EngineBuilder.Build(Model(), 5000.0);
        var foreign = new DuctSolver(DuctGeometry.Uniform(0.2, 20, 0.04), new PerfectGasModel(PerfectGas.Air));

        var act = () => engine.AddProbe(foreign, 5, "nope");
        act.Should().Throw<ArgumentException>("a probe on a duct outside the engine records nothing meaningful");

        engine.AddProbe(engine.Ducts[0], 5, "intake");
        engine.CaptureCycles(1);
        engine.ClearProbes();
        engine.Probes.Should().BeEmpty();
        engine.Capture.SampleCount.Should().Be(0, "clearing probes drops the timeline they shared");
    }
}
