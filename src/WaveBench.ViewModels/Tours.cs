namespace WaveBench.ViewModels;

/// <summary>
/// One step of a guided tour: what to say, and what to point at.
/// </summary>
/// <param name="Title">The step's headline.</param>
/// <param name="Body">What it explains.</param>
/// <param name="Target">
/// What to highlight — a field, a figure or a component, addressed exactly as
/// a <see cref="WarningLink"/> is, so the same navigation code serves both.
/// </param>
public sealed record TourStep(string Title, string Body, WarningLink? Target = null);

/// <summary>
/// A guided tour of one workspace (plan §8.9: <i>"Guided tours per workspace,
/// skippable and re-runnable"</i>).
/// </summary>
/// <param name="Workspace">Which workspace it explains.</param>
/// <param name="Title">Shown on the "take the tour" offer.</param>
/// <param name="Steps">In order.</param>
public sealed record Tour(Workspace Workspace, string Title, IReadOnlyList<TourStep> Steps);

/// <summary>
/// The tours themselves. Data, and every target is a real address that
/// <see cref="WarningLink"/> already knows how to resolve — so a tour cannot
/// point at a field that does not exist, and a test can check that.
/// </summary>
public static class TourLibrary
{
    public static IReadOnlyList<Tour> All { get; } =
    [
        new(Workspace.Design, "How this engine is described",
        [
            new("Start with the cylinder",
                "Bore, stroke and compression ratio fix the displacement and the thermodynamic ceiling. Nothing "
                + "else in the model can make up for getting these wrong, and everything downstream is scaled by "
                + "them.",
                WarningLink.Field("Engine.BoreMm")),

            new("Then the valves",
                "Valve size and lift set how much air can get in at all. On most naturally aspirated engines this "
                + "is the binding constraint — the manifold can only tune what the head lets through.",
                WarningLink.Field("IntakeValves.HeadDiameterMm")),

            new("Intake closing is the one to watch",
                "Of the four cam events, intake closing moves volumetric efficiency most. Late closing keeps "
                + "filling past BDC at speed and pushes charge back out at idle; that single trade is most of what "
                + "a cam choice is.",
                WarningLink.Field("IntakeValves.CloseDeg")),

            new("Runner length picks an rpm",
                "A long runner tunes low, a short one high. Press \"Show me\" beside it to sweep the field and "
                + "watch the torque curve move — it takes about ten seconds and settles most arguments.",
                WarningLink.Field("IntakeRunner.LengthMm")),

            new("Every value says where it came from",
                "The badge at the end of each row is its provenance: Auto, Wizard, You, Imported or Optimised. "
                + "Hover it for the derivation and its citation. Nothing in this model is a number without a "
                + "history."),
        ]),

        new(Workspace.Boost, "Reading a turbo match",
        [
            new("The map is the screen",
                "The compressor map with this engine's operating line drawn on it is the whole of a turbo match. "
                + "Everything else on this workspace is a consequence of where that line sits.",
                WarningLink.Plot(Workspace.Boost, "Compressor")),

            new("Surge is on the left, choke on the right",
                "The line has to sit between them with margin at every engine speed. Too large a compressor puts "
                + "the low end into surge; too small puts the top end into choke. Both are sizing decisions, not "
                + "tuning ones.",
                WarningLink.Field("ForcedInduction.TurboName")),

            new("The housing trades spool against top end",
                "A small turbine A/R spools early and chokes the top; a large one does the opposite. The A/R sweep "
                + "draws the trade rather than picking for you, because which end matters is a question about the "
                + "car, not the engine.",
                WarningLink.Plot(Workspace.Boost, "Turbine")),

            new("Boost you did not cool is boost you did not get",
                "Compression heats the air, and hot air is thin. The charge-cooling figure shows how much of the "
                + "pressure ratio survives as density — usually rather less than the boost gauge suggests.",
                WarningLink.Plot(Workspace.Boost, "Charge Cooling")),
        ]),

        new(Workspace.Results, "Reading the results",
        [
            new("Torque is the shape, power is the arithmetic",
                "Power is torque times speed, so the power curve adds no information the torque curve did not "
                + "have. Judge a design on torque; quote it in power.",
                WarningLink.Plot(Workspace.Results, "Performance")),

            new("Volumetric efficiency is what the model actually computed",
                "Torque follows trapped air almost proportionally. A VE curve above 1.0 near the tuned speed is "
                + "not an error — it is the ram and wave effects doing what the manifold was designed to do."),

            new("The wave diagram shows why",
                "Distance along the pipe on one axis, crank angle on the other. The diagonal streaks are pressure "
                + "waves, and their slope is the speed of sound. A dip in torque at one engine speed is usually a "
                + "wave arriving at the wrong moment, and this is where you can see it arrive.",
                WarningLink.Plot(Workspace.Results, "Waves")),

            new("Per-cylinder before you believe the mean",
                "Four cylinders at 0.95 and four at 1.05 average to the same number as eight even ones, and only "
                + "one of those is a manifold worth building.",
                WarningLink.Plot(Workspace.Results, "Cylinders")),
        ]),

        new(Workspace.Optimise, "Running a search",
        [
            new("Variables first",
                "Pick what is allowed to change, with bounds and — where a fabricator is going to cut it — a step. "
                + "Everything not listed is held at this model's value, which is what makes a result attributable."),

            new("Then what \"better\" means",
                "Area under torque over a band is the usual answer, because a peak that exists at one engine speed "
                + "is not a fast car. Constraints are lexicographic: a design that violates one is worse than any "
                + "design that does not, however good its objective."),

            new("The budget is the cost",
                "One number decides what a run costs. The inner loop uses a coarser mesh to rank candidates and "
                + "the full solve to report them, so the budget buys more evaluations than it looks like."),

            new("The front is the answer, not a winner",
                "Where two objectives disagree there is no best design, only a choice. The Pareto tab draws every "
                + "design that is not beaten on both, and you pick the one that suits the car."),
        ]),

        new(Workspace.Sound, "What the engine will sound like",
        [
            new("Orders, not frequencies",
                "Engine noise arrives in multiples of shaft speed. A four-cylinder four-stroke fires on 2nd order. "
                + "Those draw sloping lines in a spectrogram; a resonance draws a flat one, which is how you tell "
                + "them apart.",
                WarningLink.Plot(Workspace.Sound, "Spectrum")),

            new("Silencing is a transmission loss, per frequency",
                "A silencer does not turn the volume down evenly. It attenuates some bands and passes others, and "
                + "what gets through is what the exhaust sounds like.",
                WarningLink.Plot(Workspace.Sound, "Silencing")),

            new("Then listen to it",
                "The audition tab renders the model. Two designs that differ by three decibels can sound entirely "
                + "different, and no number on this screen will tell you that.",
                WarningLink.Plot(Workspace.Sound, "Audition")),
        ]),
    ];

