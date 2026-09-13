using FluentAssertions;
using WaveBench.Model;
using WaveBench.Optimize;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// NSGA-II against fronts whose exact shape is known in closed form.
///
/// The ZDT family (Zitzler, Deb &amp; Thiele, <i>Evolutionary Computation</i>
/// 8(2):173–195, 2000) is the standard benchmark set for exactly this, and
/// each member is built to break a different thing:
///
/// <list type="bullet">
/// <item><b>ZDT1</b> — a convex front at g = 1, f₂ = 1 − √f₁. Checks the front
/// is found at all and is spread along its length.</item>
/// <item><b>ZDT2</b> — the same problem with a CONCAVE front, f₂ = 1 − f₁².
/// A weighted-sum method cannot reach the middle of a concave front at any
/// weighting, which is the concrete reason the plan asks for a front rather
/// than a scalarisation.</item>
/// </list>
/// </summary>
public class NsgaIITests(ITestOutputHelper output)
{
    private const string F1Key = "f1";
    private const string F2Key = "f2";

    /// <summary>Two objectives read straight off the evaluator's metrics, both minimised.</summary>
    private static ObjectiveSet TwoObjectives { get; } = new(
    [
        new MetricObjective("f1", "", F1Key, ObjectiveSense.Minimise),
        new MetricObjective("f2", "", F2Key, ObjectiveSense.Minimise),
    ]);

    private sealed class Zdt(Func<IReadOnlyList<double>, (double F1, double F2)> function) : IDesignEvaluator
    {
        public IReadOnlyList<EvaluationFidelity> Fidelities => [EvaluationFidelity.Solved];

