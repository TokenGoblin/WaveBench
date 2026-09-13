using WaveBench.Core.Solver;

namespace WaveBench.Optimize;

/// <summary>
/// How much was actually spent on an answer (plan §9.5's cost management).
///
/// The surrogate inner loop only works if a cheap answer can be told from an
/// expensive one after the fact — an archive that mixes the two without
/// saying which is which cannot be re-explored honestly.
/// </summary>
public enum EvaluationFidelity
{
    /// <summary>An analytical or transfer-matrix estimate. Microseconds to milliseconds.</summary>
    Surrogate,

    /// <summary>A converged nonlinear solve over the operating points.</summary>
    Solved,
}

/// <summary>
/// What one candidate design turned out to be worth.
///
/// <b>Deliberately not a score.</b> It carries the raw sweep and a bag of
/// named scalars, and the objectives and constraints read from it. Collapsing
/// to a number here would mean re-evaluating every design whenever a weight
/// changed — and plan §9.5 asks for "a full design archive re-explorable
/// after the run", which is only possible if what was measured outlives the
/// weights it was judged by.
/// </summary>
public sealed record DesignEvaluation
{
    public required DesignPoint Design { get; init; }

    /// <summary>Operating points in rpm order. Empty when the fidelity did not produce a sweep.</summary>
    public IReadOnlyList<OperatingPointResult> Sweep { get; init; } = [];

    /// <summary>
    /// Named scalars the sweep does not carry: acoustic metrics, boost
    /// margins, packaging length, anything a higher layer computed.
    ///
    /// A dictionary rather than a fat record because
    /// <c>WaveBench.Optimize</c> references only Core and Model — it must not
    /// need to know what an Order Purity Index is in order to optimise one.
    /// The evaluator that knows fills these in; the objective that cares reads
    /// them by name.
    /// </summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public required EvaluationFidelity Fidelity { get; init; }

    /// <summary>Wall-clock cost, for the run report and for surrogate budgeting.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Set when the design could not be evaluated at all — a solve that went
    /// non-physical, a geometry the mesher refused.
    ///
    /// A failed evaluation is DATA, not an exception to swallow: a region of
    /// the space the model cannot handle is something the user needs told,
    /// and an optimiser that silently skips it will keep walking back into it.
    /// </summary>
    public string? Failure { get; init; }

    public bool Failed => Failure is not null;

    public double Metric(string name, double fallback = double.NaN) =>
        Metrics.TryGetValue(name, out var value) ? value : fallback;
}

/// <summary>Evaluates a design at a requested fidelity.</summary>
public interface IDesignEvaluator
{
    /// <summary>
    /// The fidelities this evaluator can produce, cheapest first. An
    /// evaluator with only <see cref="EvaluationFidelity.Solved"/> is legal
    /// and simply means no surrogate is available.
    /// </summary>
    IReadOnlyList<EvaluationFidelity> Fidelities { get; }

    DesignEvaluation Evaluate(DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default);
}

/// <summary>
/// A content-addressed cache over designs (plan §9.5: <i>"a content-addressed
/// cache keyed on a hash of the resolved model so repeated designs are
/// free"</i>).
///
/// <b>Keyed on the snapped design, not on the search coordinates.</b> Two
/// coordinates a hair apart in the unit cube routinely denormalise to the same
/// tube size; keying on coordinates would re-solve that design every time. On
/// a discrete space — which is most real ones, because fabricators sell
/// discrete sizes — this is the difference between a search that finishes and
/// one that does not.
///
/// Thread-safe: parallel evaluation is the point of having it.
/// </summary>
public sealed class EvaluationCache(IDesignEvaluator inner) : IDesignEvaluator
{
    private readonly IDesignEvaluator _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Dictionary<string, DesignEvaluation> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<EvaluationFidelity> Fidelities => _inner.Fidelities;

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Everything evaluated so far, for the archive.</summary>
    public IReadOnlyList<DesignEvaluation> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.ToList();
            }
        }
    }

    public DesignEvaluation Evaluate(
        DesignPoint design, EvaluationFidelity fidelity, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(design);

        var key = $"{fidelity}|{design.Key()}";

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                Hits++;
                return cached;
            }
        }

        // Deliberately OUTSIDE the lock: an evaluation is seconds of solve and
        // holding the lock across it would serialise the whole parallel sweep
        // this cache exists to make affordable. The cost is that two threads
        // can race on the same unseen design and both compute it — which
        // wastes one evaluation and is still far cheaper than serialising
        // every one of them.
        var evaluation = _inner.Evaluate(design, fidelity, cancellation);

        lock (_gate)
        {
            _entries[key] = evaluation;
            Misses++;
        }

        return evaluation;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Hits = 0;
            Misses = 0;
        }
    }
}
