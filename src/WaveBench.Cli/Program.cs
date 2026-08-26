using System.CommandLine;
using WaveBench.Acoustics;
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
        var listenerOption = new Option<string>("--listener")
        {
            Description = "Listener position: source | fsae | j1287 | drive-by | chase-cam",
        };
        listenerOption.DefaultValueFactory = _ => "source";
        var outletHeightOption = new Option<double>("--outlet-height")
        {
            Description = "Outlet height above ground, m — sets the ground-reflection geometry",
        };
        outletHeightOption.DefaultValueFactory = _ => 0.35;
        var loadsOption = new Option<string>("--loads")
        {
            Description = "Load lines to solve, comma-separated manifold pressure fractions "
                          + "(plan §3.6 wants at least two; pass 1.0 alone to halve the solve)",
        };
        loadsOption.DefaultValueFactory = _ => "1.0,0.35";
        var liftAtOption = new Option<double>("--lift-at")
        {
            Description = "Lift off the throttle at this time, s — 0 holds wide-open throttle",
        };
        liftAtOption.DefaultValueFactory = _ => 0.0;
        var cruiseLoadOption = new Option<double>("--cruise-load")
        {
            Description = "Manifold pressure fraction after the lift",
        };
        cruiseLoadOption.DefaultValueFactory = _ => 0.35;
        var broadbandOption = new Option<double>("--broadband")
        {
            Description = "Broadband flow-noise stem level, as a fraction of tonal RMS "
                          + "(shape is physical, absolute level is NOT calibrated; 0 disables)",
        };
        broadbandOption.DefaultValueFactory = _ => 0.06;
        var mechanicalOption = new Option<double>("--mechanical")
        {
            Description = "Mechanical stem level — COSMETIC, predicts nothing; 0 disables",
        };
        mechanicalOption.DefaultValueFactory = _ => 0.0;
        var flacOption = new Option<bool>("--flac")
        {
            Description = "Also write FLAC alongside every WAV (identical audio, roughly a third the size)",
        };

        var renderCommand = new Command("render", "Auralise a model: solve an rpm × load grid and synthesise audio")
        {
            modelArg, renderFromOption, renderToOption, secondsOption, gridOption,
            audioOutOption, seedOption, lufsOption, burbleOption, listenerOption, outletHeightOption,
            loadsOption, liftAtOption, cruiseLoadOption, broadbandOption, mechanicalOption, flacOption,
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
            var loads = ParseLoads(parse.GetValue(loadsOption));
            Console.WriteLine(
                $"solving {grid.Count} speeds × {loads.Count} load line{(loads.Count == 1 ? "" : "s")} " +
                $"= {grid.Count * loads.Count} operating points for wavetables...");
            var banks = AuralisationPipeline.BuildBanks(
                document, grid, progress: line => Console.WriteLine("  " + line), loadLines: loads);

            var liftAt = parse.GetValue(liftAtOption);
            var loadProfile = liftAt > 0.0
                ? LoadProfile.LiftOff(seconds, liftAt, parse.GetValue(cruiseLoadOption))
                : LoadProfile.Constant(loads.Max(), seconds);

            Console.WriteLine(
                $"synthesising {from:F0} → {to:F0} rpm over {seconds:F1} s" +
                (liftAt > 0.0
                    ? $", lifting to {parse.GetValue(cruiseLoadOption) * 100:F0}% load at {liftAt:F1} s..."
                    : $" at {loads.Max() * 100:F0}% load..."));

            var profile = RpmProfile.Sweep(from, to, seconds);
            var synth = new WavetableSynthesizer(seed);
            var stems = new List<AudioStem>();
            var heldFraction = 0.0;
            foreach (var bank in banks.Pressure.Values)
            {
                stems.Add(synth.Render(
                    bank, profile, Loudness.SupportedSampleRate, variation: null, startAngleDeg: 0.0,
                    load: loadProfile));
                heldFraction = Math.Max(heldFraction, synth.LastRenderHeldAtGridEdge);
            }

            if (heldFraction > 0.0)
            {
                // Held-edge audio is not a solved result, so it does not get to
                // pass as one just because it sounds plausible.
                Console.WriteLine(
                    $"WARNING: {heldFraction * 100:F0}% of the render fell outside the solved " +
                    "rpm × load grid and was held at the nearest edge. Widen --from/--to or --loads.");
            }

            // Intake radiates less than the tailpipe on an NA engine; the
            // relative gain is a documented default, adjustable per model.
            var parts = stems.Select(s => (s, s.Name == "intake" ? 0.35 : 1.0)).ToList();

            // Broadband flow noise, from the SAME solve's velocity tables. Its
            // spectral shape and its variation with speed and load are physical
            // (§3.4 Curle/Lighthill scaling); its absolute level is not, so it
            // enters as a user-set mix gain and is labelled uncalibrated.
            var broadbandLevel = parse.GetValue(broadbandOption);
            if (broadbandLevel > 0.0)
            {
                var tonalRms = Rms(StemMixer.Mix("tonal", parts.ToArray()).Samples);
                foreach (var (name, velocityBank) in banks.Velocity)
                {
                    var track = synth.Render(
                        velocityBank, profile, Loudness.SupportedSampleRate,
                        SynthesisVariation.None, 0.0, loadProfile);

                    var diameterMm = name == "intake"
                        ? document.IntakeRunner.DiameterMm
                        : document.ExhaustRunner.DiameterMm;

                    // Exit-jet mixing noise at the tailpipe is quadrupole (U⁸,
                    // Lighthill); the intake mouth is the dipole case (U⁶, Curle).
                    var noise = FlowNoise.Generate(
                        Array.ConvertAll(track.Samples, s => (double)s),
                        Loudness.SupportedSampleRate, diameterMm * 1e-3, seed ^ 0x9E37,
                        calibrationFactor: 1.0,
                        velocityExponent: name == "intake" ? 6.0 : 8.0);

                    var rms = Rms(noise);
                    if (rms <= 0.0)
                    {
                        continue;
                    }

                    // One constant scale over the whole render: it fixes the
                    // unknown absolute level without touching the physical
                    // variation within it.
                    var scale = broadbandLevel * tonalRms / rms;
                    var samples = new float[noise.Length];
                    for (var i = 0; i < samples.Length; i++)
                    {
                        samples[i] = (float)(noise[i] * scale);
                    }

                    var stem = new AudioStem($"broadband-{name}", samples, Loudness.SupportedSampleRate);
                    stems.Add(stem);
                    parts.Add((stem, 1.0));
                }

                Console.WriteLine(
                    $"broadband: {broadbandLevel:F3} of tonal RMS — UNCALIBRATED. The U⁶/U⁸ scaling and " +
                    "spectral shape are physical; the absolute level is a knob (plan §3.4).");
            }

            // Mechanical layer: cosmetic, and separate so it can be soloed,
            // muted, and kept out of every metric.
            var mechanicalLevel = parse.GetValue(mechanicalOption);
            if (mechanicalLevel > 0.0)
            {
                var character = new MechanicalCharacter(
                    ValveTrainLevel: 0.05 * mechanicalLevel,
                    TimingDriveLevel: 0.02 * mechanicalLevel,
                    InjectorLevel: document.Combustion is null ? 0.0 : 0.015 * mechanicalLevel);

                var mechanical = MechanicalLayer.Render(
                    profile, document.Engine.CylinderCount, document.IntakeValves.Count,
                    Loudness.SupportedSampleRate, seed ^ 0x5EED, character);

                stems.Add(mechanical);
                parts.Add((mechanical, 1.0));
                Console.WriteLine(
                    "mechanical: COSMETIC — valve, timing-drive and injector events are placed on the "
                    + "real crank angles, but their levels are knobs and predict nothing.");
            }

            if (parse.GetValue(burbleOption))
            {
                var burble = StemMixer.OverrunBurble(profile, Loudness.SupportedSampleRate, seed);
                stems.Add(burble);
                parts.Add((burble, 0.5));
            }

            // The listener chain is linear, so filtering each stem and mixing
            // is identical to filtering the mix — but this way the exported
            // stems are the same signal as their contribution to the mix,
            // instead of quietly being the pre-propagation source.
            var listener = ResolveListener(parse.GetValue(listenerOption));
            var listenerDescription = "source (no listener chain applied)";
            if (listener is not null)
            {
                var path = listener.ToPath(parse.GetValue(outletHeightOption));
                listenerDescription = ListenerChain.Describe(listener, path);

                var dry = StemMixer.Mix("dry", parts.ToArray());
                for (var i = 0; i < stems.Count; i++)
                {
                    stems[i] = ListenerChain.Apply(stems[i], path);
                    parts[i] = (stems[i], parts[i].Item2);
                }

                var wet = StemMixer.Mix("wet", parts.ToArray());
                Console.WriteLine(
                    $"listener: {listener.Name} at {listener.SlantDistanceM:F2} m " +
                    $"({ListenerChain.InsertionGainDb(dry, wet):+0.0;-0.0} dB before normalisation)");
            }
            else
            {
                Console.WriteLine(
                    "listener: source — you are hearing the outlet, not a listener. " +
                    "Use --listener drive-by for what a bystander hears.");
            }

            var mix = StemMixer.Mix("mix", parts.ToArray());
            var (normalised, gainDb) = Loudness.NormaliseTo(mix, parse.GetValue(lufsOption));

            var metadata = new RenderMetadata
            {
                ModelName = document.Name,
                ModelHash = RenderMetadata.HashOf(document.Save()),
                RpmProfile = $"{from:F0}→{to:F0} rpm over {seconds:F1} s",
                ListenerPreset = listenerDescription,
                Seed = seed,
                // docs/numerics.md §5: measured −3 dB bandwidth at this mesh.
                ResolvedBandwidthHz = 2800.0 * 6.0 / Math.Max(document.Solver.CellSizeMm, 1e-6),
                IntegratedLufs = Loudness.IntegratedLufs(normalised.Samples, Loudness.SupportedSampleRate),
            };

            var baseName = Path.GetFileNameWithoutExtension(parse.GetValue(modelArg)!.Name);
            var result = RenderExport.Write(
                outDir.FullName, baseName, normalised, stems, metadata, parse.GetValue(flacOption));

            Console.WriteLine($"loudness {metadata.IntegratedLufs:F1} LUFS (applied {gainDb:+0.0;-0.0} dB)");
            Console.WriteLine($"mix:  {result.MixPath}");
            foreach (var stem in result.StemPaths)
            {
                Console.WriteLine($"stem: {stem}");
            }

            if (result.FlacPaths.Count > 0)
            {
                var wavBytes = result.StemPaths.Append(result.MixPath).Sum(p => new FileInfo(p).Length);
                var flacBytes = result.FlacPaths.Sum(p => new FileInfo(p).Length);
                Console.WriteLine(
                    $"flac: {result.FlacPaths.Count} files, {100.0 * flacBytes / Math.Max(wavBytes, 1):F0}% of the WAV size");
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

    private static double Rms(IReadOnlyList<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return samples.Count > 0 ? Math.Sqrt(sum / samples.Count) : 0.0;
    }

    private static double Rms(IReadOnlyList<double> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return samples.Count > 0 ? Math.Sqrt(sum / samples.Count) : 0.0;
    }

    /// <summary>Parses --loads: comma-separated manifold pressure fractions.</summary>
    private static IReadOnlyList<double> ParseLoads(string? value)
    {
        var loads = (value ?? "1.0")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => double.TryParse(part, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var load)
                ? load
                : throw new ArgumentException($"--loads: '{part}' is not a number."))
            .Distinct()
            .OrderByDescending(load => load)
            .ToList();

        if (loads.Count == 0)
        {
            throw new ArgumentException("--loads needs at least one value.");
        }

        foreach (var load in loads.Where(load => load is <= 0.0 or > 1.0))
        {
            throw new ArgumentException(
                $"--loads: {load} is outside (0, 1]. Load is manifold pressure as a fraction of ambient.");
        }

        return loads;
    }

    /// <summary>
    /// Resolves --listener. Null means "render the source", which is the
    /// default: it is what earlier versions produced, and silently moving the
    /// microphone would change every existing render's output.
    /// </summary>
    private static ListenerPreset? ResolveListener(string? name) =>
        (name ?? "source").Trim().ToLowerInvariant() switch
        {
            "" or "source" or "none" => null,
            "fsae" or "fsae-static" => ListenerPreset.FsaeStatic,
            "j1287" or "sae-j1287" => ListenerPreset.SaeJ1287,
            "drive-by" or "driveby" => ListenerPreset.DriveBy,
            "chase-cam" or "chasecam" => ListenerPreset.ChaseCam,
            var other => throw new ArgumentException(
                $"Unknown listener '{other}'. Use source, fsae, j1287, drive-by or chase-cam."),
        };

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
