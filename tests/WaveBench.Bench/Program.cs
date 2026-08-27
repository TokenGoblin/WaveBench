using System.Diagnostics;
using BenchmarkDotNet.Running;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.ViewModels;

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

        if (args.Length > 0 && args[0].Equals("canvas", StringComparison.OrdinalIgnoreCase))
        {
            RunCanvasGate();
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

    /// <summary>
    /// Plan Phase 18 gate: the canvas stays at 60 fps with 40 components.
    ///
    /// What is measured is the per-frame work the VIEW MODEL does — the
    /// geometry summary, the design warnings and a full hit-test pass — since
    /// that is what runs on every frame of a drag and what would make the
    /// canvas stutter. WPF's own rendering of 40 rectangles is not the risk.
    /// Measured here rather than in the xUnit suite for the same reason as
    /// the TMM gate: that suite runs engine solves in parallel, so a
    /// wall-clock sample taken in it measures contention.
    /// </summary>
    private static void RunCanvasGate()
    {
        var document = new EngineModelDocument
        {
            Name = "canvas gate",
            Engine = new EngineSpec
            {
                BoreMm = 86, StrokeMm = 86, RodLengthMm = 145, CompressionRatio = 10.5, CylinderCount = 8,
            },
            IntakeValves = new ValveTrainSpec
            {
                HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580,
            },
            ExhaustValves = new ValveTrainSpec
            {
                HeadDiameterMm = 28, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 370,
            },
            IntakeRunner = new DuctSpec { LengthMm = 420, DiameterMm = 38 },
            ExhaustRunner = new DuctSpec { LengthMm = 600, DiameterMm = 36 },
        };

        var session = new ProjectSession(document);
        var workspace = new ManifoldWorkspace(session);

        // An eight-cylinder X-pipe is the largest thing the library builds;
        // pad with loose components to reach the gate's 40.
        workspace.ApplyConfiguration("x-pipe");
        while ((session.Document.ExhaustManifold?.Nodes.Count ?? 0) < 40)
        {
            workspace.Add(WaveBench.Model.ManifoldNodeKind.Pipe, 14, session.Document.ExhaustManifold!.Nodes.Count);
        }

        var nodes = session.Document.ExhaustManifold!.Nodes.Count;
        workspace.SelectAll();

        // Warm up the JIT.
        for (var i = 0; i < 50; i++)
        {
            _ = workspace.Summary();
            _ = workspace.Warnings();
            HitTestPass(workspace);
        }

        const int frames = 300;
        var samples = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            var sw = Stopwatch.StartNew();
            _ = workspace.Summary();
            _ = workspace.Warnings();
            HitTestPass(workspace);
            sw.Stop();
            samples[f] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var median = samples[frames / 2];
        var worst = samples[^1];
        var p99 = samples[(int)(frames * 0.99)];

        Console.WriteLine($"Canvas gate: {nodes} components, {frames} frames of summary + warnings + hit-test");
        Console.WriteLine($"median {median:F3} ms, p99 {p99:F3} ms, worst {worst:F3} ms (60 fps budget: 16.67 ms)");
        Console.WriteLine($"headroom at p99: {16.67 / Math.Max(p99, 1e-6):F0}×");
        Console.WriteLine(p99 < 16.67 ? "CANVAS GATE MET" : "CANVAS GATE MISSED");
        Console.WriteLine();
    }

    /// <summary>One frame's worth of hit-testing: every node against a cursor.</summary>
    private static void HitTestPass(ManifoldWorkspace workspace)
    {
        var spec = workspace.Manifold;
        if (spec is null)
        {
            return;
        }

        double best = double.MaxValue;
        foreach (var node in spec.Nodes)
        {
            var dx = node.X - 6.0;
            var dy = node.Y - 3.0;
            best = Math.Min(best, (dx * dx) + (dy * dy));
        }

        _ = best;
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
