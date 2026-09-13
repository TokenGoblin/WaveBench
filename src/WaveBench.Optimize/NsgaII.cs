namespace WaveBench.Optimize;

/// <summary>
/// A design with its place in the non-dominated sorting.
/// </summary>
/// <param name="Design">The scored candidate.</param>
/// <param name="Rank">
/// 0 is the Pareto front: nothing found beats it on every objective at once.
/// 1 is the front behind it, and so on.
/// </param>
/// <param name="Crowding">
/// How much room the design has along the front. Large means it occupies a
/// sparse stretch and is worth keeping for spread; infinite marks the two
/// extremes of each objective, which are never discarded.
/// </param>
public sealed record RankedDesign(ScoredDesign Design, int Rank, double Crowding);

/// <summary>What a multi-objective search returned.</summary>
/// <param name="Front">The non-dominated set, spread along the front.</param>
/// <param name="History">Every design scored, in order.</param>
/// <param name="Generations">Generations completed.</param>
/// <param name="Reason">Why it stopped.</param>
public sealed record ParetoResult(
    IReadOnlyList<ScoredDesign> Front,
    IReadOnlyList<ScoredDesign> History,
    int Generations,
    string Reason)
{
    public int Evaluations => History.Count;

    /// <summary>
    /// The front's extent in each objective — how much of a trade there
    /// actually is. A front that spans nothing is a front with no decision on
    /// it, and saying so is more useful than drawing a cluster of dots.
    /// </summary>
    public IReadOnlyList<(double Min, double Max)> Extent(ObjectiveSet objectives)
    {
        ArgumentNullException.ThrowIfNull(objectives);

        var ranges = new List<(double, double)>();
        for (var i = 0; i < objectives.Count; i++)
        {
            var values = Front
                .Select(d => d.Objectives[i])
                .Where(double.IsFinite)
                .ToList();

            ranges.Add(values.Count == 0 ? (double.NaN, double.NaN) : (values.Min(), values.Max()));
        }

        return ranges;
    }
}

/// <summary>
/// NSGA-II (plan §9.4: <i>"NSGA-II for Pareto fronts"</i>).
///
/// Deb, Pratap, Agarwal &amp; Meyarivan, "A Fast and Elitist Multiobjective
/// Genetic Algorithm: NSGA-II," <i>IEEE Transactions on Evolutionary
/// Computation</i> 6(2):182–197, 2002. Fast non-dominated sorting, crowding
/// distance for diversity, binary tournament selection, simulated binary
/// crossover and polynomial mutation, with (μ+λ) elitist replacement.
///
/// <b>Why a front rather than a weighted sum.</b> Plan §9.6 asks for power
/// against sound and response against peak power as fronts the user explores.
/// A weighted sum answers a question nobody asked — it needs the weights
/// chosen BEFORE the trade is known, which is exactly backwards. A front
/// shows what the trade costs, and the user picks afterwards knowing what they
/// are buying.
///
/// <b>Constraint handling is Deb's constrained-domination.</b> A feasible
/// design dominates every infeasible one; between two infeasible designs the
/// less-violating wins. Together with taking the returned front from the
/// feasible set, that is what keeps Phase 22's "never violated in a returned
/// design" true here as well as in the scalar search.
/// </summary>
public sealed class NsgaII
{
    private readonly OptimisationProblem _problem;
    private readonly int _populationSize;
    private readonly Random _random;
    private readonly double _crossoverRate;
    private readonly double _mutationRate;
    private readonly double _distributionIndex;

    /// <param name="problem">What is being optimised; needs at least two objectives to be worth running.</param>
    /// <param name="populationSize">
    /// Size of the front carried between generations. Even, because selection
    /// pairs parents.
    /// </param>
    /// <param name="seed">Fixed, so a run reproduces (plan Part 0).</param>
    /// <param name="crossoverRate">Probability a pair recombines rather than passing through.</param>
    /// <param name="mutationRate">Per-variable mutation probability; 1/n by default, the usual choice.</param>
    /// <param name="distributionIndex">
    /// SBX and polynomial-mutation spread. Large keeps children near their
    /// parents; 20 is Deb's own default and is what the published behaviour
    /// was measured with.
    /// </param>
    public NsgaII(
        OptimisationProblem problem,
        int populationSize = 40,
        int seed = 20260913,
        double crossoverRate = 0.9,
        double? mutationRate = null,
        double distributionIndex = 20.0)
    {
        _problem = problem ?? throw new ArgumentNullException(nameof(problem));
        ArgumentOutOfRangeException.ThrowIfLessThan(populationSize, 4);

        _populationSize = populationSize % 2 == 0 ? populationSize : populationSize + 1;
        _random = new Random(seed);
        _crossoverRate = crossoverRate;
        _mutationRate = mutationRate ?? (1.0 / problem.Space.Dimension);
        _distributionIndex = distributionIndex;
    }

    public int PopulationSize => _populationSize;

