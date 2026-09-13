using WaveBench.Core.EngineModel;
using WaveBench.Model;
using WaveBench.Optimize;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels;

/// <summary>
/// Valve-to-piston clearance over the cycle (plan §2.6, and the hard
/// constraint Phase 22's gate names).
///
/// <b>One input the document schema does not yet carry.</b> The plan computes
/// this "from piston dome and valve-pocket geometry", and neither is in the
/// model today — so the clearance available at TDC with the valve shut is a
/// stated parameter rather than a derived one. Everything else is real: the
/// piston's position comes from the crank kinematics and the valve's lift from
/// the cam profile, both at every degree of the overlap window.
///
/// Stated rather than guessed, because a clearance constraint computed from an
/// invented pocket depth is worse than no constraint at all: it would read as
/// a safety check while permitting exactly the collision it appears to
/// prevent.
/// </summary>
/// <param name="ClearanceAtTdcMm">
/// Gap between the closed valve and the piston at TDC, mm. Measure it with
/// clay; it is the number the whole constraint rests on.
/// </param>
/// <param name="MinimumMm">
/// How much must remain at the worst point of the cycle. 1.0–1.5 mm on the
/// intake and 1.5–2.0 on the exhaust is common practice for a steel rod at
/// moderate speed — the exhaust gets more because it is hotter and grows more.
/// </param>
public sealed record ValveClearance(double ClearanceAtTdcMm = 4.0, double MinimumMm = 1.5)
{
    /// <summary>
    /// The tightest gap anywhere in the cycle, mm.
    ///
    /// Swept over the whole overlap region rather than evaluated at TDC alone:
    /// the minimum is NOT at TDC. The piston is at its highest there, but both
    /// valves are near their seats; the pinch point is 10–20° either side,
    /// where the piston has barely dropped and the valve is substantially
    /// open. Checking TDC only is the classic way to build an engine that
    /// passes the check and bends a valve.
    /// </summary>
    public double Minimum(EngineModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var crank = new CrankGeometry
        {
            Bore = document.Engine.BoreMm * 1e-3,
            Stroke = document.Engine.StrokeMm * 1e-3,
            RodLength = document.Engine.RodLengthMm * 1e-3,
            PinOffset = document.Engine.PinOffsetMm * 1e-3,
            CompressionRatio = document.Engine.CompressionRatio,
        };

        var intake = CamProfile.Harmonic(
            document.IntakeValves.OpenDeg, document.IntakeValves.CloseDeg,
            document.IntakeValves.MaxLiftMm * 1e-3);

        var exhaust = CamProfile.Harmonic(
            document.ExhaustValves.OpenDeg, document.ExhaustValves.CloseDeg,
            document.ExhaustValves.MaxLiftMm * 1e-3);

        // Gas-exchange TDC is 360° on the 0-720 cycle this project uses.
        var atTdc = crank.PistonPosition(360.0);
        var worst = double.PositiveInfinity;

        for (var angle = 270.0; angle <= 450.0; angle += 0.5)
        {
            // How far the crown has dropped from its TDC height, mm.
            var dropped = (crank.PistonPosition(angle) - atTdc) * 1000.0;

            var intakeGap = ClearanceAtTdcMm + dropped - (intake.Lift(angle) * 1000.0);
            var exhaustGap = ClearanceAtTdcMm + dropped - (exhaust.Lift(angle) * 1000.0);

            worst = Math.Min(worst, Math.Min(intakeGap, exhaustGap));
        }

        return worst;
    }

    /// <summary>As a hard constraint the optimiser may never break.</summary>
    public IConstraint AsConstraint() =>
        StandardConstraints.ValveToPistonClearance(Minimum, MinimumMm);
}

/// <summary>
/// A ready-made optimisation: what to vary, what to aim at, and what may never
/// be broken (plan §9.7).
/// </summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Name">What it is called on screen.</param>
/// <param name="Why">What question it answers, and what it will cost.</param>
public sealed record OptimisationPreset(string Id, string Name, string Why)
{
    public required Action<OptimiseWorkspace> Apply { get; init; }

