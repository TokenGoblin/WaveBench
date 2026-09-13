using FluentAssertions;
using WaveBench.Optimize;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// Bayesian optimisation and the Gaussian process behind it (plan §9.4).
///
/// <b>The claim being tested is not "it converges".</b> CMA-ES already
/// converges, and on a large budget it converges further. The reason the plan
/// calls Bayesian optimisation <i>"the right default when each evaluation
/// costs a 20-point rpm sweep"</i> is that it does better per EVALUATION at
/// small budgets — which is the budget this project actually has. So the
/// headline test compares the two at a budget of forty, and the comparison is
/// over many seeds because one run of either proves nothing.
/// </summary>
public class BayesianTests(ITestOutputHelper output)
{
    // ---- The surrogate ------------------------------------------------------

    [Fact]
    public void The_process_interpolates_its_observations_and_is_certain_there()
    {
        // A GP conditioned on noiseless-ish data must pass through it. If it
        // does not, the posterior is wrong and everything built on it is
        // guesswork with error bars.
        double[][] points = [[0.1, 0.2], [0.5, 0.5], [0.9, 0.3], [0.3, 0.8]];
        var values = points.Select(p => (p[0] * p[0]) + Math.Sin(3.0 * p[1])).ToList();

        var gp = new GaussianProcess(points, values, lengthScale: 0.3, noise: 1e-8);

        for (var i = 0; i < points.Length; i++)
        {
            var belief = gp.At(points[i]);
            belief.Mean.Should().BeApproximately(values[i], 1e-4, $"observation {i}");
            belief.StandardDeviation.Should().BeLessThan(1e-3, "the model has seen this point");
        }
    }

    [Fact]
    public void Uncertainty_grows_with_distance_from_everything_observed()
    {
        // This is the property the whole method rests on: the surrogate has to
        // know the difference between "I predict this is poor" and "I have no
        // idea what this is". Without it, expected improvement is meaningless
        // and the search degenerates into hill-climbing a fitted surface.
        double[][] points = [[0.5, 0.5], [0.52, 0.48], [0.48, 0.52]];
        var values = points.Select(_ => 1.0).ToList();

        var gp = new GaussianProcess(points, values, lengthScale: 0.15, noise: 1e-6);

        var near = gp.At([0.5, 0.5]).StandardDeviation;
        var middling = gp.At([0.65, 0.65]).StandardDeviation;
        var far = gp.At([0.05, 0.95]).StandardDeviation;

        output.WriteLine($"σ at the cluster {near:E2}, nearby {middling:E3}, far away {far:F3}");

        near.Should().BeLessThan(middling);
        middling.Should().BeLessThan(far);
    }

    [Fact]
    public void The_length_scale_is_fitted_rather_than_guessed()
    {
        // A length scale chosen by hand is a claim about how quickly torque
        // varies with runner length, and nobody knows that in advance. Fitted
        // against a rapidly varying function it should come out SHORT; against
        // a nearly flat one, long.
        var x = Doe.LatinHypercube(1, 24, seed: 4).ToList();

        var wiggly = GaussianProcess.Fit(x, x.Select(p => Math.Sin(18.0 * p[0])).ToList());
        var smooth = GaussianProcess.Fit(x, x.Select(p => 2.0 * p[0]).ToList());

        output.WriteLine($"length scale: wiggly {wiggly.LengthScale:F3}, smooth {smooth.LengthScale:F3}");

        wiggly.LengthScale.Should().BeLessThan(smooth.LengthScale,
            "a function that varies quickly needs a shorter correlation distance, and the marginal likelihood "
            + "is what discovers that without being told");
    }

