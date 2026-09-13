namespace WaveBench.Optimize;

/// <summary>
/// Derivative-free local refinement (plan §9.4: <i>"Nelder–Mead and Powell for
/// local refinement"</i>).
///
/// <b>These are polishers, not searches.</b> A global method spends its budget
/// deciding which basin to be in and rarely bottoms one out — CMA-ES stops
/// when its step size collapses, Bayesian optimisation when the expected
/// improvement stops justifying an evaluation. Neither is the same as "there
/// is nothing better within a millimetre". Handing the winner to a local
/// method afterwards is cheap and routinely finds the last percent, and on a
/// discrete space it is what walks a design onto the best nearby GRID point
/// rather than the best continuous one.
///
/// Both are derivative-free, because there is no gradient to be had: one
/// evaluation is a converged nonlinear solve, and a finite-difference
/// gradient would cost n evaluations per step to approximate something the
/// solver's own convergence tolerance makes noisy anyway.
/// </summary>
public static class LocalSearch
{
    /// <summary>
    /// Nelder–Mead simplex (Nelder &amp; Mead, <i>The Computer Journal</i>
    /// 7(4):308–313, 1965), with the standard reflection, expansion,
    /// contraction and shrink coefficients.
    ///
    /// Robust and undemanding: it needs only comparisons, tolerates a noisy
    /// objective, and makes progress from a simplex that is nowhere near the
    /// optimum. Its weakness is dimension — above about ten variables the
    /// simplex collapses and stalls — which is exactly why it is used here to
    /// finish a global search rather than to replace one.
    /// </summary>
    /// <param name="problem">What is being refined.</param>
    /// <param name="start">Where to start; usually a global search's winner.</param>
    /// <param name="maximumEvaluations">Budget, including building the initial simplex.</param>
    /// <param name="step">
    /// Initial simplex size as a fraction of the unit cube. Small, because this
    /// is a refinement: the answer is believed to be near the start, and a
    /// large simplex would throw that information away.
    /// </param>
    /// <param name="fidelity">What to ask the evaluator for.</param>
    /// <param name="progress">Reported once per iteration.</param>
    /// <param name="cancellation">Cancels between evaluations; work done is still returned.</param>
    /// <param name="tolerance">
    /// Stop when the simplex has shrunk below this fraction of the cube. Not a
    /// claim of optimality — the point past which the method is sampling
    /// inside its own numerical noise.
    /// </param>
    public static OptimiserResult NelderMead(
        OptimisationProblem problem,
        DesignPoint start,
        int maximumEvaluations = 80,
        double step = 0.05,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        IProgress<OptimiserProgress>? progress = null,
        CancellationToken cancellation = default,
        double tolerance = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvaluations);

        var n = problem.Space.Dimension;
        var history = new List<ScoredDesign>();
        ScoredDesign? best = null;

        ScoredDesign Score(double[] x)
        {
            var scored = problem.Score(new DesignPoint(problem.Space, x), fidelity, cancellation);
            history.Add(scored);
            if (best is null || scored.ScalarScore < best.ScalarScore)
            {
                best = scored;
            }

            return scored;
        }

        // The initial simplex: the start plus one point per dimension, offset
        // INWARD when the start sits against a bound — offsetting outward
        // would put a vertex outside the cube, where it clamps back onto the
        // start and the simplex is degenerate from the first iteration.
        var vertices = new List<double[]> { start.Coordinates.ToArray() };
        for (var i = 0; i < n; i++)
        {
            var vertex = start.Coordinates.ToArray();
            vertex[i] = vertex[i] + step <= 1.0 ? vertex[i] + step : vertex[i] - step;
            vertices.Add(vertex.Select(v => Math.Clamp(v, 0.0, 1.0)).ToArray());
        }

        var scores = new List<ScoredDesign>();
        foreach (var vertex in vertices)
        {
            if (history.Count >= maximumEvaluations)
            {
                break;
            }

            scores.Add(Score(vertex));
        }

        var iteration = 0;
        var reason = "evaluation budget spent";

