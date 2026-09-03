using System.Text.Json;
using System.Text.Json.Serialization;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>How the engine breathes — decides the fuel default <see cref="EngineEntry.Seed"/> picks.</summary>
public enum EngineAspiration
{
    NaturallyAspirated,
    Turbocharged,
    Supercharged,
}

/// <summary>
/// Real, publicly-sourced facts about a production engine, plus enough to
/// seed a runnable <see cref="EngineModelDocument"/> from them.
///
/// <b>Only the facts a source like Wikipedia actually publishes are here.</b>
/// An infobox gives bore, stroke, displacement, compression ratio (sometimes),
/// valve count and a peak-power rpm — never duct lengths, cam lift/duration,
/// rod length, combustion duration or wall thermal properties, all of which a
/// real build needs. <see cref="Seed"/> fills those the same way the wizard
/// fills any gap: derived, marked <see cref="Provenance.Wizard"/>, never
/// presented as if measured. Only the fields actually confirmed in
/// <see cref="Source"/> are stamped <see cref="Provenance.Imported"/>.
///
/// Mirrors <c>WaveBench.Boost.TurboEntry</c>'s provenance discipline: an entry
/// without a source and a licence cannot be redistributed, so
/// <see cref="Validate"/> refuses to let one through.
/// </summary>
public sealed record EngineEntry
{
    public required string Name { get; init; }

    public required string Manufacturer { get; init; }

    /// <summary>Factory engine code, when the source names one unambiguously (e.g. "S54B32").</summary>
    public string? Code { get; init; }

    /// <summary>Engine family/platform (e.g. "EA888 Gen 3").</summary>
    public string? Family { get; init; }

    public required double BoreMm { get; init; }

    public required double StrokeMm { get; init; }

    /// <summary>
    /// Null when the source doesn't publish one — common for a turbocharged
    /// variant sharing a block with an NA sibling, where only the NA figure is
    /// stated. <see cref="Seed"/> falls back to a generic, aspiration-typical
    /// value rather than leaving the model unrunnable, and does NOT stamp that
    /// fallback as cited.
    /// </summary>
    public double? CompressionRatio { get; init; }

    public required int CylinderCount { get; init; }

    /// <summary>Total valves per cylinder (e.g. 4 for a DOHC 16v four). Null if the source doesn't say.</summary>
    public int? ValveCountPerCylinder { get; init; }

    public required EngineAspiration Aspiration { get; init; }

    /// <summary>
    /// The source's own published displacement, kept only as a cross-check —
    /// <see cref="Validate"/> rejects the entry if it disagrees with
    /// bore×stroke×count by more than 3%, since that gap means one of the
    /// figures was mistranscribed, not that the engine is unusual.
    /// </summary>
    public double? DisplacementCc { get; init; }

    /// <summary>
    /// Rpm of the published peak-power figure, used as the target speed for
    /// <see cref="Wizard.SeedGeometry"/>'s duct-length tuning. Not itself
    /// stamped as cited — it steers a derived default, not a modelled field.
    /// </summary>
    public double? PeakPowerRpm { get; init; }

    /// <summary>Where the figures were read — an article title and its full URL.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// The licence the data is contributed under. Wikipedia's own text is
    /// CC BY-SA 4.0; what is stored here is bare numeric fact extracted from
    /// it, not copied prose, and licence text should say so explicitly.
    /// </summary>
    public required string Licence { get; init; }

