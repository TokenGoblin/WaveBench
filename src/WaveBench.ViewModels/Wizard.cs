using System.Globalization;
using WaveBench.Core.EngineModel;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>The nine wizard steps (plan §8.6).</summary>
public enum WizardStep
{
    Purpose,
    Engine,
    Head,
    Fuel,
    Aspiration,
    Constraints,
    Goal,
    Review,
    Compute,
}

/// <summary>What the engine is being built for. Sets objectives, rules and defaults.</summary>
public enum BuildPurpose
{
    Fsae,
    Track,
    Street,
    DynoDrag,
    Restoration,
    Learning,
}

/// <summary>Cam character, for a user who has not measured a profile.</summary>
public enum CamCharacter
{
    Stock,
    Mild,
    Aggressive,
    Race,
}

/// <summary>Where the torque should sit.</summary>
public enum TorqueShape
{
    BroadMidrange,
    Balanced,
    PeakPower,
}

/// <summary>
/// How much a recommendation can be relied on (plan §8.6: a confidence
/// indicator distinguishing well-founded from rough).
/// </summary>
public enum Confidence
{
    /// <summary>Generic default or an unmeasured input. A starting point, not an answer.</summary>
    Rough,

    /// <summary>An accepted rule of thumb, applied inside its usual range.</summary>
    Fair,

    /// <summary>A validated correlation or a converged solve.</summary>
    Good,
}

/// <summary>
/// One line of the Design Brief: recommendation → number → why → confidence
/// (plan §8.6).
/// </summary>
/// <param name="Group">Section heading — INTAKE, EXHAUST, CAM.</param>
/// <param name="Label">What is being recommended.</param>
/// <param name="Value">The number, formatted with its unit.</param>
/// <param name="Why">One sentence linking it to the physics.</param>
/// <param name="Confidence">How well founded it is.</param>
/// <param name="Basis">What that confidence rests on — the source, or the reason it is rough.</param>
public sealed record BriefLine(
    string Group,
    string Label,
    string Value,
    string Why,
    Confidence Confidence,
    string Basis)
{
    /// <summary>Four dots, filled to the confidence level — the plan's ●●●○ indicator.</summary>
    public string Indicator => Confidence switch
    {
        Confidence.Good => "●●●○",
        Confidence.Fair => "●●○○",
        _ => "●○○○",
    };

    public string ConfidenceWord => Confidence switch
    {
        Confidence.Good => "good",
        Confidence.Fair => "fair",
        _ => "rough",
    };
}

/// <summary>
/// A predicted performance number with its uncertainty band (plan §8.6:
/// <i>"Simple mode never presents a bare number as if measured"</i>).
/// </summary>
public sealed record Prediction(string Label, double Value, string Unit, double RelativeUncertainty, double AtRpm)
{
    public string Format() =>
        $"{Value.ToString(Value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture)} {Unit} "
        + $"@ {AtRpm:F0} rpm (±{RelativeUncertainty * 100:F0}%)";
}

/// <summary>One item a fabricator can order or cut (plan §8.6's build list).</summary>
public sealed record BuildItem(int Quantity, string Description);

/// <summary>
/// The Design Brief (plan §8.6): what to build, why, how sure, and what it
/// should do.
/// </summary>
public sealed record DesignBrief
{
    public required string ModelName { get; init; }

    public required IReadOnlyList<BriefLine> Lines { get; init; }

    public required IReadOnlyList<Prediction> Predictions { get; init; }

    public required IReadOnlyList<BuildItem> BuildList { get; init; }

    /// <summary>Anything the user must know that is not a recommendation.</summary>
    public IReadOnlyList<string> Caveats { get; init; } = [];

    /// <summary>Sweep behind the predictions, for the brief's torque curve.</summary>
    public IReadOnlyList<Core.Solver.OperatingPointResult> Sweep { get; init; } = [];

    public IEnumerable<string> Groups => Lines.Select(l => l.Group).Distinct();

    /// <summary>
    /// The weakest confidence in the brief. A brief is only as good as its
    /// shakiest input, and saying so up front is more honest than letting a
    /// reader average the dots themselves.
    /// </summary>
    public Confidence WeakestConfidence =>
        Lines.Count == 0 ? Confidence.Rough : Lines.Min(l => l.Confidence);
}

