using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>
/// Where to put a probe: a fraction along a named pipe.
/// </summary>
/// <param name="Name">Shown on plots.</param>
/// <param name="Pipe">Manifold graph node id, or "exhaust"/"intake" for a model without a graph.</param>
/// <param name="Fraction">0 at the port end, 1 at the far end.</param>
public sealed record ProbeRequest(string Name, string Pipe, double Fraction = 0.5);

/// <summary>What to capture alongside the numbers.</summary>
public sealed record CaptureOptions
{
    /// <summary>Cycles of detail to record after convergence (plan §3.4 "last k converged cycles").</summary>
    public int Cycles { get; init; } = 2;

    /// <summary>Wave-diagram frames per 720°.</summary>
    public int FramesPerCycle { get; init; } = 720;

    /// <summary>Probe samples per 720°.</summary>
    public int ProbeSamplesPerCycle { get; init; } = 1440;

    /// <summary>Pipes to record as x–t fields. Empty records the first exhaust pipe.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    public FieldQuantity FieldQuantity { get; init; } = FieldQuantity.Pressure;

    /// <summary>Probes to place. Empty places one mid-primary and one mid-intake.</summary>
    public IReadOnlyList<ProbeRequest> Probes { get; init; } = [];
}

/// <summary>Progress of a run, for the job tray.</summary>
/// <param name="Completed">Operating points finished.</param>
/// <param name="Total">Operating points requested, plus one for the capture pass.</param>
/// <param name="Stage">What is happening now.</param>
public sealed record RunProgress(int Completed, int Total, string Stage);

/// <summary>
/// Runs a model and collects everything the Results workspace needs
/// (plan Phase 19).
///
/// Two passes on purpose. The sweep is embarrassingly parallel and wants no
/// capture at all; the detail pass is one speed with probes and field captures
/// attached, which costs memory proportional to cells × frames. Doing both in
/// one pass would either capture thirteen operating points nobody asked to
/// look at, or make the sweep serial to avoid it.
/// </summary>
public static class ResultsRunner
{
    public static RunResult Run(
        EngineModelDocument document,
        IReadOnlyList<double> rpms,
        double? captureRpm = null,
        CaptureOptions? options = null,
        IProgress<RunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rpms);

        var capture = options ?? new CaptureOptions();
        var speeds = rpms.Count > 0 ? rpms : [document.Solver.MinCycles > 0 ? 6000.0 : 6000.0];
        var target = captureRpm ?? speeds[speeds.Count / 2];
        var total = speeds.Count + 1;

