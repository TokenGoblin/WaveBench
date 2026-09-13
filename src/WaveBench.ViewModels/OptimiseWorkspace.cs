using System.Globalization;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Model;
using WaveBench.Optimize;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels;

/// <summary>Tabs of the Optimise workspace, in shell order (plan §8.3).</summary>
public enum OptimiseTab
{
    Variables,
    Objectives,
    Run,
    Pareto,
    Archive,
}

/// <summary>Which search to use (plan §9.4).</summary>
public enum SearchAlgorithm
{
    /// <summary>Space-filling orientation pass. Answers "what does this space look like", not "what is best".</summary>
    Doe,

    /// <summary>Morris elementary effects: which variables matter, for a fiftieth of a full study.</summary>
    Screening,

    /// <summary>Single-objective global search.</summary>
    CmaEs,

    /// <summary>
    /// Model-based single-objective search. The right default when each
    /// evaluation is a converged sweep, which here it is.
    /// </summary>
    Bayesian,

    /// <summary>Multi-objective, returning a front rather than a point.</summary>
    NsgaII,
}

/// <summary>
/// How each search is named on screen.
///
/// The enum members are C# identifiers and read as such — "CmaEs" and
/// "NsgaII" are not what these methods are called, and "NsgaII" in a
/// sans-serif face is very nearly unreadable. These are published algorithms
/// with published names, and a user searching for one should find the same
/// spelling here as in the paper.
/// </summary>
public static class SearchAlgorithms
{
    public static string Title(this SearchAlgorithm algorithm) => algorithm switch
    {
        SearchAlgorithm.Doe => "DOE",
        SearchAlgorithm.Screening => "Screening",
        SearchAlgorithm.CmaEs => "CMA-ES",
        SearchAlgorithm.Bayesian => "Bayesian",
        SearchAlgorithm.NsgaII => "NSGA-II",
        _ => algorithm.ToString(),
    };
}

/// <summary>
/// The Optimise workspace (plan Phase 22, §8.4): variables, objectives and
/// constraints; the run; the Pareto explorer; the archive.
///
/// Contains no UI-framework types. Everything the screen draws comes from
/// here, and every figure is a <see cref="PlotModel"/> — so the fronts export
/// and the whole workspace is testable without a window.
///
/// <b>The explorer is the point of the phase.</b> Plan §9.6: the headline
/// views are power against sound and response against peak power, as fronts
/// with click-to-audition and click-to-inspect. A front the user cannot
/// interrogate is a picture of a trade rather than a way to make one, so
/// selecting a design here yields its geometry, its torque curve and its
/// sound — the three things a builder decides on.
/// </summary>
public sealed class OptimiseWorkspace
{
    private readonly ProjectSession _session;
    private readonly List<OptimisationVariable> _variables = [];
    private readonly List<string> _log = [];

    public OptimiseWorkspace(ProjectSession session, UserPreferences? preferences = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Preferences = preferences ?? new UserPreferences();

        foreach (var variable in OptimisationCatalogue.Suggested(session.Document))
        {
            _variables.Add(variable);
        }
    }

    public UserPreferences Preferences { get; }

    public EngineModelDocument Document => _session.Document;

    public OptimiseTab SelectedTab { get; set; } = OptimiseTab.Variables;

    public SearchAlgorithm Algorithm { get; set; } = SearchAlgorithm.CmaEs;

    /// <summary>Evaluation budget. The single number that decides what a run costs.</summary>
    public int Budget { get; set; } = 60;

    /// <summary>Operating points every evaluation solves.</summary>
    public IReadOnlyList<double> Speeds { get; set; } = [4000, 5000, 6000, 7000, 8000];

    public double BandFromRpm { get; set; } = 4000;

    public double BandToRpm { get; set; } = 8000;

    // ---- Variables ---------------------------------------------------------

    public IReadOnlyList<OptimisationVariable> Variables => _variables;

