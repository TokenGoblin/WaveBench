using FluentAssertions;
using WaveBench.Optimize;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// Nelder–Mead and Powell (plan §9.4).
///
/// <b>The claim being tested is that they FINISH what a global search
/// started.</b> Neither is a global method and neither is offered as one: a
/// global search decides which basin to be in and stops when its own progress
/// measure collapses, which is not the same as "there is nothing better within
/// a millimetre". So the headline test hands each one a stopped CMA-ES result
/// and asks whether a small extra budget improves it.
/// </summary>
public class LocalSearchTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate_they_improve_on_a_global_search_that_has_already_stopped()
    {
        // A deliberately tight budget for the global search, so it stops while
        // there is still something on the table — which is the situation a
        // refiner exists for, and the situation every real run is in when the
        // evaluations cost minutes each.
        var improvedByNelderMead = 0;
        var improvedByPowell = 0;
        var nelderGains = new List<double>();
        var powellGains = new List<double>();

        for (var seed = 0; seed < 10; seed++)
        {
            var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 3);
            var global = new CmaEs(problem, problem.Space.Centre(), seed: 400 + seed).Run(60);
            var before = global.Best.Objectives[0];

            var nelder = LocalSearch.NelderMead(problem, global.Best.Design, maximumEvaluations: 60);
            var powell = LocalSearch.Powell(problem, global.Best.Design, maximumEvaluations: 60);

            nelderGains.Add(before - nelder.Best.Objectives[0]);
            powellGains.Add(before - powell.Best.Objectives[0]);

            if (nelder.Best.Objectives[0] < before)
            {
                improvedByNelderMead++;
            }

            if (powell.Best.Objectives[0] < before)
            {
                improvedByPowell++;
            }
        }

        output.WriteLine("Rosenbrock 3-D, CMA-ES stopped at 60 evaluations, then 60 more of refinement:");
        output.WriteLine($"  Nelder–Mead improved {improvedByNelderMead}/10, median gain {Median(nelderGains):E2}");
        output.WriteLine($"  Powell      improved {improvedByPowell}/10, median gain {Median(powellGains):E2}");

        improvedByNelderMead.Should().BeGreaterThanOrEqualTo(8,
            "a refiner that rarely improves on a stopped search is not earning its evaluations");
        improvedByPowell.Should().BeGreaterThanOrEqualTo(8);

        // And never WORSE — both return the best design they saw, which
        // includes the one they were handed.
        nelderGains.Should().OnlyContain(g => g >= -1e-12, "refinement must not lose ground");
        powellGains.Should().OnlyContain(g => g >= -1e-12);
    }

    [Fact]
    public void Nelder_mead_bottoms_out_a_smooth_bowl_from_nearby()
    {
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3);
        var space = problem.Space;

        // Start off-optimum but in the right basin — a refiner's job.
        var start = space.Point(
            space.Variables[0].Normalise(0.4),
            space.Variables[1].Normalise(-0.3),
            space.Variables[2].Normalise(0.25));

        var result = LocalSearch.NelderMead(problem, start, maximumEvaluations: 400, step: 0.02);

        output.WriteLine(
            $"{result.Reason} after {result.Evaluations} evaluations; {result.Best.Objectives[0]:E3} at "
            + string.Join(", ", result.Best.Design.Values.Select(v => v.ToString("F4"))));

        result.Best.Objectives[0].Should().BeLessThan(1e-4);
        result.Reason.Should().Contain("converged", "a refiner must stop rather than burn its budget");
    }

    [Fact]
    public void Powell_follows_a_valley_that_lies_along_no_axis()
    {
        // The reason Powell is here rather than plain coordinate descent: the
        // direction replacement aligns the search with the valley. Rosenbrock's
        // valley is the standard demonstration, and coordinate descent famously
        // crawls along it.
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 2);
        var space = problem.Space;

        var start = space.Point(space.Variables[0].Normalise(-1.2), space.Variables[1].Normalise(1.0));
        var before = problem.Score(start).Objectives[0];

        var result = LocalSearch.Powell(problem, start, maximumEvaluations: 600);

        output.WriteLine(
            $"Rosenbrock 2-D from the classic start: {before:F2} → {result.Best.Objectives[0]:E3} "
            + $"in {result.Evaluations} evaluations ({result.Reason})");

        result.Best.Objectives[0].Should().BeLessThan(before / 100.0,
            "following the valley should gain orders of magnitude, not percent");
    }

    [Fact]
    public void A_refiner_respects_hard_constraints()
    {
        var constraints = new ConstraintSet(
        [
            StandardConstraints.ValveToPistonClearance(d => d.IntakeRunner.LengthMm, 2.0),
        ]);

        for (var seed = 0; seed < 6; seed++)
        {
            var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3, constraints);
            var global = new CmaEs(problem, problem.Space.Centre(), seed: 800 + seed).Run(200);

            foreach (var result in new[]
            {
                LocalSearch.NelderMead(problem, global.Best.Design, maximumEvaluations: 60),
                LocalSearch.Powell(problem, global.Best.Design, maximumEvaluations: 60),
            })
            {
                result.Best.Feasible.Should().BeTrue($"seed {seed}");
                result.Best.Design.ValueOf("IntakeRunner.LengthMm").Should().BeGreaterThanOrEqualTo(2.0 - 1e-9);
            }
        }
    }

    [Fact]
    public void A_start_against_a_bound_still_produces_a_usable_simplex()
    {
        // A refiner is most often handed a design sitting ON a bound — that is
        // where a constrained optimum lives. Offsetting every vertex outward
        // from there would clamp them all back onto the start, leaving a
        // degenerate simplex that cannot move.
        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3);
        var space = problem.Space;

        // The corner of the cube: every variable at its maximum.
        var corner = space.Point(1.0, 1.0, 1.0);
        var result = LocalSearch.NelderMead(problem, corner, maximumEvaluations: 200);

        output.WriteLine(
            $"from the corner: {problem.Score(corner).Objectives[0]:F2} → {result.Best.Objectives[0]:F2}");

        result.Best.Objectives[0].Should().BeLessThan(problem.Score(corner).Objectives[0],
            "a simplex built at a bound must still be able to move inward");
    }

    [Fact]
    public void Both_are_reproducible_and_cancellable()
    {
        var (a, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 3);
        var (b, _) = SyntheticProblems.Problem(SyntheticProblems.Rosenbrock, 3);

        var first = LocalSearch.NelderMead(a, a.Space.Centre(), maximumEvaluations: 120);
        var second = LocalSearch.NelderMead(b, b.Space.Centre(), maximumEvaluations: 120);

        first.Best.Objectives[0].Should().Be(second.Best.Objectives[0],
            "these methods are deterministic, so there is no seed to vary — the same start must give the same "
            + "answer every time");

        // Cancellation keeps the work.
        var (c, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3);
        using var cancellation = new CancellationTokenSource();
        var count = 0;

        var cancelled = LocalSearch.Powell(
            c,
            c.Space.Centre(),
            maximumEvaluations: 5000,
            progress: new SyntheticProblems.Immediate<OptimiserProgress>(_ =>
            {
                if (++count >= 2)
                {
                    cancellation.Cancel();
                }
            }),
            cancellation: cancellation.Token);

        cancelled.History.Should().NotBeEmpty();
        cancelled.Evaluations.Should().BeLessThan(5000);
        output.WriteLine($"{cancelled.Reason} with {cancelled.Evaluations} evaluations kept");
    }

    [Fact]
    public void Neither_returns_a_design_worse_than_the_one_it_was_given()
    {
        // The contract a refiner has to honour: it may fail to improve, but it
        // must never hand back something worse than its start. A local method
        // that can regress turns "polish the answer" into a gamble.
        for (var seed = 0; seed < 8; seed++)
        {
            var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Rastrigin, 3);
            var start = new DesignPoint(
                problem.Space,
                Doe.LatinHypercube(3, 8, seed)[seed % 8]);

            var before = problem.Score(start).ScalarScore;

            LocalSearch.NelderMead(problem, start, maximumEvaluations: 50).Best.ScalarScore
                .Should().BeLessThanOrEqualTo(before, $"Nelder–Mead, seed {seed}");
            LocalSearch.Powell(problem, start, maximumEvaluations: 50).Best.ScalarScore
                .Should().BeLessThanOrEqualTo(before, $"Powell, seed {seed}");
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
