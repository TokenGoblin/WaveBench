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

    /// <summary>
    /// Plan Phase 8 gate: a 20-element TMM network across 1–10 kHz in under
    /// 10 ms, so a geometry slider refreshes interactively. Measured here
    /// rather than in the xUnit suite, which runs engine simulations in
    /// parallel and therefore cannot time anything meaningfully.
    /// </summary>
    private static void RunTmmGate()
    {
        var air = WaveBench.Acoustics.AcousticMedium.Air20C;
        var pipeArea = Math.PI / 4.0 * 0.040 * 0.040;
        var chamberArea = Math.PI / 4.0 * 0.080 * 0.080;

        var network = new WaveBench.Acoustics.AcousticNetwork(air, pipeArea, pipeArea);
        for (var i = 0; i < 6; i++)
        {
            network.Elements.Add(new WaveBench.Acoustics.UniformDuctElement(0.1 + 0.01 * i, pipeArea));
            network.Elements.Add(new WaveBench.Acoustics.AreaDiscontinuityElement(pipeArea, chamberArea));
            network.Elements.Add(new WaveBench.Acoustics.QuarterWaveStubElement(0.2, pipeArea * 0.5));
        }

        network.Elements.Add(new WaveBench.Acoustics.UniformDuctElement(0.3, pipeArea));
        network.Elements.Add(new WaveBench.Acoustics.HelmholtzResonatorElement(0.04, 2e-4, 8e-4));

        var frequencies = new double[512];
        for (var i = 0; i < frequencies.Length; i++)
        {
            frequencies[i] = 1000.0 + 9000.0 * i / (frequencies.Length - 1);
        }

        network.TransmissionLossSweep(frequencies);

        var samples = new double[21];
        for (var i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            network.TransmissionLossSweep(frequencies);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        Console.WriteLine($"TMM gate: {network.Elements.Count} elements, 512 frequencies 1–10 kHz");
        Console.WriteLine($"median {median:F2} ms, best {samples[0]:F2} ms (plan Phase 8 target: < 10 ms)");
        Console.WriteLine(median < 10.0 ? "TMM GATE MET" : "TMM GATE MISSED");
        Console.WriteLine();
    }

    private static void RunBudget()
    {
        RunTmmGate();

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