    /// <summary>Free-form: manufacturer, family, era, "turbocharged", "tuner-popular".</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Broad, aspiration-agnostic guess used only when a source gives no peak-power rpm at all.</summary>
    private const double FallbackTargetRpm = 6800.0;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source))
        {
            throw new InvalidDataException($"{Name}: every engine entry must record where its figures came from.");
        }

        if (string.IsNullOrWhiteSpace(Licence))
        {
            throw new InvalidDataException(
                $"{Name}: every engine entry must record the licence it is contributed under.");
        }

        if (BoreMm <= 0 || StrokeMm <= 0)
        {
            throw new InvalidDataException($"{Name}: bore and stroke must be positive.");
        }

        if (CylinderCount < 1)
        {
            throw new InvalidDataException($"{Name}: cylinder count must be at least 1.");
        }

        if (CompressionRatio is { } cr && cr is <= 1.0 or > 25.0)
        {
            throw new InvalidDataException(
                $"{Name}: compression ratio {cr:F1}:1 is outside the model's representable (1, 25] range.");
        }

        if (DisplacementCc is { } published)
        {
            var computed = ComputedDisplacementCc();
            var relativeError = Math.Abs(computed - published) / published;
            if (relativeError > 0.03)
            {
                throw new InvalidDataException(
                    $"{Name}: bore × stroke × cylinder count computes {computed:F0} cc, but the published "
                    + $"displacement is {published:F0} cc ({relativeError:P1} apart) — one of the source "
                    + "figures was likely mistranscribed.");
            }
        }
    }

    private double ComputedDisplacementCc() =>
        Math.PI / 4.0 * BoreMm * BoreMm * StrokeMm * CylinderCount / 1000.0;

    /// <summary>
    /// Generic compression ratio by aspiration, used only when the source
    /// publishes none. A Wizard-style rough default — NOT a claim about this
    /// engine, and never stamped as cited.
    /// </summary>
    private double FallbackCompressionRatio =>
        Aspiration == EngineAspiration.NaturallyAspirated ? 10.5 : 8.8;

    /// <summary>
    /// Seed a runnable session from this entry: real facts go in cited and
    /// protected as <see cref="Provenance.Imported"/>; every other field is
    /// filled by <see cref="Wizard.Derive"/> exactly as it fills any wizard
    /// answer, and stays <see cref="Provenance.Wizard"/> so the UI shows it
    /// as a default rather than a measurement.
    /// </summary>
    public ProjectSession Seed()
    {
        // A placeholder — required only so the document's `required` members
        // are satisfiable before ApplyWizard overwrites every one of these
        // paths. Deliberately sentinel values (never a plausible bore/stroke-
        // derived number): ApplyWizard treats a value that already matches
        // what Derive() would compute as a no-op and skips it, which would
        // leave that field unstamped (bare default Auto, no derivation text)
        // instead of recording the Wizard-derived explanation it should show.
        // Bore, stroke and cylinder count still come from the real entry —
        // Derive() needs the true values to compute everything downstream of
        // them (rod length, valve sizes, tuned duct lengths).
        const double sentinel = 1.0;
        var placeholder = new EngineModelDocument
        {
            Name = Name,
            Engine = new EngineSpec
            {
                BoreMm = BoreMm, StrokeMm = StrokeMm, RodLengthMm = sentinel,
                CompressionRatio = sentinel + 1.0, CylinderCount = CylinderCount,
            },
            IntakeValves = new ValveTrainSpec
            {
                HeadDiameterMm = sentinel, MaxLiftMm = sentinel, OpenDeg = sentinel, CloseDeg = sentinel * 2,
            },
            ExhaustValves = new ValveTrainSpec
            {
                HeadDiameterMm = sentinel, MaxLiftMm = sentinel, OpenDeg = sentinel, CloseDeg = sentinel * 2,
            },
            IntakeRunner = new DuctSpec { LengthMm = sentinel, DiameterMm = sentinel },
            ExhaustRunner = new DuctSpec { LengthMm = sentinel, DiameterMm = sentinel },
            Combustion = new CombustionSpec { Fuel = string.Empty, Lambda = sentinel },
        };

        var session = new ProjectSession(placeholder);
        var targetRpm = PeakPowerRpm ?? FallbackTargetRpm;

        var wizard = new Wizard(session)
        {
            BoreMm = BoreMm,
            StrokeMm = StrokeMm,
            Cylinders = CylinderCount,
            CompressionRatio = CompressionRatio ?? FallbackCompressionRatio,
            RedlineRpm = targetRpm,
            BandFromRpm = Math.Round(targetRpm * 0.55 / 10.0) * 10.0,
            BandToRpm = targetRpm,
            Fuel = Aspiration == EngineAspiration.NaturallyAspirated ? "Gasoline RON95" : "Gasoline RON98",
        };

        wizard.Apply();

        void Cite(string path, string derivation) => session.Provenance.Set(path, new ProvenanceEntry
        {
            Origin = Model.Provenance.Imported,
            Derivation = derivation,
            Citation = Source,
            SourceRef = Source,
        });

        Cite("Engine.BoreMm", $"Published bore for {Name}.");
        Cite("Engine.StrokeMm", $"Published stroke for {Name}.");
        Cite("Engine.CylinderCount", $"Published cylinder count for {Name}.");
        if (CompressionRatio is not null)
        {
            Cite("Engine.CompressionRatio", $"Published compression ratio for {Name}.");
        }

        return session;
    }
}

/// <summary>
/// A curated library of <see cref="EngineEntry"/> records — the "start from a
/// known engine" analogue of <c>WaveBench.Boost.TurboDatabase</c>. A direct
/// pick by name, not a multi-point match, so there is a <see cref="Find"/>
/// rather than a ranking function.
/// </summary>
public sealed class EngineDatabase
{
    private readonly List<EngineEntry> _entries = [];

    public IReadOnlyList<EngineEntry> Entries => _entries;

    public void Add(EngineEntry entry)
    {
        entry.Validate();

        if (_entries.Any(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"An engine named \"{entry.Name}\" is already in the database.");
        }

        _entries.Add(entry);
    }

    public EngineEntry? Find(string name) =>
        _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public string Save() =>
        JsonSerializer.Serialize(_entries, EngineDatabaseJson.Default.ListEngineEntry);

    public static EngineDatabase Load(string json)
    {
        var database = new EngineDatabase();
        var entries = JsonSerializer.Deserialize(json, EngineDatabaseJson.Default.ListEngineEntry) ?? [];
        foreach (var entry in entries)
        {
            database.Add(entry);
        }

        return database;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<EngineEntry>))]
public partial class EngineDatabaseJson : JsonSerializerContext;