        while (history.Count < maximumEvaluations && scores.Count == n + 1)
        {
            if (cancellation.IsCancellationRequested)
            {
                reason = "cancelled";
                break;
            }

            iteration++;

            // Order worst-last.
            var order = Enumerable.Range(0, scores.Count).OrderBy(i => scores[i].ScalarScore).ToArray();
            vertices = order.Select(i => vertices[i]).ToList();
            scores = order.Select(i => scores[i]).ToList();

            // Converged when the simplex is smaller than the tolerance in
            // every direction.
            var extent = 0.0;
            for (var i = 0; i < n; i++)
            {
                var lo = vertices.Min(v => v[i]);
                var hi = vertices.Max(v => v[i]);
                extent = Math.Max(extent, hi - lo);
            }

            if (extent < tolerance)
            {
                reason = $"converged: the simplex shrank to {extent:E2} of the design space";
                break;
            }

            // Centroid of everything but the worst.
            var centroid = new double[n];
            for (var i = 0; i < n; i++)
            {
                for (var v = 0; v < n; v++)
                {
                    centroid[i] += vertices[v][i];
                }

                centroid[i] /= n;
            }

            var worst = scores[^1];

            double[] Along(double factor)
            {
                var point = new double[n];
                for (var i = 0; i < n; i++)
                {
                    point[i] = Math.Clamp(centroid[i] + (factor * (centroid[i] - vertices[^1][i])), 0.0, 1.0);
                }

                return point;
            }

            var reflected = Along(1.0);
            var reflectedScore = Score(reflected);

            if (reflectedScore.ScalarScore < scores[0].ScalarScore)
            {
                // Better than the best: try going further.
                var expanded = Along(2.0);
                var expandedScore = history.Count < maximumEvaluations ? Score(expanded) : reflectedScore;

                if (expandedScore.ScalarScore < reflectedScore.ScalarScore)
                {
                    vertices[^1] = expanded;
                    scores[^1] = expandedScore;
                }
                else
                {
                    vertices[^1] = reflected;
                    scores[^1] = reflectedScore;
                }
            }
            else if (reflectedScore.ScalarScore < scores[^2].ScalarScore)
            {
                vertices[^1] = reflected;
                scores[^1] = reflectedScore;
            }
            else
            {
                // Contract, on whichever side is better.
                var outside = reflectedScore.ScalarScore < worst.ScalarScore;
                var contracted = Along(outside ? 0.5 : -0.5);
                var contractedScore = history.Count < maximumEvaluations ? Score(contracted) : reflectedScore;

                var reference = outside ? reflectedScore : worst;

                if (contractedScore.ScalarScore < reference.ScalarScore)
                {
                    vertices[^1] = contracted;
                    scores[^1] = contractedScore;
                }
                else
                {
                    // Shrink everything toward the best vertex.
                    for (var v = 1; v < vertices.Count && history.Count < maximumEvaluations; v++)
                    {
                        var shrunk = new double[n];
                        for (var i = 0; i < n; i++)
                        {
                            shrunk[i] = Math.Clamp(
                                vertices[0][i] + (0.5 * (vertices[v][i] - vertices[0][i])), 0.0, 1.0);
                        }

                        vertices[v] = shrunk;
                        scores[v] = Score(shrunk);
                    }
                }
            }

            progress?.Report(new OptimiserProgress(iteration, history.Count, best!, extent));
        }