    [Fact]
    public void Duplicated_observations_do_not_break_the_model()
    {
        // A discrete design space produces duplicated points constantly — two
        // coordinates a hair apart snap to the same tube size — so the kernel
        // matrix routinely has identical rows. Without a noise floor that is
        // exactly singular and the factorisation fails.
        //
        // The constructor clamps the noise to 1e-8 for that reason: it is
        // jitter, the standard remedy, and it is why asking for zero noise
        // here still produces a usable model rather than an exception.
        double[][] duplicated = [[0.4, 0.4], [0.4, 0.4], [0.4, 0.4]];

        var gp = new GaussianProcess(duplicated, [1.0, 1.0, 1.0], lengthScale: 1.0, noise: 0.0);
        gp.Noise.Should().BeGreaterThan(0.0, "the floor is what keeps the matrix factorable");
        gp.At([0.4, 0.4]).Mean.Should().BeApproximately(1.0, 1e-6);

        // Fit survives it too, which is what a real run depends on.
        var fitted = GaussianProcess.Fit(duplicated, [1.0, 1.0, 1.0]);
        fitted.At([0.4, 0.4]).Mean.Should().BeApproximately(1.0, 0.2);

        // The noise floor makes the positive-definite guard in the
        // factorisation UNREACHABLE through this constructor — every attempt
        // to contrive a singular matrix here, including coincident points at
        // an unbounded length scale, still factors once the jitter is added.
        // That is the point of the floor, and it is worth saying rather than
        // asserting a throw that cannot happen: the guard stays as defence in
        // depth against a future kernel or a caller reaching the factorisation
        // another way, not as behaviour anything depends on.
        var coincident = new GaussianProcess(
            [[0.0], [0.0]], [1.0, 2.0], lengthScale: double.PositiveInfinity, noise: 1e-300);

        // Two contradictory observations at one point: the posterior splits
        // the difference, which is what a noise model is for.
        coincident.At([0.0]).Mean.Should().BeApproximately(1.5, 0.5);
    }

    // ---- The optimiser ------------------------------------------------------

    /// <summary>Run both searches on one problem and report how often the model-based one wins.</summary>
    private (int Wins, double BayesMedian, double CmaMedian) Compare(
        Func<IReadOnlyList<double>, double> function, int dimension, int budget,
        double lower = -5.0, double upper = 5.0, int seeds = 12)
    {
        var wins = 0;
        var bayes = new List<double>();
        var cma = new List<double>();

        for (var seed = 0; seed < seeds; seed++)
        {
            var (a, _) = SyntheticProblems.Problem(function, dimension, lower: lower, upper: upper);
            var bayesBest = new BayesianOptimiser(a, seed: 300 + seed).Run(budget).Best.Objectives[0];

            var (b, _) = SyntheticProblems.Problem(function, dimension, lower: lower, upper: upper);
            var cmaBest = new CmaEs(b, b.Space.Centre(), seed: 300 + seed).Run(budget).Best.Objectives[0];

            bayes.Add(bayesBest);
            cma.Add(cmaBest);
            if (bayesBest < cmaBest)
            {
                wins++;
            }
        }

        return (wins, Median(bayes), Median(cma));
    }

    [Fact]
    public void Gate_it_beats_cma_es_per_evaluation_at_the_budget_it_exists_for()
    {
        // The reason this algorithm is in the project at all. At twenty-five to
        // forty evaluations — a few hours of real solving — the model-based
        // search should be ahead of the evolutionary one on the kind of
        // response an engine actually produces: smooth, low-dimensional, and
        // varying by a modest factor across the design space.
        //
        // Asserted as a RATE over seeds, because one run of either proves
        // nothing about a stochastic search.
        (string Name, Func<IReadOnlyList<double>, double> F, int D, double Lo, double Hi)[] cases =
        [
            ("sphere 3-D", SyntheticProblems.Sphere, 3, -5, 5),
            ("sphere 6-D", SyntheticProblems.Sphere, 6, -5, 5),
            ("Rosenbrock 3-D, tight box", SyntheticProblems.Rosenbrock, 3, -2, 2),
            ("Rastrigin 3-D", SyntheticProblems.Rastrigin, 3, -5, 5),
        ];

        output.WriteLine("at 40 evaluations, over 12 seeds each:");
        output.WriteLine("  problem                       BO median    CMA median   BO wins");

        foreach (var (name, f, d, lo, hi) in cases)
        {
            var (wins, bayes, cma) = Compare(f, d, budget: 40, lower: lo, upper: hi);
            output.WriteLine($"  {name,-28} {bayes,10:E2}  {cma,10:E2}   {wins}/12");

            wins.Should().BeGreaterThanOrEqualTo(9,
                $"{name}: at this budget the model-based search should usually be ahead — that is the entire "
                + "reason the plan calls it the right default for expensive evaluations");
            bayes.Should().BeLessThan(cma, $"{name}: the typical result, not just the win count");
        }
    }