    /// <summary>
    /// Run until the evaluation budget is spent.
    /// </summary>
    public ParetoResult Run(
        int maximumEvaluations = 800,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        IProgress<OptimiserProgress>? progress = null,
        CancellationToken cancellation = default,
        IReadOnlyList<DesignPoint>? seedDesigns = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvaluations);

        var history = new List<ScoredDesign>();
        var reason = "evaluation budget spent";

        // The initial population: a space-filling DOE rather than random, so
        // the first generation already covers the cube. Seeds supplied by the
        // caller — the baseline, or a previous run's front — go in first.
        var initial = new List<DesignPoint>();
        if (seedDesigns is { Count: > 0 })
        {
            initial.AddRange(seedDesigns.Take(_populationSize));
        }

        initial.AddRange(Doe.Orient(_problem.Space, _populationSize - initial.Count));

        var population = new List<ScoredDesign>();
        foreach (var design in initial)
        {
            if (history.Count >= maximumEvaluations || cancellation.IsCancellationRequested)
            {
                break;
            }

            var scored = _problem.Score(design, fidelity, cancellation);
            population.Add(scored);
            history.Add(scored);
        }

        var generation = 0;

        while (history.Count < maximumEvaluations && population.Count >= 4)
        {
            if (cancellation.IsCancellationRequested)
            {
                reason = "cancelled";
                break;
            }

            generation++;

            var ranked = Rank(population);
            var children = new List<ScoredDesign>(_populationSize);

            while (children.Count < _populationSize && history.Count < maximumEvaluations)
            {
                var a = Tournament(ranked);
                var b = Tournament(ranked);

                var (childA, childB) = Crossover(a.Design.Design.Coordinates, b.Design.Design.Coordinates);
                Mutate(childA);
                Mutate(childB);

                foreach (var coordinates in new[] { childA, childB })
                {
                    if (children.Count >= _populationSize || history.Count >= maximumEvaluations)
                    {
                        break;
                    }

                    var scored = _problem.Score(
                        new DesignPoint(_problem.Space, coordinates), fidelity, cancellation);
                    children.Add(scored);
                    history.Add(scored);
                }
            }

            // (μ+λ) elitism: parents and children compete together, so a good
            // design is never lost to an unlucky generation.
            population = Truncate([.. population, .. children], _populationSize);

            if (progress is not null)
            {
                var best = population.OrderBy(d => d.ScalarScore).First();
                progress.Report(new OptimiserProgress(generation, history.Count, best, double.NaN));
            }
        }

        // The returned front is taken from the FEASIBLE designs. Where none is
        // feasible the front is empty rather than quietly populated with
        // designs that break a hard limit.
        var feasible = history.Where(d => d.Feasible).ToList();
        var front = feasible.Count > 0
            ? Rank(feasible).Where(r => r.Rank == 0)
                .OrderByDescending(r => r.Crowding)
                .Select(r => r.Design)
                .ToList()
            : [];