    /// <summary>Whether this preset makes sense for a given model.</summary>
    public Func<EngineModelDocument, bool> AppliesTo { get; init; } = _ => true;
}

/// <summary>
/// The presets plan §9.7 asks for, plus the two obvious starting points.
///
/// <b>A preset is a question, not a shortcut.</b> Each one states what it is
/// asking and what it will cost, because the expensive mistake in optimisation
/// is not picking the wrong algorithm — it is running a long search on a
/// question nobody wanted answered.
/// </summary>
public static class OptimisationPresets
{
    /// <summary>
    /// Cam timing with clearance as a hard constraint (plan §9.7).
    ///
    /// The four valve events, bounded to what a cam can actually be ground to,
    /// with valve-to-piston clearance as a constraint the search may never
    /// break. Without that constraint this preset reliably returns enormous
    /// overlap — which breathes beautifully and bends a valve on the first
    /// start.
    /// </summary>
    public static OptimisationPreset CamTiming(ValveClearance? clearance = null)
    {
        var limits = clearance ?? new ValveClearance();

        return new OptimisationPreset(
            "cam-timing",
            "Cam timing",
            "Where should the valves open and close? Varies all four events with valve-to-piston clearance as "
            + "a hard limit. Without that limit the answer is always 'more overlap', right up until the valve "
            + "meets the piston.")
        {
            AppliesTo = d => d.IntakeValves.MaxLiftMm > 0 && d.ExhaustValves.MaxLiftMm > 0,
            Apply = workspace =>
            {
                Reset(workspace);

                workspace.Add("IntakeValves.OpenDeg");
                workspace.Add("IntakeValves.CloseDeg");
                workspace.Add("ExhaustValves.OpenDeg");
                workspace.Add("ExhaustValves.CloseDeg");

                workspace.Constraints.Add(limits.AsConstraint());
                workspace.Algorithm = SearchAlgorithm.Bayesian;
                workspace.Budget = 60;
            },
        };
    }

    /// <summary>Intake wave tuning — the two variables that dominate an NA engine.</summary>
    public static OptimisationPreset IntakeTuning { get; } = new(
        "intake-tuning",
        "Intake tuning",
        "Where does the torque peak sit? Varies runner length and diameter, which interact strongly and are "
        + "the biggest single lever on where an engine makes its power.")
    {
        Apply = workspace =>
        {
            Reset(workspace);
            workspace.Add("IntakeRunner.LengthMm");
            workspace.Add("IntakeRunner.DiameterMm");
            workspace.Algorithm = SearchAlgorithm.Bayesian;
            workspace.Budget = 40;
        },
    };

    /// <summary>The turbo trade, as a front rather than a single answer.</summary>
    public static OptimisationPreset BoostTrade { get; } = new(
        "boost-trade",
        "Response against power",
        "The fundamental turbo trade. Varies boost target and turbine A/R and returns a FRONT — because there "
        + "is no single best answer, only a choice about what the engine is for.")
    {
        AppliesTo = d => d.ForcedInduction.IsForced,
        Apply = workspace =>
        {
            Reset(workspace);
            workspace.Add("ForcedInduction.TargetBoostKPa");
            workspace.Add("ForcedInduction.TurbineAreaRatio");

            workspace.Objectives.Add(new AreaUnderTorque(workspace.BandFromRpm, workspace.BandToRpm));
            workspace.Objectives.Add(new ValueAtRpm(
                "Torque at the bottom", "N·m", workspace.BandFromRpm, p => p.TorqueNm));

            workspace.Algorithm = SearchAlgorithm.NsgaII;
            workspace.Budget = 80;
        },
    };

    public static IReadOnlyList<OptimisationPreset> All(ValveClearance? clearance = null) =>
        [IntakeTuning, CamTiming(clearance), BoostTrade];

    public static IReadOnlyList<OptimisationPreset> For(EngineModelDocument document, ValveClearance? clearance = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return All(clearance).Where(p => p.AppliesTo(document)).ToList();
    }

