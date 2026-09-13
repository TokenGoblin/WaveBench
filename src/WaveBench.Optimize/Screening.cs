namespace WaveBench.Optimize;

/// <summary>
/// How much one variable matters, and in what way.
/// </summary>
/// <param name="Variable">Which variable.</param>
/// <param name="MeanAbsoluteEffect">
/// Morris μ*: the mean ABSOLUTE elementary effect. The headline number — how
/// much moving this variable moves the answer, anywhere in the space.
///
/// Absolute rather than signed, because a variable whose effect is positive in
/// half the space and negative in the other half averages to zero while being
/// the most important thing in the model. That is not a corner case here: a
/// runner length either side of its tuned peak does exactly this.
/// </param>
/// <param name="StandardDeviation">
/// Morris σ: the spread of the elementary effects. Large means the variable's
/// effect DEPENDS on where you are — it interacts with something else, or its
/// own response is strongly nonlinear. A variable with a big μ* and a small σ
/// can be tuned on its own; one with a big σ cannot.
/// </param>
/// <param name="MeanEffect">
/// The signed mean, which says which way the variable pushes on average. Kept
/// beside μ* rather than instead of it: where the two differ greatly, the
/// effect changes sign somewhere in the range, and that is worth saying.
/// </param>
public sealed record MorrisEffect(
    OptimisationVariable Variable, double MeanAbsoluteEffect, double StandardDeviation, double MeanEffect)
{
    /// <summary>
    /// True where the effect reverses sign across the space — |mean| well
    /// below μ*. A variable like this has an interior optimum, which is
    /// usually the interesting kind.
    /// </summary>
    public bool ChangesSign => MeanAbsoluteEffect > 0 && Math.Abs(MeanEffect) < 0.5 * MeanAbsoluteEffect;

    /// <summary>
    /// True where the effect is not constant across the space.
    ///
    /// <b>Morris cannot say WHY, and this name does not pretend it can.</b> A
    /// large σ means the elementary effect differs from place to place, which
    /// happens when the variable interacts with another AND when its own
    /// response is simply nonlinear — a parabola in one variable produces a
    /// large σ with no interaction whatever. Separating the two needs a
    /// variance decomposition (<see cref="SobolIndex.InteractionShare"/>),
    /// which costs about fifty times as much. What this flag supports is the
    /// conclusion both cases share: the variable cannot be tuned by moving it
    /// once and reading the result.
    /// </summary>
    public bool EffectVaries => MeanAbsoluteEffect > 0 && StandardDeviation > MeanAbsoluteEffect;
}

/// <summary>
/// Variance-based sensitivity indices for one variable.
/// </summary>
/// <param name="Variable">Which variable.</param>
/// <param name="FirstOrder">
/// S_i: the fraction of the output's variance explained by this variable
/// ALONE, averaged over everything else. What you would remove by fixing it at
/// its best value.
/// </param>
/// <param name="Total">
/// S_Ti: the fraction explained by this variable and every interaction it
/// takes part in. The one that decides whether a variable can be dropped —
/// a near-zero total is a variable the optimiser can stop carrying.
/// </param>
public sealed record SobolIndex(OptimisationVariable Variable, double FirstOrder, double Total)
{
    /// <summary>
    /// S_Ti − S_i: how much of this variable's influence only exists in
    /// combination with others. Zero means it acts independently.
    /// </summary>
    public double InteractionShare => Math.Max(0.0, Total - FirstOrder);

    /// <summary>A variable worth dropping from the search entirely.</summary>
    public bool Negligible => Total < 0.01;
}

