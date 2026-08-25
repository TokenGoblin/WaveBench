using System.Diagnostics;
using BenchmarkDotNet.Running;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Bench;

/// <summary>
/// BenchmarkDotNet host, plus the plan §5.7 budget measurement mode:
/// `dotnet run -c Release -- budget` times a four-cylinder model for 30
/// cycles at 8000 rpm against the &lt; 5 s/operating-point target.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("budget", StringComparison.OrdinalIgnoreCase))
        {
            RunBudget();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    private static void RunBudget()
    {
        var document = new EngineModelDocument
        {
            Name = "budget 4-cylinder",
            Engine = new EngineSpec
            {
                BoreMm = 82, StrokeMm = 56.5, RodLengthMm = 100, CompressionRatio = 12, CylinderCount = 4,
            },
            IntakeValves = new ValveTrainSpec { HeadDiameterMm = 29, Count = 2, MaxLiftMm = 9.5, OpenDeg = 340, CloseDeg = 590 },
            ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 24, Count = 2, MaxLiftMm = 9.0, OpenDeg = 130, CloseDeg = 380 },
            IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 36 },
            ExhaustRunner = new DuctSpec { LengthMm = 450, DiameterMm = 34 },
            Combustion = new CombustionSpec { Fuel = "RON95" },
            // 7.5 mm cells: inside the plan §5.3 performance-run range
            // (5–15 mm); the mesh-sensitivity utility exists to justify the
            // choice per model.
            Solver = new SolverSpec { CellSizeMm = 7.5, MinCycles = 30, MaxCycles = 30 },
        };

        // Warm-up (JIT) with a short run.
        var warm = document with { Solver = document.Solver with { MinCycles = 2, MaxCycles = 2 } };
        OperatingPointRunner.Run(warm, 8000.0);

        // Three timed runs; the minimum isolates background-load noise, which
        // is ±40% on a busy desktop.
        var best = TimeSpan.MaxValue;
        OperatingPointResult result = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var attemptWatch = Stopwatch.StartNew();
            result = OperatingPointRunner.Run(document, 8000.0);
            attemptWatch.Stop();
            Console.WriteLine($"attempt {attempt + 1}: {attemptWatch.Elapsed.TotalSeconds:F2} s");
            if (attemptWatch.Elapsed < best)
            {
                best = attemptWatch.Elapsed;
            }
        }

        WaveBench.Core.EngineModel.EngineSimulator.EnableProfiling = true;
        Array.Clear(WaveBench.Core.EngineModel.EngineSimulator.ProfileTicks);
        var stopwatch = Stopwatch.StartNew();
        OperatingPointRunner.Run(document, 8000.0);
        stopwatch.Stop();
        WaveBench.Core.EngineModel.EngineSimulator.EnableProfiling = false;
        stopwatch = Stopwatch.StartNew();
        stopwatch.Stop();

        var ticks = WaveBench.Core.EngineModel.EngineSimulator.ProfileTicks;
        double Sec(long t) => t / (double)Stopwatch.Frequency;
        Console.WriteLine($"breakdown: dtScan+junctions {Sec(ticks[3]):F2} s, valves {Sec(ticks[0]):F2} s, " +
                          $"ducts {Sec(ticks[1]):F2} s, cylinders+plenums {Sec(ticks[2]):F2} s");

        // Micro-timing: one engine-sized duct stepped alone.
        var probe = engineForMicro();
        var duct0 = probe.Ducts[0];
        var dtMicro = duct0.StableTimestep();
        for (var i = 0; i < 1000; i++) { duct0.Step(dtMicro); } // warm
        var sw2 = Stopwatch.StartNew();
        const int micro = 20_000;
        for (var i = 0; i < micro; i++) { duct0.Step(dtMicro); }
        sw2.Stop();
        Console.WriteLine($"single {duct0.CellCount}-cell duct: {sw2.Elapsed.TotalMilliseconds * 1000.0 / micro:F1} us/step " +
                          $"({sw2.Elapsed.TotalSeconds * 1e9 / (micro * (double)duct0.CellCount):F0} ns/cell-step)");

        WaveBench.Core.EngineModel.EngineSimulator engineForMicro() =>
            EngineBuilder.Build(document, 8000.0);

        var engine = EngineBuilder.Build(document, 8000.0);
        var cells = engine.Ducts.Sum(d => d.CellCount);

        Console.WriteLine($"budget case: 4 cylinders, {engine.Ducts.Count} pipes, {cells} cells, 30 cycles at 8000 rpm");
        Console.WriteLine($"wall time (best of 3): {best.TotalSeconds:F2} s (plan §5.7 target: < 5 s)");
        Console.WriteLine($"result: VE {result.VolumetricEfficiency:F3}, torque {result.TorqueNm:F1} Nm");
        Console.WriteLine(best.TotalSeconds < 5.0 ? "BUDGET MET" : "BUDGET MISSED — profile before adding features (plan §5.7)");
    }
}
