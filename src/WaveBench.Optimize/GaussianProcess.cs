namespace WaveBench.Optimize;

/// <summary>
/// What the surrogate believes about one unobserved point.
/// </summary>
/// <param name="Mean">Posterior mean.</param>
/// <param name="StandardDeviation">
/// Posterior standard deviation — how little the model knows here. Zero at an
/// observed point and largest far from every observation, which is exactly
/// what makes it usable for deciding where to look next.
/// </param>
public readonly record struct Belief(double Mean, double StandardDeviation);

/// <summary>
/// A Gaussian-process regressor over the unit cube, for Bayesian optimisation
/// (plan §9.4).
///
/// <b>Why a GP and not a fitted response surface.</b> A quadratic fit gives a
/// number and no idea how much to trust it. The whole mechanism of Bayesian
/// optimisation is that the surrogate reports its own UNCERTAINTY, so the
/// search can tell "I predict this is poor" from "I have no idea what this
/// is" — and spend an expensive evaluation on the second rather than the
/// first. A model without error bars cannot make that distinction, and
/// without it the method degenerates into hill-climbing a fitted surface.
///
/// Matérn 5/2 kernel (Rasmussen &amp; Williams, <i>Gaussian Processes for
/// Machine Learning</i>, MIT Press, 2006, §4.2), which is the standard choice
/// for optimisation: the squared-exponential kernel assumes the response is
/// infinitely differentiable, which for a torque curve through a resonance is
/// an assumption that produces over-confident extrapolation exactly where the
/// interesting structure is.
/// </summary>
public sealed class GaussianProcess
{
    private readonly double[][] _x;
    private readonly double[] _y;
    private readonly double[] _alpha;
    private readonly double[,] _l;
    private readonly double _mean;
    private readonly double _scale;

    /// <param name="points">Observed locations, each in the unit cube.</param>
    /// <param name="values">Observed values, to be MINIMISED by the caller's convention.</param>
    /// <param name="lengthScale">Correlation distance in cube units.</param>
    /// <param name="noise">Observation noise as a fraction of the standardised signal.</param>
    public GaussianProcess(
        IReadOnlyList<double[]> points, IReadOnlyList<double> values, double lengthScale, double noise)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(values);

        if (points.Count != values.Count || points.Count == 0)
        {
            throw new ArgumentException("A process needs at least one observation, and one value per point.");
        }

        LengthScale = Math.Max(lengthScale, 1e-6);
        Noise = Math.Max(noise, 1e-8);

        _x = points.Select(p => p.ToArray()).ToArray();

        // Standardised, so the kernel's signal variance can be fixed at one.
        // Fitting a signal variance as well as a length scale on thirty points
        // is fitting noise; standardising removes the need to.
        _mean = values.Average();
        var variance = values.Count > 1
            ? values.Sum(v => (v - _mean) * (v - _mean)) / (values.Count - 1)
            : 1.0;
        _scale = variance > 1e-30 ? Math.Sqrt(variance) : 1.0;

        _y = values.Select(v => (v - _mean) / _scale).ToArray();