/// <summary>
/// Sensitivity screening (plan §9.4: <i>"Morris screening and Sobol
/// sensitivity indices — tell the user which three variables actually matter
/// before spending compute"</i>).
///
/// <b>This is the highest-value compute in the whole module.</b> A twelve-
/// variable optimisation costs roughly the square of a three-variable one, and
/// on most engines nine of the twelve do almost nothing. Spending a few dozen
/// evaluations finding that out first is the difference between a search that
/// finishes overnight and one that does not finish.
///
/// Two methods, because they answer different questions at different prices:
/// Morris is cheap and ranks; Sobol is expensive and quantifies.
/// </summary>
public static class Screening
{
    /// <summary>
    /// Morris elementary effects, by the trajectory design (Morris,
    /// <i>Technometrics</i> 33(2):161–174, 1991; the μ* improvement from
    /// Campolongo, Cariboni &amp; Saltelli, <i>Environmental Modelling &amp;
    /// Software</i> 22(10):1509–1518, 2007).
    ///
    /// Each trajectory starts at a random grid point and walks one variable at
    /// a time, so <c>k + 1</c> evaluations yield <c>k</c> elementary effects —
    /// one per variable. That ratio is what makes it affordable: ranking
    /// twelve variables costs about <c>r·13</c> evaluations, not the hundreds
    /// a variance decomposition needs.
    ///
    /// Returns the variables ordered by μ*, most influential first.
    /// </summary>
    /// <param name="problem">The problem; its first objective is screened.</param>
    /// <param name="trajectories">
    /// How many walks. Ten is the usual minimum for a stable ranking; the
    /// estimate is a mean over this many samples per variable and is noisy
    /// below that.
    /// </param>
    /// <param name="levels">
    /// Grid resolution per variable. Must be even, and Δ is fixed at
    /// <c>p/(2(p−1))</c>, which is the choice that makes the design
    /// equiprobable — every grid point is equally likely to be visited.
    /// </param>
    /// <param name="fidelity">What to ask the evaluator for; a surrogate is usually enough to rank.</param>
    /// <param name="objective">Which objective to screen; the first by default.</param>
    /// <param name="seed">Fixed, so a screening reproduces (plan Part 0).</param>
    /// <param name="cancellation">Checked between trajectories.</param>
    /// <param name="observed">Called with every design scored, so a screening pass lands in the archive.</param>
    public static IReadOnlyList<MorrisEffect> Morris(
        OptimisationProblem problem,
        int trajectories = 10,
        int levels = 8,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        int objective = 0,
        int seed = 20260913,
        CancellationToken cancellation = default,
        Action<ScoredDesign>? observed = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trajectories);

