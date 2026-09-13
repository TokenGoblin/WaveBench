using System.Diagnostics;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Optimize;

/// <summary>
/// Evaluates a design by actually solving it (plan §9.5).
///
/// <b>Two fidelities, and the difference between them is the whole cost
/// argument.</b> Plan §9.5: <i>"Use the analytical layer and TMM as cheap
/// surrogates in the inner loop, and the full nonlinear solve only for
/// candidates the surrogate ranks highly."</i> Here that is a coarse mesh and
/// a short cycle budget for <see cref="EvaluationFidelity.Surrogate"/>, and
/// the document's own solver settings for <see cref="EvaluationFidelity.Solved"/>.
///
/// The surrogate is NOT claimed to be accurate in absolute terms. What it has
/// to be is monotone with the solve — to rank two designs the same way the
/// full solve would — and a search that uses it for ranking and the solve for
/// the answer is correct even where the surrogate's numbers are not. Anything
/// reported to the user comes from the solved fidelity, and
/// <see cref="DesignEvaluation.Fidelity"/> says which was used.
/// </summary>
public sealed class SweepEvaluator : IDesignEvaluator
{
    private readonly EngineModelDocument _baseline;
    private readonly IReadOnlyList<double> _rpms;
    private readonly IReadOnlyList<double> _surrogateRpms;
    private readonly int? _maxParallel;

    /// <param name="baseline">The document each design modifies.</param>
    /// <param name="rpms">Operating points of a solved evaluation, in rpm order.</param>
    /// <param name="surrogateRpms">
    /// Speeds for the surrogate pass. <b>Defaults to the SAME speeds as the
    /// solve, and dropping points is a trap.</b>
    ///
    /// Sampling a band more sparsely does not make an area-under-torque
    /// objective cheaper — it makes it a DIFFERENT objective. The trapezium
    /// integral over every second point is a different function of the design,
    /// so the surrogate optimises something the solve is not measuring, and
    /// the two rank designs differently. That was measured, not assumed: on
    /// the FSAE intake case, halving the operating points dropped the
    /// surrogate's Spearman rank correlation against the solve to 0.68 and
    /// swapped two of the seven designs, while mesh coarsening from 1× to 2×
    /// changed the correlation by nothing at all. The cheapness belongs in the
    /// mesh and the cycle budget, which affect how well each point is
    /// resolved; it does not belong in which points exist.
    ///
    /// Pass a different set only when the objective genuinely is defined on
    /// it.
    /// </param>
    /// <param name="surrogateCellScale">
    /// Mesh coarsening for the surrogate. 2.0 doubles the cell size, which is
    /// roughly a quarter of the cost per cycle and still resolves the primary
    /// wave action the ranking depends on.
    /// </param>
    /// <param name="surrogateMaxCycles">Cycle cap for the surrogate; the solve keeps the document's own.</param>
    /// <param name="maxParallel">Degree of parallelism across operating points; cores − 1 by default.</param>
    public SweepEvaluator(
        EngineModelDocument baseline,
        IReadOnlyList<double> rpms,
        IReadOnlyList<double>? surrogateRpms = null,
        double surrogateCellScale = 2.0,
        int surrogateMaxCycles = 6,
        int? maxParallel = null)
    {
        _baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        ArgumentNullException.ThrowIfNull(rpms);

        if (rpms.Count == 0)
        {
            throw new ArgumentException("An evaluation needs at least one operating point.", nameof(rpms));
        }

        _rpms = rpms.OrderBy(r => r).ToList();
        _surrogateRpms = surrogateRpms?.OrderBy(r => r).ToList() ?? _rpms;
        SurrogateCellScale = surrogateCellScale;
        SurrogateMaxCycles = surrogateMaxCycles;
        _maxParallel = maxParallel;
    }

    public double SurrogateCellScale { get; }

    public int SurrogateMaxCycles { get; }

    public IReadOnlyList<EvaluationFidelity> Fidelities =>
        [EvaluationFidelity.Surrogate, EvaluationFidelity.Solved];