/// <summary>
/// The Simple-mode wizard (plan Phase 23, §8.6).
///
/// <b>Every answer writes into the FULL model.</b> There is no parallel simple
/// model — the plan is explicit, and the reason is that a second model is a
/// second thing to keep in sync and the first thing to drift. Derived fields
/// are applied through <see cref="ProjectSession.ApplyWizard"/>, which is what
/// makes a re-run safe: it touches only <c>Auto</c> and <c>Wizard</c> fields,
/// so anything the user typed, imported or optimised survives untouched
/// without the wizard having to know about it.
/// </summary>
public sealed class Wizard(ProjectSession session)
{
    private readonly ProjectSession _session = session;

    public ProjectSession Session => _session;

    public WizardStep Step { get; private set; } = WizardStep.Purpose;

    // ---- Answers ----------------------------------------------------------

    public BuildPurpose Purpose { get; set; } = BuildPurpose.Track;

    public double BoreMm { get; set; } = 82;

    public double StrokeMm { get; set; } = 78;

    public int Cylinders { get; set; } = 4;

    public double CompressionRatio { get; set; } = 10.5;

    public double RedlineRpm { get; set; } = 7500;

    public CamCharacter Cam { get; set; } = CamCharacter.Mild;

    /// <summary>Intake valve head diameter, mm. Zero derives it from the bore.</summary>
    public double IntakeValveMm { get; set; }

    public double ExhaustValveMm { get; set; }

    public string Fuel { get; set; } = "Gasoline RON95";

    public double Lambda { get; set; } = 1.0;

    public double AmbientTemperatureC { get; set; } = 20.0;

    public double AltitudeM { get; set; }

    /// <summary>Naturally aspirated only for now — see <see cref="AspirationNote"/>.</summary>
    public bool ForcedInduction { get; set; }

    /// <summary>Longest runner the car can physically take, mm.</summary>
    public double PackagingLimitMm { get; set; } = 700;

    public double NoiseLimitDbc { get; set; } = 110;

    /// <summary>Lowest speed the engine has to work at.</summary>
    public double BandFromRpm { get; set; } = 4000;

    public double BandToRpm { get; set; } = 7500;

    public TorqueShape Shape { get; set; } = TorqueShape.Balanced;

    public string SoundTarget { get; set; } = "Straight-six howl";

    // ---- Steps ------------------------------------------------------------

    public static IReadOnlyList<(WizardStep Step, string Title, string Question)> Steps { get; } =
    [
        (WizardStep.Purpose, "Purpose", "What are you building?"),
        (WizardStep.Engine, "Engine", "The basics: bore, stroke, cylinders, compression, redline."),
        (WizardStep.Head, "Head & cam", "Valve sizes and how aggressive the cam is."),
        (WizardStep.Fuel, "Fuel & conditions", "What it runs on, and where."),
        (WizardStep.Aspiration, "Aspiration", "Naturally aspirated, or forced induction?"),
        (WizardStep.Constraints, "Constraints", "What will physically fit, and what you must stay under."),
        (WizardStep.Goal, "Goal", "Where you want the torque, and what you want it to sound like."),
        (WizardStep.Review, "Review", "Every assumption, editable in place."),
        (WizardStep.Compute, "Compute", "Fill the model, search the geometry, write the brief."),
    ];

