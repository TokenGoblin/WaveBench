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
/// <param name="Help">Why the field exists and what moving it does.</param>
/// <param name="Typical">Where the answer usually lands; numeric fields only.</param>
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
    string? Help = null,
    TypicalRange? Typical = null) : IEditableField;

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

    /// <summary>Pipe surface treatments (plan §2.9), from the solver's own presets.</summary>
    public static IReadOnlyList<string> PipeSurfaces { get; } =
        Core.Solver.WallSurface.Presets.Select(s => s.Name).ToList();

    public static IReadOnlyList<DesignField> Fields { get; } =
    [
        // ---- Engine (plan §8.4 "Design → Engine") --------------------------
        new("Name", "Model name", DesignTab.Engine, FieldKind.Text, Simple: true,
            Help: "Shown in the title bar and written into every export's metadata."),
        new("Engine.BoreMm", "Bore", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", true, 20, 200,
            Help: "Cylinder diameter. Piston area scales with its square, so bore sets how much force a given "
                  + "pressure makes — and how large a valve the head can hold, which is usually the real limit.",
            Typical: new TypicalRange(65, 105, "small car engines at the bottom, large-displacement V8s at the top")),
        new("Engine.StrokeMm", "Stroke", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", true, 20, 200,
            Help: "Crank throw doubled. With bore it fixes displacement; on its own it fixes mean piston speed, "
                  + "which is what caps engine speed long before the airflow does.",
            Typical: new TypicalRange(55, 100, "under about 70 mm is a short-stroke, high-revving layout")),
        new("Engine.RodLengthMm", "Rod length", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", false, 40, 400,
            Help: "Centre to centre. Must exceed the stroke; below 1.5× stroke the model warns. A longer rod dwells "
                  + "the piston nearer TDC, which changes where in the burn the volume is still small.",
            Typical: new TypicalRange(110, 175, "usually 1.5–1.8 × the stroke")),
        new("Engine.PinOffsetMm", "Wrist-pin offset", DesignTab.Engine, FieldKind.Number, Quantity.Length, "mm", false, -10, 10,
            Help: "Positive offsets true TDC away from the crank centreline. Production engines use a millimetre or "
                  + "two to soften piston slap; it barely moves the gas dynamics.",
            Typical: new TypicalRange(-1.5, 1.5, "zero is a perfectly reasonable model")),
        new("Engine.CompressionRatio", "Compression ratio", DesignTab.Engine, FieldKind.Number, Quantity.None, "", true, 4, 20,
            Help: "Swept plus clearance volume over clearance volume. Raising it raises thermal efficiency and "
                  + "raises end-gas temperature with it, so the fuel's knock resistance sets the ceiling.",
            Typical: new TypicalRange(8, 14, "8–10 boosted on pump fuel, 12–14 naturally aspirated on high octane")),
        new("Engine.CylinderCount", "Cylinders", DesignTab.Engine, FieldKind.Integer, Quantity.None, "", true, 1, 16,
            Help: "How many cylinders share the manifolds. It sets the firing interval, and therefore whether "
                  + "exhaust pulses help or fight each other in a shared primary.",
            Typical: new TypicalRange(1, 8, "the solver handles up to 16; run time scales with the count")),
        new("Combustion.WallTemperatureK", "Wall temperature", DesignTab.Engine, FieldKind.Number, Quantity.Temperature, "K",
            false, 300, 700,
            Help: "Area-averaged combustion-chamber wall temperature; a fixed input until the thermal network lands. "
                  + "It sets the heat-loss driving temperature in the Woschni/Hohenberg/Annand correlation.",
            Typical: new TypicalRange(400, 520, "liquid-cooled; an air-cooled head runs hotter")),

        // The aspiration selector (plan §8.4 "Design → Engine"). This one
        // field is what reveals the Boost workspace — plan §8.3 asks that
        // "add forced induction" live here and in the command palette, not
        // only in the wizard. Everything downstream of the choice — turbo,
        // wastegate, cooler, restrictor — belongs to the Boost workspace and
        // is catalogued in BoostCatalogue.
        new("ForcedInduction.Aspiration", "Aspiration", DesignTab.Engine, FieldKind.Choice, Quantity.None, "", false,
            Choices: AspirationKinds.All,
            Help: "Choosing anything but naturally aspirated reveals the Boost workspace and its five screens."),

        // ---- Head & Cam (plan §8.4 "Design → Head & Cam") ------------------
        new("IntakeValves.HeadDiameterMm", "Intake valve head Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            true, 10, 80,
            Help: "The valve's outside diameter. Curtain area is π × this × lift, so it is the single biggest lever "
                  + "on how much air reaches the cylinder — and the bore is what bounds it.",
            Typical: new TypicalRange(25, 42, "roughly 0.40–0.50 × bore on a four-valve head")),
        new("IntakeValves.ThroatDiameterMm", "Intake throat Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            false, 0, 80,
            Help: "The narrowest section under the seat. Once lift passes about a quarter of the head diameter it is "
                  + "the throat, not the curtain, that limits the flow. Zero lets the solver take it as a fraction "
                  + "of the head diameter.",
            Typical: new TypicalRange(0, 36, "0 means derive it; otherwise about 0.85 × the head diameter")),
        new("IntakeValves.Count", "Intake valves per cylinder", DesignTab.HeadAndCam, FieldKind.Integer, Quantity.None, "",
            true, 1, 4,
            Help: "Two smaller valves fit more curtain area into the same bore than one large one, and they are "
                  + "lighter, so the valvetrain survives more rpm.",
            Typical: new TypicalRange(1, 2, "2 is the modern default; 1 suits an older two-valve head")),
        new("IntakeValves.MaxLiftMm", "Intake max lift", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            true, 1, 25,
            Help: "Peak lift off the seat. Past roughly 0.30 × head diameter the port has stopped gaining flow, so "
                  + "further lift buys valvetrain load and nothing else.",
            Typical: new TypicalRange(8, 14, "about 0.25–0.33 × the valve head diameter")),
        new("IntakeValves.OpenDeg", "Intake opens", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°",
            false, 0, 720,
            Help: "Cycle degrees, 0 = firing TDC. Opening before the overlap TDC at 360° lets the departing exhaust "
                  + "start the intake column moving — that is what overlap is for.",
            Typical: new TypicalRange(330, 365, "360° is the overlap TDC; below it is early opening")),
        new("IntakeValves.CloseDeg", "Intake closes", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°",
            false, 0, 900,
            Help: "The event that actually sets volumetric efficiency: close late and the inertia of a fast-moving "
                  + "column keeps filling past BDC, close early and low-speed charge is not pushed back out.",
            Typical: new TypicalRange(540, 620, "BDC is 540°; 40–80° after it is the usual window")),
        new("IntakeValves.CamShape", "Intake cam shape", DesignTab.HeadAndCam, FieldKind.Choice, Quantity.None, "", false,
            Choices: CamShapes,
            Help: "Analytic profiles are generic and flagged as such; measured lift always wins."),

        new("ExhaustValves.HeadDiameterMm", "Exhaust valve head Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length,
            "mm", true, 10, 80,
            Help: "Smaller than the intake, because blowdown does most of the emptying under its own pressure ratio "
                  + "while the intake has only one atmosphere to push with.",
            Typical: new TypicalRange(21, 36, "usually 0.75–0.85 × the intake valve")),
        new("ExhaustValves.ThroatDiameterMm", "Exhaust throat Ø", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length,
            "mm", false, 0, 80,
            Help: "The narrowest section under the seat. It chokes during blowdown, which is what sets how much of "
                  + "the cylinder empties before the piston has to do the work. Zero derives it.",
            Typical: new TypicalRange(0, 31, "0 means derive it; otherwise about 0.85 × the head diameter")),
        new("ExhaustValves.Count", "Exhaust valves per cylinder", DesignTab.HeadAndCam, FieldKind.Integer, Quantity.None, "",
            true, 1, 4,
            Help: "As on the intake side, though the exhaust gains less from splitting: it is pressure-driven rather "
                  + "than area-starved.",
            Typical: new TypicalRange(1, 2, "2 on a four-valve head, 1 on a two-valve")),
        new("ExhaustValves.MaxLiftMm", "Exhaust max lift", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Length, "mm",
            true, 1, 25,
            Help: "Peak lift off the seat. It matters least of the four cam numbers — the pressure ratio across the "
                  + "valve at opening is doing the work, not the area.",
            Typical: new TypicalRange(7, 13, "typically a little less than the intake")),
        new("ExhaustValves.OpenDeg", "Exhaust opens", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°", false, 0, 720,
            Help: "Cycle degrees, 0 = firing TDC. Opening before BDC throws away expansion work but starts blowdown "
                  + "early enough that the piston is not pumping against a full cylinder.",
            Typical: new TypicalRange(110, 165, "BDC is 180°; 15–70° before it is the usual window")),
        new("ExhaustValves.CloseDeg", "Exhaust closes", DesignTab.HeadAndCam, FieldKind.Number, Quantity.Angle, "°", false, 0, 900,
            Help: "Closing after the overlap TDC at 360° is the other half of overlap. Too late on a boosted engine "
                  + "and fresh charge goes straight out of the port.",
            Typical: new TypicalRange(350, 395, "360° is the overlap TDC; after it is late closing")),
        new("ExhaustValves.CamShape", "Exhaust cam shape", DesignTab.HeadAndCam, FieldKind.Choice, Quantity.None, "", false,
            Choices: CamShapes,
            Help: "Analytic profiles are generic and flagged as such; measured lift always wins."),

        // ---- Manifold (the runners; the canvas itself is Phase 18) ---------
        new("IntakeRunner.LengthMm", "Intake runner length", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 20, 2000,
            Help: "Sets which engine speed the intake's own resonance lands on: a long runner tunes low and fattens "
                  + "mid-range torque, a short one tunes high. This is the classic tuned-length trade.",
            Typical: new TypicalRange(150, 600, "short race intakes at the bottom, long touring manifolds at the top")),
        new("IntakeRunner.DiameterMm", "Intake runner Ø", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 10, 200,
            Help: "Too small and the runner throttles the top end; too large and port velocity collapses, which "
                  + "costs the ram effect and low-speed torque.",
            Typical: new TypicalRange(30, 55, "roughly 0.85–1.0 × the intake valve head diameter")),
        new("IntakeRunner.RoughnessMm", "Intake wall roughness", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            false, 0, 5,
            Help: "Absolute roughness ε for the friction correlation. It barely moves an intake answer — it is here "
                  + "because the same duct model serves both sides.",
            Typical: new TypicalRange(0, 0.3, "0.0015 drawn tube, 0.05 cast aluminium, 0.25 sand-cast")),
        new("ExhaustRunner.LengthMm", "Exhaust primary length", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 20, 2000,
            Help: "Sets when the reflected expansion wave arrives back at the valve. Land it during overlap and it "
                  + "scavenges the cylinder; land it late and it pushes exhaust back in.",
            Typical: new TypicalRange(300, 900, "shorter primaries tune higher in the rev range")),
        new("ExhaustRunner.DiameterMm", "Exhaust primary Ø", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            true, 10, 200,
            Help: "A larger primary lowers back pressure but weakens the pulse that does the scavenging, so the best "
                  + "answer is rarely the biggest one that fits.",
            Typical: new TypicalRange(32, 55, "usually a little above the exhaust valve head diameter")),
        new("ExhaustRunner.RoughnessMm", "Exhaust wall roughness", DesignTab.Manifold, FieldKind.Number, Quantity.Length, "mm",
            false, 0, 5,
            Help: "Absolute roughness ε for the friction correlation. It matters more here than on the intake, "
                  + "because exhaust gas moves faster and friction goes with velocity squared.",
            Typical: new TypicalRange(0, 0.3, "0.0015 mandrel-bent tube, 0.25 cast iron")),

        // ---- Pipe thermal (plan §2.1, §2.3, §2.9) --------------------------
        // Exhaust surface treatment sits in Simple mode because it is a real
        // choice a builder makes at the parts counter, and it moves the wall
        // by tens of kelvin — which moves the sound speed, and with it the
        // tuned length and every acoustic resonance.
        new("PipeThermal.ExhaustSurface", "Exhaust surface", DesignTab.Manifold, FieldKind.Choice, Quantity.None, "",
            true, Choices: PipeSurfaces,
            Help: "Bare, coated, wrapped or insulated. A wrap raises the wall temperature and the exhaust sound speed."),
        new("PipeThermal.IntakeSurface", "Intake surface", DesignTab.Manifold, FieldKind.Choice, Quantity.None, "",
            false, Choices: PipeSurfaces,
            Help: "The same treatments on the cold side. It changes very little while the intake wall is held at the "
                  + "coolant temperature, which is the default."),
        new("PipeThermal.ExhaustWallStartK", "Exhaust wall temperature", DesignTab.Manifold, FieldKind.Number,
            Quantity.Temperature, "K", false, 293, 1400,
            Help: "Starting value only — the solver iterates it to the cycle-average balance, unless it is held "
                  + "fixed. A hotter wall raises the sound speed and shortens the tuned length.",
            Typical: new TypicalRange(600, 950, "where converged values land; the start only sets how long it takes")),
        new("PipeThermal.IntakeWallStartK", "Intake wall temperature", DesignTab.Manifold, FieldKind.Number,
            Quantity.Temperature, "K", false, 273, 500,
            Help: "Held fixed by default: an intake port's wall is set by the coolant, not by the air through it.",
            Typical: new TypicalRange(290, 350, "ambient for a plenum, nearer coolant temperature for a head port")),
        new("PipeThermal.FixExhaustWall", "Hold exhaust wall fixed", DesignTab.Manifold, FieldKind.Toggle,
            Quantity.None, "", false,
            Help: "Impose the temperature above instead of solving for it — for a measured wall."),
        new("PipeThermal.FixIntakeWall", "Hold intake wall fixed", DesignTab.Manifold, FieldKind.Toggle,
            Quantity.None, "", false,
            Help: "On by default, and it should usually stay on: solving an intake wall from the air alone ignores "
                  + "the coolant that actually sets it (docs/physics.md §1.11)."),
        new("PipeThermal.WallHeatTransfer", "Wall heat transfer", DesignTab.Manifold, FieldKind.Toggle,
            Quantity.None, "", false,
            Help: "Colburn heat transfer with a wall thermal node. Off makes every pipe adiabatic — a diagnostic, not a model."),
        new("PipeThermal.Friction", "Wall friction", DesignTab.Manifold, FieldKind.Toggle, Quantity.None, "", false,
            Help: "Haaland/Darcy wall friction. Off makes every pipe frictionless — a diagnostic, not a model."),
        new("PipeThermal.ArealHeatCapacityJPerM2K", "Wall heat capacity", DesignTab.Manifold, FieldKind.Number,
            Quantity.None, "J/m²K", false, 100, 100_000,
            Help: "ρ·t·c of the pipe wall; 2 mm stainless is about 7900. Affects the transient only, never the converged answer.",
            Typical: new TypicalRange(3000, 20000, "thin stainless at the bottom, a thick cast manifold at the top")),
        new("PipeThermal.ExternalHtcWPerM2K", "External convection", DesignTab.Manifold, FieldKind.Number,
            Quantity.None, "W/m²K", false, 1, 500,
            Help: "Outside the pipe: natural convection plus whatever airflow the installation gives it. It sets "
                  + "where the solved wall temperature settles, so it moves the exhaust sound speed.",
            Typical: new TypicalRange(5, 60, "5–15 in still air, 30–60 in a moving vehicle's engine bay")),
        new("PipeThermal.WallConvergenceK", "Wall convergence band", DesignTab.Manifold, FieldKind.Number,
            Quantity.None, "K", false, 0.01, 50,
            Help: "Cycle-to-cycle wall temperature change below which the operating point counts as converged. "
                  + "Tighter costs cycles; looser leaves the wall — and the sound speed — still drifting.",
            Typical: new TypicalRange(0.5, 5, "1 K is the default and is tight enough for torque")),

        // ---- Fuel & Combustion (plan §8.4) ---------------------------------
        new("Combustion.Fuel", "Fuel", DesignTab.FuelAndCombustion, FieldKind.Choice, Quantity.None, "", true,
            Choices: FuelNames, Help: "From the shipped library; the full property table is editable in Library → Fuels."),
        new("Combustion.Lambda", "λ (relative AFR)", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.None, "",
            true, 0.5, 2.0, Help: "1.0 is stoichiometric. Below 1 is rich, which cools the charge and resists knock.",
            Typical: new TypicalRange(0.80, 1.05, "0.85 or richer under boost, 1.0 for cruise economy")),
        new("Combustion.StartDeg", "Spark advance", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Angle, "°",
            false, -60, 20,
            Help: "Degrees relative to firing TDC; negative is before TDC. Advance until peak pressure lands about "
                  + "15° after TDC — earlier than that and the rising piston is fighting the burn.",
            Typical: new TypicalRange(-35, -8, "boosted engines run the least advance, lean or slow-burning ones the most")),
        new("Combustion.DurationDeg", "Burn duration", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Angle, "°",
            false, 10, 120,
            Help: "0–100% burn angle; the Wiebe a = 5 convention reaches 99.3% at this angle. A faster burn is closer "
                  + "to constant volume and so more efficient, and it leaves the end gas less time to knock.",
            Typical: new TypicalRange(30, 70, "a compact four-valve chamber burns fast, an old two-valve slowly")),
        new("Combustion.Efficiency", "Combustion efficiency", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.None, "",
            false, 0.5, 1.0,
            Help: "The fraction of the fuel's heating value actually released — crevice and quench losses, not "
                  + "thermodynamic efficiency, which the cycle computes for itself.",
            Typical: new TypicalRange(0.94, 0.99, "it falls off rich, where there is not enough oxygen to finish")),
        new("Combustion.HeatTransfer", "Heat-transfer correlation", DesignTab.FuelAndCombustion, FieldKind.Choice,
            Quantity.None, "", false, Choices: HeatTransferCorrelations,
            Help: "Which in-cylinder correlation to use. Woschni is the default and the most widely validated; the "
                  + "three disagree by a few per cent of heat loss, which is worth seeing for yourself."),
        new("Combustion.TwoZoneHeatTransfer", "Two-zone wall heat transfer", DesignTab.FuelAndCombustion, FieldKind.Toggle,
            Quantity.None, "", false,
            Help: "Resolve heat loss by burned/unburned zone. More faithful; costs ~0.8% torque."),
        new("Combustion.TrackKnock", "Track knock", DesignTab.FuelAndCombustion, FieldKind.Toggle, Quantity.None, "", false,
            Help: "Accumulates the Livengood–Wu integral. The ranking between fuels is verified; the absolute value is not."),
        new("Ambient.PressureKPa", "Ambient pressure", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Pressure,
            "kPa", true, 20, 200,
            Help: "Absolute pressure the engine breathes from. Air density follows it directly, which is why a "
                  + "naturally aspirated engine loses roughly a per cent of its torque per 100 m of altitude.",
            Typical: new TypicalRange(85, 103, "101.325 at sea level; about 85 at 1500 m")),
        new("Ambient.TemperatureK", "Ambient temperature", DesignTab.FuelAndCombustion, FieldKind.Number, Quantity.Temperature,
            "K", true, 200, 350,
            Help: "Inlet air temperature. Density goes as 1/T, so a hot day costs torque before anything else in the "
                  + "model has had a say.",
            Typical: new TypicalRange(273, 313, "0–40 °C; the SAE J1349 correction reference is 25 °C")),
    ];

    public static IReadOnlyList<DesignField> For(DesignTab tab) =>
        Fields.Where(f => f.Tab == tab).ToList();

    public static DesignField? Find(string path) =>
        Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
}