    /// <summary>
    /// Extra scalars to attach to every evaluation — acoustic metrics, boost
    /// margins, packaging length. The evaluator cannot compute these itself:
    /// <c>WaveBench.Optimize</c> references only Core and Model, and must not
    /// need to know what an Order Purity Index is in order to optimise one.
    /// A caller that does know supplies this.
    /// </summary>
    public Func<EngineModelDocument, IReadOnlyList<OperatingPointResult>, IReadOnlyDictionary<string, double>>?
        Metrics { get; init; }

    public DesignEvaluation Evaluate(
        DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(design);

        var document = design.Materialise(_baseline);
        var stopwatch = Stopwatch.StartNew();

        // A design the document's own validator rejects is not a design. This
        // is reported as a FAILURE rather than thrown, because a region the
        // model cannot describe is something the search needs told — an
        // exception here would abort an overnight run over one bad candidate.
        var errors = document.Validate()
            .Where(i => i.Severity == ModelIssueSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Elapsed = stopwatch.Elapsed,
                Failure = "The model is invalid: " + string.Join("; ", errors.Select(e => $"{e.Path} — {e.Message}")),
            };
        }

        var speeds = fidelity == EvaluationFidelity.Surrogate ? _surrogateRpms : _rpms;
        var toSolve = fidelity == EvaluationFidelity.Surrogate ? Coarsen(document) : document;

        try
        {
            cancellation.ThrowIfCancellationRequested();

            var sweep = Sweep(toSolve, speeds, cancellation);

            // A non-finite torque anywhere means the solve went non-physical.
            // Reporting the whole evaluation as failed is the honest answer:
            // an area under a curve with a NaN in it is not a smaller area, it
            // is no area at all.
            if (sweep.Any(p => !double.IsFinite(p.TorqueNm) || !double.IsFinite(p.VolumetricEfficiency)))
            {
                return new DesignEvaluation
                {
                    Design = design,
                    Fidelity = fidelity,
                    Sweep = sweep,
                    Elapsed = stopwatch.Elapsed,
                    Failure = "The solve produced a non-finite result — this geometry is outside what the model "
                              + "can describe.",
                };
            }

            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Sweep = sweep,
                Elapsed = stopwatch.Elapsed,
                Metrics = Metrics?.Invoke(document, sweep)
                          ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is ArithmeticException or InvalidOperationException or ArgumentException)
        {
            // The solver refusing a geometry is data about the space, not a
            // reason to lose the run.
            return new DesignEvaluation
            {
                Design = design,
                Fidelity = fidelity,
                Elapsed = stopwatch.Elapsed,
                Failure = $"The solve failed: {e.Message}",
            };
        }
    }

    /// <summary>
    /// Solve the operating points.
    ///
    /// Parallel across points, which is safe because each point is an
    /// independent engine instance — the property
    /// <see cref="OperatingPointRunner.Sweep"/> already relies on, and the
    /// reason scheduling cannot change a result. Falls back to a serial loop
    /// when a cancellation token is live, so a cancelled run stops promptly
    /// rather than at the end of the whole sweep.
    /// </summary>
    private IReadOnlyList<OperatingPointResult> Sweep(
        EngineModelDocument document, IReadOnlyList<double> speeds, CancellationToken cancellation)
    {
        if (!cancellation.CanBeCanceled)
        {
            return OperatingPointRunner.Sweep(document, speeds, _maxParallel);
        }

        var results = new OperatingPointResult[speeds.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxParallel ?? Math.Max(1, Environment.ProcessorCount - 1),
            CancellationToken = cancellation,
        };

        Parallel.For(0, speeds.Count, options, i => results[i] = OperatingPointRunner.Run(document, speeds[i]));
        return results;
    }

    /// <summary>The same model on a coarser mesh and a shorter cycle budget.</summary>
    private EngineModelDocument Coarsen(EngineModelDocument document) => document with
    {
        Solver = document.Solver with
        {
            CellSizeMm = document.Solver.CellSizeMm * SurrogateCellScale,
            MaxCycles = Math.Min(document.Solver.MaxCycles, SurrogateMaxCycles),
            MinCycles = Math.Min(document.Solver.MinCycles, Math.Max(2, SurrogateMaxCycles / 2)),
        },
    };

}
