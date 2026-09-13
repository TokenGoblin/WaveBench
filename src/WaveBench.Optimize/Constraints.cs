using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Optimize;

/// <summary>
/// How far a design is from satisfying a constraint.
/// </summary>
/// <param name="Name">What was checked.</param>
/// <param name="Violation">
/// Zero when satisfied, positive and GRADED by how badly it is broken.
///
/// Graded rather than boolean because a search needs a gradient out of an
/// infeasible region. A constraint that answers only yes/no makes the whole
/// infeasible side flat, and an optimiser that starts there — which it will,
/// on a tight clearance constraint — has nothing to follow back.
/// </param>
/// <param name="Message">What to tell the user, in their units.</param>
public readonly record struct ConstraintCheck(string Name, double Violation, string Message)
{
    public bool Satisfied => Violation <= 0.0;
}

/// <summary>
/// A hard limit on a returned design (plan §9.3).
///
/// <b>A constraint is not an objective with a big weight.</b> Plan Phase 22's
/// gate says <i>"the clearance constraint is never violated in a returned
/// design"</i> — never, not rarely. So feasibility is checked separately from
/// scoring, and an infeasible design cannot win however good its objectives
/// are.
/// </summary>
public interface IConstraint
{
    string Name { get; }

    /// <summary>
    /// Check a design. Constraints that depend only on geometry can answer
    /// from the document without a solve, which is what lets them be enforced
    /// BEFORE spending an evaluation — see <see cref="ConstraintSet.CheckGeometry"/>.
    /// </summary>
    ConstraintCheck Check(EngineModelDocument document, DesignEvaluation? evaluation);

    /// <summary>
    /// True when this constraint can be decided from the document alone. Such
    /// a constraint rejects an impossible design for free rather than at the
    /// cost of a converged sweep.
    /// </summary>
    bool IsGeometric { get; }
}

/// <summary>
/// A geometric limit computed straight off the document — dimensions,
/// manufacturability, anything that does not need the gas dynamics.
/// </summary>
/// <param name="Name">What it is.</param>
/// <param name="Measure">The quantity, from the document.</param>
/// <param name="Limit">The bound.</param>
/// <param name="MustNotExceed">True for an upper bound, false for a lower one.</param>
/// <param name="Unit">For the message.</param>
public sealed record GeometricLimit(
    string Name, Func<EngineModelDocument, double> Measure, double Limit, bool MustNotExceed, string Unit = "")
    : IConstraint
{
    public bool IsGeometric => true;

    public ConstraintCheck Check(EngineModelDocument document, DesignEvaluation? evaluation)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = evaluation;

        var value = Measure(document);
        if (!double.IsFinite(value))
        {
            return new ConstraintCheck(Name, double.PositiveInfinity,
                $"{Name}: could not be measured on this design.");
        }

        var violation = MustNotExceed ? value - Limit : Limit - value;

        // Normalised by the limit so constraints of different magnitudes are
        // comparable when they are summed into a penalty — otherwise a
        // millimetre of clearance and a kilopascal of pressure would count
        // a thousand to one for no reason but their units.
        var scale = Math.Max(Math.Abs(Limit), 1e-9);

        return new ConstraintCheck(
            Name,
            Math.Max(0.0, violation) / scale,
            violation <= 0
                ? $"{Name}: {value:F2}{Unit} against a limit of {Limit:F2}{Unit} — satisfied."
                : $"{Name}: {value:F2}{Unit} {(MustNotExceed ? "exceeds" : "is below")} the limit of "
                  + $"{Limit:F2}{Unit} by {Math.Abs(violation):F2}{Unit}.");
    }
}

/// <summary>
/// A limit on something only a solve can report — knock margin, peak cylinder
/// pressure, surge margin, turbine inlet temperature.
/// </summary>
/// <param name="Name">What it is.</param>
/// <param name="Measure">
/// The quantity, from the evaluation. Returns NaN when this evaluation cannot
/// answer, which is treated as UNKNOWN rather than as satisfied — a
/// constraint silently passing because nobody measured it is the worst
/// possible failure mode for a hard limit.
/// </param>
/// <param name="Limit">The bound.</param>
/// <param name="MustNotExceed">True for an upper bound, false for a lower one.</param>
/// <param name="Unit">For the message.</param>
public sealed record SolvedLimit(
    string Name, Func<DesignEvaluation, double> Measure, double Limit, bool MustNotExceed, string Unit = "")
    : IConstraint
{
    public bool IsGeometric => false;

    public ConstraintCheck Check(EngineModelDocument document, DesignEvaluation? evaluation)
    {
        _ = document;

        if (evaluation is null)
        {
            return new ConstraintCheck(Name, double.PositiveInfinity,
                $"{Name}: not checked — this design has not been evaluated.");
        }

        var value = Measure(evaluation);
        if (!double.IsFinite(value))
        {
            return new ConstraintCheck(Name, double.PositiveInfinity,
                $"{Name}: this evaluation does not report it, so the limit could not be checked. "
                + "An unchecked hard limit counts as violated, never as met.");
        }

        var violation = MustNotExceed ? value - Limit : Limit - value;
        var scale = Math.Max(Math.Abs(Limit), 1e-9);

        return new ConstraintCheck(
            Name,
            Math.Max(0.0, violation) / scale,
            violation <= 0
                ? $"{Name}: {value:F3}{Unit} against a limit of {Limit:F3}{Unit} — satisfied."
                : $"{Name}: {value:F3}{Unit} {(MustNotExceed ? "exceeds" : "is below")} the limit of "
                  + $"{Limit:F3}{Unit}.");
    }

    /// <summary>Worst knock integral over the sweep must stay below a threshold (1.0 is onset).</summary>
    public static SolvedLimit KnockMargin(double maximumIntegral = 0.8) => new(
        "Knock margin",
        e => e.Sweep.Count == 0
            ? double.NaN
            : e.Sweep.Select(p => p.KnockIntegral).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max(),
        maximumIntegral,
        MustNotExceed: true);

    /// <summary>Peak cylinder pressure must stay below what the bottom end will take.</summary>
    public static SolvedLimit PeakCylinderPressure(double limitBar = 120.0) => new(
        "Peak cylinder pressure",
        e => e.Sweep.Count == 0
            ? double.NaN
            : e.Sweep.Select(p => p.PeakPressurePa / 1e5).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max(),
        limitBar,
        MustNotExceed: true,
        " bar");

    /// <summary>
    /// The design must not be worse than the baseline it started from, by
    /// more than a stated fraction (plan §9.3: "minimum torque or
    /// area-under-curve relative to a baseline").
    /// </summary>
    public static SolvedLimit NoWorseThan(string name, Func<DesignEvaluation, double> measure, double floor) =>
        new(name, measure, floor, MustNotExceed: false);
}