    [Fact]
    public void Where_it_does_not_win_is_measured_and_stated_rather_than_avoided()
    {
        // <b>The honest limit of this implementation.</b> Rosenbrock over the
        // wide box spans roughly five orders of magnitude — 0 to about 1e5 —
        // and a stationary Gaussian process with ONE shared length scale
        // cannot model that. The output standardisation is dominated by the
        // extremes, so the near-optimal region the search actually cares about
        // is compressed into numerical noise, and the surrogate stops
        // discriminating exactly where discrimination matters.
        //
        // Measured, not assumed: on the same function over a box a quarter the
        // width — where the range is about 3600 rather than 1e5 — the model
        // wins 10 of 12. The dynamic range is the cause, not the valley.
        //
        // This is kept as a test rather than buried in a comment because it is
        // the condition under which a user should reach for CMA-ES instead, and
        // a limit nobody measured is a limit nobody can rely on.
        var wide = Compare(SyntheticProblems.Rosenbrock, 3, budget: 70, lower: -5, upper: 5);
        var tight = Compare(SyntheticProblems.Rosenbrock, 3, budget: 70, lower: -2, upper: 2);

        output.WriteLine("Rosenbrock 3-D at 70 evaluations:");
        output.WriteLine(
            $"  wide box  [-5, 5]: BO median {wide.BayesMedian:E2}, CMA {wide.CmaMedian:E2}, BO wins {wide.Wins}/12");
        output.WriteLine(
            $"  tight box [-2, 2]: BO median {tight.BayesMedian:E2}, CMA {tight.CmaMedian:E2}, BO wins {tight.Wins}/12");

        wide.Wins.Should().BeLessThan(8,
            "this is the documented weak case; if it starts winning here the surrogate has been improved and "
            + "this test should be revisited rather than deleted");
        tight.Wins.Should().BeGreaterThanOrEqualTo(9,
            "and narrowing the dynamic range restores the advantage, which is what identifies the cause");

        // Real engineering objectives are the tight case, not the wide one:
        // area under a torque curve varies by a few percent across a plausible
        // design space, not by five orders of magnitude.
        tight.Wins.Should().BeGreaterThan(wide.Wins);
    }

    [Fact]
    public void It_converges_on_a_problem_with_a_known_optimum()
    {
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3);
        var result = new BayesianOptimiser(problem, seed: 17).Run(60);

        output.WriteLine(
            $"sphere 3-D in {result.Evaluations} evaluations: {result.Best.Objectives[0]:E3} at "
            + string.Join(", ", result.Best.Design.Values.Select(v => v.ToString("F3"))));