    public static Tour? For(Workspace workspace) =>
        All.FirstOrDefault(t => t.Workspace == workspace);
}

/// <summary>
/// Which tour is running and where it has got to.
///
/// <b>Skippable and re-runnable</b>, which between them mean the state cannot
/// live in a "seen it" flag: a user who skipped has not seen it, and a user
/// who finished may want it again. So it is an explicit position, started and
/// stopped on demand, and dismissing it sets nothing permanent.
/// </summary>
public sealed class TourController
{
    public Tour? Current { get; private set; }

    public int Step { get; private set; }

    public bool IsRunning => Current is not null;

    public TourStep? CurrentStep =>
        Current is not null && Step >= 0 && Step < Current.Steps.Count ? Current.Steps[Step] : null;

    /// <summary>"Step 2 of 5", for the view.</summary>
    public string Position => Current is null ? "" : $"Step {Step + 1} of {Current.Steps.Count}";

    public bool AtEnd => Current is not null && Step >= Current.Steps.Count - 1;

    public bool Start(Workspace workspace)
    {
        var tour = TourLibrary.For(workspace);
        if (tour is null)
        {
            return false;
        }

        Current = tour;
        Step = 0;
        return true;
    }

    /// <summary>Advance, ending the tour after the last step.</summary>
    public void Next()
    {
        if (Current is null)
        {
            return;
        }

        if (AtEnd)
        {
            Stop();
            return;
        }

        Step++;
    }

    public void Back() => Step = Math.Max(0, Step - 1);

    /// <summary>Skip or finish. Records nothing, so the tour can be taken again.</summary>
    public void Stop()
    {
        Current = null;
        Step = 0;
    }
}
