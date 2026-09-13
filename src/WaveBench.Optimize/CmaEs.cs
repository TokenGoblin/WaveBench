namespace WaveBench.Optimize;

/// <summary>Progress from a running search, for the job tray and the log.</summary>
/// <param name="Iteration">Generation number, from 1.</param>
/// <param name="Evaluations">Designs measured so far.</param>
/// <param name="Best">The best design found so far.</param>
/// <param name="Sigma">Current step size, where the algorithm has one.</param>
public sealed record OptimiserProgress(int Iteration, int Evaluations, ScoredDesign Best, double Sigma);

/// <summary>What a single-objective search returned.</summary>
/// <param name="Best">The best FEASIBLE design, or the least-infeasible one when none was feasible.</param>
/// <param name="History">Every design scored, in the order they were scored.</param>
/// <param name="Iterations">Generations completed.</param>
/// <param name="Reason">Why it stopped.</param>
public sealed record OptimiserResult(
    ScoredDesign Best, IReadOnlyList<ScoredDesign> History, int Iterations, string Reason)
{
    public int Evaluations => History.Count;

    /// <summary>Every feasible design found, best first.</summary>
    public IReadOnlyList<ScoredDesign> Feasible =>
        History.Where(d => d.Feasible).OrderBy(d => d.ScalarScore).ToList();
}

/// <summary>
/// Covariance Matrix Adaptation Evolution Strategy (plan §9.4: <i>"CMA-ES for
/// single-objective global search"</i>).
///
/// Hansen &amp; Ostermeier, "Completely Derandomized Self-Adaptation in
/// Evolution Strategies," <i>Evolutionary Computation</i> 9(2):159–195, 2001;
/// parameter defaults from Hansen, "The CMA Evolution Strategy: A Tutorial,"
/// 2016. Implemented with the standard weights, rank-one plus rank-μ
/// covariance update and cumulative step-size adaptation.
///
/// <b>Why this one is the default for a single objective.</b> The response
/// surface of a manifold is not separable — runner length and diameter trade
/// against each other, and the ridge they form does not lie along either
/// axis. A search that adapts only a per-axis step size crawls along such a
/// ridge; CMA-ES learns the ridge's orientation from the population itself,
/// which is exactly the structure this problem has.
///
/// <b>Derivative-free and rank-based.</b> It uses only the ORDER of the
/// candidates, never the objective values themselves, which is what makes the
/// lexicographic feasible-before-infeasible score in
/// <see cref="ScoredDesign.ScalarScore"/> safe: the enormous numeric gap
/// between the feasible and infeasible bands never enters the arithmetic.
/// </summary>
public sealed class CmaEs
{
    private readonly OptimisationProblem _problem;
    private readonly int _n;
    private readonly Random _random;

    // Strategy parameters, fixed at construction from the dimension.
    private readonly int _lambda;
    private readonly int _mu;
    private readonly double[] _weights;
    private readonly double _muEff;
    private readonly double _cc;
    private readonly double _cs;
    private readonly double _c1;
    private readonly double _cmu;
    private readonly double _damps;
    private readonly double _chiN;

    // State.
    private double[] _mean;
    private double _sigma;
    private double[,] _c;
    private double[] _pc;
    private double[] _ps;
    private double[,] _b;
    private double[] _d;
    private int _eigenEvaluations;

    /// <param name="problem">What is being optimised.</param>
    /// <param name="start">Starting mean; the baseline's own design when null.</param>
    /// <param name="sigma">
    /// Initial step size as a fraction of the unit cube. 0.3 covers roughly a
    /// third of each variable's range in one standard deviation, which is the
    /// usual advice for a search with no prior: large enough to leave a local
    /// basin, small enough not to spend the first generations at the bounds.
    /// </param>
    /// <param name="populationSize">λ; the standard 4 + ⌊3·ln n⌋ when null.</param>
    /// <param name="seed">Fixed so a run is reproducible (plan Part 0: determinism).</param>
    public CmaEs(
        OptimisationProblem problem,
        DesignPoint? start = null,
        double sigma = 0.3,
        int? populationSize = null,
        int seed = 20260913)
    {
        _problem = problem ?? throw new ArgumentNullException(nameof(problem));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sigma);

        _n = problem.Space.Dimension;
        _random = new Random(seed);

        _lambda = populationSize ?? (4 + (int)Math.Floor(3.0 * Math.Log(_n)));
        _lambda = Math.Max(_lambda, 4);
        _mu = _lambda / 2;

