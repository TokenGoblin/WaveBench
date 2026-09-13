using FluentAssertions;
using WaveBench.Optimize;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// Sensitivity screening against functions whose indices are known in closed
/// form (plan §9.4).
///
/// <b>The Ishigami function is the reason this test exists.</b>
/// <c>f = sin x₁ + a·sin²x₂ + b·x₃⁴·sin x₁</c> over U(−π, π)³ has an exact
/// variance decomposition, and its third variable is the perfect trap: x₃ has
/// a first-order index of EXACTLY ZERO — on its own it does nothing — while
/// its total index is about 0.24, because it matters entirely through its
/// interaction with x₁.
///
/// A screening that reported only first-order effects would tell a user to
/// drop x₃, and they would be dropping a quarter of the variance. That
/// distinction is the whole reason the plan asks for total indices as well as
/// first-order ones, and this is where it is demonstrated rather than
/// asserted.
/// </summary>
public class ScreeningTests(ITestOutputHelper output)
{
    private const double A = 7.0;
    private const double B = 0.1;

    private static double Ishigami(IReadOnlyList<double> x) =>
        Math.Sin(x[0]) + (A * Math.Pow(Math.Sin(x[1]), 2.0)) + (B * Math.Pow(x[2], 4.0) * Math.Sin(x[0]));

    /// <summary>The analytic indices, from the standard decomposition.</summary>
    private static (double[] First, double[] Total) IshigamiExact()
    {
        var pi4 = Math.Pow(Math.PI, 4.0);
        var pi8 = Math.Pow(Math.PI, 8.0);

        var v1 = (B * pi4 / 5.0) + (B * B * pi8 / 50.0) + 0.5;
        var v2 = A * A / 8.0;
        var v13 = B * B * pi8 * ((1.0 / 18.0) - (1.0 / 50.0));
        var v = v1 + v2 + v13;

        return ([v1 / v, v2 / v, 0.0], [(v1 + v13) / v, v2 / v, v13 / v]);
    }

    private static OptimisationProblem IshigamiProblem()
    {
        var variables = SyntheticProblems.Paths.Take(3)
            .Select(p => new OptimisationVariable(p, -Math.PI, Math.PI))
            .ToList();

        return new OptimisationProblem(
            SyntheticProblems.Document(),
            new DesignSpace(variables),
            SyntheticProblems.Minimise,
            new SyntheticProblems.Analytic(Ishigami));
    }

    [Fact]
    public void Sobol_indices_match_the_analytic_decomposition()
    {
        var (exactFirst, exactTotal) = IshigamiExact();
        var indices = Screening.SobolIndices(IshigamiProblem(), samples: 4000, seed: 31);

        output.WriteLine("variable        S_i (exact)      S_Ti (exact)");
        foreach (var index in indices)
        {
            var i = SyntheticProblems.Paths.ToList().IndexOf(index.Variable.Path);
            output.WriteLine(
                $"x{i + 1}          {index.FirstOrder:F3} ({exactFirst[i]:F3})     "
                + $"{index.Total:F3} ({exactTotal[i]:F3})");
        }

        foreach (var index in indices)
        {
            var i = SyntheticProblems.Paths.ToList().IndexOf(index.Variable.Path);

            // A Monte Carlo estimator on a few thousand samples; 0.05 is the
            // honest tolerance, not a tuned one.
            index.FirstOrder.Should().BeApproximately(exactFirst[i], 0.05, $"first-order index of x{i + 1}");
            index.Total.Should().BeApproximately(exactTotal[i], 0.05, $"total index of x{i + 1}");
        }
    }

    [Fact]
    public void Gate_a_variable_that_matters_only_through_interaction_is_not_reported_as_negligible()
    {
        // x₃'s first-order index is exactly zero and its total is about 0.24.
        // Screening on first-order effects alone would drop it — and with it a
        // quarter of the variance.
        var indices = Screening.SobolIndices(IshigamiProblem(), samples: 4000, seed: 32);
        var third = indices.Single(s => s.Variable.Path == SyntheticProblems.Paths[2]);

        output.WriteLine(
            $"x3: first-order {third.FirstOrder:F3}, total {third.Total:F3}, "
            + $"interaction share {third.InteractionShare:F3}");

        third.FirstOrder.Should().BeLessThan(0.06, "on its own x3 does nothing");
        third.Total.Should().BeGreaterThan(0.15, "but it carries a quarter of the variance through x1");
        third.InteractionShare.Should().BeGreaterThan(0.15);
        third.Negligible.Should().BeFalse("dropping it would drop a quarter of the variance");

        // ...and the ranking is by TOTAL effect, so it is not sorted last.
        indices.Should().BeInDescendingOrder(s => s.Total);
    }