        // Sweep. Sequential here rather than OperatingPointRunner.Sweep so the
        // job tray can report a point at a time and a cancel can land between
        // points instead of after all of them.
        var points = new List<OperatingPointResult>();
        for (var i = 0; i < speeds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new RunProgress(i, total, $"{speeds[i]:F0} rpm"));
            points.Add(OperatingPointRunner.Run(document, speeds[i]));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new RunProgress(speeds.Count, total, $"capturing detail at {target:F0} rpm"));

        var (probes, fields) = CaptureDetail(document, target, capture, cancellationToken);

        progress?.Report(new RunProgress(total, total, "done"));

        var t0 = document.Ambient.TemperatureK;
        return new RunResult
        {
            ModelName = document.Name,
            Points = points,
            CaptureRpm = target,
            Probes = probes,
            Fields = fields,
            ValveEvents = ValveEvents(document),
            ReferencePressurePa = document.Ambient.PressureKPa * 1000.0,
            ReferenceSoundSpeed = Math.Sqrt(
                PerfectGas.Air.Gamma * PerfectGas.Air.SpecificGasConstant * t0),
            Gamma = PerfectGas.Air.Gamma,
        };
    }

    /// <summary>Valve events as cycle angles, for overlaying on any angle plot.</summary>
    public static IReadOnlyList<(double AngleDeg, string Label)> ValveEvents(EngineModelDocument document) =>
    [
        (document.ExhaustValves.OpenDeg, "EVO"),
        (document.ExhaustValves.CloseDeg, "EVC"),
        (document.IntakeValves.OpenDeg, "IVO"),
        (document.IntakeValves.CloseDeg, "IVC"),
    ];

    private static (IReadOnlyList<ProbeTrace> Probes, IReadOnlyList<DuctFieldCapture> Fields) CaptureDetail(
        EngineModelDocument document,
        double rpm,
        CaptureOptions options,
        CancellationToken cancellationToken)
    {
        var engine = EngineBuilder.Build(document, rpm);
        engine.WallConvergenceK = document.PipeThermal.WallConvergenceK;

        var named = NamedPipes(engine, document);

        // Field captures.
        var fieldNames = options.Fields.Count > 0
            ? options.Fields
            : [named.Keys.FirstOrDefault(k => !k.StartsWith("intake", StringComparison.Ordinal)) ?? "exhaust"];

        var fields = new List<DuctFieldCapture>();
        foreach (var name in fieldNames)
        {
            if (named.TryGetValue(name, out var duct))
            {
                fields.Add(engine.AddFieldCapture(
                    duct, name, options.FieldQuantity, options.FramesPerCycle, options.Cycles));
            }
        }

        // Probes.
        var requests = options.Probes.Count > 0
            ? options.Probes
            : DefaultProbes(named);

        var probeCaptures = new List<ProbeCapture>();
        foreach (var request in requests)
        {
            if (!named.TryGetValue(request.Pipe, out var duct))
            {
                continue;
            }

            var cell = Math.Clamp(
                (int)Math.Round(request.Fraction * (duct.CellCount - 1)), 0, duct.CellCount - 1);
            probeCaptures.Add(engine.AddProbe(duct, cell, request.Name));
        }

        cancellationToken.ThrowIfCancellationRequested();

        engine.RunToConvergence(
            r => r.NetValveMass.Length > 0 ? r.NetValveMass[0] : 0.0,
            document.Solver.ConvergenceTolerance,
            document.Solver.MinCycles,
            document.Solver.MaxCycles);

        cancellationToken.ThrowIfCancellationRequested();
        engine.CaptureCycles(options.Cycles);

        // Resample probes onto the crank-angle grid the plots use.
        var traces = new List<ProbeTrace>();
        foreach (var probe in probeCaptures)
        {
            var pressure = probe.ResampleToCrankAngle(engine.Capture, options.ProbeSamplesPerCycle);
            var velocity = probe.ResampleVelocityToCrankAngle(engine.Capture, options.ProbeSamplesPerCycle);

            // The resampler returns whole cycles from 0°; take the last one,
            // which is the most converged.
            var perCycle = options.ProbeSamplesPerCycle;
            var start = Math.Max(0, pressure.Length - perCycle);
            var count = pressure.Length - start;

            var angles = new double[count];
            var p = new double[count];
            var u = new double[count];
            for (var i = 0; i < count; i++)
            {
                angles[i] = 720.0 * i / perCycle;
                p[i] = pressure[start + i];
                u[i] = velocity[start + i];
            }

            traces.Add(new ProbeTrace(probe.Name, angles, p, u));
        }

        return (traces, fields);
    }

    /// <summary>
    /// Pipes by the name a user would recognise: the manifold graph's node ids
    /// where there is a graph, and "intake"/"exhaust" where there is not.
    /// </summary>
    private static Dictionary<string, DuctSolver> NamedPipes(
        EngineSimulator engine, EngineModelDocument document)
    {
        var named = new Dictionary<string, DuctSolver>(StringComparer.Ordinal);

        foreach (var (id, duct) in engine.ManifoldPipes)
        {
            named[id] = duct;
        }

        // Intake runners are never on the graph, and without a graph neither
        // is the exhaust. Both are found through the valves, which is the only
        // place the association is recorded.
        for (var c = 0; c < engine.Cylinders.Count; c++)
        {
            var intakeValve = 2 * c;
            var exhaustValve = intakeValve + 1;

            if (intakeValve < engine.Valves.Count)
            {
                named[c == 0 ? "intake" : $"intake{c + 1}"] = engine.Valves[intakeValve].Duct;
            }

            if (exhaustValve < engine.Valves.Count && document.ExhaustManifold is null)
            {
                named[c == 0 ? "exhaust" : $"exhaust{c + 1}"] = engine.Valves[exhaustValve].Duct;
            }
        }

        return named;
    }

    private static IReadOnlyList<ProbeRequest> DefaultProbes(IReadOnlyDictionary<string, DuctSolver> named)
    {
        var probes = new List<ProbeRequest>();

        var exhaust = named.Keys.FirstOrDefault(k => k is "exhaust" or "pri1")
                      ?? named.Keys.FirstOrDefault(k => !k.StartsWith("intake", StringComparison.Ordinal));
        if (exhaust is not null)
        {
            probes.Add(new ProbeRequest($"{exhaust} mid", exhaust));
        }

        if (named.ContainsKey("intake"))
        {
            probes.Add(new ProbeRequest("intake mid", "intake"));
        }

        return probes;
    }
}
