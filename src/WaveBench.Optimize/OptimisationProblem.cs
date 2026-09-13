using WaveBench.Model;

namespace WaveBench.Optimize;

/// <summary>
/// One design, everything measured about it, and the verdict.
/// </summary>
/// <param name="Design">The candidate.</param>
/// <param name="Evaluation">What it measured, or null when geometry rejected it before any solve.</param>
/// <param name="Objectives">Objective values in their own units, in the set's order.</param>
/// <param name="Checks">Every constraint, satisfied or not.</param>
public sealed record ScoredDesign(
    DesignPoint Design,
    DesignEvaluation? Evaluation,
    IReadOnlyList<double> Objectives,
    IReadOnlyList<ConstraintCheck> Checks)
{
    public bool Feasible => ConstraintSet.IsFeasible(Checks);

    public double Violation => ConstraintSet.TotalViolation(Checks);

    /// <summary>Constraints this design breaks, for the archive and the UI.</summary>
    public IReadOnlyList<ConstraintCheck> Failures => Checks.Where(c => !c.Satisfied).ToList();

    /// <summary>
    /// Objectives as costs to MINIMISE, which is the form every search here
    /// works in.
    /// </summary>
    public required IReadOnlyList<double> Costs { get; init; }

    /// <summary>
    /// The single number a scalar optimiser sorts on.
    ///
    /// <b>Infeasible designs are ranked BELOW every feasible one, always.</b>
    /// Not by a large weight — by construction. A penalty big enough to
    /// dominate is a penalty that flattens the objective landscape inside the
    /// feasible region too; a penalty small enough not to is one the optimiser
    /// will happily pay. The lexicographic order — feasibility first, then
    /// objective — has neither problem, and it is what makes "the clearance
    /// constraint is never violated in a returned design" true by construction
    /// rather than by tuning.
    ///
    /// Within the infeasible set, designs are ordered by how badly they break
    /// the constraints, so a search that starts infeasible still has a
    /// gradient to follow back.
    /// </summary>
    public double ScalarScore { get; init; } = double.PositiveInfinity;
}

/// <summary>
/// A complete optimisation problem (plan Part 9): what may change, what
/// "better" means, what may never be broken, and how a candidate is measured.
///
/// Holds no search state, so the same problem can be handed to DOE, CMA-ES,
/// NSGA-II and Bayesian optimisation in turn — which is exactly what the plan
/// asks for, and what stops the definition drifting between them.
/// </summary>
public sealed class OptimisationProblem
{
    private readonly EngineModelDocument _baseline;

    public OptimisationProblem(
        EngineModelDocument baseline,
        DesignSpace space,
        ObjectiveSet objectives,
        IDesignEvaluator evaluator,
        ConstraintSet? constraints = null)
    {
        _baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        Space = space ?? throw new ArgumentNullException(nameof(space));
        Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
        Evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        Constraints = constraints ?? new ConstraintSet([]);

        // Fail here, not after an hour of search: a variable that does not
        // address a writable field is a typo, and the only cheap moment to
        // find it is before the first evaluation.
        Space.ValidateAgainst(_baseline);
    }

    public DesignSpace Space { get; }

    public ObjectiveSet Objectives { get; }

    public ConstraintSet Constraints { get; }

    public IDesignEvaluator Evaluator { get; }

    /// <summary>The document every candidate is a modification of.</summary>
    public EngineModelDocument Baseline => _baseline;

    /// <summary>
    /// Called with every design this problem scores, as it is scored.
    ///
    /// <b>The archive has to fill incrementally, not in bulk at the end.</b>
    /// Every search here used to hand its history over once it finished, which
    /// works right up until a run is cancelled — and then the evaluations the
    /// user already paid for are lost, because the hand-over never happened.
    /// Hooking it HERE rather than in each algorithm means one mechanism
    /// covers all of them, including any added later, and a cancelled run
    /// keeps its work by construction.
    /// </summary>
    public Action<ScoredDesign>? Observed { get; set; }

    /// <summary>How many designs have actually been measured.</summary>
    public int Evaluations { get; private set; }