    /// <summary>
    /// The "why this matters" explainer for a step (plan §8.6: two or three
    /// plain sentences). Not decoration — a user who does not know why a
    /// question is being asked cannot answer it well, and a wizard that
    /// collects bad answers efficiently is worse than one that is slow.
    /// </summary>
    public static string Explainer(WizardStep step) => step switch
    {
        WizardStep.Purpose =>
            "What the engine is for decides everything downstream: which rules apply, what counts as a "
            + "good torque curve, and how much noise you are allowed to make. An FSAE engine and a street "
            + "engine want opposite things from the same hardware.",

        WizardStep.Engine =>
            "Bore and stroke set displacement and mean piston speed, which together bound what the engine "
            + "can breathe and how fast it can safely turn. Compression ratio trades efficiency against "
            + "knock — and the fuel you pick later decides how much of it you can use.",

        WizardStep.Head =>
            "Valve area is the throttle on everything else: no runner length rescues a head that cannot "
            + "flow. Cam character sets how long the valves are open and how much they overlap, which is "
            + "what decides whether wave tuning has a window to work in.",

        WizardStep.Fuel =>
            "Fuel sets the stoichiometric ratio, the knock resistance and how much the charge cools as it "
            + "evaporates. Altitude and temperature set air density, and a 30 °C day at 1500 m is a "
            + "different engine from a 10 °C day at sea level.",

        WizardStep.Aspiration =>
            "A naturally aspirated engine is tuned by wave action alone, so runner and primary lengths do "
            + "the whole job. Forced induction changes what the exhaust is for — energy for the turbine "
            + "rather than scavenging — and moves the optimum.",

        WizardStep.Constraints =>
            "The best runner length is worthless if it does not fit the car. Telling the optimiser what is "
            + "physically possible up front is the difference between a recommendation you can build and a "
            + "number you have to compromise afterwards.",

        WizardStep.Goal =>
            "An engine cannot be tuned everywhere at once: a long runner fills the midrange and chokes the "
            + "top, a short one does the reverse. Choosing the band is choosing which one you want, and "
            + "the sound target picks between designs that make similar power.",

        WizardStep.Review =>
            "Everything below was either answered by you or derived from your answers. Derived values are "
            + "marked Auto and will be replaced if you change what they came from; anything you edit here "
            + "becomes yours and is never overwritten.",

        _ =>
            "The model is filled in, a bounded search runs over the geometry that is still free, and the "
            + "winner is verified with a converged sweep. Every number in the brief comes from that model — "
            + "the same one Advanced mode opens.",
    };

    /// <summary>
    /// Honest statement of what the aspiration step can currently do. Forced
    /// induction is Phases 12–15, which are not built; offering the choice and
    /// then silently modelling it as naturally aspirated would be worse than
    /// saying so.
    /// </summary>
    public static string AspirationNote =>
        "Forced induction is not modelled yet — the turbomachinery phases are outstanding. Choosing it "
        + "here records the intent and keeps the rest of the brief naturally aspirated, clearly marked, "
        + "rather than quietly pretending.";

    public bool CanGoBack => Step != WizardStep.Purpose;

    public bool CanGoNext => Step != WizardStep.Compute;

    public void Next()
    {
        if (CanGoNext)
        {
            Step = (WizardStep)((int)Step + 1);
        }
    }

    public void Back()
    {
        if (CanGoBack)
        {
            Step = (WizardStep)((int)Step - 1);
        }
    }

    public void GoTo(WizardStep step) => Step = step;

    // ---- Derivation -------------------------------------------------------

    /// <summary>
    /// Fill the full model from the answers plus derived defaults.
    ///
    /// Returns paths and values rather than writing them, so the caller can
    /// preview a re-run before committing — which is what plan §8.8 rule 3
    /// requires and what makes the wizard safe to run twice.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Derive()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        var stroke = StrokeMm;
        var bore = BoreMm;
        var displacementCc = Math.PI / 4.0 * bore * bore * stroke * Cylinders / 1000.0;

        values["Engine.BoreMm"] = bore;
        values["Engine.StrokeMm"] = stroke;
        values["Engine.CylinderCount"] = Cylinders;
        values["Engine.CompressionRatio"] = CompressionRatio;

        // Rod ratio 1.7 is the middle of normal practice; below 1.5 the piston
        // dwells too briefly near TDC and side loads climb, above 2.0 the
        // block gets tall for little gain.
        values["Engine.RodLengthMm"] = Math.Round(stroke * 1.7, 1);

        // Valve sizes from the bore, when not given. The intake is the one
        // that matters: 0.50 × bore is a normal four-valve intake head
        // diameter and about the largest that fits with a matching exhaust.
        var intake = IntakeValveMm > 0 ? IntakeValveMm : Math.Round(bore * 0.40, 1);
        var exhaust = ExhaustValveMm > 0 ? ExhaustValveMm : Math.Round(intake * 0.85, 1);
        values["IntakeValves.HeadDiameterMm"] = intake;
        values["ExhaustValves.HeadDiameterMm"] = exhaust;
        values["IntakeValves.Count"] = 2;
        values["ExhaustValves.Count"] = 2;
        values["IntakeValves.MaxLiftMm"] = Math.Round(intake * 0.30, 1);
        values["ExhaustValves.MaxLiftMm"] = Math.Round(exhaust * 0.30, 1);

