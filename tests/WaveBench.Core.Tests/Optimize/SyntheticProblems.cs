using WaveBench.Model;
using WaveBench.Optimize;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// Optimisation problems with an optimum known in closed form.
///
/// <b>This is how the Phase 22 gate's first clause is checkable at all.</b>
/// <i>"On a synthetic problem with a known optimum the optimiser converges
/// reliably"</i> — reliably means over many seeds, and the only way to run a
/// search hundreds of times is for an evaluation to cost microseconds. A test
/// that drove the real solver could afford perhaps one run, which would test
/// luck rather than convergence.
///
/// The functions are the standard derivative-free benchmarks, chosen for the
/// specific failure each one provokes:
///
/// <list type="bullet">
/// <item><b>Sphere</b> — separable and convex. Anything that cannot solve this
/// is broken.</item>
/// <item><b>Rosenbrock</b> — a curved, narrow valley whose axis lies along no
/// coordinate. A search with per-axis step sizes crawls; CMA-ES is supposed to
/// learn the valley's orientation, and this is where that is demonstrated
/// rather than asserted.</item>
/// <item><b>Rastrigin</b> — a quadratic bowl under a cosine lattice, so it is
/// riddled with local minima. This is the one that distinguishes a global
/// search from a local one.</item>
/// </list>
/// </summary>
internal static class SyntheticProblems
{
    /// <summary>
    /// A document with enough spare numeric fields to carry a synthetic
    /// design vector.
    ///
    /// Real fields, deliberately: the optimiser writes through
    /// <see cref="ModelPath"/> into an actual document and reads back what the
    /// document stored, so a coercion or a rounding at that boundary shows up
    /// here rather than in a real run. The values mean nothing physically —
    /// the objective is an analytic function of them, not a solve.
    /// </summary>
    public static EngineModelDocument Document() => new()
    {
        Name = "synthetic",
        Engine = new EngineSpec { BoreMm = 86, StrokeMm = 86, RodLengthMm = 145, CompressionRatio = 10 },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 33, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 28, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 370 },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
        ExhaustRunner = new DuctSpec { LengthMm = 400, DiameterMm = 38 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
    };

    /// <summary>The paths the synthetic spaces use, in order.</summary>
    public static IReadOnlyList<string> Paths { get; } =
    [
        "IntakeRunner.LengthMm",
        "IntakeRunner.DiameterMm",
        "ExhaustRunner.LengthMm",
        "ExhaustRunner.DiameterMm",
    ];

    /// <summary>
    /// A space of <paramref name="dimension"/> variables, each spanning
    /// [-5, 5] in "model units" so the benchmark functions sit in their usual
    /// domain.
    /// </summary>
    public static DesignSpace Space(int dimension, double lower = -5.0, double upper = 5.0)
    {
        var variables = Paths.Take(dimension)
            .Select(p => new OptimisationVariable(p, lower, upper))
            .ToList();
        return new DesignSpace(variables);
    }

    /// <summary>
    /// An evaluator that computes an analytic function of the design values
    /// and reports it as a named metric.
    /// </summary>
    public sealed class Analytic(Func<IReadOnlyList<double>, double> function) : IDesignEvaluator
    {
        public const string MetricKey = "synthetic";

        public int Calls { get; private set; }

        public IReadOnlyList<EvaluationFidelity> Fidelities => [EvaluationFidelity.Solved];

        public DesignEvaluation Evaluate(
            DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default)
        {
            Calls++;
            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    [MetricKey] = function(design.Values),
                },
            };
        }
    }

    /// <summary>Minimise the synthetic metric.</summary>
    public static ObjectiveSet Minimise { get; } = new(
        [new MetricObjective("Synthetic", "", Analytic.MetricKey, ObjectiveSense.Minimise)]);

    // ---- The functions ----------------------------------------------------

    /// <summary>Σxᵢ². Optimum 0 at the origin.</summary>
    public static double Sphere(IReadOnlyList<double> x) => x.Sum(v => v * v);

    /// <summary>
    /// Σ[100(x_{i+1} − xᵢ²)² + (1 − xᵢ)²]. Optimum 0 at (1, 1, …).
    /// </summary>
    public static double Rosenbrock(IReadOnlyList<double> x)
    {
        var total = 0.0;
        for (var i = 0; i < x.Count - 1; i++)
        {
            var a = x[i + 1] - (x[i] * x[i]);
            var b = 1.0 - x[i];
            total += (100.0 * a * a) + (b * b);
        }

        return total;
    }

    /// <summary>10n + Σ[xᵢ² − 10cos(2πxᵢ)]. Optimum 0 at the origin, under a lattice of local minima.</summary>
    public static double Rastrigin(IReadOnlyList<double> x) =>
        (10.0 * x.Count) + x.Sum(v => (v * v) - (10.0 * Math.Cos(2.0 * Math.PI * v)));

    /// <summary>Build a problem over one of the functions.</summary>
    public static (OptimisationProblem Problem, Analytic Evaluator) Problem(
        Func<IReadOnlyList<double>, double> function,
        int dimension,
        ConstraintSet? constraints = null,
        double lower = -5.0,
        double upper = 5.0)
    {
        var evaluator = new Analytic(function);
        var problem = new OptimisationProblem(
            Document(), Space(dimension, lower, upper), Minimise, evaluator, constraints);
        return (problem, evaluator);
    }
}
