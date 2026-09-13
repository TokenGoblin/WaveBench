using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveBench.Optimize;

/// <summary>
/// One design as the archive keeps it: enough to re-explore, rank and resume,
/// and no more.
///
/// <b>Deliberately not the whole evaluation.</b> A converged sweep carries
/// per-cylinder arrays and captured fields at every operating point; storing
/// those for every candidate of a thousand-evaluation run would produce a file
/// nobody saves and nobody opens. What is kept is what a user actually
/// explores a run by — the design, what it scored, and whether it was legal —
/// and the full evaluation stays in the in-memory cache for the session that
/// produced it. Clicking a design to inspect it re-materialises the document
/// from its coordinates, which is exact, and re-solves only if the cache has
/// been lost.
/// </summary>
/// <param name="Coordinates">The design in the unit cube — exact, and enough to rebuild the document.</param>
/// <param name="Values">The same design in model units, for reading without a space to hand.</param>
/// <param name="Objectives">Objective values in their own units, in the set's order.</param>
/// <param name="Feasible">Whether every hard constraint was satisfied.</param>
/// <param name="Violation">Total normalised violation; zero when feasible.</param>
/// <param name="Failures">Names of the constraints this design broke.</param>
/// <param name="Fidelity">What was spent measuring it.</param>
/// <param name="ElapsedMs">How long it took, for the run report.</param>
public sealed record ArchivedDesign(
    IReadOnlyList<double> Coordinates,
    IReadOnlyList<double> Values,
    IReadOnlyList<double> Objectives,
    bool Feasible,
    double Violation,
    IReadOnlyList<string> Failures,
    EvaluationFidelity Fidelity,
    double ElapsedMs)
{
    /// <summary>Rebuild the point this record describes.</summary>
    public DesignPoint Point(DesignSpace space) => new(space, Coordinates);
}

/// <summary>
/// A run's whole history, re-explorable after it finishes (plan §9.5:
/// <i>"checkpoint/resume; a full design archive re-explorable after the
/// run"</i>).
///
/// Two jobs, and they pull in the same direction: it is what the Pareto
/// explorer reads, and it is what a killed process comes back from. Both need
/// the same thing — every design that was measured, with what it scored, in a
/// form that survives being written to disk.
/// </summary>
public sealed class DesignArchive
{
    private readonly List<ArchivedDesign> _designs = [];
    private readonly Lock _gate = new();

    public DesignArchive(string runId, DesignSpace space, ObjectiveSet objectives)
    {
        RunId = string.IsNullOrWhiteSpace(runId) ? throw new ArgumentException("A run needs an id.", nameof(runId)) : runId;
        Space = space ?? throw new ArgumentNullException(nameof(space));
        Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
    }

    /// <summary>
    /// Identifies this run. The same id is stamped on every field the run
    /// writes back to a session (§8.5 <c>Optimised</c> provenance), so a value
    /// in a document can always be traced to the search that chose it.
    /// </summary>
    public string RunId { get; }

    public DesignSpace Space { get; }

