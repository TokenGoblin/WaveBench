using System.Globalization;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Thermo;
using WaveBench.Core.Thermo.Fuels;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>A field as the UI should draw it right now.</summary>
/// <param name="Field">Its static description.</param>
/// <param name="Display">Value formatted in the user's display units.</param>
/// <param name="DisplayUnit">The unit that value is in.</param>
/// <param name="Provenance">Where the value came from (§8.5 badge).</param>
public sealed record FieldView(
    IEditableField Field,
    string Display,
    string DisplayUnit,
    ProvenanceEntry Provenance);

/// <summary>
/// The outcome of an edit. Rejection carries a reason the UI shows next to
/// the field rather than a silently discarded keystroke.
/// </summary>
public sealed record EditOutcome(bool Accepted, string? Reason = null)
{
    public static EditOutcome Ok { get; } = new(true);

    public static EditOutcome Reject(string reason) => new(false, reason);
}

/// <summary>A computed number, never editable, always labelled as derived.</summary>
/// <param name="Label">What it is.</param>
/// <param name="Value">Formatted with its unit.</param>
/// <param name="Note">Why it matters, or the caveat that applies.</param>
/// <param name="Warning">Set when the value is outside sane practice.</param>
public sealed record DerivedReadout(string Label, string Value, string? Note = null, string? Warning = null);