/// <summary>
/// Every constraint on a run, and the feasibility verdict they add up to.
/// </summary>
public sealed class ConstraintSet(IReadOnlyList<IConstraint> constraints)
{
    public IReadOnlyList<IConstraint> Constraints { get; } =
        constraints ?? throw new ArgumentNullException(nameof(constraints));

    public int Count => Constraints.Count;

    /// <summary>
    /// Only the constraints decidable from geometry. Checked BEFORE an
    /// evaluation is spent: a design 40 mm over its packaging box does not
    /// need a converged solve to be rejected, and on a tight space most of
    /// what a search proposes is exactly that.
    /// </summary>
    public IReadOnlyList<ConstraintCheck> CheckGeometry(EngineModelDocument document) =>
        Constraints.Where(c => c.IsGeometric).Select(c => c.Check(document, null)).ToList();

    public IReadOnlyList<ConstraintCheck> CheckAll(EngineModelDocument document, DesignEvaluation? evaluation) =>
        Constraints.Select(c => c.Check(document, evaluation)).ToList();

    /// <summary>Total normalised violation; zero is feasible.</summary>
    public static double TotalViolation(IReadOnlyList<ConstraintCheck> checks) =>
        checks.Where(c => !c.Satisfied).Sum(c => double.IsFinite(c.Violation) ? c.Violation : 1e6);

    public static bool IsFeasible(IReadOnlyList<ConstraintCheck> checks) => checks.All(c => c.Satisfied);
}

/// <summary>
/// The constraints the plan names explicitly, as ready-made factories.
/// </summary>
public static class StandardConstraints
{
    /// <summary>
    /// Valve-to-piston clearance (plan §2.6, §9.3) — <b>the</b> hard
    /// constraint of a cam optimisation, and the one Phase 22's gate names.
    ///
    /// The clearance is computed from the cam events and the crank geometry by
    /// the caller's own measure, because the pocket geometry lives outside the
    /// document schema today. What this type adds is the discipline: it is
    /// geometric, so it is checked before an evaluation is spent, and it is
    /// hard, so a design that breaks it cannot be returned whatever its
    /// torque.
    /// </summary>
    public static GeometricLimit ValveToPistonClearance(
        Func<EngineModelDocument, double> measureMm, double minimumMm) =>
        new("Valve-to-piston clearance", measureMm, minimumMm, MustNotExceed: false, " mm");

    /// <summary>A dimension the design has to fit inside.</summary>
    public static GeometricLimit MaximumLength(
        Func<EngineModelDocument, double> measureMm, double limitMm, string what = "Packaging length") =>
        new(what, measureMm, limitMm, MustNotExceed: true, " mm");

    /// <summary>
    /// A length must be buildable from stock tube. Not a bound on the value —
    /// that is the variable's own <see cref="OptimisationVariable.Choices"/> —
    /// but a check that nothing downstream moved it off the list.
    /// </summary>
    public static GeometricLimit OnTheShelf(
        string what, Func<EngineModelDocument, double> measureMm, IReadOnlyList<double> stockMm,
        double toleranceMm = 0.01) =>
        new(
            what,
            d =>
            {
                var value = measureMm(d);
                return stockMm.Count == 0 ? 0.0 : stockMm.Min(s => Math.Abs(s - value));
            },
            toleranceMm,
            MustNotExceed: true,
            " mm");

    /// <summary>Model validation itself, as a constraint: a design the validator rejects is not a design.</summary>
    public static GeometricLimit ModelIsValid() => new(
        "Model validity",
        d => d.Validate().Count(i => i.Severity == ModelIssueSeverity.Error),
        0.0,
        MustNotExceed: true,
        " errors");
}
