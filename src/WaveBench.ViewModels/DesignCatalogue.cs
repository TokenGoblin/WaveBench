using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>Design workspace sub-tabs (plan §8.3).</summary>
public enum DesignTab
{
    Engine,
    HeadAndCam,
    Manifold,
    FuelAndCombustion,
}

/// <summary>How a field is edited, which decides parsing and the control.</summary>
public enum FieldKind
{
    Number,
    Integer,
    Text,
    Choice,
    Toggle,
}

/// <summary>
/// The physical quantity a field carries, taken from its declared unit rather
/// than guessed from the property name. This is the ONE place model units meet
/// display units — the plan's "SI internally, everywhere… converted once at
/// the boundary" (CLAUDE.md conventions, plan §8.11 mm/inch toggle).
/// </summary>
public enum Quantity
{
    /// <summary>Dimensionless, or a unit with no imperial counterpart worth offering.</summary>
    None,
    Length,
    Angle,
    Pressure,
    Temperature,
}

/// <summary>
/// One editable model field, described as DATA rather than as a hand-written
/// control. Everything the UI needs — where it lives, what it is called, how
/// it converts, whether Simple mode shows it, what counts as a plausible
/// value — hangs off this record, so adding a field is a list entry and not a
/// new branch in a rendering method.
/// </summary>
/// <param name="Path">Model path, e.g. <c>Engine.BoreMm</c>.</param>
/// <param name="Label">Human label.</param>
/// <param name="Tab">Which Design sub-tab it appears on.</param>
/// <param name="Kind">Editing behaviour.</param>
/// <param name="Quantity">Unit family for display conversion.</param>
/// <param name="ModelUnit">Unit the DOCUMENT stores, always the SI-ish one.</param>
/// <param name="Simple">Whether Simple mode surfaces it.</param>
/// <param name="Minimum">Lower plausibility bound in model units, if any.</param>
/// <param name="Maximum">Upper plausibility bound in model units, if any.</param>
/// <param name="Choices">Allowed values for <see cref="FieldKind.Choice"/>.</param>
/// <param name="Help">One-line explanation, shown on hover.</param>
public sealed record DesignField(
    string Path,
    string Label,
    DesignTab Tab,
    FieldKind Kind = FieldKind.Number,
    Quantity Quantity = Quantity.None,
    string ModelUnit = "",
    bool Simple = false,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null,
    string? Help = null);

/// <summary>
/// Every field of <see cref="EngineModelDocument"/> that the Design workspace
/// edits, in display order.
///
/// <b>Completeness is a gate requirement, not a nicety.</b> Phase 17 asks that
/// a complete model can be built and saved entirely through the UI, so a
/// document property missing from this list is a property a user cannot set —
/// and a test asserts that every editable property of the document schema
/// appears here exactly once.
///
/// Solver settings are deliberately absent: plan §8.4 puts them in the Run
/// workspace, and their defaults already produce a runnable model. The
/// completeness test knows about that exclusion by name rather than by
/// pattern, so adding a new solver field will not silently widen the hole.
/// </summary>
public static class DesignCatalogue
{
    /// <summary>Document blocks the Design workspace does not own.</summary>
    public static IReadOnlySet<string> OwnedElsewhere { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SchemaVersion",     // schema metadata, not user data
        "Solver",            // Run workspace (plan §8.4)

        // The manifold is a GRAPH. It is edited by the canvas — place a
        // component, drag a connection, select a node and edit it in the
        // inspector — not by a list of labelled rows. Listing its nodes as
        // fields here would be claiming an editing model that does not fit
        // the thing being edited.
        "ExhaustManifold",
    };

    // NOTE: these three MUST be declared before Fields. Static initialisers
    // run in declaration order, so a choice list declared after the catalogue
    // is still null while the catalogue is being built — which silently
    // produces choice fields with no choices rather than failing loudly.
    public static IReadOnlyList<string> CamShapes { get; } = ["Harmonic", "Sine", "Polydyne"];

    public static IReadOnlyList<string> HeatTransferCorrelations { get; } = ["Woschni", "Hohenberg", "Annand"];

    public static IReadOnlyList<string> FuelNames { get; } =
        Core.Thermo.Fuels.FuelLibrary.All.Select(f => f.Name).ToList();

