namespace WaveBench.Optimize;

/// <summary>
/// Bayesian optimisation with a Gaussian-process surrogate and
/// expected-improvement acquisition (plan §9.4: <i>"the right default when
/// each evaluation costs a 20-point rpm sweep"</i>).
///
/// <b>Why it is the right default HERE.</b> CMA-ES is excellent and it spends
/// evaluations freely — a few thousand is nothing to it. In this project one
/// evaluation is a converged twenty-point sweep, so a few thousand is a week.
/// Bayesian optimisation is built for the opposite budget: it fits a model of
/// everything seen so far and spends each new evaluation where that model says
/// the most is to be learned, which is worth the arithmetic precisely when the
/// arithmetic is cheap relative to the thing being measured.
///
/// <b>Expected improvement, not "wherever the mean is best".</b> EI weighs how
/// much better a point might be against how unsure the model is there
/// (Jones, Schonlau &amp; Welch, <i>Journal of Global Optimization</i>
/// 13(4):455–492, 1998). Chasing the posterior mean alone converges to
/// whichever basin the initial design happened to land in and never looks
/// anywhere else; EI is what makes the search global.
/// </summary>
public sealed class BayesianOptimiser
{
    private readonly OptimisationProblem _problem;
    private readonly Random _random;
    private readonly int _objective;

    /// <param name="problem">What is being optimised.</param>
    /// <param name="objective">Which objective to drive; the first by default.</param>
    /// <param name="explorationWeight">
    /// ξ in the EI formula: how much improvement must be expected before a
    /// point counts as an improvement at all. Zero is the textbook form and
    /// exploits hard; a small positive value pushes the search outward. 0.01
    /// of the standardised range is the usual compromise and is what this
    /// uses.
    /// </param>
    /// <param name="seed">Fixed, so a run reproduces (plan Part 0).</param>
    public BayesianOptimiser(
        OptimisationProblem problem,
        int objective = 0,
        double explorationWeight = 0.01,
        int seed = 20260913)
    {
        _problem = problem ?? throw new ArgumentNullException(nameof(problem));
        _objective = objective;
        ExplorationWeight = explorationWeight;
        _random = new Random(seed);
    }

    public double ExplorationWeight { get; }

    /// <summary>The surrogate as it stood at the end of the run, for inspection.</summary>
    public GaussianProcess? Surrogate { get; private set; }

    /// <summary>
    /// Run until the evaluation budget is spent.
    /// </summary>
    /// <param name="maximumEvaluations">
    /// The whole budget, initial design included. Small on purpose: this
    /// method exists for budgets CMA-ES would consider a warm-up.
    /// </param>
    /// <param name="initialDesigns">
    /// How many space-filling points before the model takes over. Too few and
    /// the first surrogate is fitted to nothing; the usual guidance is a
    /// handful per dimension, and 2(d+1) is the default here.
    /// </param>
    /// <param name="fidelity">What to ask the evaluator for.</param>
    /// <param name="progress">Reported once per model-guided iteration.</param>
    /// <param name="cancellation">Cancels between evaluations; the work done so far is still returned.</param>
    /// <param name="seedDesigns">Designs to evaluate first — a baseline, or a previous run's front.</param>
    public OptimiserResult Run(
        int maximumEvaluations = 60,
        int? initialDesigns = null,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        IProgress<OptimiserProgress>? progress = null,
        CancellationToken cancellation = default,
        IReadOnlyList<DesignPoint>? seedDesigns = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvaluations);

        var space = _problem.Space;
        var dimension = space.Dimension;
        var initial = Math.Clamp(initialDesigns ?? (2 * (dimension + 1)), 2, maximumEvaluations);

        var history = new List<ScoredDesign>();
        ScoredDesign? best = null;

        void Record(ScoredDesign scored)
        {
            history.Add(scored);
            if (best is null || scored.ScalarScore < best.ScalarScore)
            {
                best = scored;
            }
        }

        // Seeds first — a previous run's front, or the baseline — then a
        // space-filling design to fill in around them.
        foreach (var design in (seedDesigns ?? []).Take(initial))
        {
            if (history.Count >= maximumEvaluations || cancellation.IsCancellationRequested)
            {
                break;
            }

            Record(_problem.Score(design, fidelity, cancellation));
        }

        foreach (var design in Doe.Orient(space, Math.Max(0, initial - history.Count)))
        {
            if (history.Count >= maximumEvaluations || cancellation.IsCancellationRequested)
            {
                break;
            }

            Record(_problem.Score(design, fidelity, cancellation));
        }

        var iteration = 0;
        var reason = "evaluation budget spent";