        if (levels < 4 || levels % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(levels), "The grid must have an even number of at least four levels.");
        }

        var k = problem.Space.Dimension;
        var random = new Random(seed);
        var delta = levels / (2.0 * (levels - 1));

        var effects = new List<double>[k];
        for (var i = 0; i < k; i++)
        {
            effects[i] = [];
        }

        for (var t = 0; t < trajectories && !cancellation.IsCancellationRequested; t++)
        {
            // Start on the grid, low enough that a +delta step stays inside
            // the cube for every variable.
            var current = new double[k];
            for (var i = 0; i < k; i++)
            {
                current[i] = random.Next(levels / 2) / (double)(levels - 1);
            }

            // A random order, so the trajectories are not all the same walk.
            var order = Enumerable.Range(0, k).OrderBy(_ => random.Next()).ToArray();

            var previous = Measure(problem, current, fidelity, objective, cancellation, observed);

            foreach (var variable in order)
            {
                var next = current.ToArray();
                next[variable] = Math.Clamp(next[variable] + delta, 0.0, 1.0);

                var value = Measure(problem, next, fidelity, objective, cancellation, observed);
                var step = next[variable] - current[variable];

                if (step > 1e-12 && double.IsFinite(value) && double.IsFinite(previous))
                {
                    effects[variable].Add((value - previous) / step);
                }

                current = next;
                previous = value;
            }
        }

        return Enumerable.Range(0, k)
            .Select(i => new MorrisEffect(
                problem.Space.Variables[i],
                effects[i].Count == 0 ? 0.0 : effects[i].Average(Math.Abs),
                effects[i].Count < 2 ? 0.0 : StandardDeviation(effects[i]),
                effects[i].Count == 0 ? 0.0 : effects[i].Average()))
            .OrderByDescending(e => e.MeanAbsoluteEffect)
            .ToList();
    }

    /// <summary>
    /// Variance-based first-order and total-effect indices, by the Saltelli
    /// estimator (Saltelli et al., <i>Computer Physics Communications</i>
    /// 181(2):259–270, 2010; Jansen's total-effect form).
    ///
    /// <b>Expensive and worth it when the answer matters.</b> The cost is
    /// <c>N(k + 2)</c> evaluations, so this is for the surrogate or for a
    /// model cheap enough to afford it — not for a twelve-variable problem
    /// whose objective is a twenty-point converged sweep. Morris ranks for a
    /// fiftieth of the price; this one QUANTIFIES, and the number it gives —
    /// "runner length explains 61% of the variance, and 9% of that only
    /// through its interaction with diameter" — is the one worth putting in a
    /// report.
    /// </summary>
    /// <param name="problem">The problem; its first objective is screened by default.</param>
    /// <param name="samples">
    /// N, the base sample size. The total evaluation count is N(k + 2).
    /// Below a few hundred the indices are too noisy to rank confidently.
    /// </param>
    /// <param name="fidelity">What to ask the evaluator for.</param>
    /// <param name="objective">Which objective to decompose.</param>
    /// <param name="seed">Fixed, so a decomposition reproduces.</param>
    /// <param name="cancellation">Checked between samples.</param>
    /// <param name="observed">Called with every design scored, so the pass lands in the archive.</param>
    public static IReadOnlyList<SobolIndex> SobolIndices(
        OptimisationProblem problem,
        int samples = 256,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        int objective = 0,
        int seed = 20260913,
        CancellationToken cancellation = default,
        Action<ScoredDesign>? observed = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);

        var k = problem.Space.Dimension;

        // Two independent sample matrices. Latin hypercube rather than Sobol
        // here because the two must be INDEPENDENT — drawing both from one
        // low-discrepancy sequence correlates them, and the estimator's whole
        // derivation assumes they are not.
        var a = Doe.LatinHypercube(k, samples, seed);
        var b = Doe.LatinHypercube(k, samples, seed + 7919);

        var fa = new double[samples];
        var fb = new double[samples];

        for (var j = 0; j < samples; j++)
        {
            cancellation.ThrowIfCancellationRequested();
            fa[j] = Measure(problem, a[j], fidelity, objective, cancellation, observed);
            fb[j] = Measure(problem, b[j], fidelity, objective, cancellation, observed);
        }

        var finite = fa.Concat(fb).Where(double.IsFinite).ToList();
        if (finite.Count < 2)
        {
            throw new InvalidOperationException(
                "The objective could not be measured anywhere in the space, so its variance is undefined.");
        }

        var mean = finite.Average();
        var variance = finite.Sum(v => (v - mean) * (v - mean)) / (finite.Count - 1);

        if (variance <= 0)
        {
            // A flat objective: every index is zero, and saying so is more
            // useful than dividing by zero and reporting NaN.
            return problem.Space.Variables.Select(v => new SobolIndex(v, 0.0, 0.0)).ToList();
        }

        var indices = new List<SobolIndex>(k);

        for (var i = 0; i < k; i++)
        {
            var first = 0.0;
            var total = 0.0;
            var used = 0;

            for (var j = 0; j < samples; j++)
            {
                cancellation.ThrowIfCancellationRequested();

                // AB_i: row from A with column i taken from B.
                var row = a[j].ToArray();
                row[i] = b[j][i];
                var fab = Measure(problem, row, fidelity, objective, cancellation, observed);

                if (!double.IsFinite(fab) || !double.IsFinite(fa[j]) || !double.IsFinite(fb[j]))
                {
                    continue;
                }

                // Saltelli 2010 for S_i; Jansen for S_Ti.
                first += fb[j] * (fab - fa[j]);
                total += (fa[j] - fab) * (fa[j] - fab);
                used++;
            }

            if (used == 0)
            {
                indices.Add(new SobolIndex(problem.Space.Variables[i], double.NaN, double.NaN));
                continue;
            }

            // Estimators are unbiased but not bounded, so a small index comes
            // back slightly negative on a finite sample. Clamping to [0, 1] is
            // honest — a negative variance share is an artefact of the
            // estimator, not a finding — and the alternative of reporting
            // "-0.003 of the variance" is just noise dressed as a number.
            indices.Add(new SobolIndex(
                problem.Space.Variables[i],
                Math.Clamp(first / used / variance, 0.0, 1.0),
                Math.Clamp(total / (2.0 * used) / variance, 0.0, 1.0)));
        }

        return indices.OrderByDescending(s => s.Total).ToList();
    }

    /// <summary>
    /// The sentence the plan actually asks for: which variables matter, and
    /// which can be dropped.
    /// </summary>
    /// <param name="effects">A Morris screening, most influential first.</param>
    /// <param name="keep">How many to name as the ones that matter.</param>
    public static string Explain(IReadOnlyList<MorrisEffect> effects, int keep = 3)
    {
        ArgumentNullException.ThrowIfNull(effects);

        if (effects.Count == 0)
        {
            return "Nothing was screened.";
        }

        var strongest = effects[0].MeanAbsoluteEffect;
        if (strongest <= 0)
        {
            return "No variable moved the objective at all. Either the bounds are too tight to matter or the "
                   + "objective does not depend on this variable set.";
        }

        var top = effects.Take(Math.Min(keep, effects.Count)).ToList();
        var negligible = effects.Where(e => e.MeanAbsoluteEffect < 0.05 * strongest).ToList();

        var lines = new List<string>
        {
            $"{string.Join(", ", top.Select(e => e.Variable.Name))} "
            + $"{(top.Count == 1 ? "is the variable" : "are the variables")} that move this objective; "
            + $"{top[0].Variable.Name} most of all, at "
            + $"{top[0].MeanAbsoluteEffect / strongest:P0} of the largest effect measured.",
        };

        foreach (var effect in top.Where(e => e.EffectVaries))
        {
            lines.Add($"{effect.Variable.Name}'s effect is not constant across the space (σ above μ*) — it is "
                      + "nonlinear, it interacts with another variable, or both, and Morris cannot tell those "
                      + "apart. Either way, moving it once and reading the result will not tune it.");
        }

        foreach (var effect in top.Where(e => e.ChangesSign))
        {
            lines.Add($"{effect.Variable.Name} helps in part of its range and hurts in the rest, so it has an "
                      + "optimum somewhere inside the bounds rather than at an end.");
        }

        if (negligible.Count > 0)
        {
            lines.Add($"{string.Join(", ", negligible.Select(e => e.Variable.Name))} "
                      + $"barely move it and can be held fixed — which takes the search from "
                      + $"{effects.Count} variables to {effects.Count - negligible.Count}.");
        }

        return string.Join(" ", lines);
    }

    /// <summary>
    /// Score one point and return the requested objective, or NaN where it
    /// could not be measured.
    ///
    /// Screening reads the OBJECTIVE, not the scalar score: the score carries
    /// the lexicographic feasibility band, and an elementary effect computed
    /// across that band would be an enormous number describing a constraint
    /// boundary rather than a sensitivity.
    ///
    /// Every design scored is handed to <paramref name="observed"/>. An
    /// evaluation that was paid for and not recorded is an evaluation the
    /// archive cannot re-explore and the cache cannot reuse — and a screening
    /// pass spends dozens of them.
    /// </summary>
    private static double Measure(
        OptimisationProblem problem,
        IReadOnlyList<double> coordinates,
        EvaluationFidelity fidelity,
        int objective,
        CancellationToken cancellation,
        Action<ScoredDesign>? observed = null)
    {
        var scored = problem.Score(new DesignPoint(problem.Space, coordinates), fidelity, cancellation);
        observed?.Invoke(scored);
        return objective < scored.Objectives.Count ? scored.Objectives[objective] : double.NaN;
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }
}
