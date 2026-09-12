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

    /// <summary>
    /// Aspiration and everything that hangs off it (plan §4). Always present
    /// and defaulted to naturally aspirated rather than nullable: the
    /// aspiration selector is a FIELD on Design → Engine (plan §8.4), and a
    /// field whose "unset" state is a missing parent block cannot be edited,
    /// undone or provenance-stamped the way every other field is. The Boost
    /// workspace's visibility keys off <see cref="ForcedInductionSpec.IsForced"/>,
    /// not off the block's existence (plan §8.3).
    /// </summary>
    public ForcedInductionSpec ForcedInduction { get; set; } = new();

    public PipeThermalSpec PipeThermal { get; set; } = new();

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

        var fi = ForcedInduction;

        if (!AspirationKinds.All.Contains(fi.Aspiration, StringComparer.OrdinalIgnoreCase))
        {
            Error("forcedInduction.aspiration",
                $"Aspiration must be one of: {string.Join(", ", AspirationKinds.All)}.");
        }

        if (fi.IsForced)
        {
            if (fi.TargetBoostKPa is <= 0 or > 400)
            {
                Error("forcedInduction.targetBoostKPa", "Boost target must be in (0, 400] kPa gauge.");
            }

            if (fi.CoolerEffectiveness is < 0 or > 1)
            {
                Error("forcedInduction.coolerEffectiveness", "Cooler effectiveness must be in [0, 1].");
            }

            if (fi.TurbineAreaRatio <= 0)
            {
                Error("forcedInduction.turbineAreaRatio", "Turbine A/R must be positive.");
            }

            if (string.IsNullOrWhiteSpace(fi.TurboName))
            {
                Warn("forcedInduction.turboName",
                    "No turbo chosen: the Boost workspace cannot draw an operating line without one.");
            }

            // Boost with no charge cooling is legal and common on a
            // low-boost or alcohol-fuelled engine — but it is a choice, and
            // the intake temperature it implies is the reason a knock margin
            // disappears, so it is stated rather than assumed.
            if (string.Equals(fi.ChargeCooler, ChargeCoolerKinds.None, StringComparison.OrdinalIgnoreCase)
                && fi.TargetBoostKPa > 60.0)
            {
                Warn("forcedInduction.chargeCooler",
                    $"{fi.TargetBoostKPa:F0} kPa of boost with no charge cooling — check the knock margin.");
            }

            if (fi.RestrictorFitted && fi.RestrictorThroatMm is <= 0 or > 80)
            {
                Error("forcedInduction.restrictorThroatMm", "Restrictor throat must be in (0, 80] mm.");
            }

            if (fi.TransientUncertaintyPercent is < 0 or > 100)
            {
                Error("forcedInduction.transientUncertaintyPercent",
                    "The transient uncertainty band must be in [0, 100]%.");
            }
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

/// <summary>
/// Wall friction and wall heat transfer in the ducts (plan §2.1, §2.3, §2.9).
///
/// These were implemented and component-tested in Phase 3 but never switched
/// on for a built engine, so every duct ran adiabatic and frictionless. They
/// are ON by default because the plan requires them and because an exhaust
/// that cannot lose heat is not an exhaust: gas temperature sets the sound
/// speed, and the sound speed sets both the tuned length and the acoustic
/// resonances.
/// </summary>
public sealed record PipeThermalSpec
{
    /// <summary>Haaland/Darcy wall friction (plan §2.1). Costs ~0.5% torque.</summary>
    public bool Friction { get; set; } = true;

    /// <summary>Colburn wall heat transfer with a wall thermal node (plan §2.3, §2.9).</summary>
    public bool WallHeatTransfer { get; set; } = true;

    /// <summary>
    /// Surface treatment of the intake tract. Named from the shipped presets:
    /// "Bare stainless", "Ceramic coated", "Water jacketed", "Header wrap",
    /// "Insulated".
    /// </summary>
    public string IntakeSurface { get; set; } = "Bare stainless";

    /// <summary>Surface treatment of the exhaust tract — the header wrap choice.</summary>
    public string ExhaustSurface { get; set; } = "Bare stainless";

    /// <summary>
    /// Starting wall temperature for the intake tract, K. The cyclic-steady
    /// solve moves it to the balance point; this only sets where it starts.
    /// </summary>
    public double IntakeWallStartK { get; set; } = 330.0;

    /// <summary>Starting wall temperature for the exhaust tract, K.</summary>
    public double ExhaustWallStartK { get; set; } = 700.0;

    /// <summary>
    /// Hold the intake wall at <see cref="IntakeWallStartK"/> instead of
    /// solving for it.
    ///
    /// <b>True by default, and that is the physics, not a shortcut.</b> An
    /// intake port's wall temperature is set by the coolant and the head it is
    /// cast into — not by the air passing through it. Left free to float, the
    /// wall balances against the intake charge alone and settles at or below
    /// ambient, because gas expanding down a runner genuinely runs cooler than
    /// the air outside. That would model an intake tract that CHILLS the
    /// charge, where plan §2.2 asks for the opposite: ambient plus wall heat
    /// pickup. Predicting it properly needs the coolant circuit and a head
    /// conduction path, which the model does not have.
    /// </summary>
    public bool FixIntakeWall { get; set; } = true;

    /// <summary>
    /// Hold the exhaust wall fixed rather than solving for it. False by
    /// default: an exhaust pipe hanging in air genuinely is in balance with
    /// the gas inside it and the air outside, which is exactly what the
    /// cyclic-steady solve computes.
    /// </summary>
    public bool FixExhaustWall { get; set; }

    /// <summary>
    /// Wall areal heat capacity ρ·t·c, J/(m²·K). 2 mm stainless is ≈ 7900.
    /// Affects only the transient path to the answer, never the converged
    /// cyclic-steady temperature.
    /// </summary>
    public double ArealHeatCapacityJPerM2K { get; set; } = 7900.0;

    /// <summary>External convection coefficient, W/(m²·K); natural plus light forced.</summary>
    public double ExternalHtcWPerM2K { get; set; } = 15.0;

    /// <summary>
    /// Cycle-to-cycle wall temperature change, K, below which the wall counts
    /// as converged. A converged gas state over a wall still marching toward
    /// its own temperature is not a converged operating point.
    /// </summary>
    public double WallConvergenceK { get; set; } = 0.5;
}

/// <summary>
/// Aspiration and the forced-induction hardware (plan §4).
///
/// <b>Stored as names, not as maps.</b> <see cref="TurboName"/> points into
/// the turbo library rather than embedding a compressor map in the project
/// file: plan §4.7 forbids redistributing manufacturer maps, and a project
/// that carried one would carry that problem with it wherever it was shared.
/// The library entry records its own source and licence.
/// </summary>
public sealed record ForcedInductionSpec
{
    /// <summary>Naturally aspirated (the default), turbocharged or supercharged.</summary>
    public string Aspiration { get; set; } = AspirationKinds.NaturallyAspirated;

    /// <summary>True once the model has a compressor — what reveals the Boost workspace (plan §8.3).</summary>
    [JsonIgnore]
    public bool IsForced => !string.Equals(
        Aspiration, AspirationKinds.NaturallyAspirated, StringComparison.OrdinalIgnoreCase);

    /// <summary>Library entry name. Empty means "not chosen yet", which the Boost workspace says out loud.</summary>
    public string TurboName { get; set; } = "";

    /// <summary>Boost target, kPa GAUGE — the number a builder actually quotes.</summary>
    public double TargetBoostKPa { get; set; } = 100.0;

    /// <summary>
    /// Turbine volute area ratio, m. Separate from the map's own
    /// <c>AreaRatio</c> because choosing an A/R is the decision plan §4.7's
    /// sweep exists to inform: the map is the measurement, this is the
    /// housing the user is considering putting on it.
    /// </summary>
    public double TurbineAreaRatio { get; set; } = 0.64;

    /// <summary>Pressure downstream of the turbine, kPa absolute — ambient plus whatever the tailpipe adds.</summary>
    public double ExhaustBackPressureKPa { get; set; } = 101.325;

    /// <summary>Wastegate spring or closed-loop control.</summary>
    public string BoostControl { get; set; } = BoostControlModes.WastegateSpring;

    /// <summary>Gauge pressure at which the wastegate spring starts to open, kPa.</summary>
    public double WastegateSpringKPa { get; set; } = 70.0;

    /// <summary>Wastegate port diameter, mm.</summary>
    public double WastegateDiameterMm { get; set; } = 38.0;

    /// <summary>Internal (in the turbine housing) or external (off the manifold).</summary>
    public string WastegatePlacement { get; set; } = "Internal";

    /// <summary>Gauge differential at which the blow-off valve cracks, kPa.</summary>
    public double BlowOffCrackingKPa { get; set; } = 30.0;

    /// <summary>Recirculating (back to the compressor inlet) rather than venting to atmosphere.</summary>
    public bool BlowOffRecirculates { get; set; } = true;

    /// <summary>None, air-to-air or air-to-water.</summary>
    public string ChargeCooler { get; set; } = ChargeCoolerKinds.AirToAir;

    /// <summary>Cooler effectiveness at its rated flow, 0–1.</summary>
    public double CoolerEffectiveness { get; set; } = 0.75;

    /// <summary>Core pressure drop at rated flow, kPa. Boost measured before the core is boost the engine never sees.</summary>
    public double CoolerPressureDropKPa { get; set; } = 8.0;

    /// <summary>Coolant-side temperature, K — ambient air for air-to-air.</summary>
    public double CoolantTemperatureK { get; set; } = 298.15;

    /// <summary>An intake restrictor upstream of the compressor (plan §4.6.4, the FSAE case).</summary>
    public bool RestrictorFitted { get; set; }

    /// <summary>Restrictor throat diameter, mm. FSAE petrol is 20 mm, E85 19 mm.</summary>
    public double RestrictorThroatMm { get; set; } = 20.0;

    /// <summary>Throat discharge coefficient; 0.95–0.98 for a proper venturi.</summary>
    public double RestrictorDischargeCoefficient { get; set; } = 0.96;

    /// <summary>Fraction of the dynamic head the diffuser recovers; 0.8 for a shallow cone.</summary>
    public double RestrictorDiffuserRecovery { get; set; } = 0.80;

    /// <summary>
    /// How far shaft inertia and bearing friction are allowed to move for the
    /// transient sensitivity band, percent (plan Part 14 gotcha #25: transient
    /// results depend on states users rarely know accurately, so report a band
    /// rather than a single number). This is the uncertainty ON THE INPUTS —
    /// the band it produces is whatever the physics makes of it, never an
    /// invented ± on the answer.
    /// </summary>
    public double TransientUncertaintyPercent { get; set; } = 25.0;
}

/// <summary>The aspiration choices, spelled once so a selector and a check cannot disagree.</summary>
public static class AspirationKinds
{
    public const string NaturallyAspirated = "Naturally aspirated";

    public const string Turbocharged = "Turbocharged";

    public const string Supercharged = "Supercharged";

    public static IReadOnlyList<string> All { get; } = [NaturallyAspirated, Turbocharged, Supercharged];
}

/// <summary>Boost-control strategies (plan §4.5).</summary>
public static class BoostControlModes
{
    public const string WastegateSpring = "Wastegate spring";

    public const string ClosedLoop = "Closed-loop to target";

    public static IReadOnlyList<string> All { get; } = [WastegateSpring, ClosedLoop];
}

/// <summary>Charge-cooling choices (plan §4.4).</summary>
public static class ChargeCoolerKinds
{
    public const string None = "None";

    public const string AirToAir = "Air to air";

    public const string AirToWater = "Air to water";

    public static IReadOnlyList<string> All { get; } = [None, AirToAir, AirToWater];
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