        while (history.Count < maximumEvaluations)
        {
            if (cancellation.IsCancellationRequested)
            {
                reason = "cancelled";
                break;
            }

            iteration++;

            // The surrogate is fitted to FEASIBLE observations only.
            //
            // The alternative — fitting it to the scalar score — would have it
            // model the lexicographic feasibility band, a step of order 1e308
            // between the feasible and infeasible regions. A Gaussian process
            // asked to interpolate that produces nonsense everywhere, not just
            // near the boundary.
            var observed = history
                .Where(d => d.Feasible && _objective < d.Costs.Count && double.IsFinite(d.Costs[_objective]))
                .ToList();

            if (observed.Count < 2)
            {
                // Nothing to model yet. Keep exploring rather than fitting a
                // process to one point and pretending it knows something.
                var design = new DesignPoint(space, RandomPoint(dimension));
                Record(_problem.Score(design, fidelity, cancellation));
                continue;
            }

            var surrogate = GaussianProcess.Fit(
                observed.Select(d => d.Design.Coordinates.ToArray()).ToList(),
                observed.Select(d => d.Costs[_objective]).ToList());

            Surrogate = surrogate;

            var incumbent = observed.Min(d => d.Costs[_objective]);
            var next = MaximiseExpectedImprovement(surrogate, incumbent, dimension);

            Record(_problem.Score(new DesignPoint(space, next), fidelity, cancellation));

            progress?.Report(new OptimiserProgress(iteration, history.Count, best!, surrogate.LengthScale));
        }

        if (best is null)
        {
            throw new InvalidOperationException("The budget did not allow a single evaluation.");
        }

        var feasible = history.Where(d => d.Feasible).OrderBy(d => d.ScalarScore).FirstOrDefault();
        return new OptimiserResult(feasible ?? best, history, iteration, reason);
    }

    /// <summary>
    /// Where to look next: the point of greatest expected improvement.
    ///
    /// Found by scoring a large space-filling candidate set and polishing the
    /// best few by coordinate ascent. The acquisition function costs one GP
    /// posterior — microseconds — so thousands of candidates are affordable,
    /// and a thorough search of a cheap function is exactly the trade Bayesian
    /// optimisation is built on. Using a sophisticated optimiser on the
    /// acquisition would buy very little and would make the result depend on
    /// that optimiser's own quirks.
    /// </summary>
    private double[] MaximiseExpectedImprovement(GaussianProcess surrogate, double incumbent, int dimension)
    {
        var candidates = new List<double[]>();
        candidates.AddRange(
            dimension <= Doe.MaxDimension
                ? Doe.Sobol(dimension, 512, skip: _random.Next(1, 4096))
                : Doe.LatinHypercube(dimension, 512, _random.Next()));

        // Plus a scatter of purely random points, so the candidate set is not
        // itself a lattice the acquisition can be blind between.
        for (var i = 0; i < 256; i++)
        {
            candidates.Add(RandomPoint(dimension));
        }

        var best = candidates[0];
        var bestEi = double.NegativeInfinity;

        foreach (var candidate in candidates)
        {
            var ei = ExpectedImprovement(surrogate, candidate, incumbent);
            if (ei > bestEi)
            {
                bestEi = ei;
                best = candidate;
            }
        }

        // Coordinate ascent on the winner: cheap, deterministic, and enough to
        // move off the candidate lattice onto the actual local maximum.
        var current = best.ToArray();
        for (var pass = 0; pass < 3; pass++)
        {
            var step = 0.08 / (pass + 1);

            for (var d = 0; d < dimension; d++)
            {
                foreach (var delta in new[] { step, -step })
                {
                    var trial = current.ToArray();
                    trial[d] = Math.Clamp(trial[d] + delta, 0.0, 1.0);

                    var ei = ExpectedImprovement(surrogate, trial, incumbent);
                    if (ei > bestEi)
                    {
                        bestEi = ei;
                        current = trial;
                    }
                }
            }
        }

        return current;
    }

    /// <summary>
    /// Expected improvement over the incumbent, for a MINIMISED objective.
    ///
    /// <c>EI = (f* − μ − ξ)·Φ(z) + σ·φ(z)</c>, with
    /// <c>z = (f* − μ − ξ)/σ</c>.
    ///
    /// The two terms are the whole idea: the first is how much better the
    /// point is expected to be, and the second is how much room the
    /// uncertainty leaves for it to be better than expected. A point the model
    /// is confident is mediocre scores nothing; a point it knows nothing about
    /// scores something even if its mean is poor. That second term is what
    /// stops the search collapsing into the first basin it finds.
    /// </summary>
    private double ExpectedImprovement(GaussianProcess surrogate, IReadOnlyList<double> point, double incumbent)
    {
        var (mean, sigma) = surrogate.At(point);

        if (sigma <= 1e-12)
        {
            // No uncertainty: an already-observed point, which cannot improve
            // on itself.
            return 0.0;
        }

        var improvement = incumbent - mean - (ExplorationWeight * Math.Abs(incumbent));
        var z = improvement / sigma;

        return (improvement * NormalCdf(z)) + (sigma * NormalPdf(z));
    }

    private double[] RandomPoint(int dimension)
    {
        var point = new double[dimension];
        for (var i = 0; i < dimension; i++)
        {
            point[i] = _random.NextDouble();
        }

        return point;
    }

    private static double NormalPdf(double z) => Math.Exp(-0.5 * z * z) / Math.Sqrt(2.0 * Math.PI);

    /// <summary>
    /// Standard normal CDF via the error function, by Abramowitz &amp; Stegun
    /// 7.1.26 — accurate to about 1.5e-7, which is far inside anything the
    /// acquisition's ranking is sensitive to.
    /// </summary>
    private static double NormalCdf(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    private static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);

        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var t = 1.0 / (1.0 + (p * x));
        var y = 1.0 - ((((((((a5 * t) + a4) * t) + a3) * t) + a2) * t) + a1) * t * Math.Exp(-x * x);

        return sign * y;
    }
}
