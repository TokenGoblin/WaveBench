using System.CommandLine;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Analysis;
using WaveBench.Analysis.ValidationCases;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Cli;

/// <summary>
/// Headless runner (plan Phase 7): run, sweep, mesh-sensitivity, validate,
/// info. No telemetry, no network calls.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var modelArg = new Argument<FileInfo>("model") { Description = "Engine model JSON file" };

        var rpmOption = new Option<double>("--rpm") { Description = "Engine speed, rpm", Required = true };
        var dbOption = new Option<FileInfo?>("--db") { Description = "SQLite results store to append to" };

        var runCommand = new Command("run", "Solve one operating point") { modelArg, rpmOption, dbOption };
        runCommand.SetAction(parse =>
        {
            var document = LoadModel(parse.GetValue(modelArg)!);
            var result = OperatingPointRunner.Run(document, parse.GetValue(rpmOption));
            PrintPoint(result);
            SaveIfRequested(parse.GetValue(dbOption), document, [result]);
            return 0;
        });

        var fromOption = new Option<double>("--from") { Description = "Sweep start, rpm", Required = true };
        var toOption = new Option<double>("--to") { Description = "Sweep end, rpm", Required = true };
        var stepOption = new Option<double>("--step") { Description = "Sweep step, rpm" };
        stepOption.DefaultValueFactory = _ => 500.0;
        var plotOption = new Option<FileInfo?>("--plot") { Description = "Write a torque/VE plot PNG" };
        var parallelOption = new Option<int?>("--parallel") { Description = "Max parallel operating points" };

        var sweepCommand = new Command("sweep", "Solve an rpm sweep (parallel)")
        {
            modelArg, fromOption, toOption, stepOption, dbOption, plotOption, parallelOption,
        };
        sweepCommand.SetAction(parse =>
        {
            var document = LoadModel(parse.GetValue(modelArg)!);
            var rpms = new List<double>();
            for (var rpm = parse.GetValue(fromOption); rpm <= parse.GetValue(toOption) + 1e-9;
                 rpm += parse.GetValue(stepOption))
            {
                rpms.Add(rpm);
            }

            var results = OperatingPointRunner.Sweep(document, rpms, parse.GetValue(parallelOption));
            foreach (var point in results)
            {
                PrintPoint(point);
            }

            SaveIfRequested(parse.GetValue(dbOption), document, results);
            if (parse.GetValue(plotOption) is { } plotFile)
            {
                WriteSweepPlot(plotFile.FullName, document.Name, results);
                Console.WriteLine($"plot written: {plotFile.FullName}");
            }

            return 0;
        });

        var meshCommand = new Command("mesh", "Mesh-sensitivity study at one operating point (plan §5.3)")
        {
            modelArg, rpmOption,
        };
        meshCommand.SetAction(parse =>
        {
            var document = LoadModel(parse.GetValue(modelArg)!);
            var sensitivity = OperatingPointRunner.MeshSensitivity(document, parse.GetValue(rpmOption));
            Console.WriteLine($"cells ×0.5: torque {sensitivity.Fine.TorqueNm:F2} Nm");
            Console.WriteLine($"cells ×1.0: torque {sensitivity.Baseline.TorqueNm:F2} Nm");
            Console.WriteLine($"cells ×2.0: torque {sensitivity.Coarse.TorqueNm:F2} Nm");
            Console.WriteLine($"relative change: fine {sensitivity.FineRelativeChange:P2}, coarse {sensitivity.CoarseRelativeChange:P2}");
            if (sensitivity.Warning)
            {
                Console.WriteLine("WARNING: mesh sensitivity above 1% — refine the mesh (plan §5.3).");
            }

            return sensitivity.Warning ? 2 : 0;
        });

        var outOption = new Option<DirectoryInfo>("--out") { Description = "Directory for plots and the summary" };
        outOption.DefaultValueFactory = _ => new DirectoryInfo("validation");
        var validateCommand = new Command("validate", "Run the validation cases and write report artefacts")
        {
            outOption,
        };
        validateCommand.SetAction(parse =>
        {
            var outDir = parse.GetValue(outOption)!;
            outDir.Create();
            Console.WriteLine("Yin (CSU thesis) runner-length case:");
            var results = YinRunnerLengthCase.RunAll(line => Console.WriteLine("  " + line));
            WriteYinReport(outDir.FullName, results);
            var gatedOk = YinRunnerLengthCase.GatedRunners.All(r =>
                Math.Abs(results.First(x => x.RunnerLengthM == r).PeakRpm -
                         YinRunnerLengthCase.PublishedOptimalRpm[r]) <= 250.0);
            Console.WriteLine(gatedOk
                ? "gated comparisons PASS (see the report for the documented short-runner discrepancy)"
                : "gated comparisons FAIL");
            return gatedOk ? 0 : 1;
        });

        var infoCommand = new Command("info", "Validate a model and print quick estimates (plan §2.10)") { modelArg };
        infoCommand.SetAction(parse =>
        {
            var document = LoadModel(parse.GetValue(modelArg)!, printIssues: true);
            var crank = new CrankGeometry
            {
                Bore = document.Engine.BoreMm * 1e-3,
                Stroke = document.Engine.StrokeMm * 1e-3,
                RodLength = document.Engine.RodLengthMm * 1e-3,
                CompressionRatio = document.Engine.CompressionRatio,
            };
            var a0 = Math.Sqrt(1.4 * 287.05 * document.Ambient.TemperatureK);
            var cam = CamProfile.Harmonic(
                document.IntakeValves.OpenDeg, document.IntakeValves.CloseDeg, document.IntakeValves.MaxLiftMm * 1e-3);
            var window = QuickEstimate.IntakeWaveReturnWindowDeg(crank, cam);
            var tuned = QuickEstimate.OrganPipeTunedRpm(a0, window, document.IntakeRunner.LengthMm * 1e-3);

            Console.WriteLine($"model: {document.Name} (schema {document.SchemaVersion})");
            Console.WriteLine($"displacement: {crank.DisplacedVolume * document.Engine.CylinderCount * 1e6:F0} cc, " +
                              $"CR {document.Engine.CompressionRatio:F1}");
            Console.WriteLine($"quick estimate — intake wave-return window {window:F0}°, " +
                              $"organ-pipe tuned speed ≈ {tuned:F0} rpm for the {document.IntakeRunner.LengthMm:F0} mm runner");
            return 0;
        });

        var renderFromOption = new Option<double>("--from") { Description = "Sweep start, rpm" };
        renderFromOption.DefaultValueFactory = _ => 2000.0;
        var renderToOption = new Option<double>("--to") { Description = "Sweep end, rpm" };
        renderToOption.DefaultValueFactory = _ => 7000.0;
        var secondsOption = new Option<double>("--seconds") { Description = "Sweep duration, s" };
        secondsOption.DefaultValueFactory = _ => 8.0;
        var gridOption = new Option<double>("--grid") { Description = "Wavetable rpm spacing (plan §3.6 default 250)" };
        gridOption.DefaultValueFactory = _ => 250.0;
        var audioOutOption = new Option<DirectoryInfo>("--out") { Description = "Output directory" };
        audioOutOption.DefaultValueFactory = _ => new DirectoryInfo("render");
        var seedOption = new Option<ulong>("--seed") { Description = "Stochastic seed (renders are reproducible)" };
        seedOption.DefaultValueFactory = _ => 20260825UL;
        var lufsOption = new Option<double>("--lufs") { Description = "Target integrated loudness" };
        lufsOption.DefaultValueFactory = _ => -20.0;
        var burbleOption = new Option<bool>("--burble") { Description = "Add overrun burble on decel (phenomenological)" };

        var renderCommand = new Command("render", "Auralise a model: solve an rpm grid and synthesise audio")
        {
            modelArg, renderFromOption, renderToOption, secondsOption, gridOption,
            audioOutOption, seedOption, lufsOption, burbleOption,
        };
        renderCommand.SetAction(parse =>
        {
            var document = LoadModel(parse.GetValue(modelArg)!);
            var from = parse.GetValue(renderFromOption);
            var to = parse.GetValue(renderToOption);
            var seconds = parse.GetValue(secondsOption);
            var seed = parse.GetValue(seedOption);
            var outDir = parse.GetValue(audioOutOption)!;

            var grid = AuralisationPipeline.Grid(from, to, parse.GetValue(gridOption));
            Console.WriteLine($"solving {grid.Count} operating points for wavetables...");
            var banks = AuralisationPipeline.BuildBanks(
                document, grid, progress: line => Console.WriteLine("  " + line));

            Console.WriteLine($"synthesising {from:F0} → {to:F0} rpm over {seconds:F1} s...");
            var profile = RpmProfile.Sweep(from, to, seconds);
            var synth = new WavetableSynthesizer(seed);
            var stems = banks.Values
                .Select(bank => synth.Render(bank, profile, Loudness.SupportedSampleRate))
                .ToList();

            // Intake radiates less than the tailpipe on an NA engine; the
            // relative gain is a documented default, adjustable per model.
            var parts = stems.Select(s => (s, s.Name == "intake" ? 0.35 : 1.0)).ToList();
            if (parse.GetValue(burbleOption))
            {
                var burble = StemMixer.OverrunBurble(profile, Loudness.SupportedSampleRate, seed);
                stems.Add(burble);
                parts.Add((burble, 0.5));
            }

            var mix = StemMixer.Mix("mix", parts.ToArray());
            var (normalised, gainDb) = Loudness.NormaliseTo(mix, parse.GetValue(lufsOption));

            var metadata = new RenderMetadata
            {
                ModelName = document.Name,
                ModelHash = RenderMetadata.HashOf(document.Save()),
                RpmProfile = $"{from:F0}→{to:F0} rpm over {seconds:F1} s",
                ListenerPreset = "source (no listener chain applied)",
                Seed = seed,
                // docs/numerics.md §5: measured −3 dB bandwidth at this mesh.
                ResolvedBandwidthHz = 2800.0 * 6.0 / Math.Max(document.Solver.CellSizeMm, 1e-6),
                IntegratedLufs = Loudness.IntegratedLufs(normalised.Samples, Loudness.SupportedSampleRate),
            };

            var baseName = Path.GetFileNameWithoutExtension(parse.GetValue(modelArg)!.Name);
            var result = RenderExport.Write(outDir.FullName, baseName, normalised, stems, metadata);

            Console.WriteLine($"loudness {metadata.IntegratedLufs:F1} LUFS (applied {gainDb:+0.0;-0.0} dB)");
            Console.WriteLine($"mix:  {result.MixPath}");
            foreach (var stem in result.StemPaths)
            {
                Console.WriteLine($"stem: {stem}");
            }

            Console.WriteLine($"meta: {result.MetadataPath}");
            Console.WriteLine($"NOTE: content above {metadata.ResolvedBandwidthHz:F0} Hz is not physically resolved (plan §5.5).");
            return 0;
        });

        var root = new RootCommand("WaveBench headless engine gas-dynamics runner")
        {
            runCommand, sweepCommand, meshCommand, validateCommand, infoCommand, renderCommand,
        };
        return root.Parse(args).Invoke();
    }

    private static EngineModelDocument LoadModel(FileInfo file, bool printIssues = false)
    {
        var document = EngineModelDocument.Load(File.ReadAllText(file.FullName));
        var issues = document.Validate();
        foreach (var issue in issues.Where(i => printIssues || i.Severity == ModelIssueSeverity.Error))
        {
            Console.WriteLine($"{issue.Severity}: {issue.Path}: {issue.Message}");
        }

        return document;
    }

    private static void PrintPoint(OperatingPointResult p) =>
        Console.WriteLine(
            $"{p.Rpm,6:F0} rpm  VE {p.VolumetricEfficiency:F3}  IMEP {p.ImepPa / 1e5,6:F2} bar  " +
            $"torque {p.TorqueNm,6:F1} Nm  power {p.PowerW / 1000.0,6:F1} kW  " +
            $"BSFC {(double.IsNaN(p.BsfcGPerKwh) ? "  n/a" : p.BsfcGPerKwh.ToString("F0")),5} g/kWh  " +
            $"({p.CyclesToConvergence} cycles)");

    private static void SaveIfRequested(
        FileInfo? db, EngineModelDocument document, IReadOnlyList<OperatingPointResult> results)
    {
        if (db is null)
        {
            return;
        }

        using var store = new ResultsStore(db.FullName);
        var runId = store.BeginRun(document.Name, document.SchemaVersion, document.Save());
        foreach (var point in results)
        {
            store.AddPoint(runId, point);
        }

        Console.WriteLine($"saved run {runId} to {db.FullName}");
    }

    private static void WriteSweepPlot(string path, string title, IReadOnlyList<OperatingPointResult> results)
    {
        var plot = new ScottPlot.Plot();
        var rpms = results.Select(r => r.Rpm).ToArray();
        var torque = plot.Add.Scatter(rpms, results.Select(r => r.TorqueNm).ToArray());
        torque.LegendText = "Torque (Nm)";
        var ve = plot.Add.Scatter(rpms, results.Select(r => r.VolumetricEfficiency * 100.0).ToArray());
        ve.LegendText = "VE (%)";
        plot.Title(title);
        plot.XLabel("Engine speed (rpm)");
        plot.ShowLegend();
        plot.SavePng(path, 900, 600);
    }

    private static void WriteYinReport(string outDir, IReadOnlyList<YinRunnerLengthCase.CaseResult> results)
    {
        var plot = new ScottPlot.Plot();
        foreach (var result in results)
        {
            var scatter = plot.Add.Scatter(
                result.Sweep.Select(p => p.Rpm).ToArray(),
                result.Sweep.Select(p => p.VolumetricEfficiency).ToArray());
            scatter.LegendText = $"{result.RunnerLengthM * 1000:F0} mm (peak {result.PeakRpm:F0}, published {result.PublishedRpm:F0})";
        }

        plot.Title("Yin (CSU thesis) runner-length study — WaveBench vs published optima");
        plot.XLabel("Engine speed (rpm)");
        plot.YLabel("Volumetric efficiency");
        plot.ShowLegend();
        var png = Path.Combine(outDir, "yin-runner-length.png");
        plot.SavePng(png, 1000, 700);

        var summary = Path.Combine(outDir, "yin-runner-length.md");
        using var writer = new StreamWriter(summary);
        writer.WriteLine("# Yin (CSU thesis) runner-length validation");
        writer.WriteLine();
        writer.WriteLine("Provenance and the documented short-runner discrepancy: docs/physics.md §1.9.");
        writer.WriteLine();
        writer.WriteLine("| Runner (mm) | WaveBench peak (rpm) | Published GT-Power (rpm) | Δ (rpm) |");
        writer.WriteLine("|---|---|---|---|");
        foreach (var result in results)
        {
            writer.WriteLine(
                $"| {result.RunnerLengthM * 1000:F0} | {result.PeakRpm:F0} | {result.PublishedRpm:F0} " +
                $"| {result.PeakRpm - result.PublishedRpm:+0;-0;0} |");
        }

        Console.WriteLine($"report written: {png}");
    }
}