/// <summary>
/// Design workspace (plan Phase 17, §8.4): the Engine, Head &amp; Cam,
/// Manifold and Fuel &amp; Combustion screens.
///
/// Contains no UI-framework types. Every edit goes through
/// <see cref="ProjectSession"/>, so provenance stamping, protection and
/// undo/redo work here for free rather than being re-implemented per screen —
/// and unit conversion happens ONLY here, at the boundary, with the document
/// always holding the SI-ish unit its property name declares.
/// </summary>
public sealed class DesignWorkspace(ProjectSession session, UserPreferences? preferences = null)
    : IFieldEditingSurface
{
    private readonly ProjectSession _session = session;

    public UserPreferences Preferences { get; } = preferences ?? new UserPreferences();

    /// <summary>
    /// The shared parse/convert/write path (see <see cref="FieldEditor"/>).
    /// Boost edits document fields too, and two copies of this logic would be
    /// two unit boundaries that round differently.
    /// </summary>
    private FieldEditor Editor => new(_session, Preferences);

    public EngineModelDocument Document => _session.Document;

    public UnitSystem Units => Preferences.Units;

    /// <summary>Tabs in shell order. Manifold is present but not yet a canvas.</summary>
    public static IReadOnlyList<(DesignTab Tab, string Title)> Tabs { get; } =
    [
        (DesignTab.Engine, "Engine"),
        (DesignTab.HeadAndCam, "Head & Cam"),
        (DesignTab.Manifold, "Manifold"),
        (DesignTab.FuelAndCombustion, "Fuel & Combustion"),
    ];

    /// <summary>
    /// Which sub-tab is showing. Lives here rather than in the view so the
    /// selection survives a re-render and stays testable.
    /// </summary>
    public DesignTab SelectedTab { get; set; } = DesignTab.Engine;

    /// <summary>The most recent rejection per field, for the view to show inline.</summary>
    public IReadOnlyDictionary<string, string> Rejections => _rejections;

    private readonly Dictionary<string, string> _rejections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fields to draw on a tab, filtered by mode. A field hidden by Simple
    /// mode is still IN FORCE — <see cref="ShellViewModel.AdvancedSettingsBanner"/>
    /// is what stops that being a lie by omission (plan §8.8 rule 7).
    /// </summary>
    public IReadOnlyList<FieldView> Fields(DesignTab tab) =>
        DesignCatalogue.For(tab)
            .Where(f => Preferences.Mode == UiMode.Advanced || f.Simple)
            .Select(View)
            .ToList();

    public FieldView View(DesignField field) => Editor.View(field);

    public FieldView View(string path) =>
        View(DesignCatalogue.Find(path) ?? throw new ArgumentException($"No design field '{path}'.", nameof(path)));

    /// <summary>
    /// Apply a user edit from text typed into the field. Parses in DISPLAY
    /// units, converts to model units, checks plausibility, and writes through
    /// the session so the value is stamped <see cref="Provenance.You"/>.
    /// </summary>
    public EditOutcome Edit(string path, string text)
    {
        var outcome = Apply(path, text);
        if (outcome.Accepted)
        {
            _rejections.Remove(path);
        }
        else if (outcome.Reason is { } reason)
        {
            _rejections[path] = reason;
        }

        return outcome;
    }

    private EditOutcome Apply(string path, string text)
    {
        var field = DesignCatalogue.Find(path);
        return field is null
            ? EditOutcome.Reject($"'{path}' is not an editable design field.")
            : Editor.Apply(field, text);
    }

    /// <summary>Model issues from the document's own validator, for this tab's fields.</summary>
    public IReadOnlyList<ModelIssue> Issues(DesignTab tab)
    {
        var paths = DesignCatalogue.For(tab)
            .Select(f => f.Path.Split('.')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Document.Validate()
            .Where(i => paths.Contains(i.Path.Split('.')[0]))
            .ToList();
    }

    // ---- Derived readouts ---------------------------------------------------

    /// <summary>
    /// Computed values for a tab. These are outputs of the model, never
    /// inputs: nothing here is editable and nothing here is stored, so they
    /// cannot drift from the fields that produce them.
    /// </summary>
    public IReadOnlyList<DerivedReadout> Derived(DesignTab tab) => tab switch
    {
        DesignTab.Engine => EngineReadouts(),
        DesignTab.HeadAndCam => HeadReadouts(),
        DesignTab.Manifold => ManifoldReadouts(),
        DesignTab.FuelAndCombustion => FuelReadouts(),
        _ => [],
    };

    private CrankGeometry Crank => new()
    {
        Bore = Document.Engine.BoreMm * 1e-3,
        Stroke = Document.Engine.StrokeMm * 1e-3,
        RodLength = Document.Engine.RodLengthMm * 1e-3,
        PinOffset = Document.Engine.PinOffsetMm * 1e-3,
        CompressionRatio = Document.Engine.CompressionRatio,
    };

    private IReadOnlyList<DerivedReadout> EngineReadouts()
    {
        var engine = Document.Engine;
        var perCylinder = Crank.DisplacedVolume;
        var total = perCylinder * engine.CylinderCount;
        var rodRatio = engine.StrokeMm > 0 ? engine.RodLengthMm / (engine.StrokeMm / 2.0) : 0.0;
        var boreStroke = engine.StrokeMm > 0 ? engine.BoreMm / engine.StrokeMm : 0.0;

        // 8000 rpm is a reference point, not a claim about this engine's limit.
        const double referenceRpm = 8000.0;
        var meanPistonSpeed = Crank.MeanPistonSpeed(referenceRpm);

        return
        [
            new("Displacement", $"{total * 1e6:F0} cc",
                $"{perCylinder * 1e6:F1} cc × {engine.CylinderCount}"),
            new("Bore/stroke", $"{boreStroke:F2}",
                boreStroke >= 1.0 ? "Oversquare — favours rpm." : "Undersquare — favours torque."),
            // Rod ratio conventionally means rod ÷ CRANK RADIUS, and runs
            // 3–4 on most engines. The document's own validator warns on
            // rod ÷ STROKE below 1.5, which is the same geometry expressed
            // differently (1.5 there is 3.0 here) — two names for one number
            // is exactly how a threshold gets applied to the wrong quantity,
            // so this states which it is.
            new("Rod ratio", $"{rodRatio:F2}", "Rod length ÷ crank radius (= 2 × rod ÷ stroke).",
                rodRatio < 3.0 ? "Below 3.0 is short: side loads and peak piston acceleration rise sharply." : null),
            new("Mean piston speed", $"{meanPistonSpeed:F1} m/s at {referenceRpm:F0} rpm",
                "Sustained road-car practice stops near 20 m/s; race engines run past 25.",
                meanPistonSpeed > 25.0 ? "Above 25 m/s — race-only territory." : null),
        ];
    }

    private IReadOnlyList<DerivedReadout> HeadReadouts()
    {
        var intake = Document.IntakeValves;
        var exhaust = Document.ExhaustValves;
        var bore = Document.Engine.BoreMm;

        // Curtain area = π·D·L per valve, the reference area the C_d map is
        // defined against (Blair's convention, docs/physics.md §1.7).
        var intakeCurtain = Math.PI * intake.HeadDiameterMm * intake.MaxLiftMm * intake.Count;
        var exhaustCurtain = Math.PI * exhaust.HeadDiameterMm * exhaust.MaxLiftMm * exhaust.Count;

        var overlap = OverlapDegrees(intake, exhaust);
        var valveToBore = bore > 0
            ? ((intake.HeadDiameterMm * intake.Count) + (exhaust.HeadDiameterMm * exhaust.Count)) / bore
            : 0.0;

        return
        [
            new("Intake curtain area", $"{intakeCurtain:F0} mm² at max lift",
                $"π·D·L × {intake.Count} valves."),
            new("Exhaust curtain area", $"{exhaustCurtain:F0} mm² at max lift",
                $"π·D·L × {exhaust.Count} valves."),
            new("Intake duration", $"{intake.CloseDeg - intake.OpenDeg:F0}°", "Opening to closing, cycle degrees."),
            new("Exhaust duration", $"{exhaust.CloseDeg - exhaust.OpenDeg:F0}°"),
            new("Valve overlap", $"{overlap:F0}°",
                "Both valves off their seats around gas-exchange TDC — where exhaust scavenging happens, and where a wide overlap costs idle quality.",
                overlap > 80.0 ? "Over 80° of overlap: expect a rough idle and reversion at low speed." : null),
            new("Valve area / bore", $"{valveToBore:F2}",
                "Summed valve diameters over bore; above ~1.05 the valves stop fitting in a flat head."),
        ];
    }

    private IReadOnlyList<DerivedReadout> ManifoldReadouts()
    {
        var intake = Document.IntakeRunner;
        var exhaust = Document.ExhaustRunner;
        var a0 = Math.Sqrt(1.4 * 287.05 * Document.Ambient.TemperatureK);

        var cam = CamProfile.Harmonic(
            Document.IntakeValves.OpenDeg, Document.IntakeValves.CloseDeg, Document.IntakeValves.MaxLiftMm * 1e-3);
        var window = QuickEstimate.IntakeWaveReturnWindowDeg(Crank, cam);
        var tuned = QuickEstimate.OrganPipeTunedRpm(a0, window, intake.LengthMm * 1e-3);

        static double Area(double diameterMm) => Math.PI / 4.0 * diameterMm * diameterMm;

        return
        [
            new("Intake tuned speed", $"≈ {tuned:F0} rpm",
                $"Organ-pipe estimate over a {window:F0}° wave-return window. A first guess, not a solved result — run the model.",
                tuned < 1000 || tuned > 15000 ? "Outside a plausible engine speed; check the runner length." : null),
            new("Intake runner area", $"{Area(intake.DiameterMm):F0} mm²"),
            new("Exhaust primary area", $"{Area(exhaust.DiameterMm):F0} mm²"),
            new("Intake L/D", $"{(intake.DiameterMm > 0 ? intake.LengthMm / intake.DiameterMm : 0):F1}"),
            new("Exhaust L/D", $"{(exhaust.DiameterMm > 0 ? exhaust.LengthMm / exhaust.DiameterMm : 0):F1}"),
            new("Canvas", "Phase 18",
                "The node-graph editor, collectors and multi-branch topologies arrive with the manifold canvas. These fields are the single-runner model the solver builds today."),
        ];
    }

    private IReadOnlyList<DerivedReadout> FuelReadouts()
    {
        var combustion = Document.Combustion;
        if (combustion is null)
        {
            return [new("Combustion", "Motored", "No combustion block: the model runs motored.")];
        }

        var readouts = new List<DerivedReadout>();
        var fuel = FuelLibrary.All.FirstOrDefault(f =>
            f.Name.Contains(combustion.Fuel, StringComparison.OrdinalIgnoreCase));

        if (fuel is not null)
        {
            var afr = fuel.StoichAfr * combustion.Lambda;
            readouts.Add(new("Air/fuel ratio", $"{afr:F1}:1",
                $"Stoichiometric {fuel.StoichAfr:F2}:1 for {fuel.Name}, × λ = {combustion.Lambda:F2}."));

            // Plan §8.4 wants this "shown prominently" — it is the reason
            // alcohol fuels make power the dyno cannot explain from LHV alone.
            var deltaT = ChargeCooling.TemperatureDrop(
                fuel, combustion.Lambda, InjectorLocation.Port, SpeciesDatabase.Default,
                Document.Ambient.TemperatureK);
            readouts.Add(new("Charge cooling ΔT", $"−{deltaT:F1} K",
                "Evaporative cooling at a port injector. The evaporated fraction is an empirical calibration parameter, not a prediction."));

            readouts.Add(new("Lower heating value", $"{fuel.LowerHeatingValue / 1e6:F1} MJ/kg"));
        }
        else
        {
            readouts.Add(new("Fuel", combustion.Fuel, null, "Not found in the shipped library."));
        }

        // Density altitude: how thin the air the engine actually sees is.
        var p = Document.Ambient.PressureKPa * 1000.0;
        var t = Document.Ambient.TemperatureK;
        var density = p / (287.05 * t);
        const double seaLevelDensity = 1.225;
        var densityAltitude = 44_330.0 * (1.0 - Math.Pow(density / seaLevelDensity, 1.0 / 4.2559));

        readouts.Add(new("Air density", $"{density:F3} kg/m³"));
        readouts.Add(new("Density altitude", $"{densityAltitude:F0} m",
            "ISA equivalent altitude for this pressure and temperature — what the engine breathes, not where it is.",
            densityAltitude > 2500 ? "Thin air: expect a noticeable power loss against sea level." : null));

        return readouts;
    }

    private static double OverlapDegrees(ValveTrainSpec intake, ValveTrainSpec exhaust)
    {
        // Both events in cycle degrees with 0 at firing TDC; overlap sits
        // around gas-exchange TDC at 360°.
        var start = Math.Max(intake.OpenDeg, exhaust.OpenDeg);
        var end = Math.Min(intake.CloseDeg, exhaust.CloseDeg);
        return Math.Max(0.0, end - start);
    }

    // ---- Units --------------------------------------------------------------
    //
    // Conversion lives in FieldEditor, which Boost shares. These stay as the
    // workspace's own vocabulary — the tests and the view call them — but
    // they must never grow a second implementation: one boundary, or the
    // same number reads differently on two screens.

    public string DisplayUnit(DesignField field) => Editor.DisplayUnit(field);

    /// <summary>Model units → display units.</summary>
    public double ToDisplay(DesignField field, double modelValue) => Editor.ToDisplay(field, modelValue);

    /// <summary>Display units → model units. Exactly inverts <see cref="ToDisplay"/>.</summary>
    public double ToModel(DesignField field, double displayValue) => Editor.ToModel(field, displayValue);
}