        return Finish(history, best, iteration, reason);
    }

    /// <summary>
    /// Powell's conjugate-direction method (Powell, <i>The Computer Journal</i>
    /// 7(2):155–162, 1964).
    ///
    /// Minimises along each of a set of directions in turn, then replaces the
    /// direction that gained the most with the net displacement of the whole
    /// sweep. That replacement is the point of the method: after a few
    /// iterations the directions align with the valley rather than with the
    /// coordinate axes, which is what lets it follow a ridge that no single
    /// variable describes — the same structure a manifold's optimum has.
    ///
    /// Chosen over Nelder–Mead when the objective is smooth and the answer is
    /// already close, because a line search extracts more from each evaluation
    /// than a simplex move does. Chosen against it when the objective is noisy,
    /// because a line search believes what it is told.
    /// </summary>
    /// <param name="problem">What is being refined.</param>
    /// <param name="start">Where to start.</param>
    /// <param name="maximumEvaluations">Budget.</param>
    /// <param name="fidelity">What to ask the evaluator for.</param>
    /// <param name="progress">Reported once per direction sweep.</param>
    /// <param name="cancellation">Cancels between evaluations.</param>
    /// <param name="tolerance">Stop when a whole sweep moves less than this.</param>
    public static OptimiserResult Powell(
        OptimisationProblem problem,
        DesignPoint start,
        int maximumEvaluations = 80,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        IProgress<OptimiserProgress>? progress = null,
        CancellationToken cancellation = default,
        double tolerance = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvaluations);

        var n = problem.Space.Dimension;
        var history = new List<ScoredDesign>();
        ScoredDesign? best = null;

        double Score(double[] x)
        {
            var scored = problem.Score(new DesignPoint(problem.Space, x), fidelity, cancellation);
            history.Add(scored);
            if (best is null || scored.ScalarScore < best.ScalarScore)
            {
                best = scored;
            }

            return scored.ScalarScore;
        }

        // Directions start as the coordinate axes and are replaced as the
        // method learns the valley.
        var directions = new List<double[]>();
        for (var i = 0; i < n; i++)
        {
            var direction = new double[n];
            direction[i] = 1.0;
            directions.Add(direction);
        }

        var current = start.Coordinates.ToArray();
        var currentScore = Score(current);
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

            var start0 = current.ToArray();
            var score0 = currentScore;
            var biggestGain = 0.0;
            var biggestIndex = 0;

            for (var d = 0; d < directions.Count && history.Count < maximumEvaluations; d++)
            {
                var before = currentScore;
                (current, currentScore) = LineMinimise(current, directions[d], currentScore);

                var gain = before - currentScore;
                if (gain > biggestGain)
                {
                    biggestGain = gain;
                    biggestIndex = d;
                }
            }

            var moved = Math.Sqrt(current.Select((v, i) => (v - start0[i]) * (v - start0[i])).Sum());
            if (moved < tolerance)
            {
                reason = $"converged: a whole sweep moved {moved:E2} of the design space";
                break;
            }

            // The new direction: the net displacement of the sweep. Replacing
            // the direction that gained most is Powell's own rule, and it is
            // what stops the direction set becoming linearly dependent — which
            // would silently reduce the search to a subspace.
            if (history.Count < maximumEvaluations && moved > 1e-12)
            {
                var combined = current.Select((v, i) => (v - start0[i]) / moved).ToArray();
                directions[biggestIndex] = combined;
                (current, currentScore) = LineMinimise(current, combined, currentScore);
            }

            progress?.Report(new OptimiserProgress(iteration, history.Count, best!, moved));

            if (Math.Abs(score0 - currentScore) < 1e-15 && moved < tolerance)
            {
                reason = "converged: no further improvement along any direction";
                break;
            }
        }

        return Finish(history, best, iteration, reason);

        // Golden-section search along one direction, bounded by the cube.
        //
        // Golden section rather than a parabolic fit: it needs only
        // comparisons, which means a noisy objective degrades it gracefully
        // instead of sending it somewhere a fitted parabola's vertex happened
        // to land.
        (double[] Point, double Score) LineMinimise(double[] origin, double[] direction, double originScore)
        {
            // How far the direction can be followed before leaving the cube.
            var limit = double.PositiveInfinity;
            for (var i = 0; i < n; i++)
            {
                if (Math.Abs(direction[i]) < 1e-12)
                {
                    continue;
                }

                var toEdge = direction[i] > 0 ? (1.0 - origin[i]) / direction[i] : -origin[i] / direction[i];
                limit = Math.Min(limit, Math.Abs(toEdge));
            }

            // A refinement, so the line search is short: the answer is
            // believed to be near, and sweeping the whole cube would throw
            // that belief away.
            var span = Math.Min(limit, 0.25);
            if (span < 1e-9)
            {
                return (origin, originScore);
            }

            double[] At(double t) =>
                origin.Select((v, i) => Math.Clamp(v + (t * direction[i]), 0.0, 1.0)).ToArray();

            const double phi = 0.6180339887498949;

            // Search both ways: a refinement has no reason to assume the
            // improvement lies in the direction's positive sense.
            var lo = -span;
            var hi = span;

            var c = hi - (phi * (hi - lo));
            var d = lo + (phi * (hi - lo));

            var fc = history.Count < maximumEvaluations ? Score(At(c)) : originScore;
            var fd = history.Count < maximumEvaluations ? Score(At(d)) : originScore;

            for (var k = 0; k < 8 && history.Count < maximumEvaluations && hi - lo > tolerance; k++)
            {
                if (fc < fd)
                {
                    hi = d;
                    d = c;
                    fd = fc;
                    c = hi - (phi * (hi - lo));
                    fc = Score(At(c));
                }
                else
                {
                    lo = c;
                    c = d;
                    fc = fd;
                    d = lo + (phi * (hi - lo));
                    fd = Score(At(d));
                }
            }

            var bestT = fc < fd ? c : d;
            var bestScore = Math.Min(fc, fd);

            // Never move to something worse than where the line started. A
            // line search that returns a worse point turns the outer loop into
            // a random walk.
            return bestScore < originScore ? (At(bestT), bestScore) : (origin, originScore);
        }
    }

    private static OptimiserResult Finish(
        List<ScoredDesign> history, ScoredDesign? best, int iterations, string reason)
    {
        if (best is null)
        {
            throw new InvalidOperationException("The budget did not allow a single evaluation.");
        }

        var feasible = history.Where(d => d.Feasible).OrderBy(d => d.ScalarScore).FirstOrDefault();
        return new OptimiserResult(feasible ?? best, history, iterations, reason);
    }
}
