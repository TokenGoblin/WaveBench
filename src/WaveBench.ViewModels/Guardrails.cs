using System.Globalization;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>How loudly a caveat has to be said.</summary>
public enum CaveatWeight
{
    /// <summary>Worth knowing. The answer is still the answer.</summary>
    Note,

    /// <summary>This is probably the largest error in the result on screen.</summary>
    Dominant,
}

/// <summary>
/// One reason to trust the answer a little less, and what to do about it.
/// </summary>
/// <param name="Title">The short form, for the banner.</param>
/// <param name="Detail">What it means and what it costs.</param>
/// <param name="Weight">Whether this is the dominant error source.</param>
/// <param name="Fix">What would remove it — always something the user can actually do.</param>
/// <param name="Links">Where to go.</param>
/// <param name="Citation">Where the claim about its size comes from, where there is one.</param>
public sealed record DataCaveat(
    string Title,
    string Detail,
    CaveatWeight Weight,
    string Fix,
    IReadOnlyList<WarningLink> Links,
    string? Citation = null);

/// <summary>
/// The guardrails of plan §8.10: the generic-defaults banner, implausible-input
/// detection, and the rule that nothing extrapolates silently.
///
/// <b>The banner is about the DATA, not the code.</b> A beginner's instinct is
/// that a simulation is wrong because the physics is approximate. Usually it
/// is wrong because the discharge coefficients are generic, the cam is an
/// analytic curve and the compressor map is an extrapolation — all things the
/// user could fix and none of which the solver can know it is missing unless
/// something looks. Plan §8.10: <i>"A beginner should know the biggest error
/// source is the data they did not supply."</i>
///
/// Nothing here blocks anything. Every item is a statement with a remedy
/// attached.
/// </summary>
public sealed class Guardrails(ProjectSession session, UserPreferences preferences)
{
    private EngineModelDocument Document => session.Document;

    /// <summary>
    /// Extra caveats contributed by a workspace that has computed something
    /// this class cannot see — a compressor operating line running outside its
    /// map's measured region, most of all.
    ///
    /// A hook rather than a dependency: making the banner solve a turbo match
    /// in order to draw itself would put a second of work behind a label.
    /// </summary>
    public IList<DataCaveat> Contributed { get; } = [];

    /// <summary>
    /// Everywhere the answer rests on data nobody supplied, worst first.
    /// </summary>
    public IReadOnlyList<DataCaveat> GenericDefaults()
    {
        var caveats = new List<DataCaveat>();

        // 1. Discharge coefficients. Stated first and as DOMINANT because on
        //    a naturally aspirated engine it usually is: the whole breathing
        //    calculation hangs off a generic curve, and every other caveat
        //    here is smaller.
        //
        //    Unconditional, because the document has nowhere to put measured
        //    flow data yet. It was briefly conditioned on "anything under
        //    IntakeValves was imported", which the shipped sample satisfies
        //    with a measured CAM file — so the largest caveat in the tool
        //    silently disappeared on the strength of evidence about a
        //    different quantity. A caveat that switches itself off for the
        //    wrong reason is worse than one that never appears at all.
        {
            caveats.Add(new(
                "Discharge coefficients are generic",
                "Valve flow is coming from a generic C_d curve rather than from this head. Two heads with the "
                + "same valve sizes can differ by 10% in flow at high lift, and torque follows flow almost "
                + "proportionally — so this is very likely the largest error in the answer.",
                CaveatWeight.Dominant,
                "Measured flow-bench data for this head would replace it; even three lift points helps.",
                [
                    WarningLink.Field("IntakeValves.ThroatDiameterMm"),
                    WarningLink.Plot(Workspace.Library, "Flow data", "import measured flow"),
                ],
                "Heywood, Internal Combustion Engine Fundamentals, §6.3"));
        }

        // 2. Analytic cam profiles.
        var analytic = new[] { "IntakeValves", "ExhaustValves" }
            .Where(side => !HasImportedLift(side))
            .ToList();

        if (analytic.Count > 0)
        {
            caveats.Add(new(
                analytic.Count == 2 ? "Both cam profiles are analytic" : $"The {Side(analytic[0])} cam is analytic",
                "Lift is coming from an idealised curve of the chosen shape, not from a measured lobe. The area "
                + "under an analytic profile is close; the opening and closing RAMPS are not, and those are where "
                + "the flow is most sensitive to timing.",
                CaveatWeight.Note,
                "Import a measured lift file in Library → Cams.",
                [
                    WarningLink.Field(analytic[0] + ".CamShape"),
                    WarningLink.Plot(Workspace.Library, "Cams", "import a measured lobe"),
                ]));
        }

        // 3. Turbo maps. Every map that ships with this tool is analytic, by
        //    licence (plan §4.7) — so this is not a gap that importing fixes
        //    by accident, and saying so is the honest form.
        if (Document.ForcedInduction.IsForced && !HasImported("ForcedInduction.TurboName"))
        {
            caveats.Add(new(
                "The turbo map is analytic",
                "Every turbocharger in the shipped library is an analytic surface, not a manufacturer map — this "
                + "tool ships no manufacturer maps without permission. The trends are right and the absolute "
                + "efficiencies are representative rather than measured.",
                CaveatWeight.Dominant,
                "Digitise the real map in Library → Turbos; the digitiser takes a screenshot of a published map.",
                [
                    WarningLink.Field("ForcedInduction.TurboName"),
                    WarningLink.Plot(Workspace.Library, "Turbos", "digitise a real map"),
                ],
                "plan §4.7"));
        }

        // 4. Anything the user has not touched that materially moves the
        //    answer. Reported as one item rather than forty, because forty
        //    would be a wall of text nobody reads — which is the failure mode
        //    a banner has.
        var untouched = Influential()
            .Where(path => session.Provenance[path].Origin == Provenance.Auto)
            .ToList();

        if (untouched.Count > 0)
        {
            caveats.Add(new(
                $"{untouched.Count} influential field{(untouched.Count == 1 ? " is" : "s are")} still at a default",
                "These have never been set by you, a wizard or an import, so they carry whatever the template or "
                + "a derivation gave them: "
                + string.Join(", ", untouched.Select(p => FieldLocator.Find(p)?.Label ?? p).Take(6))
                + (untouched.Count > 6 ? $", and {untouched.Count - 6} more." : "."),
                CaveatWeight.Note,
                "Set the ones you know. A default you have checked is worth more than one you have not.",
                untouched.Take(4).Select(p => WarningLink.Field(p)).ToList()));
        }

        caveats.AddRange(Contributed);

        return caveats.OrderByDescending(c => c.Weight).ToList();
    }