    public ObjectiveSet Objectives { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _designs.Count;
            }
        }
    }

    public IReadOnlyList<ArchivedDesign> Designs
    {
        get
        {
            lock (_gate)
            {
                return _designs.ToList();
            }
        }
    }

    public void Add(ScoredDesign scored)
    {
        ArgumentNullException.ThrowIfNull(scored);

        var record = new ArchivedDesign(
            scored.Design.Coordinates,
            scored.Design.Values,
            scored.Objectives,
            scored.Feasible,
            scored.Violation,
            scored.Failures.Select(f => f.Name).ToList(),
            scored.Evaluation?.Fidelity ?? EvaluationFidelity.Surrogate,
            scored.Evaluation?.Elapsed.TotalMilliseconds ?? 0.0);

        lock (_gate)
        {
            _designs.Add(record);
        }
    }

    public void AddRange(IEnumerable<ScoredDesign> designs)
    {
        ArgumentNullException.ThrowIfNull(designs);

        foreach (var design in designs)
        {
            Add(design);
        }
    }

    /// <summary>
    /// Feasible designs only, best first by the objective at
    /// <paramref name="objective"/>.
    ///
    /// Infeasible designs are kept in the archive — a user exploring a run
    /// needs to see where the constraints bit, and "nothing was found in this
    /// region" and "everything found there was illegal" are different
    /// findings — but they are never ranked as answers.
    /// </summary>
    public IReadOnlyList<ArchivedDesign> Best(int objective = 0, int count = 10)
    {
        var sense = Objectives.Objectives[objective].Sense;

        var feasible = Designs
            .Where(d => d.Feasible && objective < d.Objectives.Count && double.IsFinite(d.Objectives[objective]));

        var ordered = sense == ObjectiveSense.Maximise
            ? feasible.OrderByDescending(d => d.Objectives[objective])
            : feasible.OrderBy(d => d.Objectives[objective]);

        return ordered.Take(count).ToList();
    }

    /// <summary>
    /// The non-dominated set over every feasible design measured, whatever
    /// algorithm found them.
    ///
    /// Taken over the WHOLE archive rather than a final population, because a
    /// design that was on the front in generation three and got crowded out in
    /// generation forty is still a design the user may prefer — and a Pareto
    /// explorer that cannot show it is hiding part of the trade.
    /// </summary>
    public IReadOnlyList<ArchivedDesign> Front()
    {
        var feasible = Designs.Where(d => d.Feasible).ToList();
        var front = new List<ArchivedDesign>();

        foreach (var candidate in feasible)
        {
            if (!feasible.Any(other => Dominates(other, candidate)))
            {
                front.Add(candidate);
            }
        }

        // Ordered along the first objective so a plot can draw it as a line
        // rather than a scatter the reader has to trace.
        return front.OrderBy(d => d.Objectives.Count > 0 ? d.Objectives[0] : 0.0).ToList();
    }

    private bool Dominates(ArchivedDesign a, ArchivedDesign b)
    {
        var betterSomewhere = false;

        for (var i = 0; i < Objectives.Count && i < a.Objectives.Count && i < b.Objectives.Count; i++)
        {
            var sense = Objectives.Objectives[i].Sense;
            var x = a.Objectives[i];
            var y = b.Objectives[i];

            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                continue;
            }

            // In cost terms: lower is better once the sense is applied.
            var costA = sense == ObjectiveSense.Maximise ? -x : x;
            var costB = sense == ObjectiveSense.Maximise ? -y : y;

            if (costA > costB)
            {
                return false;
            }

            if (costA < costB)
            {
                betterSomewhere = true;
            }
        }

        return betterSomewhere;
    }

    /// <summary>How the run went, in numbers a report can quote.</summary>
    public ArchiveSummary Summary()
    {
        var designs = Designs;
        var feasible = designs.Count(d => d.Feasible);
        var solved = designs.Count(d => d.Fidelity == EvaluationFidelity.Solved);

        return new ArchiveSummary(
            RunId,
            designs.Count,
            feasible,
            solved,
            designs.Sum(d => d.ElapsedMs) / 1000.0,
            Front().Count);
    }

    // ---- Checkpoint and resume -------------------------------------------

    /// <summary>
    /// Write the archive to JSON.
    ///
    /// Indented and stable-ordered like every other file this project writes,
    /// so a checkpoint diffs cleanly in git and a user can read one without a
    /// tool.
    /// </summary>
    public string Save() => JsonSerializer.Serialize(
        new ArchiveFile(RunId, _designs.ToList()), ArchiveJson.Default.ArchiveFile);

    /// <summary>
    /// Read an archive back.
    ///
    /// The space and objectives are supplied by the caller rather than stored:
    /// they come from the project, and a checkpoint that carried its own copy
    /// could be resumed against a DIFFERENT problem than the one it describes
    /// without anything noticing. The count check below is the cheapest guard
    /// against that, and it is not a strong one — which is why
    /// <see cref="Resume"/> says what it assumes.
    /// </summary>
    public static DesignArchive Load(string json, DesignSpace space, ObjectiveSet objectives)
    {
        ArgumentNullException.ThrowIfNull(space);

        var file = JsonSerializer.Deserialize(json, ArchiveJson.Default.ArchiveFile)
                   ?? throw new InvalidDataException("The archive deserialised to null.");

        var archive = new DesignArchive(file.RunId, space, objectives);

        foreach (var design in file.Designs)
        {
            if (design.Coordinates.Count != space.Dimension)
            {
                throw new InvalidDataException(
                    $"This checkpoint holds {design.Coordinates.Count}-variable designs but the space has "
                    + $"{space.Dimension}. It belongs to a different problem, and resuming from it would "
                    + "silently mix two searches.");
            }

            archive._designs.Add(design);
        }

        return archive;
    }

    /// <summary>
    /// Seed a fresh search from an archive's best designs.
    ///
    /// <b>Resuming an evolutionary search is not the same as continuing
    /// it.</b> CMA-ES carries a covariance matrix and a step size that a
    /// checkpoint of designs alone does not preserve, so a resumed run restarts
    /// the adaptation from the seeds rather than picking up mid-stride. That is
    /// stated rather than papered over: the result after a resume is not
    /// bit-identical to an uninterrupted run of the same length, and a report
    /// that claimed otherwise would be wrong.
    ///
    /// What resume DOES guarantee is that no evaluation is repeated — the
    /// cache is keyed on the snapped design, so every archived design is free
    /// the second time.
    /// </summary>
    public IReadOnlyList<DesignPoint> Resume(int count = 20) =>
        Best(count: count).Select(d => d.Point(Space)).ToList();
}

/// <summary>How a run went.</summary>
/// <param name="RunId">Which run.</param>
/// <param name="Designs">How many were measured.</param>
/// <param name="Feasible">How many satisfied every hard constraint.</param>
/// <param name="Solved">How many were measured at full fidelity rather than on the surrogate.</param>
/// <param name="SolverSeconds">Total time inside the evaluator.</param>
/// <param name="FrontSize">How many designs sit on the non-dominated front.</param>
public sealed record ArchiveSummary(
    string RunId, int Designs, int Feasible, int Solved, double SolverSeconds, int FrontSize)
{
    public double FeasibleFraction => Designs > 0 ? Feasible / (double)Designs : 0.0;

    public override string ToString() =>
        $"{Designs} designs, {Feasible} feasible ({FeasibleFraction:P0}), {Solved} solved at full fidelity, "
        + $"{FrontSize} on the front, {SolverSeconds:F1} s in the solver.";
}

/// <summary>The on-disk shape of a checkpoint.</summary>
/// <param name="RunId">Which run.</param>
/// <param name="Designs">Every design measured.</param>
public sealed record ArchiveFile(string RunId, IReadOnlyList<ArchivedDesign> Designs);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ArchiveFile))]
public partial class ArchiveJson : JsonSerializerContext;