    private static void Reset(OptimiseWorkspace workspace)
    {
        foreach (var path in workspace.Variables.Select(v => v.Path).ToList())
        {
            workspace.Remove(path);
        }

        workspace.Objectives.Clear();
        workspace.Constraints.Clear();
    }

    // ---- The cam-timing study ----------------------------------------------

    /// <summary>The optimum cam timing found at one engine speed.</summary>
    /// <param name="Rpm">Where it was optimised.</param>
    /// <param name="LobeSeparationDeg">Lobe separation angle, camshaft degrees.</param>
    /// <param name="OverlapDeg">Overlap at the chosen timing, crank degrees.</param>
    /// <param name="Objective">What it scored.</param>
    /// <param name="ClearanceMm">Worst clearance of the chosen design — never below the limit.</param>
    public sealed record CamOptimum(
        double Rpm, double LobeSeparationDeg, double OverlapDeg, double Objective, double ClearanceMm);

    /// <summary>
    /// Optimum cam timing against engine speed — plan §9.7's <i>"result view
    /// plotting optimum LCA against rpm so the user can see whether VVT is
    /// worth the complexity"</i>.
    ///
    /// <b>The spread IS the answer.</b> A fixed camshaft can only be ground to
    /// one lobe separation, so what this study measures is how much that one
    /// choice costs at the speeds it was not chosen for. A flat line means a
    /// fixed cam gives away nothing and variable timing is mechanism for its
    /// own sake; a line that moves by ten degrees across the band means the
    /// engine wants two different cams and VVT is the only way to have both.
    /// Nothing else in the tool answers that question, and it is an expensive
    /// one to answer on a dyno.
    /// </summary>
    /// <param name="workspace">Configured with the cam-timing preset.</param>
    /// <param name="speeds">Engine speeds to optimise at, one run each.</param>
    /// <param name="clearance">The clearance model, to report the chosen design's margin.</param>
    /// <param name="budget">Evaluations per speed. The study costs this times the number of speeds.</param>
    /// <param name="evaluator">Supplied by the caller; the solver-backed one by default.</param>
    /// <param name="cancellation">Checked between speeds.</param>
    public static IReadOnlyList<CamOptimum> CamTimingStudy(
        OptimiseWorkspace workspace,
        IReadOnlyList<double> speeds,
        ValveClearance clearance,
        int budget = 40,
        IDesignEvaluator? evaluator = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(speeds);
        ArgumentNullException.ThrowIfNull(clearance);

        var results = new List<CamOptimum>();

        foreach (var rpm in speeds)
        {
            cancellation.ThrowIfCancellationRequested();

            // Optimised AT this speed: a narrow band around it, so the
            // objective is what the engine does here rather than an average
            // over everything.
            workspace.BandFromRpm = rpm;
            workspace.BandToRpm = rpm;
            workspace.Speeds = [rpm];
            workspace.Objectives.Clear();
            workspace.Objectives.Add(new ValueAtRpm("Torque", "N·m", rpm, p => p.TorqueNm));
            workspace.Budget = budget;

            workspace.Run(evaluator, cancellation: cancellation);

            if (workspace.LastResult?.Best is not { Feasible: true } best)
            {
                continue;
            }

            var document = best.Design.Materialise(workspace.Document);

            results.Add(new CamOptimum(
                rpm,
                LobeSeparation(document),
                Overlap(document),
                best.Objectives[0],
                clearance.Minimum(document)));
        }

        return results;
    }

    /// <summary>
    /// Lobe separation angle, camshaft degrees: half the sum of the intake
    /// centreline after TDC and the exhaust centreline before it.
    ///
    /// The number a cam grinder is given, and the one that decides overlap for
    /// a given pair of durations.
    /// </summary>
    public static double LobeSeparation(EngineModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Gas-exchange TDC sits at 360° on this project's 0–720 cycle.
        var intakeCentreline = ((document.IntakeValves.OpenDeg + document.IntakeValves.CloseDeg) / 2.0) - 360.0;
        var exhaustCentreline = 360.0 - ((document.ExhaustValves.OpenDeg + document.ExhaustValves.CloseDeg) / 2.0);

        return (intakeCentreline + exhaustCentreline) / 2.0;
    }