        return new ParetoResult(front, history, generation, reason);
    }

    // ---- Non-dominated sorting -------------------------------------------

    /// <summary>
    /// Deb's constrained domination: feasibility first, then Pareto
    /// domination on the costs.
    /// </summary>
    private static bool Dominates(ScoredDesign a, ScoredDesign b)
    {
        if (a.Feasible && !b.Feasible)
        {
            return true;
        }

        if (!a.Feasible && b.Feasible)
        {
            return false;
        }

        if (!a.Feasible && !b.Feasible)
        {
            // Both broken: the less broken one wins. This is what gives a
            // population that starts wholly infeasible a way back.
            return a.Violation < b.Violation;
        }

        var betterSomewhere = false;
        for (var i = 0; i < a.Costs.Count; i++)
        {
            var x = a.Costs[i];
            var y = b.Costs[i];

            // An objective neither could answer is not evidence either way.
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                continue;
            }

            if (x > y)
            {
                return false;
            }

            if (x < y)
            {
                betterSomewhere = true;
            }
        }

        return betterSomewhere;
    }

    /// <summary>Fast non-dominated sort plus crowding distance within each front.</summary>
    public static IReadOnlyList<RankedDesign> Rank(IReadOnlyList<ScoredDesign> population)
    {
        ArgumentNullException.ThrowIfNull(population);

        var n = population.Count;
        var dominatedBy = new int[n];
        var dominates = new List<int>[n];

        for (var i = 0; i < n; i++)
        {
            dominates[i] = [];
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (Dominates(population[i], population[j]))
                {
                    dominates[i].Add(j);
                    dominatedBy[j]++;
                }
                else if (Dominates(population[j], population[i]))
                {
                    dominates[j].Add(i);
                    dominatedBy[i]++;
                }
            }
        }

        var ranks = new int[n];
        var current = Enumerable.Range(0, n).Where(i => dominatedBy[i] == 0).ToList();
        var rank = 0;

        while (current.Count > 0)
        {
            var next = new List<int>();
            foreach (var i in current)
            {
                ranks[i] = rank;
                foreach (var j in dominates[i])
                {
                    if (--dominatedBy[j] == 0)
                    {
                        next.Add(j);
                    }
                }
            }

            current = next;
            rank++;
        }

        var crowding = Crowding(population, ranks, rank);

        return Enumerable.Range(0, n)
            .Select(i => new RankedDesign(population[i], ranks[i], crowding[i]))
            .ToList();
    }

    /// <summary>
    /// Crowding distance: the perimeter of the box a design's neighbours make
    /// around it, per front, normalised per objective.
    ///
    /// This is what keeps a front SPREAD. Without it a genetic algorithm
    /// collapses onto whichever corner of the front it found first, and the
    /// user gets a cluster of near-identical designs instead of a trade.
    /// </summary>
    private static double[] Crowding(IReadOnlyList<ScoredDesign> population, int[] ranks, int frontCount)
    {
        var distance = new double[population.Count];
        var objectives = population.Count > 0 ? population[0].Costs.Count : 0;

        for (var front = 0; front < frontCount; front++)
        {
            var members = Enumerable.Range(0, population.Count).Where(i => ranks[i] == front).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            for (var m = 0; m < objectives; m++)
            {
                var ordered = members
                    .Where(i => double.IsFinite(population[i].Costs[m]))
                    .OrderBy(i => population[i].Costs[m])
                    .ToList();

                if (ordered.Count < 2)
                {
                    continue;
                }

                // The extremes are never discarded: losing them would shrink
                // the front every generation until it was a point.
                distance[ordered[0]] = double.PositiveInfinity;
                distance[ordered[^1]] = double.PositiveInfinity;

                var span = population[ordered[^1]].Costs[m] - population[ordered[0]].Costs[m];
                if (span <= 0)
                {
                    continue;
                }

                for (var k = 1; k < ordered.Count - 1; k++)
                {
                    if (double.IsPositiveInfinity(distance[ordered[k]]))
                    {
                        continue;
                    }

                    distance[ordered[k]] +=
                        (population[ordered[k + 1]].Costs[m] - population[ordered[k - 1]].Costs[m]) / span;
                }
            }
        }

        return distance;
    }

    /// <summary>Keep the best <paramref name="count"/> by rank, then by crowding.</summary>
    private static List<ScoredDesign> Truncate(IReadOnlyList<ScoredDesign> population, int count) =>
        Rank(population)
            .OrderBy(r => r.Rank)
            .ThenByDescending(r => r.Crowding)
            .Take(count)
            .Select(r => r.Design)
            .ToList();

    // ---- Variation --------------------------------------------------------

    private RankedDesign Tournament(IReadOnlyList<RankedDesign> ranked)
    {
        var a = ranked[_random.Next(ranked.Count)];
        var b = ranked[_random.Next(ranked.Count)];

        if (a.Rank != b.Rank)
        {
            return a.Rank < b.Rank ? a : b;
        }

        return a.Crowding >= b.Crowding ? a : b;
    }

    /// <summary>
    /// Simulated binary crossover (Deb &amp; Agrawal 1995): produces children
    /// distributed around their parents the way a real-coded analogue of
    /// single-point binary crossover would.
    /// </summary>
    private (double[] A, double[] B) Crossover(IReadOnlyList<double> parentA, IReadOnlyList<double> parentB)
    {
        var n = parentA.Count;
        var a = parentA.ToArray();
        var b = parentB.ToArray();

        if (_random.NextDouble() > _crossoverRate)
        {
            return (a, b);
        }

        for (var i = 0; i < n; i++)
        {
            if (_random.NextDouble() > 0.5 || Math.Abs(a[i] - b[i]) < 1e-14)
            {
                continue;
            }

            var u = _random.NextDouble();
            var beta = u <= 0.5
                ? Math.Pow(2.0 * u, 1.0 / (_distributionIndex + 1.0))
                : Math.Pow(1.0 / (2.0 * (1.0 - u)), 1.0 / (_distributionIndex + 1.0));

            var x = a[i];
            var y = b[i];
            a[i] = Math.Clamp(0.5 * (((1.0 + beta) * x) + ((1.0 - beta) * y)), 0.0, 1.0);
            b[i] = Math.Clamp(0.5 * (((1.0 - beta) * x) + ((1.0 + beta) * y)), 0.0, 1.0);
        }

        return (a, b);
    }

    /// <summary>Polynomial mutation, bounded to the unit cube.</summary>
    private void Mutate(double[] coordinates)
    {
        for (var i = 0; i < coordinates.Length; i++)
        {
            if (_random.NextDouble() > _mutationRate)
            {
                continue;
            }

            var x = coordinates[i];
            var u = _random.NextDouble();
            var delta = u < 0.5
                ? Math.Pow(2.0 * u, 1.0 / (_distributionIndex + 1.0)) - 1.0
                : 1.0 - Math.Pow(2.0 * (1.0 - u), 1.0 / (_distributionIndex + 1.0));

            coordinates[i] = Math.Clamp(x + delta, 0.0, 1.0);
        }
    }
}
