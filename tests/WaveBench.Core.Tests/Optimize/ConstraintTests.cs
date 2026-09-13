using FluentAssertions;
using WaveBench.Model;
using WaveBench.Optimize;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.Optimize;

/// <summary>
/// Phase 22 gate, third clause: <i>"the clearance constraint is never violated
/// in a returned design"</i>.
///
/// Never, not rarely — so the property is established by construction rather
/// than by a penalty weight that happens to be big enough today. A feasible
/// design always scores below every infeasible one
/// (<see cref="ScoredDesign.ScalarScore"/>), and the returned best is taken
/// from the feasible set when one is non-empty.
/// </summary>
public class ConstraintTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate_a_hard_constraint_is_never_violated_in_a_returned_design()
    {
        // The unconstrained optimum of the sphere is the origin. The
        // constraint demands the first variable stay at or above +2, so the
        // best feasible design sits ON the bound — the hardest case, and the
        // one a penalty method gets wrong by paying the penalty for a better
        // objective.
        const double bound = 2.0;

        var constraints = new ConstraintSet(
        [
            StandardConstraints.ValveToPistonClearance(d => d.IntakeRunner.LengthMm, bound),
        ]);

        var violations = 0;
        var onTheBound = 0;

        for (var seed = 0; seed < 30; seed++)
        {
            var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 3, constraints);
            var result = new CmaEs(problem, problem.Space.Centre(), seed: 900 + seed).Run(2000);

            result.Best.Feasible.Should().BeTrue(
                $"seed {seed}: a returned design must satisfy every hard constraint");

            var clearance = result.Best.Design.ValueOf("IntakeRunner.LengthMm");
            if (clearance < bound)
            {
                violations++;
            }

            if (Math.Abs(clearance - bound) < 0.05)
            {
                onTheBound++;
            }
        }

        output.WriteLine($"{violations} violations in 30 runs; {onTheBound} landed on the bound");

        violations.Should().Be(0, "the gate says never");
        onTheBound.Should().BeGreaterThan(20,
            "the constrained optimum IS the bound, so a search that never reaches it is not optimising — "
            + "it is being scared off by the penalty");
    }

    [Fact]
    public void A_design_that_fails_geometry_is_rejected_before_an_evaluation_is_spent()
    {
        // Plan §9.5's whole cost argument: on a tightly packaged problem most
        // of what a search proposes is out of the box, and solving those spends
        // the budget learning what a bounding box already knew.
        var constraints = new ConstraintSet(
        [
            StandardConstraints.MaximumLength(d => d.IntakeRunner.LengthMm, limitMm: -4.0),
        ]);

        var (problem, evaluator) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 2, constraints);
        var result = new CmaEs(problem, problem.Space.Centre(), seed: 5).Run(300);

        output.WriteLine(
            $"{result.Evaluations} designs scored, {evaluator.Calls} actually evaluated, "
            + $"{problem.RejectedOnGeometry} rejected on geometry");

        problem.RejectedOnGeometry.Should().BeGreaterThan(0);
        evaluator.Calls.Should().BeLessThan(result.Evaluations,
            "a design rejected on geometry must not reach the evaluator");
        evaluator.Calls.Should().Be(result.Evaluations - problem.RejectedOnGeometry);
    }

    [Fact]
    public void An_unmeasurable_hard_limit_counts_as_violated_never_as_met()
    {
        // The worst possible failure mode for a hard limit is passing because
        // nobody measured it. A constraint whose quantity comes back NaN is
        // infeasible, loudly.
        var constraint = new SolvedLimit(
            "Knock margin", _ => double.NaN, 0.8, MustNotExceed: true);

        var check = constraint.Check(SyntheticProblems.Document(), new DesignEvaluation
        {
            Design = SyntheticProblems.Space(2).Centre(),
            Fidelity = EvaluationFidelity.Solved,
        });

        check.Satisfied.Should().BeFalse();
        check.Message.Should().Contain("counts as violated");

        // ...and so does one that was never evaluated at all.
        constraint.Check(SyntheticProblems.Document(), null).Satisfied.Should().BeFalse();
    }

    [Fact]
    public void Violation_is_graded_so_an_infeasible_start_still_has_a_way_back()
    {
        // A boolean constraint makes the whole infeasible region flat, and a
        // search that starts there — which it will, on a tight limit — has
        // nothing to follow. Grading is what gives it a gradient.
        var constraint = StandardConstraints.ValveToPistonClearance(d => d.IntakeRunner.LengthMm, 2.0);
        var document = SyntheticProblems.Document();

        document.IntakeRunner.LengthMm = 1.9;
        var slightly = constraint.Check(document, null);

        document.IntakeRunner.LengthMm = -4.0;
        var badly = constraint.Check(document, null);

        slightly.Satisfied.Should().BeFalse();
        badly.Satisfied.Should().BeFalse();
        badly.Violation.Should().BeGreaterThan(slightly.Violation,
            "further outside must score worse, or the search cannot tell which way is out");

        document.IntakeRunner.LengthMm = 2.5;
        var fine = constraint.Check(document, null);
        fine.Satisfied.Should().BeTrue();
        fine.Violation.Should().Be(0.0, "a satisfied constraint contributes nothing at all");
    }

    [Fact]
    public void Feasible_always_outranks_infeasible_however_good_the_objective()
    {
        // The property the gate rests on, stated directly. A design with a
        // spectacular objective and a broken constraint must still lose to a
        // mediocre feasible one.
        //
        // A SOLVED limit rather than a geometric one, deliberately: a
        // geometric constraint short-circuits the evaluation entirely (which
        // is the point of it, and is covered above), so the rejected design
        // would have no objective to be better at. This is the harder case —
        // the design was measured, it really is better, and it must still
        // lose.
        var constraints = new ConstraintSet(
        [
            new SolvedLimit(
                "Valve-to-piston clearance",
                e => e.Design.ValueOf("IntakeRunner.LengthMm"),
                2.0,
                MustNotExceed: false,
                " mm"),
        ]);

        var (problem, _) = SyntheticProblems.Problem(SyntheticProblems.Sphere, 2, constraints);
        var space = problem.Space;

        // The unconstrained optimum: objective 0, clearance -5 → infeasible.
        var brilliant = problem.Score(space.Point(space.Variables[0].Normalise(0.0), space.Variables[1].Normalise(0.0)));

        // A poor design that nevertheless clears: objective 29, feasible.
        var mediocre = problem.Score(space.Point(space.Variables[0].Normalise(5.0), space.Variables[1].Normalise(2.0)));

        output.WriteLine($"brilliant: objective {brilliant.Objectives[0]:F3}, feasible {brilliant.Feasible}");
        output.WriteLine($"mediocre : objective {mediocre.Objectives[0]:F3}, feasible {mediocre.Feasible}");

        brilliant.Feasible.Should().BeFalse();
        mediocre.Feasible.Should().BeTrue();
        brilliant.Objectives[0].Should().BeLessThan(mediocre.Objectives[0], "it really is the better objective");
        mediocre.ScalarScore.Should().BeLessThan(brilliant.ScalarScore, "and it must still lose");
    }

    [Fact]
    public void A_broken_constraint_says_what_is_wrong_in_the_users_own_units()
    {
        var constraint = StandardConstraints.ValveToPistonClearance(d => d.IntakeRunner.LengthMm, 1.5);
        var document = SyntheticProblems.Document();
        document.IntakeRunner.LengthMm = 0.8;

        var check = constraint.Check(document, null);

        output.WriteLine(check.Message);
        check.Message.Should().Contain("0.80").And.Contain("1.50").And.Contain("mm");
        check.Name.Should().Be("Valve-to-piston clearance");
    }

    [Fact]
    public void Model_validity_is_itself_a_constraint()
    {
        // The optimiser can reach geometries the document's own validator
        // rejects. Those are not designs, and they must not be returned.
        var constraint = StandardConstraints.ModelIsValid();
        var document = SyntheticProblems.Document();

        constraint.Check(document, null).Satisfied.Should().BeTrue();

        document.Engine.CompressionRatio = 40.0;
        var broken = constraint.Check(document, null);
        broken.Satisfied.Should().BeFalse();
        output.WriteLine(broken.Message);
    }

    [Fact]
    public void Stock_sizes_are_enforced_by_the_variable_not_by_hope()
    {
        // Plan §9.1: "an optional discrete step (e.g. tube sizes actually
        // available)". A continuous answer of 41.7 mm is not an answer if
        // nobody sells 41.7 mm tube.
        double[] stock = [38.0, 42.0, 45.0, 51.0];

        var variable = new OptimisationVariable("ExhaustRunner.DiameterMm", 38.0, 51.0) { Choices = stock };
        var space = new DesignSpace([variable]);

        // Every coordinate in the cube lands on a stocked size.
        for (var i = 0; i <= 100; i++)
        {
            var value = space.Point(i / 100.0).ValueOf("ExhaustRunner.DiameterMm");
            stock.Should().Contain(value, $"coordinate {i / 100.0:F2} produced {value}");
        }

        // ...and a round trip through Normalise returns the same size.
        foreach (var size in stock)
        {
            space.Point(variable.Normalise(size)).ValueOf("ExhaustRunner.DiameterMm").Should().Be(size);
        }
    }

    [Fact]
    public void The_cache_makes_a_repeated_discrete_design_free()
    {
        // Plan §9.5: "a content-addressed cache keyed on a hash of the
        // resolved model so repeated designs are free". On a discrete space
        // two nearby coordinates snap to the same design constantly, and
        // keying on coordinates rather than on the snapped values would
        // re-solve every one of them.
        var evaluator = new SyntheticProblems.Analytic(SyntheticProblems.Sphere);
        var cache = new EvaluationCache(evaluator);

        var variable = new OptimisationVariable("IntakeRunner.LengthMm", 0, 10) { Choices = [2.0, 4.0, 6.0] };
        var space = new DesignSpace([variable]);

        // Twenty distinct coordinates over three distinct designs.
        for (var i = 0; i < 20; i++)
        {
            cache.Evaluate(space.Point(i / 20.0), EvaluationFidelity.Solved);
        }

        output.WriteLine($"{evaluator.Calls} evaluations for 20 lookups over 3 designs; {cache.Hits} cache hits");

        evaluator.Calls.Should().Be(3, "three distinct snapped designs is three solves");
        cache.Hits.Should().Be(17);
        cache.Count.Should().Be(3);
    }
}
