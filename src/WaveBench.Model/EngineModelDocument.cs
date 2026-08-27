using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveBench.Model;

/// <summary>
/// The serialisable project document (v0.1 schema): one model, many lenses
/// (plan Part 0 rule 9) — every workspace and run reads this single tree.
/// JSON is source-generated (AOT-friendly, git-diffable, stable ordering).
/// Convention: numeric fields carry their unit in the property name
/// (…Mm, …Deg, …KPa, …K); the schema documents them and the loader converts
/// once at the boundary. Provenance badges attach to this tree in Phase 16.
/// </summary>
public sealed record EngineModelDocument
{
    public string SchemaVersion { get; set; } = "0.1";

    public required string Name { get; set; }

    public AmbientSpec Ambient { get; set; } = new();

    public required EngineSpec Engine { get; set; }

    public required ValveTrainSpec IntakeValves { get; set; }

    public required ValveTrainSpec ExhaustValves { get; set; }

    public required DuctSpec IntakeRunner { get; set; }

    public required DuctSpec ExhaustRunner { get; set; }

    /// <summary>
    /// Exhaust manifold as a node graph (plan §2.8). Null keeps the simple
    /// per-cylinder <see cref="ExhaustRunner"/>, which is what every model
    /// without a collector wants and what keeps existing projects working
    /// untouched. When present it REPLACES the runner: a topology and a
    /// single pipe are two answers to the same question, and honouring both
    /// would mean the exhaust a user sees is not the exhaust that runs.
    /// </summary>
    public ManifoldSpec? ExhaustManifold { get; set; }

    public CombustionSpec? Combustion { get; set; }

    public SolverSpec Solver { get; set; } = new();

    public static EngineModelDocument Load(string json) =>
        JsonSerializer.Deserialize(json, ModelJsonContext.Default.EngineModelDocument)
        ?? throw new InvalidDataException("Model document deserialised to null.");

    public string Save() =>
        JsonSerializer.Serialize(this, ModelJsonContext.Default.EngineModelDocument);

    /// <summary>Validation rules (plan Phase 7): hard errors and plausibility warnings.</summary>
    public IReadOnlyList<ModelIssue> Validate()
    {
        var issues = new List<ModelIssue>();
        void Error(string path, string message) => issues.Add(new ModelIssue(ModelIssueSeverity.Error, path, message));
        void Warn(string path, string message) => issues.Add(new ModelIssue(ModelIssueSeverity.Warning, path, message));

        if (Engine.BoreMm <= 0 || Engine.StrokeMm <= 0 || Engine.RodLengthMm <= 0)
        {
            Error("engine", "Bore, stroke and rod length must be positive.");
        }

        if (Engine.CompressionRatio is <= 1.0 or > 25.0)
        {
            Error("engine.compressionRatio", "Compression ratio must be in (1, 25].");
        }

        if (Engine.RodLengthMm < Engine.StrokeMm)
        {
            Error("engine.rodLengthMm", "Rod must be longer than the stroke (crank radius interference).");
        }
        else if (Engine.RodLengthMm < 1.5 * Engine.StrokeMm)
        {
            Warn("engine.rodLengthMm", "Rod ratio below 1.5 is unusually short — check the value.");
        }

        foreach (var (valves, path) in new[] { (IntakeValves, "intakeValves"), (ExhaustValves, "exhaustValves") })
        {
            if (valves.HeadDiameterMm <= 0 || valves.MaxLiftMm <= 0 || valves.Count < 1)
            {
                Error(path, "Valve diameter, lift and count must be positive.");
            }

            if (valves.CloseDeg <= valves.OpenDeg)
            {
                Error($"{path}.closeDeg", "Valve must close after it opens (cycle degrees, 0–720).");
            }

            if (valves.MaxLiftMm > 0.45 * valves.HeadDiameterMm)
            {
                Warn($"{path}.maxLiftMm", "Lift above 0.45·D is beyond typical valvetrain practice.");
            }
        }

        foreach (var (duct, path) in new[] { (IntakeRunner, "intakeRunner"), (ExhaustRunner, "exhaustRunner") })
        {
            if (duct.LengthMm <= 0 || duct.DiameterMm <= 0)
            {
                Error(path, "Duct length and diameter must be positive.");
            }
        }

        if (Combustion is { } combustion)
        {
            if (combustion.DurationDeg is <= 0 or > 180)
            {
                Error("combustion.durationDeg", "Burn duration must be in (0, 180] degrees.");
            }

            if (combustion.Lambda is <= 0.5 or > 2.0)
            {
                Warn("combustion.lambda", "λ outside [0.5, 2.0] is outside the model's intended range.");
            }
        }

        if (Solver.CellSizeMm is < 1.0 or > 25.0)
        {
            Warn("solver.cellSizeMm", "Cell size outside 1–25 mm; plan §5.3 targets 5–15 mm for performance runs.");
        }

        if (Ambient.PressureKPa is < 50 or > 120)
        {
            Warn("ambient.pressureKPa", "Ambient pressure outside 50–120 kPa — high-altitude or boosted intent?");
        }

        if (ExhaustManifold is { } manifold)
        {
            issues.AddRange(manifold.Validate("exhaustManifold"));

            var ported = manifold.Nodes
                .Where(n => n.Kind == ManifoldNodeKind.Port)
                .Select(n => n.Cylinder)
                .ToHashSet();

            for (var c = 1; c <= Engine.CylinderCount; c++)
            {
                if (!ported.Contains(c))
                {
                    Error("exhaustManifold", $"Cylinder {c} has no port on the exhaust manifold.");
                }
            }

            foreach (var extra in ported.Where(c => c > Engine.CylinderCount))
            {
                Error("exhaustManifold", $"The manifold has a port for cylinder {extra}, but the engine has "
                    + $"{Engine.CylinderCount}.");
            }
        }

        return issues;
    }
}