        var n = _x.Length;
        var k = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                k[i, j] = Kernel(_x[i], _x[j]);
            }

            k[i, i] += Noise;
        }

        _l = Cholesky(k);
        _alpha = SolveCholesky(_l, _y);
    }

    public double LengthScale { get; }

    public double Noise { get; }

    public int Observations => _x.Length;

    /// <summary>The posterior at a point, back in the caller's own units.</summary>
    public Belief At(IReadOnlyList<double> point)
    {
        ArgumentNullException.ThrowIfNull(point);

        var n = _x.Length;
        var k = new double[n];
        for (var i = 0; i < n; i++)
        {
            k[i] = Kernel(_x[i], point);
        }

        var mean = 0.0;
        for (var i = 0; i < n; i++)
        {
            mean += k[i] * _alpha[i];
        }

        // v = L⁻¹k; posterior variance is k(x,x) − vᵀv.
        var v = ForwardSubstitute(_l, k);
        var variance = 1.0 - v.Sum(value => value * value);

        // Rounding can take it a hair negative at an observed point, where the
        // true value is zero.
        variance = Math.Max(variance, 0.0);

        return new Belief((mean * _scale) + _mean, Math.Sqrt(variance) * _scale);
    }

    /// <summary>
    /// Log marginal likelihood, for choosing the hyperparameters.
    ///
    /// This is what makes the length scale a fitted quantity rather than a
    /// guess: it balances fitting the data against the complexity of the
    /// function needed to do so, automatically. A length scale chosen by hand
    /// is a claim about how quickly torque varies with runner length, and
    /// nobody knows that in advance.
    /// </summary>
    public double LogMarginalLikelihood()
    {
        var n = _x.Length;

        var fit = 0.0;
        for (var i = 0; i < n; i++)
        {
            fit += _y[i] * _alpha[i];
        }

        var logDeterminant = 0.0;
        for (var i = 0; i < n; i++)
        {
            logDeterminant += Math.Log(_l[i, i]);
        }

        return (-0.5 * fit) - logDeterminant - (0.5 * n * Math.Log(2.0 * Math.PI));
    }

    /// <summary>
    /// Fit the length scale and noise by maximising the log marginal
    /// likelihood over a log-spaced grid.
    ///
    /// <b>One shared length scale, not one per dimension.</b> Automatic
    /// relevance determination — a length scale per variable — is strictly
    /// more expressive and is the wrong choice here: it adds one hyperparameter
    /// per variable, and Bayesian optimisation is used precisely when there are
    /// only a few dozen observations to fit them from. Fitting six extra
    /// parameters to forty points overfits more often than it helps, and the
    /// failure mode is a surrogate confidently certain about a region it has
    /// never sampled. Screening (<see cref="Screening"/>) is the cheaper and
    /// more honest way to find out which variables matter.
    ///
    /// A grid rather than a gradient method because the likelihood surface in
    /// these two parameters is mild, the grid is deterministic — plan Part 0
    /// requires a reproducible run — and thirty-odd Cholesky factorisations of
    /// a matrix this size cost less than one engine evaluation.
    /// </summary>
    public static GaussianProcess Fit(IReadOnlyList<double[]> points, IReadOnlyList<double> values)
    {
        GaussianProcess? best = null;
        var bestScore = double.NegativeInfinity;

        // Length scales from a tenth of the cube to twice it: below that the
        // model interpolates noise, above it every point looks identical.
        foreach (var lengthScale in new[] { 0.05, 0.08, 0.12, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.5, 2.0 })
        {
            foreach (var noise in new[] { 1e-6, 1e-4, 1e-3, 1e-2, 5e-2 })
            {
                GaussianProcess candidate;
                try
                {
                    candidate = new GaussianProcess(points, values, lengthScale, noise);
                }
                catch (InvalidOperationException)
                {
                    // A covariance matrix that will not factor — duplicated
                    // points at a long length scale. Not a fit worth having.
                    continue;
                }

                var score = candidate.LogMarginalLikelihood();
                if (double.IsFinite(score) && score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best ?? new GaussianProcess(points, values, 0.3, 1e-2);
    }

    // ---- Kernel and linear algebra ----------------------------------------

    /// <summary>Matérn 5/2: k(r) = (1 + √5r + 5r²/3)·exp(−√5r), r the scaled distance.</summary>
    private double Kernel(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var squared = 0.0;
        for (var i = 0; i < a.Count; i++)
        {
            var d = a[i] - b[i];
            squared += d * d;
        }

        var r = Math.Sqrt(squared) / LengthScale;
        var root5r = Math.Sqrt(5.0) * r;

        return (1.0 + root5r + (5.0 * r * r / 3.0)) * Math.Exp(-root5r);
    }

    private static double[,] Cholesky(double[,] a)
    {
        var n = a.GetLength(0);
        var l = new double[n, n];

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var sum = a[i, j];
                for (var k = 0; k < j; k++)
                {
                    sum -= l[i, k] * l[j, k];
                }

                if (i == j)
                {
                    if (sum <= 0)
                    {
                        throw new InvalidOperationException(
                            "The covariance matrix is not positive definite — usually duplicated observations "
                            + "with too little noise to separate them.");
                    }

                    l[i, i] = Math.Sqrt(sum);
                }
                else
                {
                    l[i, j] = sum / l[j, j];
                }
            }
        }

        return l;
    }

    private static double[] ForwardSubstitute(double[,] l, double[] b)
    {
        var n = b.Length;
        var x = new double[n];

        for (var i = 0; i < n; i++)
        {
            var sum = b[i];
            for (var k = 0; k < i; k++)
            {
                sum -= l[i, k] * x[k];
            }

            x[i] = sum / l[i, i];
        }

        return x;
    }

    private static double[] SolveCholesky(double[,] l, double[] b)
    {
        var n = b.Length;
        var y = ForwardSubstitute(l, b);
        var x = new double[n];

        for (var i = n - 1; i >= 0; i--)
        {
            var sum = y[i];
            for (var k = i + 1; k < n; k++)
            {
                sum -= l[k, i] * x[k];
            }

            x[i] = sum / l[i, i];
        }

        return x;
    }
}
