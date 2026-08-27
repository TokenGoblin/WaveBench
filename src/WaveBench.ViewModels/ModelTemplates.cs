using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>
/// A starting point, described so a user can tell whether it is theirs.
/// </summary>
/// <param name="Id">Stable identifier for scripting and tests.</param>
/// <param name="Name">Display name.</param>
/// <param name="Summary">One line: what this engine is.</param>
/// <param name="Provenance">
/// Where the numbers came from. Templates are plausible representative
/// geometry, NOT measured engines — saying so is the difference between a
/// starting point and a fake datum.
/// </param>
/// <param name="Create">Builds a fresh document; never returns a shared instance.</param>
public sealed record ModelTemplate(
    string Id,
    string Name,
    string Summary,
    string Provenance,
    Func<EngineModelDocument> Create);

/// <summary>
/// Shipped templates (plan Phase 17). Every one produces a document that
/// validates without errors and runs, so "new from template → run" works
/// without the user having to guess a single number first.
///
/// <b>These are representative geometry, not measured engines.</b> No
/// template claims to be a specific product, and each carries that statement
/// in its own provenance string so it reaches the UI rather than living in a
/// comment. A template value arrives stamped
/// <see cref="Provenance.Auto"/> — the user has not chosen it, so nothing
/// here is protected from a later wizard or derivation.
/// </summary>
public static class ModelTemplates
{
    public static IReadOnlyList<ModelTemplate> All { get; } =
    [
        new("fsae-600", "FSAE 600 cc four",
            "Four-cylinder 600 cc sportbike engine with the 20 mm restrictor's usual runner lengths.",
            "Representative FSAE class geometry, not a measured engine.",
            () => new EngineModelDocument
            {
                Name = "FSAE 600 cc four",
                Engine = new EngineSpec
                {
                    BoreMm = 67.0, StrokeMm = 42.5, RodLengthMm = 90.0,
                    CompressionRatio = 12.0, CylinderCount = 4,
                },
                IntakeValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 26.0, Count = 2, MaxLiftMm = 8.5, OpenDeg = 345.0, CloseDeg = 585.0,
                },
                ExhaustValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 22.0, Count = 2, MaxLiftMm = 8.0, OpenDeg = 135.0, CloseDeg = 375.0,
                },
                IntakeRunner = new DuctSpec { LengthMm = 260.0, DiameterMm = 32.0 },
                ExhaustRunner = new DuctSpec { LengthMm = 420.0, DiameterMm = 30.0 },
                Combustion = new CombustionSpec { Fuel = "Gasoline RON95", Lambda = 0.9 },
                Solver = new SolverSpec { CellSizeMm = 6.0 },
            }),

        new("single-450", "Single-cylinder 450",
            "Big-bore four-stroke single — the simplest thing that still shows wave tuning.",
            "Representative motocross-class geometry, not a measured engine.",
            () => new EngineModelDocument
            {
                Name = "Single-cylinder 450",
                Engine = new EngineSpec
                {
                    BoreMm = 96.0, StrokeMm = 62.1, RodLengthMm = 104.0,
                    CompressionRatio = 12.5, CylinderCount = 1,
                },
                IntakeValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 36.0, Count = 2, MaxLiftMm = 10.5, OpenDeg = 340.0, CloseDeg = 590.0,
                },
                ExhaustValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 31.0, Count = 2, MaxLiftMm = 10.0, OpenDeg = 130.0, CloseDeg = 380.0,
                },
                IntakeRunner = new DuctSpec { LengthMm = 300.0, DiameterMm = 42.0 },
                ExhaustRunner = new DuctSpec { LengthMm = 700.0, DiameterMm = 40.0 },
                Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
                Solver = new SolverSpec { CellSizeMm = 7.0 },
            }),

        new("naturally-aspirated-2l", "2.0 L four, road",
            "Ordinary road-car four: moderate compression, long runners, stoichiometric.",
            "Representative road-car geometry, not a measured engine.",
            () => new EngineModelDocument
            {
                Name = "2.0 L four, road",
                Engine = new EngineSpec
                {
                    BoreMm = 86.0, StrokeMm = 86.0, RodLengthMm = 145.0,
                    CompressionRatio = 10.5, CylinderCount = 4,
                },
                IntakeValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 33.0, Count = 2, MaxLiftMm = 9.5, OpenDeg = 350.0, CloseDeg = 580.0,
                },
                ExhaustValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 28.0, Count = 2, MaxLiftMm = 9.0, OpenDeg = 140.0, CloseDeg = 370.0,
                },
                IntakeRunner = new DuctSpec { LengthMm = 420.0, DiameterMm = 38.0 },
                ExhaustRunner = new DuctSpec { LengthMm = 600.0, DiameterMm = 36.0 },
                Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
                Solver = new SolverSpec { CellSizeMm = 8.0 },
            }),

        new("e85-track", "E85 track four",
            "Same 2.0 L four on E85 — the charge-cooling readout is the point of the comparison.",
            "Representative geometry; the fuel change is the only difference from the road four.",
            () => new EngineModelDocument
            {
                Name = "E85 track four",
                Engine = new EngineSpec
                {
                    BoreMm = 86.0, StrokeMm = 86.0, RodLengthMm = 145.0,
                    CompressionRatio = 12.0, CylinderCount = 4,
                },
                IntakeValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 33.0, Count = 2, MaxLiftMm = 10.5, OpenDeg = 345.0, CloseDeg = 585.0,
                },
                ExhaustValves = new ValveTrainSpec
                {
                    HeadDiameterMm = 28.0, Count = 2, MaxLiftMm = 10.0, OpenDeg = 135.0, CloseDeg = 375.0,
                },
                IntakeRunner = new DuctSpec { LengthMm = 340.0, DiameterMm = 40.0 },
                ExhaustRunner = new DuctSpec { LengthMm = 560.0, DiameterMm = 38.0 },
                Combustion = new CombustionSpec { Fuel = "E85", Lambda = 0.85 },
                Solver = new SolverSpec { CellSizeMm = 8.0 },
            }),
    ];

    public static ModelTemplate? Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Opens a template as a session. Every field is stamped
    /// <see cref="Provenance.Auto"/> with the template named as its
    /// derivation, so the §8.5 badges tell the truth from the first frame:
    /// the user has chosen none of this yet.
    /// </summary>
    public static ProjectSession Open(ModelTemplate template)
    {
        var document = template.Create();
        var session = new ProjectSession(document);

        foreach (var field in DesignCatalogue.Fields)
        {
            session.Provenance.Set(field.Path, new ProvenanceEntry
            {
                Origin = Provenance.Auto,
                Derivation = $"From the “{template.Name}” template.",
                SourceRef = template.Id,
            });
        }

        return session;
    }
}