        // Throat diameter 0.85 × head is Blair's figure for a normal seat.
        values["IntakeValves.ThroatDiameterMm"] = Math.Round(intake * 0.85, 1);
        values["ExhaustValves.ThroatDiameterMm"] = Math.Round(exhaust * 0.85, 1);

        var (intakeOpen, intakeClose, exhaustOpen, exhaustClose) = CamEvents(Cam);
        values["IntakeValves.OpenDeg"] = intakeOpen;
        values["IntakeValves.CloseDeg"] = intakeClose;
        values["ExhaustValves.OpenDeg"] = exhaustOpen;
        values["ExhaustValves.CloseDeg"] = exhaustClose;

        values["Combustion.Fuel"] = Fuel;
        values["Combustion.Lambda"] = Lambda;
        values["Ambient.TemperatureK"] = AmbientTemperatureC + 273.15;

        // Barometric formula for the troposphere (ISA): p = p0(1 − 2.25577e-5·h)^5.25588.
        values["Ambient.PressureKPa"] =
            Math.Round(101.325 * Math.Pow(1.0 - (2.25577e-5 * AltitudeM), 5.25588), 3);

        var geometry = SeedGeometry();
        values["IntakeRunner.LengthMm"] = geometry.IntakeLengthMm;
        values["IntakeRunner.DiameterMm"] = geometry.IntakeDiameterMm;
        values["ExhaustRunner.LengthMm"] = geometry.PrimaryLengthMm;
        values["ExhaustRunner.DiameterMm"] = geometry.PrimaryDiameterMm;
        values["IntakeRunner.RoughnessMm"] = 0.045;
        values["ExhaustRunner.RoughnessMm"] = 0.045;