        // Log-decreasing recombination weights, normalised to sum to one.
        var raw = new double[_mu];
        for (var i = 0; i < _mu; i++)
        {
            raw[i] = Math.Log((_lambda / 2.0) + 0.5) - Math.Log(i + 1.0);
        }

        var sum = raw.Sum();
        _weights = raw.Select(w => w / sum).ToArray();
        _muEff = 1.0 / _weights.Sum(w => w * w);

        _cc = (4.0 + (_muEff / _n)) / (_n + 4.0 + (2.0 * _muEff / _n));
        _cs = (_muEff + 2.0) / (_n + _muEff + 5.0);
        _c1 = 2.0 / (Math.Pow(_n + 1.3, 2.0) + _muEff);
        _cmu = Math.Min(
            1.0 - _c1,
            2.0 * ((_muEff - 2.0 + (1.0 / _muEff)) / (Math.Pow(_n + 2.0, 2.0) + _muEff)));
        _damps = 1.0 + (2.0 * Math.Max(0.0, Math.Sqrt((_muEff - 1.0) / (_n + 1.0)) - 1.0)) + _cs;

        // E‖N(0,I)‖, to the usual second-order approximation.
        _chiN = Math.Sqrt(_n) * (1.0 - (1.0 / (4.0 * _n)) + (1.0 / (21.0 * _n * _n)));

        var origin = start ?? problem.Space.From(problem.Baseline);
        _mean = origin.Coordinates.ToArray();
        _sigma = sigma;