    /// <summary>How many were rejected on geometry before an evaluation was spent.</summary>
    public int RejectedOnGeometry { get; private set; }

    /// <summary>
    /// Measure and judge one design.
    ///
    /// Geometry is checked FIRST, and an infeasible geometry short-circuits
    /// the evaluation entirely. On a tightly packaged problem most of what a
    /// search proposes is out of the box, and solving those would spend the
    /// whole budget learning what a bounding box already knew.
    /// </summary>
    public ScoredDesign Score(
        DesignPoint design,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(design);

        var document = design.Materialise(_baseline);
        var geometry = Constraints.CheckGeometry(document);

        if (!ConstraintSet.IsFeasible(geometry))
        {
            RejectedOnGeometry++;

            // The full check list, with the un-evaluated ones marked as such
            // rather than quietly omitted — a constraint missing from the
            // record reads as one that passed.
            var checks = Constraints.CheckAll(document, null);
            var unknown = Objectives.Objectives.Select(_ => double.NaN).ToList();

            var rejected = new ScoredDesign(design, null, unknown, checks)
            {
                Costs = unknown,
                ScalarScore = Penalise(ConstraintSet.TotalViolation(checks)),
            };

            Observed?.Invoke(rejected);
            return rejected;
        }

        var evaluation = Evaluator.Evaluate(design, fidelity, cancellation);
        Evaluations++;

        var all = Constraints.CheckAll(document, evaluation);
        var values = Objectives.Values(evaluation);
        var costs = Objectives.Costs(evaluation);

        var scored = new ScoredDesign(design, evaluation, values, all)
        {
            Costs = costs,
            ScalarScore = Scalarise(costs, all, evaluation),
        };

        Observed?.Invoke(scored);
        return scored;
    }

    /// <summary>
    /// Collapse to the one number a scalar search sorts on, lexicographically:
    /// feasible designs occupy the finite range, infeasible ones sit above all
    /// of them ordered by violation.
    /// </summary>
    private double Scalarise(
        IReadOnlyList<double> costs, IReadOnlyList<ConstraintCheck> checks, DesignEvaluation evaluation)
    {
        if (evaluation.Failed)
        {
            // A solve that fell over is worse than any constraint violation:
            // it is a region the model cannot describe at all.
            return double.MaxValue;
        }

        var violation = ConstraintSet.TotalViolation(checks);
        if (violation > 0)
        {
            return Penalise(violation);
        }

        var total = 0.0;
        for (var i = 0; i < costs.Count; i++)
        {
            var cost = costs[i];

            // An objective this evaluation could not answer is UNKNOWN, not
            // bad. Scoring it as a loss would teach the search to avoid the
            // region rather than to measure it properly — and on a surrogate
            // pass, where half the objectives are legitimately unanswerable,
            // it would invert the ranking entirely.
            if (double.IsFinite(cost))
            {
                total += Objectives.Weights[i] * cost;
            }
        }

        return double.IsFinite(total) ? total : double.MaxValue / 4.0;
    }

    /// <summary>
    /// The infeasible band: above every finite feasible score, ordered by how
    /// badly the design breaks its constraints.
    /// </summary>
    private static double Penalise(double violation) =>
        (double.MaxValue / 2.0) + Math.Min(violation, double.MaxValue / 4.0);

    /// <summary>The baseline itself, scored — what any improvement is measured against.</summary>
    public ScoredDesign ScoreBaseline(
        EvaluationFidelity fidelity = EvaluationFidelity.Solved, CancellationToken cancellation = default) =>
        Score(Space.From(_baseline), fidelity, cancellation);

    /// <summary>
    /// Write a design back into a session as the optimiser's own (§8.5): the
    /// values are stamped <see cref="Provenance.Optimised"/> and linked to the
    /// run, so a later wizard cannot silently undo what a search found.
    /// </summary>
    public void Apply(ProjectSession session, DesignPoint design, string runId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(design);

        foreach (var (variable, value) in Space.Variables.Zip(design.Values))
        {
            session.EditByOptimiser(variable.Path, value, runId);
        }
    }
}