        _ = displacementCc;
        return values;
    }

    /// <summary>Apply the derived model. Protected fields survive untouched.</summary>
    public ApplyResult Apply(ISet<string>? optIn = null) => _session.ApplyWizard(Derive(), optIn);

    /// <summary>Preview what applying would do, without doing it.</summary>
    public ApplyResult Preview(ISet<string>? optIn = null) => _session.PreviewWizard(Derive(), optIn);

    // ---- The fast analytical seed ----------------------------------------

    /// <summary>Geometry the wizard starts from, before any search.</summary>
    /// <param name="IntakeLengthMm">Intake runner length.</param>
    /// <param name="IntakeDiameterMm">Intake runner diameter.</param>
    /// <param name="PrimaryLengthMm">Exhaust primary length.</param>
    /// <param name="PrimaryDiameterMm">Exhaust primary diameter.</param>
    /// <param name="TargetRpm">Speed the lengths are tuned to.</param>
    public readonly record struct SeedResult(
        double IntakeLengthMm,
        double IntakeDiameterMm,
        double PrimaryLengthMm,
        double PrimaryDiameterMm,
        double TargetRpm);

    /// <summary>
    /// Which reflection an intake runner is tuned to catch.
    ///
    /// The organ-pipe relation <c>L = a·Δθ/(12N)</c> is the FUNDAMENTAL: the
    /// wave making one round trip in the whole valve-open window. A real
    /// runner is tuned to a later return, and taking the fundamental instead
    /// gives lengths around three times what anyone builds — 2.29 m at 3000
    /// rpm, against a metre of engine bay.
    ///
    /// Three is checkable against this project's own §6.2 validation case: the
    /// Yin thesis engine's measured optimum is 800 mm at 3000 rpm and 600 mm
    /// at 4000 rpm on a 235° window, where the third return gives 746 mm and
    /// 560 mm. Consistently 7% short — the difference between an ideal organ
    /// pipe and a real tapered runner sitting on a plenum — which is well
    /// inside what the search that follows covers.
    /// </summary>
    public const double IntakeTuningOrder = 3.0;

    /// <summary>
    /// The same for the exhaust primary, and the same arithmetic demands it: a
    /// 620 m/s wave crossing the exhaust window at the fundamental gives a
    /// 2.1 m primary, where real four-cylinder headers are 400–800 mm. A
    /// 500 mm primary does a round trip in about 58° of crank at 6000 rpm, so
    /// between EVO and overlap the wave makes several traversals and it is a
    /// later one that arrives when it is wanted.
    ///
    /// <b>Weaker than the intake figure.</b> Three is carried over from the
    /// intake, where the §6.2 Yin case pins it; there is no equivalent
    /// validation anchor for the exhaust in this project yet, so this is a
    /// SEED for a search and not a recommendation in its own right. The search
    /// that follows spans ±20% around it and reports what it actually
    /// measured.
    /// </summary>
    public const double ExhaustTuningOrder = 3.0;

    /// <summary>
    /// Seed the geometry analytically (plan §2.10, §8.6 step 2 of the
    /// under-the-hood sequence). Milliseconds, so the preview appears
    /// immediately and the search starts somewhere sensible rather than in the
    /// middle of the bounds.
    /// </summary>
    public SeedResult SeedGeometry()
    {
        var target = TargetRpm();

        // Intake: organ-pipe wave return over the induction window, at ambient
        // sound speed since the intake tract runs near ambient.
        const double intakeSoundSpeed = 343.0;
        var intakeWindow = IntakeWindowDeg();
        var intakeLength =
            QuickEstimate.OrganPipeTunedLength(intakeSoundSpeed, intakeWindow, target)
            / IntakeTuningOrder * 1000.0;

        // Exhaust: the same relation at exhaust-gas sound speed, over the
        // exhaust window. ~620 m/s is a normal primary at these temperatures;
        // the solved value refines it.
        const double exhaustSoundSpeed = 620.0;
        var exhaustWindow = ExhaustWindowDeg();
        var primaryLength =
            QuickEstimate.OrganPipeTunedLength(exhaustSoundSpeed, exhaustWindow, target)
            / ExhaustTuningOrder * 1000.0;

        // Diameters from a target MEAN PORT VELOCITY — the standard
        // definition, piston area times mean piston speed over port area:
        //
        //     v_port = A_piston · c_m / A_port
        //
        // Not the cycle-mean volume flow, which is the tempting shortcut and
        // is wrong by a factor of four: an intake valve is open for roughly a
        // quarter of the cycle, so spreading the charge over all 720° gives a
        // 17 mm runner for an 82 mm bore, which no engine has and which the
        // solver will not even converge on.
        //
        // 90 m/s is the usual compromise: faster fills the midrange by
        // inertia, slower flows better at the top.
        var pistonArea = Math.PI / 4.0 * Math.Pow(BoreMm / 1000.0, 2);
        var meanPistonSpeed = 2.0 * (StrokeMm / 1000.0) * target / 60.0;
        var intakeArea = pistonArea * meanPistonSpeed / 90.0;
        var intakeDiameter = Math.Sqrt(4.0 * intakeArea / Math.PI) * 1000.0;

        // The exhaust carries the same mass at roughly three times the
        // specific volume, so a primary is normally a little smaller than the
        // intake runner despite the larger volume flow — velocity there is
        // allowed to be much higher.
        var primaryDiameter = intakeDiameter * 0.92;

        // But never smaller than the valve throats feeding it. A primary that
        // cannot pass what the exhaust valves can flow is a restriction the
        // valves were sized to avoid, and it is the pipe rather than the valve
        // that then sets the engine's breathing — which is the wrong way
        // round on any engine anyone would build.
        //
        // It is also where this solver currently fails: below that ratio the
        // duct's end cell goes non-positive and the run aborts. See
        // docs/numerics.md — the solver defect is real and separate, and this
        // bound is here because it is right, not to dodge it.
        var exhaustHead = ExhaustValveMm > 0
            ? ExhaustValveMm
            : (IntakeValveMm > 0 ? IntakeValveMm : BoreMm * 0.40) * 0.85;
        var throatArea = 2.0 * Math.PI / 4.0 * Math.Pow(exhaustHead * 0.85, 2);
        var minimumPrimary = Math.Sqrt(4.0 * throatArea / Math.PI) * 1.08;
        primaryDiameter = Math.Max(primaryDiameter, minimumPrimary);

        return new SeedResult(
            Math.Round(Math.Clamp(intakeLength, 80, PackagingLimitMm), 0),
            Math.Round(Math.Clamp(intakeDiameter, 20, 70), 1),
            Math.Round(Math.Clamp(primaryLength, 150, PackagingLimitMm * 1.4), 0),
            Math.Round(Math.Clamp(primaryDiameter, 18, 60), 1),
            target);
    }

    /// <summary>
    /// The speed the geometry is tuned to, from the band and the torque shape.
    /// A broad-midrange preference tunes low in the band, peak power high.
    /// </summary>
    public double TargetRpm()
    {
        var fraction = Shape switch
        {
            TorqueShape.BroadMidrange => 0.35,
            TorqueShape.PeakPower => 0.85,
            _ => 0.6,
        };

        // To the nearest 10 rpm. Math.Round's digits argument cannot be
        // negative, so the scaling is explicit.
        var target = BandFromRpm + ((BandToRpm - BandFromRpm) * fraction);
        return Math.Round(target / 10.0) * 10.0;
    }

    /// <summary>Crank degrees the intake wave has to make its round trip in.</summary>
    public double IntakeWindowDeg()
    {
        var (open, close, _, _) = CamEvents(Cam);
        return close - open;
    }

    public double ExhaustWindowDeg()
    {
        var (_, _, open, close) = CamEvents(Cam);
        return close - open;
    }

    /// <summary>
    /// Valve events by cam character, in cycle degrees with 0 at firing TDC.
    ///
    /// These are representative production and race figures, not measurements:
    /// duration climbs and overlap widens as the character gets more
    /// aggressive, which is the whole trade — a race cam gives wave tuning a
    /// wide window to work in and gives up idle quality and low-speed torque
    /// to get it. A user with a real profile imports it instead and these are
    /// never used.
    /// </summary>
    public static (double IntakeOpen, double IntakeClose, double ExhaustOpen, double ExhaustClose)
        CamEvents(CamCharacter character) => character switch
    {
        // Duration ≈ 200° intake, tiny overlap.
        CamCharacter.Stock => (355.0, 575.0, 145.0, 365.0),

        // ≈ 230°, modest overlap.
        CamCharacter.Mild => (345.0, 585.0, 135.0, 375.0),

        // ≈ 250°.
        CamCharacter.Aggressive => (335.0, 595.0, 125.0, 385.0),

        // ≈ 270°, wide overlap: idles badly, breathes at the top.
        _ => (325.0, 605.0, 115.0, 395.0),
    };

    /// <summary>Mean piston speed at the redline, m/s — the number that bounds it.</summary>
    public double MeanPistonSpeed() => 2.0 * StrokeMm * 1e-3 * RedlineRpm / 60.0;

    public double DisplacementCc() => Math.PI / 4.0 * BoreMm * BoreMm * StrokeMm * Cylinders / 1000.0;

    /// <summary>
    /// Checks on the answers themselves, before anything is derived from them.
    /// A wizard that accepts an impossible engine and then reports its
    /// performance is worse than one that objects.
    /// </summary>
    public IReadOnlyList<ModelIssue> Check()
    {
        var issues = new List<ModelIssue>();

        void Warn(string path, string message) =>
            issues.Add(new ModelIssue(ModelIssueSeverity.Warning, path, message));

        void Error(string path, string message) =>
            issues.Add(new ModelIssue(ModelIssueSeverity.Error, path, message));

        if (BandToRpm <= BandFromRpm)
        {
            Error("Goal.Band", "The top of the band must be above the bottom.");
        }

        if (BandToRpm > RedlineRpm)
        {
            Warn("Goal.Band",
                $"The band runs to {BandToRpm:F0} rpm but the redline is {RedlineRpm:F0}. "
                + "Tuning above the redline puts the peak where the engine never goes.");
        }

        var pistonSpeed = MeanPistonSpeed();
        if (pistonSpeed > 25.0)
        {
            Warn("Engine.Redline",
                $"Mean piston speed at the redline is {pistonSpeed:F1} m/s. Production engines live below "
                + "about 20 and race engines below about 25; past that, rings and rods become the limit "
                + "rather than breathing.");
        }

        if (CompressionRatio > 12.5 && Fuel.Contains("RON9", StringComparison.OrdinalIgnoreCase))
        {
            Warn("Engine.CompressionRatio",
                $"{CompressionRatio:F1}:1 on pump fuel will knock unless the cam bleeds cylinder pressure. "
                + "The solve will say whether it does.");
        }

        var seed = SeedGeometry();
        if (seed.IntakeLengthMm >= PackagingLimitMm - 1)
        {
            Warn("Constraints.Packaging",
                $"The tuned intake length for {TargetRpm():F0} rpm is longer than the {PackagingLimitMm:F0} mm "
                + "you can fit, so it has been capped. Expect the torque peak above your chosen band.");
        }

        if (ForcedInduction)
        {
            Warn("Aspiration", AspirationNote);
        }

        return issues;
    }
}