public sealed record AmbientSpec
{
    public double PressureKPa { get; set; } = 101.325;

    public double TemperatureK { get; set; } = 293.15;
}

public sealed record EngineSpec
{
    public required double BoreMm { get; set; }

    public required double StrokeMm { get; set; }

    public required double RodLengthMm { get; set; }

    public double PinOffsetMm { get; set; }

    public required double CompressionRatio { get; set; }

    public int CylinderCount { get; set; } = 1;
}

public sealed record ValveTrainSpec
{
    public required double HeadDiameterMm { get; set; }

    public double ThroatDiameterMm { get; set; }

    public int Count { get; set; } = 1;

    public required double MaxLiftMm { get; set; }

    /// <summary>Opening angle, cycle degrees (0 = TDC firing).</summary>
    public required double OpenDeg { get; set; }

    public required double CloseDeg { get; set; }

    /// <summary>"Harmonic" (default) or "Sine" analytic profile; measured tables come via import.</summary>
    public string CamShape { get; set; } = "Harmonic";
}

public sealed record DuctSpec
{
    public required double LengthMm { get; set; }

    public required double DiameterMm { get; set; }

    public double RoughnessMm { get; set; }
}

public sealed record CombustionSpec
{
    /// <summary>Wiebe anchor, deg relative to TDC firing (negative = BTDC).</summary>
    public double StartDeg { get; set; } = -15.0;

    public double DurationDeg { get; set; } = 55.0;

    /// <summary>Fuel from the shipped library by name (e.g. "Gasoline RON95").</summary>
    public string Fuel { get; set; } = "Gasoline RON95";

    public double Lambda { get; set; } = 1.0;

    public double Efficiency { get; set; } = 0.98;

    public bool TrackKnock { get; set; } = true;

    public string HeatTransfer { get; set; } = "Woschni";

    public double WallTemperatureK { get; set; } = 420.0;

    /// <summary>
    /// Resolve wall heat transfer by burned/unburned zone rather than from
    /// the bulk mean temperature (plan §2.4 Level 2).
    ///
    /// On by default: the plan requires it, and a single mean temperature
    /// under-predicts heat loss while the flame is passing. Costs 0.7–0.9%
    /// torque and 1–2 g/kWh BSFC against the single-zone model, with
    /// volumetric efficiency unchanged. Set false to recover the old
    /// behaviour. See docs/physics.md.
    /// </summary>
    public bool TwoZoneHeatTransfer { get; set; } = true;
}

public sealed record SolverSpec
{
    public double CellSizeMm { get; set; } = 6.0;

    public double Cfl { get; set; } = 0.8;

    public string Limiter { get; set; } = "VanLeer";

    public int MinCycles { get; set; } = 5;

    public int MaxCycles { get; set; } = 30;

    public double ConvergenceTolerance { get; set; } = 1e-3;
}

public enum ModelIssueSeverity
{
    Warning,
    Error,
}

public sealed record ModelIssue(ModelIssueSeverity Severity, string Path, string Message);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EngineModelDocument))]
public partial class ModelJsonContext : JsonSerializerContext;
