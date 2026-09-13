using FluentAssertions;
using WaveBench.Optimize;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// Phase 22 gate, first clause: <i>"on a synthetic problem with a known
/// optimum the optimiser converges reliably"</i>.
///
/// <b>Reliably is the word being tested.</b> One seed converging proves
/// nothing about a stochastic search — it is a coin that came up heads. Every
/// test here runs many independent seeds and asserts a success RATE, which is
/// the only claim a randomised algorithm can actually support.
/// </summary>
public class CmaEsTests(ITestOutputHelper output)
{
    /// <summary>
    /// Run one function over many seeds and report how often the search got
    /// within <paramref name="tolerance"/> of the known optimum.
    /// </summary>
    private (int Successes, double MedianBest, double WorstBest) Sweep(
        Func<IReadOnlyList<double>, double> function,
        int dimension,
        double tolerance,
        int seeds = 20,
        int budget = 3000,
        double sigma = 0.3,
        double lower = -5.0,
        double upper = 5.0)
    {
        var bests = new List<double>();

        for (var seed = 0; seed < seeds; seed++)
        {
            var (problem, _) = SyntheticProblems.Problem(function, dimension, lower: lower, upper: upper);

            // Start from the centre of the box every time. Seeding from a
            // random point would let a lucky start pass for convergence.
            var search = new CmaEs(problem, problem.Space.Centre(), sigma, seed: 1000 + seed);
            var result = search.Run(budget);

            bests.Add(result.Best.Objectives[0]);
        }

        bests.Sort();
        var median = bests[bests.Count / 2];
        return (bests.Count(b => b <= tolerance), median, bests[^1]);
    }

    [Fact]
    public void Gate_it_converges_on_a_convex_problem_from_every_seed()
    {
        // Sphere is separable and convex. Failing here would mean something
        // structural is wrong, so the bar is every seed, not most.
        var (successes, median, worst) = Sweep(SyntheticProblems.Sphere, dimension: 4, tolerance: 1e-8);

        output.WriteLine($"sphere, 4-D: {successes}/20 within 1e-8, median {median:E2}, worst {worst:E2}");
        successes.Should().Be(20, "a convex separable bowl must be solved from every start");
    }

    [Fact]
    public void Gate_it_converges_along_a_curved_valley_that_lies_on_no_axis()
    {
        // Rosenbrock's valley is the reason CMA-ES is the default here rather
        // than a per-axis strategy: the search has to learn the valley's
        // orientation from its own population. A method that cannot will sit
        // at a loss of order 1 forever rather than reaching 1e-6.
        var (successes, median, worst) = Sweep(
            SyntheticProblems.Rosenbrock, dimension: 4, tolerance: 1e-6, budget: 8000);

        output.WriteLine($"rosenbrock, 4-D: {successes}/20 within 1e-6, median {median:E2}, worst {worst:E2}");
        successes.Should().BeGreaterThanOrEqualTo(18,
            "the covariance adaptation is what makes this tractable; near-total success is the expectation");
    }

    [Fact]
    public void Gate_it_escapes_local_minima_on_a_multimodal_problem()
    {
        // Rastrigin in 2-D has about a hundred local minima inside the box.
        // A local method lands in whichever one it starts nearest, so a high
        // success rate here is evidence the search is genuinely global.
        //
        // A larger population than the default: Hansen's own advice for a
        // multimodal problem, and the honest way to pass this rather than
        // tuning the tolerance until the default looks good.
        var bests = new List<double>();
        for (var seed = 0; seed < 20; seed++)
        {
            var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Rastrigin, dimension: 2);
            var search = new CmaEs(problem, problem.Space.Centre(), sigma: 2.0, populationSize: 40, seed: 500 + seed);
            bests.Add(search.Run(6000).Best.Objectives[0]);
        }

        bests.Sort();
        var successes = bests.Count(b => b <= 1e-6);
        output.WriteLine(
            $"rastrigin, 2-D: {successes}/20 reached the global optimum, median {bests[10]:E2}, worst {bests[^1]:E2}");

        successes.Should().BeGreaterThanOrEqualTo(14,
            "a global search should find the global basin most of the time; a local one would score near zero");
    }

    [Fact]
    public void The_search_is_reproducible_to_the_bit()
    {
        // Plan Part 0: same input, bit-identical result. An optimisation that
        // cannot be re-run to the same answer cannot be defended in a report,
        // and "the optimiser found this" stops meaning anything.
        var (first, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 3);
        var (second, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 3);

        var a = new CmaEs(first, first.Space.Centre(), seed: 42).Run(400);
        var b = new CmaEs(second, second.Space.Centre(), seed: 42).Run(400);

        a.Evaluations.Should().Be(b.Evaluations);
        a.Best.Objectives[0].Should().Be(b.Best.Objectives[0]);
        a.Best.Design.Values.Should().Equal(b.Best.Design.Values);

        // ...and a different seed genuinely explores differently, or the
        // "reliably over many seeds" claim above would be one run repeated.
        var c = new CmaEs(first, first.Space.Centre(), seed: 43).Run(400);
        c.History.Select(d => d.Design.Key()).Should().NotEqual(a.History.Select(d => d.Design.Key()));
    }

    [Fact]
    public void It_reports_why_it_stopped_and_stops_early_when_converged()
    {
        var (problem, evaluator) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3);
        var result = new CmaEs(problem, problem.Space.Centre(), seed: 7).Run(maximumEvaluations: 5000);

        output.WriteLine($"{result.Reason} after {result.Evaluations} evaluations in {result.Iterations} generations");

        result.Reason.Should().Contain("converged");
        result.Evaluations.Should().BeLessThan(5000, "a converged search must stop rather than burn its budget");
        evaluator.Calls.Should().Be(result.Evaluations, "every scored design is one evaluation and no more");
    }

    [Fact]
    public void Progress_is_reported_once_per_generation_and_never_goes_backwards()
    {
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3);
        var seen = new List<OptimiserProgress>();

        var result = new CmaEs(problem, problem.Space.Centre(), seed: 11)
            .Run(600, progress: new Progress<OptimiserProgress>(seen.Add));

        // Progress<T> posts asynchronously, so allow for the tail not having
        // arrived; what matters is that what DID arrive is monotone.
        seen.Should().NotBeEmpty();
        seen.Select(p => p.Best.ScalarScore).Should().BeInDescendingOrder(
            "the running best can only improve");
        seen.Select(p => p.Iteration).Should().BeInAscendingOrder();
        result.Iterations.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Cancelling_returns_the_work_already_done_rather_than_throwing()
    {
        // Plan §8.3: switching workspaces never cancels a job, but a user
        // pressing stop must get their partial result — an optimisation that
        // threw away an hour of evaluations on cancel would be unusable.
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3);
        using var cancellation = new CancellationTokenSource();

        var count = 0;
        var progress = new Progress<OptimiserProgress>(_ => { });
        _ = progress;

        var search = new CmaEs(problem, problem.Space.Centre(), seed: 3);
        var result = search.Run(
            2000,
            progress: new Progress<OptimiserProgress>(p =>
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
}