        public DesignEvaluation Evaluate(
            DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default)
        {
            var (f1, f2) = function(design.Values);
            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    [F1Key] = f1,
                    [F2Key] = f2,
                },
            };
        }
    }

    /// <summary>ZDT1: convex front at f₂ = 1 − √f₁, reached when every xᵢ (i &gt; 0) is zero.</summary>
    private static (double, double) Zdt1(IReadOnlyList<double> x)
    {
        var f1 = x[0];
        var g = 1.0 + (9.0 * x.Skip(1).Sum() / (x.Count - 1));
        return (f1, g * (1.0 - Math.Sqrt(f1 / g)));
    }

    /// <summary>ZDT2: concave front at f₂ = 1 − f₁².</summary>
    private static (double, double) Zdt2(IReadOnlyList<double> x)
    {
        var f1 = x[0];
        var g = 1.0 + (9.0 * x.Skip(1).Sum() / (x.Count - 1));
        return (f1, g * (1.0 - Math.Pow(f1 / g, 2.0)));
    }

    private static OptimisationProblem Problem(Func<IReadOnlyList<double>, (double, double)> function, int dimension)
    {
        // ZDT is defined on [0, 1]^n, so the variables span exactly that.
        var variables = SyntheticProblems.Paths.Take(dimension)
            .Select(p => new OptimisationVariable(p, 0.0, 1.0))
            .ToList();

        return new OptimisationProblem(
            SyntheticProblems.Document(), new DesignSpace(variables), TwoObjectives, new Zdt(function));
    }

    [Fact]
    public void Gate_it_finds_a_convex_front_and_spreads_along_it()
    {
        var problem = Problem(Zdt1, dimension: 4);
        var result = new NsgaII(problem, populationSize: 60, seed: 21).Run(12_000);

        var front = result.Front.OrderBy(d => d.Objectives[0]).ToList();
        output.WriteLine($"{front.Count} designs on the front after {result.Evaluations} evaluations");

        front.Should().HaveCountGreaterThan(20, "a front of a handful of points is not explorable");

        // Every returned design must sit essentially on the true front.
        // f₂ = 1 − √f₁ is the exact solution; distance from it is the error.
        var errors = front
            .Where(d => double.IsFinite(d.Objectives[0]) && double.IsFinite(d.Objectives[1]))
            .Select(d => Math.Abs(d.Objectives[1] - (1.0 - Math.Sqrt(d.Objectives[0]))))
            .ToList();

        output.WriteLine($"distance from the true front: median {Median(errors):E2}, worst {errors.Max():E2}");
        Median(errors).Should().BeLessThan(0.02, "the front has to be the real one, not a front-shaped cloud");

        // ...and it must SPREAD. A front clustered in one corner is a front
        // with no trade on it, which is the failure crowding distance exists
        // to prevent.
        var f1 = front.Select(d => d.Objectives[0]).ToList();
        (f1.Max() - f1.Min()).Should().BeGreaterThan(0.8,
            "the front should span most of f1's range, or the user cannot see the trade");

        // No two designs stacked on top of each other.
        var gaps = f1.Zip(f1.Skip(1), (a, b) => b - a).ToList();
        gaps.Max().Should().BeLessThan(0.25, "a large gap is a stretch of the trade the user cannot explore");
    }

    [Fact]
    public void Gate_it_finds_a_concave_front_which_no_weighted_sum_can_reach()
    {
        // The concrete reason plan §9.6 asks for fronts rather than weights:
        // on a concave front, minimising w·f₁ + (1−w)·f₂ returns one of the two
        // ENDS for every w in [0, 1]. The middle of the trade — which is where
        // the interesting designs live — is unreachable by scalarisation at
        // any weighting.
        var problem = Problem(Zdt2, dimension: 4);
        var result = new NsgaII(problem, populationSize: 60, seed: 22).Run(12_000);

        var front = result.Front.OrderBy(d => d.Objectives[0]).ToList();
        var errors = front
            .Where(d => double.IsFinite(d.Objectives[0]) && double.IsFinite(d.Objectives[1]))
            .Select(d => Math.Abs(d.Objectives[1] - (1.0 - Math.Pow(d.Objectives[0], 2.0))))
            .ToList();

        output.WriteLine($"{front.Count} on the concave front; error median {Median(errors):E2}");
        Median(errors).Should().BeLessThan(0.03);

        // The middle is populated — the part a weighted sum would skip.
        var middle = front.Count(d => d.Objectives[0] is > 0.3 and < 0.7);
        output.WriteLine($"{middle} designs in the middle third of the trade");
        middle.Should().BeGreaterThan(5,
            "the interior of a concave front is exactly what a weighted sum cannot return, and the reason "
            + "this algorithm is here");
    }

    [Fact]
    public void Domination_puts_feasibility_before_every_objective()
    {
        // Deb's constrained domination, stated directly: a feasible design
        // dominates an infeasible one whatever the objectives say. This is
        // what keeps "never violated in a returned design" true on the
        // multi-objective path too.
        var constraints = new ConstraintSet(
        [
            new SolvedLimit("Clearance", e => e.Design.ValueOf(SyntheticProblems.Paths[1]), 0.5, MustNotExceed: false),
        ]);

        var variables = SyntheticProblems.Paths.Take(3)
            .Select(p => new OptimisationVariable(p, 0.0, 1.0))
            .ToList();

        var problem = new OptimisationProblem(
            SyntheticProblems.Document(), new DesignSpace(variables), TwoObjectives, new Zdt(Zdt1), constraints);

        var result = new NsgaII(problem, populationSize: 40, seed: 23).Run(4000);

        result.Front.Should().NotBeEmpty();
        result.Front.Should().OnlyContain(d => d.Feasible, "the front is drawn from the feasible set only");
        result.Front.Should().OnlyContain(d => d.Design.ValueOf(SyntheticProblems.Paths[1]) >= 0.5);

        output.WriteLine(
            $"{result.Front.Count} feasible front designs out of {result.Evaluations} evaluated; "
            + $"{result.History.Count(d => !d.Feasible)} infeasible were explored and none returned");
    }

    [Fact]
    public void A_front_reports_how_much_trade_there_actually_is()
    {
        var problem = Problem(Zdt1, dimension: 3);
        var result = new NsgaII(problem, populationSize: 40, seed: 24).Run(4000);

        var extent = result.Extent(TwoObjectives);
        output.WriteLine($"f1 spans {extent[0].Min:F3}–{extent[0].Max:F3}, f2 spans {extent[1].Min:F3}–{extent[1].Max:F3}");

        extent.Should().HaveCount(2);
        (extent[0].Max - extent[0].Min).Should().BeGreaterThan(0.5);
        (extent[1].Max - extent[1].Min).Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void The_search_is_reproducible_to_the_bit()
    {
        var a = new NsgaII(Problem(Zdt1, 3), populationSize: 20, seed: 99).Run(600);
        var b = new NsgaII(Problem(Zdt1, 3), populationSize: 20, seed: 99).Run(600);

        a.Evaluations.Should().Be(b.Evaluations);
        a.Front.Select(d => d.Design.Key()).Should().Equal(b.Front.Select(d => d.Design.Key()));
    }

    [Fact]
    public void Ranking_marks_the_extremes_as_never_discardable()
    {
        // Crowding distance is infinite at both ends of every objective. If it
        // were not, the front would shrink toward its middle one generation at
        // a time and the user would lose precisely the two designs that define
        // the trade.
        var problem = Problem(Zdt1, 2);
        var space = problem.Space;

        var population = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }
            .Select(u => problem.Score(space.Point(u, 0.0)))
            .ToList();

        var ranked = NsgaII.Rank(population);
        ranked.Should().OnlyContain(r => r.Rank == 0, "these all sit on the true front, so none dominates another");

        var byF1 = ranked.OrderBy(r => r.Design.Objectives[0]).ToList();
        double.IsPositiveInfinity(byF1[0].Crowding).Should().BeTrue();
        double.IsPositiveInfinity(byF1[^1].Crowding).Should().BeTrue();
        byF1.Skip(1).Take(3).Should().OnlyContain(r => double.IsFinite(r.Crowding) && r.Crowding > 0);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? double.NaN : sorted[sorted.Count / 2];
    }
}