        _c = Identity(_n);
        _b = Identity(_n);
        _d = Enumerable.Repeat(1.0, _n).ToArray();
        _pc = new double[_n];
        _ps = new double[_n];
    }

    public int PopulationSize => _lambda;

    public double Sigma => _sigma;

    /// <summary>
    /// Run until the evaluation budget is spent or the search converges.
    /// </summary>
    /// <param name="maximumEvaluations">Hard budget, in designs measured.</param>
    /// <param name="fidelity">What to ask the evaluator for.</param>
    /// <param name="progress">Reported once per generation.</param>
    /// <param name="cancellation">Cancels between evaluations; the work done so far is still returned.</param>
    /// <param name="tolerance">
    /// Stop when the step size has collapsed to this fraction of its start.
    /// Not a claim of optimality — it is the point past which the search is
    /// sampling inside its own numerical noise.
    /// </param>
    public OptimiserResult Run(
        int maximumEvaluations = 200,
        EvaluationFidelity fidelity = EvaluationFidelity.Solved,
        IProgress<OptimiserProgress>? progress = null,
        CancellationToken cancellation = default,
        double tolerance = 1e-4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvaluations);

        var history = new List<ScoredDesign>();
        var sigma0 = _sigma;
        ScoredDesign? best = null;
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

            var steps = new double[_lambda][];
            var candidates = new List<ScoredDesign>(_lambda);

            for (var k = 0; k < _lambda && history.Count < maximumEvaluations; k++)
            {
                var z = Gaussian(_n);
                var y = Transform(z);
                var x = new double[_n];
                for (var i = 0; i < _n; i++)
                {
                    x[i] = _mean[i] + (_sigma * y[i]);
                }

                // Box handling by repair: the sample is clamped into the unit
                // cube and the CLAMPED point is what gets evaluated and what
                // the update uses. A rejected-resample scheme stalls badly
                // once the optimum sits on a bound — which it routinely does
                // here, because a bound is usually a real limit like "the
                // longest runner that fits" and the best design leans on it.
                var repaired = x.Select(v => Math.Clamp(v, 0.0, 1.0)).ToArray();
                steps[k] = new double[_n];
                for (var i = 0; i < _n; i++)
                {
                    steps[k][i] = (repaired[i] - _mean[i]) / _sigma;
                }

                var scored = _problem.Score(new DesignPoint(_problem.Space, repaired), fidelity, cancellation);
                candidates.Add(scored);
                history.Add(scored);

                if (best is null || scored.ScalarScore < best.ScalarScore)
                {
                    best = scored;
                }
            }

            if (candidates.Count < _mu)
            {
                reason = "evaluation budget spent mid-generation";
                break;
            }

            // Rank only. The objective values themselves never enter the
            // update, which is what makes the huge feasible/infeasible gap
            // harmless.
            var order = Enumerable.Range(0, candidates.Count)
                .OrderBy(i => candidates[i].ScalarScore)
                .ToArray();

            var oldMean = _mean.ToArray();
            var newMean = new double[_n];
            for (var i = 0; i < _mu; i++)
            {
                var step = steps[order[i]];
                for (var j = 0; j < _n; j++)
                {
                    newMean[j] += _weights[i] * (oldMean[j] + (_sigma * step[j]));
                }
            }

            _mean = newMean;

            // (m_new - m_old)/sigma, in the sampling frame.
            var meanStep = new double[_n];
            for (var j = 0; j < _n; j++)
            {
                meanStep[j] = (newMean[j] - oldMean[j]) / _sigma;
            }

            // Cumulative step-size adaptation: the path length tells the
            // search whether its steps are reinforcing (too small) or
            // cancelling (too large).
            var invSqrtCy = ApplyInverseSqrtC(meanStep);
            for (var j = 0; j < _n; j++)
            {
                _ps[j] = ((1.0 - _cs) * _ps[j]) + (Math.Sqrt(_cs * (2.0 - _cs) * _muEff) * invSqrtCy[j]);
            }

            var psNorm = Math.Sqrt(_ps.Sum(v => v * v));

            // The h_sigma switch stops a long step-size path from also
            // inflating the covariance, which is what causes runaway when the
            // search is moving fast in one direction.
            var hsig = psNorm / Math.Sqrt(1.0 - Math.Pow(1.0 - _cs, 2.0 * iteration)) / _chiN
                       < 1.4 + (2.0 / (_n + 1.0))
                ? 1.0
                : 0.0;

            for (var j = 0; j < _n; j++)
            {
                _pc[j] = ((1.0 - _cc) * _pc[j]) + (hsig * Math.Sqrt(_cc * (2.0 - _cc) * _muEff) * meanStep[j]);
            }

            UpdateCovariance(steps, order, hsig);

            _sigma *= Math.Exp(_cs / _damps * ((psNorm / _chiN) - 1.0));
            _sigma = Math.Clamp(_sigma, 1e-12, 10.0);

            progress?.Report(new OptimiserProgress(iteration, history.Count, best!, _sigma));

            if (_sigma < tolerance * sigma0)
            {
                reason = $"converged: step size fell to {_sigma:E2}, below {tolerance:P2} of its start";
                break;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException("The budget did not allow a single evaluation.");
        }

        // Report the best FEASIBLE design where one exists. The running best
        // is by scalar score, which already ranks feasible above infeasible —
        // this is belt and braces, and it is the line the Phase 22 gate's
        // "never violated in a returned design" clause rests on.
        var feasible = history.Where(d => d.Feasible).OrderBy(d => d.ScalarScore).FirstOrDefault();

        return new OptimiserResult(feasible ?? best, history, iteration, reason);
    }

    // ---- Linear algebra ---------------------------------------------------

    private void UpdateCovariance(double[][] steps, int[] order, double hsig)
    {
        var rankOne = new double[_n, _n];
        for (var i = 0; i < _n; i++)
        {
            for (var j = 0; j < _n; j++)
            {
                rankOne[i, j] = _pc[i] * _pc[j];
            }
        }

        var rankMu = new double[_n, _n];
        for (var k = 0; k < _mu; k++)
        {
            var y = steps[order[k]];
            for (var i = 0; i < _n; i++)
            {
                for (var j = 0; j < _n; j++)
                {
                    rankMu[i, j] += _weights[k] * y[i] * y[j];
                }
            }
        }

        // The (1 - hsig²)·cc·(2-cc) term restores the variance h_sigma just
        // removed, so switching it does not shrink C on its own.
        var correction = (1.0 - (hsig * hsig)) * _cc * (2.0 - _cc);
        var decay = 1.0 - _c1 - _cmu;

        for (var i = 0; i < _n; i++)
        {
            for (var j = 0; j < _n; j++)
            {
                _c[i, j] = (decay * _c[i, j])
                           + (_c1 * (rankOne[i, j] + (correction * _c[i, j])))
                           + (_cmu * rankMu[i, j]);
            }
        }

        // Symmetry drifts through rounding, and an asymmetric matrix makes the
        // eigensolver return nonsense rather than fail.
        for (var i = 0; i < _n; i++)
        {
            for (var j = i + 1; j < _n; j++)
            {
                var mean = 0.5 * (_c[i, j] + _c[j, i]);
                _c[i, j] = mean;
                _c[j, i] = mean;
            }
        }

        _eigenEvaluations += _lambda;

        // Eigendecomposition is O(n³) and only needed often enough to keep B
        // and D current; Hansen's own guidance is roughly once per
        // λ/(10·n·(c1+cμ)) evaluations.
        var interval = _lambda / (10.0 * _n * (_c1 + _cmu));
        if (_eigenEvaluations > interval)
        {
            _eigenEvaluations = 0;
            Eigen();
        }
    }

    /// <summary>
    /// Symmetric eigendecomposition by the cyclic Jacobi method.
    ///
    /// Jacobi rather than anything faster because n here is the number of
    /// design variables — single digits to low tens — where its guaranteed
    /// accuracy on a symmetric matrix is worth far more than its asymptotic
    /// cost, and because it cannot produce the negative eigenvalue a less
    /// careful method occasionally hands back on a near-singular C.
    /// </summary>
    private void Eigen()
    {
        var a = (double[,])_c.Clone();
        var v = Identity(_n);

        for (var sweep = 0; sweep < 100; sweep++)
        {
            var off = 0.0;
            for (var p = 0; p < _n; p++)
            {
                for (var q = p + 1; q < _n; q++)
                {
                    off += a[p, q] * a[p, q];
                }
            }

            if (off < 1e-30)
            {
                break;
            }

            for (var p = 0; p < _n; p++)
            {
                for (var q = p + 1; q < _n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300)
                    {
                        continue;
                    }

                    var theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt((theta * theta) + 1.0));
                    if (theta == 0.0)
                    {
                        t = 1.0;
                    }

                    var cos = 1.0 / Math.Sqrt((t * t) + 1.0);
                    var sin = t * cos;

                    for (var k = 0; k < _n; k++)
                    {
                        var akp = a[k, p];
                        var akq = a[k, q];
                        a[k, p] = (cos * akp) - (sin * akq);
                        a[k, q] = (sin * akp) + (cos * akq);
                    }

                    for (var k = 0; k < _n; k++)
                    {
                        var apk = a[p, k];
                        var aqk = a[q, k];
                        a[p, k] = (cos * apk) - (sin * aqk);
                        a[q, k] = (sin * apk) + (cos * aqk);
                    }

                    for (var k = 0; k < _n; k++)
                    {
                        var vkp = v[k, p];
                        var vkq = v[k, q];
                        v[k, p] = (cos * vkp) - (sin * vkq);
                        v[k, q] = (sin * vkp) + (cos * vkq);
                    }
                }
            }
        }

        _b = v;
        _d = new double[_n];
        for (var i = 0; i < _n; i++)
        {
            // Clamped positive: a covariance that has gone numerically
            // non-positive-definite is a degenerate search, not a licence to
            // take the square root of a negative number.
            _d[i] = Math.Sqrt(Math.Max(a[i, i], 1e-20));
        }
    }

    /// <summary>B·D·z — a standard normal sample shaped by the current covariance.</summary>
    private double[] Transform(double[] z)
    {
        var dz = new double[_n];
        for (var i = 0; i < _n; i++)
        {
            dz[i] = _d[i] * z[i];
        }

        var y = new double[_n];
        for (var i = 0; i < _n; i++)
        {
            var sum = 0.0;
            for (var j = 0; j < _n; j++)
            {
                sum += _b[i, j] * dz[j];
            }

            y[i] = sum;
        }

        return y;
    }

    /// <summary>C^(-1/2)·y = B·D⁻¹·Bᵀ·y, for the step-size path.</summary>
    private double[] ApplyInverseSqrtC(double[] y)
    {
        var bt = new double[_n];
        for (var i = 0; i < _n; i++)
        {
            var sum = 0.0;
            for (var j = 0; j < _n; j++)
            {
                sum += _b[j, i] * y[j];
            }

            bt[i] = sum / _d[i];
        }

        var result = new double[_n];
        for (var i = 0; i < _n; i++)
        {
            var sum = 0.0;
            for (var j = 0; j < _n; j++)
            {
                sum += _b[i, j] * bt[j];
            }

            result[i] = sum;
        }

        return result;
    }

    private double[] Gaussian(int n)
    {
        var z = new double[n];
        for (var i = 0; i < n; i++)
        {
            // Box-Muller. NextDouble can return exactly 0 and log(0) is -∞, so
            // the low end is nudged off zero.
            var u1 = Math.Max(_random.NextDouble(), 1e-15);
            var u2 = _random.NextDouble();
            z[i] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        return z;
    }

    private static double[,] Identity(int n)
    {
        var m = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            m[i, i] = 1.0;
        }

        return m;
    }
}