    /// <summary>Overlap in crank degrees: both valves off their seats around gas-exchange TDC.</summary>
    public static double Overlap(EngineModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var start = Math.Max(document.IntakeValves.OpenDeg, document.ExhaustValves.OpenDeg);
        var end = Math.Min(document.IntakeValves.CloseDeg, document.ExhaustValves.CloseDeg);
        return Math.Max(0.0, end - start);
    }

    /// <summary>
    /// The study as a figure: optimum lobe separation against engine speed,
    /// with the fixed-cam compromise drawn across it.
    /// </summary>
    public static PlotModel CamTimingChart(IReadOnlyList<CamOptimum> study)
    {
        ArgumentNullException.ThrowIfNull(study);

        if (study.Count < 2)
        {
            return new PlotModel
            {
                Title = "Optimum cam timing against speed",
                Subtitle = "nothing to draw",
                XAxis = new PlotAxis("", 0, 1),
                YAxis = new PlotAxis("", 0, 1),
                Notes = ["The study needs at least two speeds to show whether the optimum moves."],
            };
        }

        var rpm = study.Select(s => s.Rpm).ToList();
        var lsa = study.Select(s => s.LobeSeparationDeg).ToList();
        var overlap = study.Select(s => s.OverlapDeg).ToList();

        // What one fixed cam would have to be: the torque-weighted mean of the
        // per-speed optima. Not the plain mean — a cam should compromise
        // toward where the engine actually makes its torque.
        var weight = study.Sum(s => Math.Max(s.Objective, 0.0));
        var compromise = weight > 0
            ? study.Sum(s => Math.Max(s.Objective, 0.0) * s.LobeSeparationDeg) / weight
            : lsa.Average();

        var spread = lsa.Max() - lsa.Min();

        return new PlotModel
        {
            Title = "Optimum cam timing against speed",
            Subtitle = $"Lobe separation the engine wants at each speed · spread {spread:F1}° of cam",
            XAxis = new PlotAxis("Engine speed", rpm.Min(), rpm.Max(), "rpm"),
            YAxis = new PlotAxis("Lobe separation", Math.Floor(lsa.Min() - 2), Math.Ceiling(lsa.Max() + 2), "° cam"),
            RightAxis = new PlotAxis("Overlap", 0, Math.Ceiling((overlap.Max() + 10) / 10) * 10, "° crank"),
            Series =
            [
                new PlotSeries("Optimum LSA", rpm, lsa, "Brush.Accent"),
                new PlotSeries("One fixed cam", rpm, rpm.Select(_ => compromise).ToList(),
                    "Brush.Warning", PlotSeriesKind.Dashed),
                new PlotSeries("Overlap at the optimum", rpm, overlap, "Brush.Info",
                    PlotSeriesKind.Dotted, RightAxis: true),
            ],
            Notes =
            [
                spread < 3.0
                    ? $"The optimum moves {spread:F1}° of cam across this band, which is less than a grinder's "
                      + "tolerance on a single lobe. A fixed camshaft gives away almost nothing here, and "
                      + "variable timing would be mechanism for its own sake."
                    : $"The optimum moves {spread:F1}° of cam across this band. One fixed cam has to sit "
                      + $"somewhere near {compromise:F1}° and be wrong at both ends — which is the entire "
                      + "argument for variable timing, stated as a number rather than as a preference.",
                "Lobe separation is what a cam grinder is given. Wide separates the lobes and cuts overlap; "
                + "narrow brings them together and increases it, which fills at high speed and pushes exhaust "
                + "back into the intake at low.",
                "Every point satisfies the valve-to-piston clearance limit — the constraint is hard, so a "
                + "design that broke it could not be returned however well it breathed.",
            ],
        };
    }
}