    public static IReadOnlyList<DesignField> Fields { get; } =
    [
        // ---- Engine (plan §8.4 "Design → Engine") --------------------------
        new("Name", "Model name", DesignTab.Engine, FieldKind.Text, Simple: true,
            Help: "Shown in the title bar and written into every export's metadata."),
        new("Engine.BoreMm", "Bore", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", true, 20, 200),
        new("Engine.StrokeMm", "Stroke", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", true, 20, 200),
        new("Engine.RodLengthMm", "Rod length", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", false, 40, 400,
            Help: "Centre to centre. Must exceed the stroke; below 1.5× stroke the model warns."),
        new("Engine.PinOffsetMm", "Wrist-pin offset", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", false, -10, 10,
            Help: "Positive offsets true TDC away from the crank centreline."),
        new("Engine.CompressionRatio", "Compression ratio", DesignTab.Engine, FieldKind.Number, Quantity.None, "", true, 4, 20),
        new("Engine.CylinderCount", "Cylinders", DesignTab.Engine, FieldKind.Integer, Quantity.None, "", true, 1, 16),
        new("Combustion.WallTemperatureK", "Wall temperature", DesignTab.Engine, FieldKind.Number, Quantity.Temperature, "K",
            false, 300, 700,
            Help: "Area-averaged combustion-chamber wall temperature; a fixed input until the thermal network lands."),

        // ---- Head & Cam (plan §8.4 "Design → Head & Cam") ------------------
        new("IntakeValves.HeadDiameterMm", "Intake valve head Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            true, 10, 80),
        new("IntakeValves.ThroatDiameterMm", "Intake throat Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            false, 0, 80, Help: "Zero lets the solver take the throat as a fraction of the head diameter."),
        new("IntakeValves.Count", "Intake valves per cylinder", DesignTab.HeadAndCam, FieldKind.Integer, Quantity.None, "",
            true, 1, 4),
        new("IntakeValves.MaxLiftMm", "Intake max lift", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            true, 1, 25),
        new("IntakeValves.OpenDeg", "Intake opens", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°",
            false, 0, 720, Help: "Cycle degrees, 0 = firing TDC."),
        new("IntakeValves.CloseDeg", "Intake closes", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°",
            false, 0, 900),
        new("IntakeValves.CamShape", "Intake cam shape", DesignTab.HeadAndCam, FieldKind.Choice, Quantity.None, "", false,
            Choices: CamShapes,
            Help: "Analytic profiles are generic and flagged as such; measured lift always wins."),

        new("ExhaustValves.HeadDiameterMm", "Exhaust valve head Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length,
            "mm", true, 10, 80),
        new("ExhaustValves.ThroatDiameterMm", "Exhaust throat Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length,
            "mm", false, 0, 80),
        new("ExhaustValves.Count", "Exhaust valves per cylinder", DesignTab.HeadAndCam, FieldKind.Integer, Quantity.None, "",
            true, 1, 4),
        new("ExhaustValves.MaxLiftMm", "Exhaust max lift", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            true, 1, 25),
        new("ExhaustValves.OpenDeg", "Exhaust opens", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°", false, 0, 720),
        new("ExhaustValves.CloseDeg", "Exhaust closes", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°", false, 0, 900),
        new("ExhaustValves.CamShape", "Exhaust cam shape", DesignTab.HeadAndCam, FieldKind.Choice, Quantity.None, "", false,
            Choices: CamShapes),

        // ---- Manifold (the runners; the canvas itself is Phase 18) ---------
        new("IntakeRunner.LengthMm", "Intake runner length", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 20, 2000),
        new("IntakeRunner.DiameterMm", "Intake runner Ø", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 10, 200),
        new("IntakeRunner.RoughnessMm", "Intake wall roughness", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            false, 0, 5, Help: "Absolute roughness ε for the friction correlation."),
        new("ExhaustRunner.LengthMm", "Exhaust primary length", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 20, 2000),
        new("ExhaustRunner.DiameterMm", "Exhaust primary Ø", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 10, 200),
        new("ExhaustRunner.RoughnessMm", "Exhaust wall roughness", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            false, 0, 5),

        // ---- Fuel & Combustion (plan §8.4) ---------------------------------
        new("Combustion.Fuel", "Fuel", DesignTab.FuelAndCombustion, FieldKind.Choice, Quantity.None, "", true,
            Choices: FuelNames, Help: "From the shipped library; the full property table is editable in Library → Fuels."),
        new("Combustion.Lambda", "λ (relative AFR)", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.None, "",
            true, 0.5, 2.0, Help: "1.0 is stoichiometric. Below 1 is rich, which cools the charge and resists knock."),
        new("Combustion.StartDeg", "Spark advance", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Angle, "°",
            false, -60, 20, Help: "Degrees relative to firing TDC; negative is before TDC."),
        new("Combustion.DurationDeg", "Burn duration", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Angle, "°",
            false, 10, 120, Help: "0–100% burn angle; the Wiebe a = 5 convention reaches 99.3% at this angle."),
        new("Combustion.Efficiency", "Combustion efficiency", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.None, "",
            false, 0.5, 1.0),
        new("Combustion.HeatTransfer", "Heat-transfer correlation", DesignTab.FuelAndCombustion, FieldKind.Choice,
            Quantity.None, "", false, Choices: HeatTransferCorrelations),
        new("Combustion.TwoZoneHeatTransfer", "Two-zone wall heat transfer", DesignTab.FuelAndCombustion, FieldKind.Toggle,
            Quantity.None, "", false,
            Help: "Resolve heat loss by burned/unburned zone. More faithful; costs ~0.8% torque."),
        new("Combustion.TrackKnock", "Track knock", DesignTab.FuelAndCombustion, FieldKind.Toggle, Quantity.None, "", false,
            Help: "Accumulates the Livengood–Wu integral. The ranking between fuels is verified; the absolute value is not."),
        new("Ambient.PressureKPa", "Ambient pressure", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Pressure,
            "kPa", true, 20, 200),
        new("Ambient.TemperatureK", "Ambient temperature", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Temperature,
            "K", true, 200, 350),
    ];

    public static IReadOnlyList<DesignField> For(DesignTab tab) =>
        Fields.Where(f => f.Tab == tab).ToList();

    public static DesignField? Find(string path) =>
        Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
}