        result.Best.Objectives[0].Should().BeLessThan(0.5,
            "the optimum is 0 at the origin, and sixty evaluations is generous for three variables");
        result.Best.Feasible.Should().BeTrue();
    }

    [Fact]
    public void Expected_improvement_looks_where_the_model_is_ignorant_not_only_where_it_is_hopeful()
    {
        // The mechanism, isolated. A search driven by the posterior MEAN alone
        // converges to whichever basin the initial design landed in; expected
        // improvement is what makes it global. So: cluster every observation
        // in one corner and check the search leaves it.
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Rastrigin, 2);
        var space = problem.Space;

        var seeds = new List<DesignPoint>();
        for (var i = 0; i < 6; i++)
        {
            seeds.Add(space.Point(0.80 + (0.03 * (i % 3)), 0.80 + (0.03 * (i / 3))));
        }

        var result = new BayesianOptimiser(problem, seed: 21).Run(
            maximumEvaluations: 40, initialDesigns: seeds.Count, seedDesigns: seeds);

        // How far the search travelled from the corner it was started in.
        var visited = result.History
            .Select(d => d.Design.Coordinates)
            .Select(c => Math.Sqrt(Math.Pow(c[0] - 0.83, 2.0) + Math.Pow(c[1] - 0.83, 2.0)))
            .ToList();

        output.WriteLine($"furthest sample from the seeded corner: {visited.Max():F3} of a possible ~1.17");
        visited.Max().Should().BeGreaterThan(0.5,
            "a search that only exploited would never leave the corner it was handed");
    }

    [Fact]
    public void A_hard_constraint_is_respected_here_too()
    {
        var constraints = new ConstraintSet(
        [
            StandardConstraints.ValveToPistonClearance(d => d.IntakeRunner.LengthMm, 2.0),
        ]);

        for (var seed = 0; seed < 8; seed++)
        {
            var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3, constraints);
            var result = new BayesianOptimiser(problem, seed: 700 + seed).Run(40);

            result.Best.Feasible.Should().BeTrue($"seed {seed}");
            result.Best.Design.ValueOf("IntakeRunner.LengthMm").Should().BeGreaterThanOrEqualTo(2.0 - 1e-9);
        }
    }

    [Fact]
    public void The_surrogate_is_fitted_to_feasible_observations_only()
    {
        // Fitting it to the scalar score would have it model the lexicographic
        // feasibility band — a step of order 1e308. A Gaussian process asked to
        // interpolate that produces nonsense everywhere, not just at the
        // boundary.
        var constraints = new ConstraintSet(
        [
            StandardConstraints.ValveToPistonClearance(d => d.IntakeRunner.LengthMm, 0.0),
        ]);

        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 2, constraints);
        var search = new BayesianOptimiser(problem, seed: 5);
        var result = search.Run(40);

        result.History.Should().Contain(d => !d.Feasible, "half the box is infeasible, so some were");
        search.Surrogate.Should().NotBeNull();

        // The model's predictions stay in the range of the feasible data — a
        // process that had swallowed a 1e308 observation could not.
        var feasibleCosts = result.History.Where(d => d.Feasible).Select(d => d.Costs[0]).ToList();
        var probe = search.Surrogate!.At([0.5, 0.5]);

        output.WriteLine(
            $"feasible costs span {feasibleCosts.Min():F2}–{feasibleCosts.Max():F2}; "
            + $"surrogate at the centre predicts {probe.Mean:F2} ± {probe.StandardDeviation:F2}");

        probe.Mean.Should().BeInRange(feasibleCosts.Min() - 50.0, feasibleCosts.Max() + 50.0);
    }

    [Fact]
    public void The_search_is_reproducible_to_the_bit()
    {
        var (first, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 3);
        var (second, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 3);

        var a = new BayesianOptimiser(first, seed: 88).Run(30);
        var b = new BayesianOptimiser(second, seed: 88).Run(30);

        a.Best.Objectives[0].Should().Be(b.Best.Objectives[0]);
        a.History.Select(d => d.Design.Key()).Should().Equal(b.History.Select(d => d.Design.Key()));
    }

    [Fact]
    public void Cancelling_returns_the_work_already_done()
    {
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 2);
        using var cancellation = new CancellationTokenSource();

        var count = 0;
        var result = new BayesianOptimiser(problem, seed: 9).Run(
            200,
            progress: new Progress<OptimiserProgress>(_ =>
            {
                if (++count >= 3)
                {
                    cancellation.Cancel();
                }
            }),
            cancellation: cancellation.Token);

        result.History.Should().NotBeEmpty();
        result.Best.Should().NotBeNull();
        output.WriteLine($"{result.Reason} with {result.Evaluations} evaluations kept");
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