    [Fact]
    public void Morris_ranks_the_variables_by_how_much_they_move_the_answer()
    {
        // A linear function with known coefficients: the elementary effects
        // ARE the coefficients, so the ranking is exact and the magnitudes are
        // checkable.
        var problem = Linear([1.0, 8.0, 0.25, 3.0]);
        var effects = Screening.Morris(problem, trajectories: 20, seed: 33);

        output.WriteLine("variable                      μ*        σ");
        foreach (var effect in effects)
        {
            output.WriteLine($"{effect.Variable.Path,-28} {effect.MeanAbsoluteEffect,8:F3} {effect.StandardDeviation,8:F3}");
        }

        effects.Select(e => e.Variable.Path).Should().Equal(
            SyntheticProblems.Paths[1], SyntheticProblems.Paths[3],
            SyntheticProblems.Paths[0], SyntheticProblems.Paths[2]);

        // On a linear function the elementary effect is the coefficient
        // exactly, at every point — so σ is zero and there is nothing to
        // interact with.
        effects.Should().OnlyContain(e => e.StandardDeviation < 1e-9,
            "a linear response has the same effect everywhere");
        effects[0].MeanAbsoluteEffect.Should().BeApproximately(8.0 * 2.0 * Math.PI, 1e-6,
            "the effect is per unit of the CUBE, so it carries the variable's own range");
    }

    [Fact]
    public void Morris_flags_a_variable_whose_effect_changes_sign()
    {
        // A runner length either side of its tuned peak does exactly this: it
        // helps, then it hurts. The signed mean averages to nearly nothing
        // while μ* stays large — which is why μ* is the headline and the
        // signed mean sits beside it rather than instead of it.
        var problem = Quadratic();
        var effects = Screening.Morris(problem, trajectories: 30, seed: 34);
        var curved = effects.Single(e => e.Variable.Path == SyntheticProblems.Paths[0]);

        output.WriteLine(
            $"μ* {curved.MeanAbsoluteEffect:F3}, signed mean {curved.MeanEffect:F3}, σ {curved.StandardDeviation:F3}");

        curved.MeanAbsoluteEffect.Should().BeGreaterThan(1.0, "it is the dominant variable");
        Math.Abs(curved.MeanEffect).Should().BeLessThan(curved.MeanAbsoluteEffect,
            "the signed mean cancels because the effect reverses");
        curved.ChangesSign.Should().BeTrue();

        output.WriteLine(Screening.Explain(effects));
        Screening.Explain(effects).Should().Contain("optimum somewhere inside the bounds");

        // A parabola has a large σ with NO interaction whatever — which is why
        // the flag is named for what it measures (the effect varies) rather
        // than for a cause Morris cannot identify. Sobol confirms there is no
        // interaction here at all.
        curved.EffectVaries.Should().BeTrue();

        var indices = Screening.SobolIndices(Quadratic(), samples: 2000, seed: 34);
        var curvedIndex = indices.Single(s => s.Variable.Path == SyntheticProblems.Paths[0]);
        output.WriteLine(
            $"Sobol: S_i {curvedIndex.FirstOrder:F3}, S_Ti {curvedIndex.Total:F3}, "
            + $"interaction share {curvedIndex.InteractionShare:F3}");

        curvedIndex.InteractionShare.Should().BeLessThan(0.05,
            "a bowl in one variable interacts with nothing; the large σ was pure nonlinearity");
    }