    /// <summary>
    /// Values that are legal but unusual (plan §8.10). One per field, in
    /// catalogue order, and never a refusal — the value is already in the
    /// document by the time this runs.
    /// </summary>
    public IReadOnlyList<DataCaveat> UnusualInputs()
    {
        var editor = new FieldEditor(session, preferences);
        var caveats = new List<DataCaveat>();

        foreach (var field in FieldLocator.All)
        {
            // A forced-induction field on a naturally aspirated model is not
            // unusual, it is unused. Complaining about an inert value is how a
            // warning list teaches people to ignore warning lists.
            if (!Document.ForcedInduction.IsForced && field.Path.StartsWith("ForcedInduction.", StringComparison.Ordinal))
            {
                continue;
            }

            if (editor.Unusual(field) is not { } message)
            {
                continue;
            }

            caveats.Add(new(
                $"{field.Label} is outside the usual range",
                message,
                CaveatWeight.Note,
                "If that is deliberate, nothing here stops you — this is a note, not a limit.",
                [WarningLink.Field(field.Path)]));
        }

        return caveats;
    }

    /// <summary>Everything, for the banner and for the report's caveats section.</summary>
    public IReadOnlyList<DataCaveat> All() => [.. GenericDefaults(), .. UnusualInputs()];

    /// <summary>
    /// The one line the shell shows above every workspace, or null when there
    /// is genuinely nothing to say.
    /// </summary>
    public string? Banner()
    {
        var all = All();
        if (all.Count == 0)
        {
            return null;
        }

        var dominant = all.Where(c => c.Weight == CaveatWeight.Dominant).ToList();

        return dominant.Count > 0
            ? $"{dominant[0].Title} — likely the largest error here"
              + (all.Count > dominant.Count ? $", and {all.Count - 1} other caveat{Plural(all.Count - 1)}." : ".")
            : $"{all.Count} caveat{Plural(all.Count)} on this model: {all[0].Title.ToLowerInvariant()}"
              + (all.Count > 1 ? ", and others." : ".");
    }

    // ---- Internals --------------------------------------------------------

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string Side(string block) =>
        block.StartsWith("Intake", StringComparison.Ordinal) ? "intake" : "exhaust";

    /// <summary>
    /// Whether a measured LIFT file is behind one side of the valvetrain.
    ///
    /// Named for the quantity rather than for the path prefix, because a
    /// prefix check answers the wrong question: everything about a valve lives
    /// under the same block, so "something under IntakeValves was imported" is
    /// equally true of a measured cam, a measured flow curve and a seat
    /// diameter typed off a drawing — and only one of those says anything
    /// about the lift.
    /// </summary>
    private bool HasImportedLift(string side) =>
        new[] { ".MaxLiftMm", ".OpenDeg", ".CloseDeg", ".CamShape" }
            .Any(suffix => session.Provenance[side + suffix].Origin == Provenance.Imported);

    /// <summary>Whether anything under a path prefix came from a file.</summary>
    private bool HasImported(string prefix) =>
        session.Provenance.RecordedPaths.Any(p =>
            p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && session.Provenance[p].Origin == Provenance.Imported);

    /// <summary>
    /// The fields whose default is worth flagging: the ones a sensitivity
    /// study puts at the top, not every field in the document.
    ///
    /// Listed rather than derived, because "influential" is a claim about the
    /// physics and the honest way to make it is by name. Everything here moves
    /// torque by more than a per cent across its own typical range.
    /// </summary>
    private static IReadOnlyList<string> Influential() =>
    [
        "IntakeRunner.LengthMm",
        "IntakeRunner.DiameterMm",
        "ExhaustRunner.LengthMm",
        "ExhaustRunner.DiameterMm",
        "IntakeValves.CloseDeg",
        "ExhaustValves.OpenDeg",
        "IntakeValves.MaxLiftMm",
        "Engine.CompressionRatio",
        "Combustion.Lambda",
        "Combustion.DurationDeg",
        "Combustion.StartDeg",
    ];
}