    /// <summary>Fields that could be optimised but are not in the run yet.</summary>
    public IReadOnlyList<OptimisableField> Available =>
        OptimisationCatalogue.Fields
            .Where(f => !_variables.Any(v => string.Equals(v.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public bool Add(string path)
    {
        var field = OptimisationCatalogue.Find(path);
        if (field is null || _variables.Any(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _variables.Add(field.Variable(Document));
        return true;
    }

    public bool Remove(string path) =>
        _variables.RemoveAll(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Change a variable's bounds without disturbing the rest of the run definition.</summary>
    public bool SetBounds(string path, double minimum, double maximum)
    {
        var index = _variables.FindIndex(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || maximum <= minimum)
        {
            return false;
        }

        _variables[index] = _variables[index] with { Minimum = minimum, Maximum = maximum };
        return true;
    }

    // ---- Objectives and constraints ----------------------------------------

    /// <summary>
    /// The objectives of this run, in order. Defaults to the plan's own
    /// default (§9.2): area under the torque curve over the band.
    /// </summary>
    public List<IObjective> Objectives { get; } = [];

    public List<IConstraint> Constraints { get; } = [];

    private ObjectiveSet ObjectiveSet()
    {
        if (Objectives.Count > 0)
        {
            return new ObjectiveSet(Objectives.ToList());
        }

        return new ObjectiveSet([new AreaUnderTorque(BandFromRpm, BandToRpm)]);
    }

    // ---- The run -----------------------------------------------------------

    public DesignArchive? Archive { get; private set; }

    public ScoredDesign? Baseline { get; private set; }

    public OptimiserResult? LastResult { get; private set; }

    public ParetoResult? LastFront { get; private set; }

    public IReadOnlyList<MorrisEffect> LastScreening { get; private set; } = [];

    /// <summary>
    /// What the run has said so far.
    ///
    /// Snapshotted under a lock because a run appends to it from a background
    /// thread while the screen reads it: enumerating a <c>List</c> another
    /// thread is adding to throws, and it would throw exactly when the user is
    /// watching a long run — the one moment the screen must not fall over.
    /// </summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_logGate)
            {
                return _log.ToList();
            }
        }
    }

    private readonly Lock _logGate = new();

    public bool HasRun => Archive is { Count: > 0 };

    /// <summary>True while a search is in flight.</summary>
    public bool IsRunning { get; private set; }

    private CancellationTokenSource? _cancellation;

    /// <summary>
    /// Start a search on a background thread.
    ///
    /// <b>The workspace owns the run, not the view.</b> Plan §8.3 requires
    /// that switching workspaces never cancels a job, so the thing that
    /// survives navigation has to hold it — and the workspace is what lives
    /// for the session while the view is rebuilt on every click. A token
    /// source held in a renderer would be collected the first time the user
    /// looked at another tab.
    /// </summary>
    public async Task StartAsync(
        IDesignEvaluator? evaluator = null, IProgress<OptimiserProgress>? progress = null)
    {
        if (IsRunning)
        {
            return;
        }

        // Built on the CALLING thread, so a bad variable set or an unwritable
        // path throws where the user can see it rather than inside a task
        // whose exception nobody observes.
        var problem = Problem(evaluator);

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        IsRunning = true;

        try
        {
            await Task.Run(() => Run(problem, progress, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Note("Cancelled. Everything measured so far is kept — the archive and the cache both survive.");
        }
        finally
        {
            IsRunning = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// Ask the running search to stop.
    ///
    /// Every algorithm here returns the work it has already done rather than
    /// discarding it, so a cancelled run still leaves a usable archive — an
    /// optimisation that threw away an hour of evaluations on cancel would be
    /// unusable.
    /// </summary>
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// Build the problem this run describes. Exposed so a caller can score a
    /// single design — the Pareto explorer's click-to-inspect needs exactly
    /// that, at full fidelity, for one design out of hundreds.
    /// </summary>
    public OptimisationProblem Problem(IDesignEvaluator? evaluator = null)
    {
        if (_variables.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one variable before running an optimisation.");
        }

        var space = new DesignSpace(_variables.ToList());
        var actual = evaluator ?? new EvaluationCache(new SweepEvaluator(Document, Speeds));

        return new OptimisationProblem(
            Document, space, ObjectiveSet(), actual,
            new ConstraintSet([StandardConstraints.ModelIsValid(), .. Constraints]));
    }

    /// <summary>
    /// Run the chosen search.
    ///
    /// Synchronous and cancellable: the caller puts it on a background thread
    /// and into the job tray, because plan §8.3 requires that switching
    /// workspaces never cancels a run.
    /// </summary>
    public void Run(
        IDesignEvaluator? evaluator = null,
        IProgress<OptimiserProgress>? progress = null,
        CancellationToken cancellation = default) =>
        Run(Problem(evaluator), progress, cancellation);

    private void Run(
        OptimisationProblem problem,
        IProgress<OptimiserProgress>? progress,
        CancellationToken cancellation)
    {
        var runId = $"opt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

        Archive = new DesignArchive(runId, problem.Space, problem.Objectives);

        // Every design the problem scores lands in the archive as it is
        // scored, whatever algorithm is driving. Collecting each search's
        // history at the END instead lost the whole run on cancellation — the
        // evaluations were paid for and the hand-over never happened.
        problem.Observed = Archive.Add;

        lock (_logGate)
        {
            _log.Clear();
        }

        LastResult = null;
        LastFront = null;
        LastScreening = [];

        Note($"run {runId}: {Algorithm}, {_variables.Count} variables, budget {Budget}");

        // The baseline first, always. Every number this screen reports is a
        // comparison against the design the user already had, and a run that
        // cannot say what it improved on has not answered the question.
        Baseline = problem.Score(problem.Space.From(Document), cancellation: cancellation);
        Note($"baseline scored: {Describe(Baseline, problem.Objectives)}");

        switch (Algorithm)
        {
            case SearchAlgorithm.Doe:
            {
                foreach (var design in Doe.Orient(problem.Space, Budget))
                {
                    cancellation.ThrowIfCancellationRequested();
                    Archive.Add(problem.Score(design, cancellation: cancellation));
                }

                Note($"{Budget}-point space-filling pass complete.");
                break;
            }

            case SearchAlgorithm.Screening:
            {
                var trajectories = Math.Max(4, Budget / (_variables.Count + 1));

                LastScreening = Screening.Morris(
                    problem, trajectories, fidelity: EvaluationFidelity.Surrogate, cancellation: cancellation);

                Note(Screening.Explain(LastScreening));
                break;
            }

            case SearchAlgorithm.NsgaII:
            {
                var search = new NsgaII(problem, populationSize: Math.Max(8, Math.Min(40, Budget / 4)));
                LastFront = search.Run(Budget, progress: progress, cancellation: cancellation);
                Note($"{LastFront.Front.Count} designs on the front after {LastFront.Evaluations} evaluations.");
                break;
            }

            case SearchAlgorithm.Bayesian:
            {
                var search = new BayesianOptimiser(problem);
                LastResult = search.Run(
                    Budget,
                    progress: progress,
                    cancellation: cancellation,
                    seedDesigns: [problem.Space.From(Document)]);

                Note($"{LastResult.Reason}. Best: {Describe(LastResult.Best, problem.Objectives)}");

                if (search.Surrogate is { } surrogate)
                {
                    Note($"The surrogate fitted a correlation distance of {surrogate.LengthScale:F2} of the "
                         + $"design space over {surrogate.Observations} feasible observations — short means the "
                         + "response changes quickly across these variables, long means it barely changes at all.");
                }

                if (LastResult.Best.Design.BoundWarning() is { } bound)
                {
                    Note(bound);
                }

                break;
            }

            case SearchAlgorithm.CmaEs:
            default:
            {
                var search = new CmaEs(problem, problem.Space.From(Document));
                LastResult = search.Run(Budget, progress: progress, cancellation: cancellation);
                Note($"{LastResult.Reason}. Best: {Describe(LastResult.Best, problem.Objectives)}");

                if (LastResult.Best.Design.BoundWarning() is { } warning)
                {
                    Note(warning);
                }

                break;
            }
        }

        Note(Archive.Summary().ToString());
    }

    private void Note(string line)
    {
        lock (_logGate)
        {
            _log.Add(line);
        }
    }

    private static string Describe(ScoredDesign design, ObjectiveSet objectives)
    {
        var parts = objectives.Objectives.Select((o, i) =>
            $"{o.Name} {design.Objectives[i].ToString("N3", CultureInfo.InvariantCulture)} {o.Unit}".TrimEnd());
        return string.Join(", ", parts) + (design.Feasible ? "" : " (INFEASIBLE)");
    }

    // ---- The Pareto explorer ------------------------------------------------

    /// <summary>Index of the design the user has clicked, into <see cref="FrontDesigns"/>.</summary>
    public int SelectedIndex { get; set; }

    /// <summary>The non-dominated designs, ordered along the first objective.</summary>
    public IReadOnlyList<ArchivedDesign> FrontDesigns => Archive?.Front() ?? [];

    public ArchivedDesign? Selected =>
        FrontDesigns is { Count: > 0 } front
            ? front[Math.Clamp(SelectedIndex, 0, front.Count - 1)]
            : null;

    /// <summary>
    /// The trade, as a front (plan §9.6).
    ///
    /// Drawn in the objectives' OWN units, never normalised: a front labelled
    /// "0.82 versus 0.31" is a picture, and a front labelled "451 000 N·m·rpm
    /// versus 0.94 order purity" is a decision. The baseline is marked, because
    /// the only question a user actually has is what the trade costs relative
    /// to what they already have.
    /// </summary>
    public PlotModel ParetoChart(int xObjective = 0, int yObjective = 1)
    {
        var objectives = ObjectiveSet();

        if (Archive is null || objectives.Count < 2)
        {
            return Empty(
                "Pareto front",
                objectives.Count < 2
                    ? "A front needs two objectives. With one, the answer is a single best design — the Run tab "
                      + "reports it."
                    : "No run yet.");
        }

        var front = FrontDesigns;
        var all = Archive.Designs.Where(d => d.Feasible).ToList();

        if (front.Count == 0)
        {
            return Empty("Pareto front", "No feasible design was found, so there is no trade to explore.");
        }

        var xs = front.Select(d => d.Objectives[xObjective]).ToList();
        var ys = front.Select(d => d.Objectives[yObjective]).ToList();

        var series = new List<PlotSeries>
        {
            // Every feasible design, faintly: the front is the answer, but the
            // cloud behind it shows how much of the space was actually
            // explored — and a front drawn over three points is a different
            // claim from one drawn over three hundred.
            new("Explored", all.Select(d => d.Objectives[xObjective]).ToList(),
                all.Select(d => d.Objectives[yObjective]).ToList(), "Brush.BorderSubtle", PlotSeriesKind.Scatter),
            new("Front", xs, ys, "Brush.Accent"),
        };

        if (Baseline is { Feasible: true })
        {
            series.Add(new PlotSeries(
                "Baseline",
                [Baseline.Objectives[xObjective]],
                [Baseline.Objectives[yObjective]],
                "Brush.Warning",
                PlotSeriesKind.Bar));
        }

        if (Selected is { } selected)
        {
            series.Add(new PlotSeries(
                "Selected",
                [selected.Objectives[xObjective]],
                [selected.Objectives[yObjective]],
                "Brush.Success",
                PlotSeriesKind.Scatter));
        }

        var xAxis = objectives.Objectives[xObjective];
        var yAxis = objectives.Objectives[yObjective];

        // Ranged over EVERYTHING drawn, not just the front. Sizing the axes to
        // the front alone put the baseline off the bottom of the chart while
        // the note underneath quoted its coordinates — and comparing against
        // the baseline is the only question the user actually has here.
        var drawnX = series.SelectMany(s => s.X).ToList();
        var drawnY = series.SelectMany(s => s.Y).ToList();

        return new PlotModel
        {
            Title = $"{xAxis.Name} against {yAxis.Name}",
            Subtitle = $"{front.Count} designs on the front of {all.Count} feasible · click a point to inspect it",
            XAxis = new PlotAxis(xAxis.Name, Floor(drawnX), Ceil(drawnX), xAxis.Unit),
            YAxis = new PlotAxis(yAxis.Name, Floor(drawnY), Ceil(drawnY), yAxis.Unit),
            Series = series,
            Notes =
            [
                $"Every design on this line is optimal: none of them can be improved in {xAxis.Name} without "
                + $"giving up {yAxis.Name}. Which one to build is a judgement about what the engine is for, and "
                + "that is not a judgement an optimiser can make.",
                Baseline is null
                    ? "No baseline was scored."
                    : $"The baseline sits at {Baseline.Objectives[xObjective]:N0} {xAxis.Unit} / "
                      + $"{Baseline.Objectives[yObjective]:N3} {yAxis.Unit}. Anything up and to the "
                      + $"{(xAxis.Sense == ObjectiveSense.Maximise ? "right" : "left")} of it is a strict "
                      + "improvement on what you already have.",
            ],
        };
    }

    /// <summary>
    /// Three or more objectives at once, as parallel coordinates (plan §9.6:
    /// <i>"which is more readable than a 3D front"</i>).
    ///
    /// Each design is a line across the axes, each axis normalised to its own
    /// range so they can share a plot. A line that stays high across every
    /// axis is a design that wins everywhere; the crossings between axes are
    /// the trades, and they are visible here in a way a rotated 3-D scatter
    /// never is.
    /// </summary>
    public PlotModel ParallelCoordinates()
    {
        var objectives = ObjectiveSet();
        var front = FrontDesigns;

        if (front.Count == 0 || objectives.Count < 2)
        {
            return Empty("Parallel coordinates", "Needs a front over at least two objectives.");
        }

        var axes = Enumerable.Range(0, objectives.Count).Select(i => (double)i).ToList();

        // Per-axis range over the front, so each axis uses its full height.
        var ranges = Enumerable.Range(0, objectives.Count).Select(i =>
        {
            var values = front.Select(d => d.Objectives[i]).Where(double.IsFinite).ToList();
            return values.Count == 0 ? (Min: 0.0, Max: 1.0) : (Min: values.Min(), Max: values.Max());
        }).ToList();

        double Normalise(int axis, double value)
        {
            var (min, max) = ranges[axis];
            var span = max - min;
            var unit = span > 0 ? (value - min) / span : 0.5;

            // Always drawn so that UP IS BETTER, whatever the objective's own
            // sense. A plot where some axes mean "more" and others mean "less"
            // is one every reader misreads at least once.
            return objectives.Objectives[axis].Sense == ObjectiveSense.Maximise ? unit : 1.0 - unit;
        }

        var series = new List<PlotSeries>();

        // Cap the number of lines drawn: a front of six thousand designs is
        // an unreadable smear, and the point of this figure is to be read.
        var step = Math.Max(1, front.Count / 40);
        for (var i = 0; i < front.Count; i += step)
        {
            var design = front[i];
            var selected = Selected is { } s && ReferenceEquals(s, design);

            series.Add(new PlotSeries(
                selected ? "Selected" : $"#{i}",
                axes,
                axes.Select(a => Normalise((int)a, design.Objectives[(int)a])).ToList(),
                selected ? "Brush.Success" : "Brush.BorderSubtle",
                selected ? PlotSeriesKind.Line : PlotSeriesKind.Dotted));
        }

        if (Baseline is { Feasible: true })
        {
            series.Add(new PlotSeries(
                "Baseline",
                axes,
                axes.Select(a => Normalise((int)a, Baseline.Objectives[(int)a])).ToList(),
                "Brush.Warning",
                PlotSeriesKind.Dashed));
        }

        return new PlotModel
        {
            Title = "Objectives together",
            Subtitle = $"{front.Count} front designs · every axis normalised to its own range, up is better",
            XAxis = new PlotAxis("", -0.2, objectives.Count - 0.8, "", axes),
            YAxis = new PlotAxis("Better →", 0, 1, ""),
            Series = series,
            Notes =
            [
                "Axes, left to right: " + string.Join(
                    ", ", objectives.Objectives.Select((o, i) => $"{i} {o.Name} ({ranges[i].Min:G4}–{ranges[i].Max:G4})")),
                "A line that stays high everywhere wins outright. Where lines CROSS between two axes, that pair "
                + "is a genuine trade — and the crossings are what a three-dimensional front hides.",
                step > 1 ? $"Showing every {step}th design of {front.Count}, for legibility." : "",
            ],
        };
    }

    // ---- Click to inspect ---------------------------------------------------

    /// <summary>
    /// The selected design's geometry, in the units a fabricator works in.
    ///
    /// Plan §9.7: <i>"always show the geometry, not just the number."</i> A
    /// front point that cannot be turned back into millimetres is a score, and
    /// nobody builds a score.
    /// </summary>
    public IReadOnlyList<DerivedReadout> InspectGeometry()
    {
        if (Selected is not { } selected || Archive is null)
        {
            return [];
        }

        var readouts = new List<DerivedReadout>();
        var baseline = Baseline?.Design.Values;

        for (var i = 0; i < Archive.Space.Variables.Count; i++)
        {
            var variable = Archive.Space.Variables[i];
            var value = selected.Values[i];
            var note = baseline is not null && i < baseline.Count
                ? $"baseline {baseline[i]:G6} — {(value - baseline[i]):+0.##;-0.##;no change}"
                : null;

            readouts.Add(new DerivedReadout(variable.Name, $"{value:G6}", note));
        }

        if (selected.Point(Archive.Space).BoundWarning() is { } warning)
        {
            readouts.Add(new DerivedReadout("Bound-limited", "yes", warning, warning));
        }

        return readouts;
    }

    /// <summary>
    /// The selected design's torque curve against the baseline's.
    ///
    /// <b>This is the figure that makes a front point trustworthy.</b> An area
    /// under a curve is one number, and a design that won it by spiking at one
    /// speed while collapsing either side is not the design anyone wanted. The
    /// only way to know which happened is to look at the curve.
    ///
    /// Solved at full fidelity on demand, for this one design out of hundreds
    /// — which the cache makes nearly free when the design was already solved.
    /// </summary>
    public PlotModel InspectTorque(IDesignEvaluator? evaluator = null, CancellationToken cancellation = default)
    {
        if (Selected is not { } selected || Archive is null)
        {
            return Empty("Torque curve", "Select a design on the front to inspect it.");
        }

        var problem = Problem(evaluator);
        var scored = problem.Score(selected.Point(problem.Space), cancellation: cancellation);

        if (scored.Evaluation is not { Sweep.Count: > 0 } evaluation)
        {
            return Empty("Torque curve", scored.Evaluation?.Failure ?? "This design could not be solved.");
        }

        var rpm = evaluation.Sweep.Select(p => p.Rpm).ToList();
        var torque = evaluation.Sweep.Select(p => p.TorqueNm).ToList();

        var series = new List<PlotSeries>
        {
            new("Selected", rpm, torque, "Brush.Accent"),
        };

        if (Baseline?.Evaluation is { Sweep.Count: > 0 } baseline)
        {
            series.Add(new PlotSeries(
                "Baseline",
                baseline.Sweep.Select(p => p.Rpm).ToList(),
                baseline.Sweep.Select(p => p.TorqueNm).ToList(),
                "Brush.TextSecondary",
                PlotSeriesKind.Dashed));
        }

        var all = series.SelectMany(s => s.Y).Where(double.IsFinite).ToList();

        return new PlotModel
        {
            Title = "Torque curve of the selected design",
            Subtitle = string.Join(", ", Archive.Space.Variables.Select((v, i) => $"{v.Name} {selected.Values[i]:G6}")),
            XAxis = new PlotAxis("Engine speed", rpm.Min(), rpm.Max(), "rpm"),
            YAxis = new PlotAxis("Torque", Math.Max(0, Math.Floor(all.Min() / 10) * 10), Math.Ceiling(all.Max() / 10) * 10, "N·m"),
            Series = series,
            Markers = [new PlotMarker(BandFromRpm, "band", "Brush.Info"), new PlotMarker(BandToRpm, "", "Brush.Info")],
            Notes =
            [
                "The curve, not just the area. A design that won its objective by spiking at one speed while "
                + "collapsing either side scores well and drives badly, and this is the only figure that "
                + "distinguishes the two.",
            ],
        };
    }

    // ---- Click to audition ---------------------------------------------------

    /// <summary>
    /// A level-matched A/B of the selected design against the baseline
    /// (plan §9.6's click-to-audition).
    ///
    /// <b>What this auditions is the COLLECTOR geometry the design implies.</b>
    /// The optimiser's variables include primary length, and primary length is
    /// what sets the arrival timing that decides an exhaust's order structure —
    /// so a design that moved it sounds different, and a user choosing a point
    /// on a power-versus-sound front is entitled to hear what they are buying
    /// before they buy it.
    ///
    /// Level-matched, because an unmatched comparison measures loudness rather
    /// than character — the same rule the Sound workspace's own audition
    /// follows.
    /// </summary>
    public AbAudition? Audition(double rpm = 6000.0, double seconds = 3.0)
    {
        if (Selected is not { } selected || Archive is null || Baseline is null)
        {
            return null;
        }

        var baselineDesign = SoundDesignFor(Baseline.Design.Values, "Baseline");
        var selectedDesign = SoundDesignFor(selected.Values, "Optimised");

        return new AbAudition(Stem(baselineDesign), Stem(selectedDesign), targetLufs: -23.0);

        AudioStem Stem(ExhaustSoundDesign design)
        {
            var timing = CollectorTiming.Analyze(design.Branches, rpm);
            var amplitudes = Enumerable.Range(0, design.Branches.Count).Select(design.AmplitudeOf).ToArray();
            var samples = CollectorPulseTrain.Render(
                timing, rpm, seconds, Loudness.SupportedSampleRate, pulseWidthDeg: 18.0, amplitudes);
            return new AudioStem(design.Name, samples, Loudness.SupportedSampleRate);
        }
    }

    /// <summary>
    /// Build the collector the design implies: the document's own firing
    /// geometry with this design's primary length written into it.
    ///
    /// Where the run does not optimise a primary length, both sides of the A/B
    /// are the same collector and the audition is honest about that rather
    /// than manufacturing a difference — see <see cref="AuditionIsMeaningful"/>.
    /// </summary>
    private ExhaustSoundDesign SoundDesignFor(IReadOnlyList<double> values, string name)
    {
        var cylinders = Math.Max(1, Document.Engine.CylinderCount);
        var lengthMm = Document.ExhaustRunner.LengthMm;

        if (Archive is not null)
        {
            var index = Archive.Space.IndexOf("ExhaustRunner.LengthMm");
            if (index >= 0 && index < values.Count)
            {
                lengthMm = values[index];
            }
        }

        // Even firing, which is what the document describes: the manifold
        // canvas owns per-branch geometry, and a run that did not vary it has
        // no business inventing an unequal one here.
        var spacing = 720.0 / cylinders;
        var branches = Enumerable.Range(0, cylinders)
            .Select(i => new CollectorBranch(
                Cylinder: i + 1,
                FiringAngleDeg: i * spacing,
                PrimaryLength: lengthMm / 1000.0,
                MeanSoundSpeed: SoundSpeed(),
                MeanFlowVelocity: 60.0))
            .ToList();

        return new ExhaustSoundDesign { Name = name, Branches = branches };
    }

    /// <summary>
    /// Whether an A/B would actually differ.
    ///
    /// Stated rather than left for the user to discover: if the run never
    /// touched the exhaust geometry, the two stems are identical and the
    /// honest thing is to say so — playing two identical clips and inviting
    /// someone to hear a difference is worse than offering nothing.
    /// </summary>
    public bool AuditionIsMeaningful =>
        Archive is not null && Archive.Space.IndexOf("ExhaustRunner.LengthMm") >= 0;

    private double SoundSpeed()
    {
        // Exhaust gas at the wall temperature the thermal model starts from —
        // the same basis the Sound workspace's instant model uses.
        var temperature = Math.Max(400.0, Document.PipeThermal.ExhaustWallStartK);
        return Math.Sqrt(1.33 * 287.05 * temperature);
    }

    // ---- Archive ------------------------------------------------------------

    /// <summary>The run history, best first, for the Archive tab's table.</summary>
    public IReadOnlyList<ArchiveRow> ArchiveRows(int count = 50)
    {
        if (Archive is null)
        {
            return [];
        }

        var objectives = ObjectiveSet();
        var front = Archive.Front().ToHashSet();

        return Archive.Best(count: count)
            .Select(d => new ArchiveRow(
                string.Join(", ", Archive.Space.Variables.Select((v, i) => $"{v.Name} {d.Values[i]:G6}")),
                objectives.Objectives.Select((o, i) => $"{d.Objectives[i]:N3} {o.Unit}".TrimEnd()).ToList(),
                d.Feasible,
                front.Contains(d),
                d.Fidelity == EvaluationFidelity.Solved ? "solved" : "surrogate"))
            .ToList();
    }

    /// <summary>What the run cost and what it found.</summary>
    public ArchiveSummary? Summary() => Archive?.Summary();

    // ---- Internals ----------------------------------------------------------

    private static PlotModel Empty(string title, string why) => new()
    {
        Title = title,
        Subtitle = "nothing to draw",
        XAxis = new PlotAxis("", 0, 1),
        YAxis = new PlotAxis("", 0, 1),
        Notes = [why],
    };

    private static double Floor(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).ToList();
        if (finite.Count == 0)
        {
            return 0;
        }

        var min = finite.Min();
        var max = finite.Max();
        var pad = Math.Max((max - min) * 0.1, Math.Abs(min) * 0.01);
        return min - pad;
    }

    private static double Ceil(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).ToList();
        if (finite.Count == 0)
        {
            return 1;
        }

        var min = finite.Min();
        var max = finite.Max();
        var pad = Math.Max((max - min) * 0.1, Math.Abs(max) * 0.01);
        return max + pad;
    }
}

/// <summary>One row of the archive table.</summary>
/// <param name="Design">The geometry, in model units.</param>
/// <param name="Objectives">Objective values, formatted with their units.</param>
/// <param name="Feasible">Whether it satisfied every hard constraint.</param>
/// <param name="OnFront">Whether it is non-dominated.</param>
/// <param name="Fidelity">What was spent measuring it.</param>
public sealed record ArchiveRow(
    string Design, IReadOnlyList<string> Objectives, bool Feasible, bool OnFront, string Fidelity);