    [Fact]
    public void Morris_finds_the_interaction_in_a_product_term()
    {
        // f = x1·x2: neither variable has a fixed effect, because each one's
        // effect IS the other's value. σ above μ* is the signature, and it is
        // the thing that tells a user these two cannot be tuned one at a time.
        var problem = Product();
        var effects = Screening.Morris(problem, trajectories: 30, seed: 35);

        foreach (var effect in effects.Take(2))
        {
            output.WriteLine(
                $"{effect.Variable.Path,-28} μ* {effect.MeanAbsoluteEffect:F3}, σ {effect.StandardDeviation:F3}, "
                + $"varies {effect.EffectVaries}");

            effect.EffectVaries.Should().BeTrue("a product term is pure interaction");
        }

        // Morris cannot separate interaction from nonlinearity, and the
        // explanation says exactly that rather than claiming a finding it
        // cannot support. The variance decomposition is what settles it — so
        // that is what this asserts the interaction with.
        Screening.Explain(effects).Should().Contain("Morris cannot tell those apart");

        var indices = Screening.SobolIndices(Product(), samples: 2000, seed: 35);
        foreach (var index in indices)
        {
            output.WriteLine(
                $"{index.Variable.Path,-28} S_i {index.FirstOrder:F3}, S_Ti {index.Total:F3}, "
                + $"interaction share {index.InteractionShare:F3}");

            index.InteractionShare.Should().BeGreaterThan(0.5,
                "a product really is almost all interaction, and only Sobol can say so");
        }
    }

    [Fact]
    public void Screening_says_which_variables_can_be_held_fixed()
    {
        // The sentence the plan actually asks for: "tell the user which three
        // variables actually matter before spending compute."
        var problem = Linear([10.0, 9.0, 8.0, 0.001]);
        var effects = Screening.Morris(problem, trajectories: 20, seed: 36);
        var explanation = Screening.Explain(effects);

        output.WriteLine(explanation);

        explanation.Should().Contain("can be held fixed");
        explanation.Should().Contain(SyntheticProblems.Paths[3], "the negligible one is named");
        explanation.Should().Contain("4 variables to 3", "and the saving is stated");
    }

    [Fact]
    public void A_flat_objective_reports_zero_rather_than_dividing_by_zero()
    {
        var problem = Linear([0.0, 0.0, 0.0]);

        var indices = Screening.SobolIndices(problem, samples: 64, seed: 37);
        indices.Should().OnlyContain(s => s.FirstOrder == 0.0 && s.Total == 0.0);

        var effects = Screening.Morris(problem, trajectories: 4, seed: 37);
        effects.Should().OnlyContain(e => e.MeanAbsoluteEffect == 0.0);
        Screening.Explain(effects).Should().Contain("No variable moved the objective");
    }

    [Fact]
    public void Screening_is_reproducible()
    {
        var a = Screening.Morris(IshigamiProblem(), trajectories: 8, seed: 38);
        var b = Screening.Morris(IshigamiProblem(), trajectories: 8, seed: 38);

        a.Select(e => e.MeanAbsoluteEffect).Should().Equal(b.Select(e => e.MeanAbsoluteEffect));
    }

    // ---- Problems with known structure ------------------------------------

    private static OptimisationProblem Linear(double[] coefficients)
    {
        var variables = SyntheticProblems.Paths.Take(coefficients.Length)
            .Select(p => new OptimisationVariable(p, -Math.PI, Math.PI))
            .ToList();

        return new OptimisationProblem(
            SyntheticProblems.Document(),
            new DesignSpace(variables),
            SyntheticProblems.Minimise,
            new SyntheticProblems.Analytic(x => x.Zip(coefficients, (v, c) => v * c).Sum()));
    }

    /// <summary>A bowl in the first variable, linear in the second.</summary>
    private static OptimisationProblem Quadratic()
    {
        var variables = SyntheticProblems.Paths.Take(2)
            .Select(p => new OptimisationVariable(p, -Math.PI, Math.PI))
            .ToList();

        return new OptimisationProblem(
            SyntheticProblems.Document(),
            new DesignSpace(variables),
            SyntheticProblems.Minimise,
            new SyntheticProblems.Analytic(x => (x[0] * x[0]) + (0.1 * x[1])));
    }

    private static OptimisationProblem Product()
    {
        var variables = SyntheticProblems.Paths.Take(2)
            .Select(p => new OptimisationVariable(p, -Math.PI, Math.PI))
            .ToList();

        return new OptimisationProblem(
            SyntheticProblems.Document(),
            new DesignSpace(variables),
            SyntheticProblems.Minimise,
            new SyntheticProblems.Analytic(x => x[0] * x[1]));
    }
}
