using System.Globalization;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.Optimize;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels;

/// <summary>One value of the swept parameter and what the engine did with it.</summary>
/// <param name="Value">The value, in MODEL units.</param>
/// <param name="Display">The same value in the user's display units, formatted.</param>
/// <param name="Sweep">Operating points in rpm order; empty when the design failed.</param>
/// <param name="Failure">Why this value produced nothing, or null.</param>
public sealed record ShowMeSample(
    double Value,
    string Display,
    IReadOnlyList<OperatingPointResult> Sweep,
    string? Failure = null)
{
    public bool Failed => Failure is not null;

    public double PeakTorqueNm => Sweep.Count == 0 ? double.NaN : Sweep.Max(p => p.TorqueNm);

    /// <summary>
    /// Where peak torque lands — <b>quantised to the sampled speeds.</b> With
    /// five operating points it can only take five values, so it shows a big
    /// shift and cannot resolve a small one. Read it as a coarse indicator,
    /// never as an answer to "what rpm does this engine peak at".
    /// </summary>
    public double RpmAtPeakTorque => Sweep.Count == 0 ? double.NaN : Sweep.MaxBy(p => p.TorqueNm)!.Rpm;

    /// <summary>Torque at the lowest speed sampled.</summary>
    public double LowEndTorqueNm => Sweep.Count == 0 ? double.NaN : Sweep[0].TorqueNm;

    /// <summary>Torque at the highest speed sampled.</summary>
    public double TopEndTorqueNm => Sweep.Count == 0 ? double.NaN : Sweep[^1].TorqueNm;

    /// <summary>
    /// Low-end torque over top-end torque: WHERE the torque is, rather than
    /// how much of it there is.
    ///
    /// This is the well-conditioned way to read a tuning change on a coarse
    /// speed grid. <see cref="RpmAtPeakTorque"/> can only move in jumps the
    /// size of the sampling interval; this moves continuously, because both
    /// its terms do.
    /// </summary>
    public double LowToTopRatio => TopEndTorqueNm is 0 or double.NaN ? double.NaN : LowEndTorqueNm / TopEndTorqueNm;

    public double PeakPowerKw => Sweep.Count == 0 ? double.NaN : Sweep.Max(p => p.PowerW) / 1000.0;

    /// <summary>Trapezium area under the torque curve, N·m·rpm — the usual single-number score.</summary>
    public double AreaUnderTorque
    {
        get
        {
            if (Sweep.Count < 2)
            {
                return double.NaN;
            }

            var area = 0.0;
            for (var i = 1; i < Sweep.Count; i++)
            {
                area += (Sweep[i].Rpm - Sweep[i - 1].Rpm) * (Sweep[i].TorqueNm + Sweep[i - 1].TorqueNm) / 2.0;
            }

            return area;
        }
    }
}

/// <summary>
/// The answer to "what does this one number actually do?" — the samples, the
/// two figures, and a sentence saying what they show.
/// </summary>
/// <param name="Path">The field that was swept.</param>
/// <param name="Label">Its label.</param>
/// <param name="Unit">Display unit the values are quoted in.</param>
/// <param name="Baseline">The document's current value, in model units.</param>
/// <param name="Samples">One per swept value, in ascending order.</param>
/// <param name="Response">Peak torque and the speed it happens at, against the parameter.</param>
/// <param name="Curves">The torque curves themselves, one per value.</param>
/// <param name="Narration">Plain-language reading of the figures (plan §8.9 "Explain this result").</param>
/// <param name="Elapsed">What it cost, so the next one can be budgeted.</param>
public sealed record ShowMeStudy(
    string Path,
    string Label,
    string Unit,
    double Baseline,
    IReadOnlyList<ShowMeSample> Samples,
    PlotModel Response,
    PlotModel Curves,
    string Narration,
    TimeSpan Elapsed)
{
    public IReadOnlyList<PlotModel> AllPlots() => [Response, Curves];

    /// <summary>Samples that actually solved.</summary>
    public IReadOnlyList<ShowMeSample> Solved => Samples.Where(s => !s.Failed).ToList();

    /// <summary>
    /// Whether the parameter moved the answer at all. A flat response is a
    /// RESULT — "this does nothing here" is worth knowing, and is the honest
    /// output for a field the steady solve does not read.
    /// </summary>
    public bool Flat
    {
        get
        {
            var solved = Solved;
            if (solved.Count < 2)
            {
                return true;
            }

            var peaks = solved.Select(s => s.PeakTorqueNm).ToList();
            var span = peaks.Max() - peaks.Min();
            return span < 1e-6 * Math.Max(1.0, Math.Abs(peaks.Max()));
        }
    }
}

/// <summary>
/// The state behind the "Show me" button: which field is being studied, whether
/// it is still running, and what came back.
///
/// Lives here rather than in the view because a sweep takes seconds and the
/// screen has to stay usable through it — which means the result arrives after
/// the click, into state, rather than being returned to the handler that
/// started it. The renderers own no part of that.
/// </summary>
public sealed class ShowMeController
{
    private CancellationTokenSource? _cancellation;

    /// <summary>The field currently shown, or null when the panel is closed.</summary>
    public string? Path { get; private set; }

    public bool IsRunning { get; private set; }

    public ShowMeStudy? Study { get; private set; }

    /// <summary>Set when the sweep could not be run at all, with the reason.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised when the state changes, so the view can redraw.</summary>
    public Action? Changed { get; set; }

    /// <summary>
    /// Start a sweep, replacing whatever was showing. Returns the task so a
    /// test can await it; the UI ignores it and redraws on <see cref="Changed"/>.
    /// </summary>
    public Task StartAsync(EngineModelDocument document, UserPreferences preferences, string path)
    {
        Cancel();

        Path = path;
        Study = null;
        Error = null;
        IsRunning = true;
        Changed?.Invoke();

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        return Task.Run(() =>
        {
            try
            {
                var study = new ShowMe(document, preferences).Run(path, cancellation.Token);
                if (!cancellation.IsCancellationRequested)
                {
                    Study = study;
                }
            }
            catch (OperationCanceledException)
            {
                // A cancelled sweep is not a failure; the panel simply closes.
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                Error = e.Message;
            }
            finally
            {
                IsRunning = false;
                Changed?.Invoke();
            }
        }, cancellation.Token);
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        IsRunning = false;
    }

    /// <summary>Close the panel.</summary>
    public void Close()
    {
        Cancel();
        Path = null;
        Study = null;
        Error = null;
        Changed?.Invoke();
    }
}

/// <summary>
/// "Show me" (plan §8.9): a small parametric sweep of ONE parameter with
/// everything else held fixed, plotted inline. <i>"What does runner length
/// actually do?" becomes a ten-second experiment instead of a forum
/// argument.</i>
///
/// <b>It solves. It does not estimate.</b> The whole value of the feature is
/// that the curve on screen is the same physics the rest of the tool runs, so
/// a user can trust what it taught them. What makes it cheap is the surrogate
/// fidelity — a coarser mesh and a shorter cycle budget, the same trade
/// <see cref="SweepEvaluator"/> already makes for the optimiser's inner loop —
/// not a different model.
/// </summary>
public sealed class ShowMe(EngineModelDocument document, UserPreferences preferences)
{
    /// <summary>
    /// Numeric fields the steady sweep does not read, each with the reason.
    ///
    /// Stated by name rather than detected, and a test asserts that every
    /// numeric catalogue field is either sweepable or listed here — so a new
    /// field cannot quietly join the excluded set, and an excluded one that
    /// starts mattering is caught by the test that checks each of these really
    /// is flat.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotInTheSteadySolve { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Engine.CylinderCount"] =
                "Changing it changes the manifold topology, so nothing else can be held fixed — which is what a "
                + "single-parameter sweep means. Compare two models instead.",
            ["PipeThermal.ArealHeatCapacityJPerM2K"] =
                "It sets how fast the pipe wall reaches its converged temperature, not what that temperature is. "
                + "The steady answer is the converged one.",
            ["PipeThermal.WallConvergenceK"] =
                "A solver tolerance: it decides when to stop iterating the wall, not what the engine does.",
            ["ForcedInduction.TransientUncertaintyPercent"] =
                "A sensitivity band on the transient. The steady sweep never reads it.",
        };

    /// <summary>Whether "Show me" is offered for a field.</summary>
    public static bool Supports(IEditableField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Kind is FieldKind.Number or FieldKind.Integer
               && !NotInTheSteadySolve.ContainsKey(field.Path);
    }

    /// <summary>Operating points each sample solves. Five is enough to see a curve move.</summary>
    public IReadOnlyList<double> Speeds { get; set; } = [3000, 4500, 6000, 7500, 9000];

    /// <summary>
    /// How many values of the parameter to try.
    ///
    /// Four, because four is how many line styles a reader can tell apart
    /// without colour (plan §8.11, and the convention the rest of the plots
    /// keep): solid, dashed, dotted and points. A fifth curve would have to
    /// repeat one, and two dotted series on the same axes are distinguishable
    /// by colour alone — which is the one thing a chart here may not be.
    /// </summary>
    public int Steps { get; set; } = 4;

    /// <summary>
    /// Surrogate by default — this is a teaching sweep, and 25 full solves is
    /// not a ten-second experiment. <see cref="EvaluationFidelity.Solved"/> is
    /// available for a user who wants the real numbers and will wait.
    /// </summary>
    public EvaluationFidelity Fidelity { get; set; } = EvaluationFidelity.Surrogate;

    /// <summary>
    /// Mesh coarsening for the surrogate. 2.0 is the optimiser's own setting,
    /// and is the one that was measured to leave the ranking between designs
    /// untouched — so it is the one used here rather than something cheaper
    /// and unverified.
    /// </summary>
    public double SurrogateCellScale { get; set; } = 2.0;

    /// <summary>Cycle cap for the surrogate.</summary>
    public int SurrogateMaxCycles { get; set; } = 6;

    /// <summary>
    /// The values the sweep will try, in model units: the typical range
    /// clipped to the plausible bounds, widened to include wherever the
    /// document currently sits.
    ///
    /// Including the current value matters. A sweep that brackets a design
    /// without containing it shows the user somebody else's engine, and the
    /// first question they will ask is where theirs is on the chart.
    /// </summary>
    public IReadOnlyList<double> Values(IEditableField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var current = Current(field);
        var (low, high) = Span(field, current);

        if (high - low < 1e-12)
        {
            return [current];
        }

        var steps = Math.Max(2, Steps);
        var values = new List<double>(steps);
        for (var i = 0; i < steps; i++)
        {
            var value = low + ((high - low) * i / (steps - 1));
            values.Add(field.Kind == FieldKind.Integer ? Math.Round(value) : value);
        }

        return values.Distinct().OrderBy(v => v).ToList();
    }

    /// <summary>Run the sweep.</summary>
    public ShowMeStudy Run(string path, CancellationToken cancellation = default)
    {
        var field = FieldLocator.Find(path)
                    ?? throw new ArgumentException($"No catalogued field at '{path}'.", nameof(path));

        if (!Supports(field))
        {
            var why = NotInTheSteadySolve.TryGetValue(field.Path, out var reason)
                ? reason
                : "Only numeric parameters can be swept.";
            throw new InvalidOperationException($"\"Show me\" does not apply to {field.Label}. {why}");
        }

        var started = DateTime.UtcNow;
        var editor = new FieldEditor(new ProjectSession(document), preferences);
        var unit = editor.DisplayUnit(field);
        var values = Values(field);

        if (values.Count < 2)
        {
            throw new InvalidOperationException(
                $"{field.Label} has no range left to sweep: its bounds and this model's value coincide.");
        }

        // The sweep IS a one-variable optimisation space, so it is built as
        // one. That is not cleverness for its own sake: DesignPoint already
        // owns the deep copy, the discrete snapping and the write-through, and
        // a second implementation of those here would be a second place for
        // them to disagree with what the optimiser actually does.
        var space = new DesignSpace([
            new OptimisationVariable(field.Path, values[0], values[^1])
            {
                Label = field.Label,
                Step = field.Kind == FieldKind.Integer ? 1.0 : null,
            },
        ]);

        var samples = SolveGrid(space, field, values, editor, cancellation);

        var elapsed = DateTime.UtcNow - started;
        var baseline = Current(field);
        var response = ResponsePlot(field, unit, samples, editor, baseline);
        var curves = CurvePlot(field, unit, samples);
        var study = new ShowMeStudy(
            field.Path, field.Label, unit, baseline, samples, response, curves, "", elapsed);

        return study with { Narration = Narrate(study, field, editor) };
    }

    // ---- Internals --------------------------------------------------------

    private double Current(IEditableField field) =>
        Convert.ToDouble(ModelPath.GetOrDefault(document, field.Path) ?? 0.0, CultureInfo.InvariantCulture);

    /// <summary>The band to sweep, in model units.</summary>
    private static (double Low, double High) Span(IEditableField field, double current)
    {
        var low = field.Typical?.Minimum ?? field.Minimum ?? current * 0.5;
        var high = field.Typical?.Maximum ?? field.Maximum ?? current * 1.5;

        // Widen to include where the user actually is, then clip to what the
        // field will accept — in that order, because a current value outside
        // the plausible bounds cannot be shown and clipping first would let a
        // widened end escape them.
        low = Math.Min(low, current);
        high = Math.Max(high, current);

        if (field.Minimum is { } min)
        {
            low = Math.Max(low, min);
            high = Math.Max(high, min);
        }

        if (field.Maximum is { } max)
        {
            high = Math.Min(high, max);
            low = Math.Min(low, max);
        }

        return (low, high);
    }

    /// <summary>
    /// Solve every (value, speed) pair, parallel across the WHOLE grid.
    ///
    /// This is the difference between a teaching feature and a coffee break.
    /// Solving value by value, with only the speeds inside each one running
    /// together, keeps as many threads busy as there are operating points —
    /// five — however many cores the machine has: measured at 17 s for twelve
    /// surrogate solves. The pairs are mutually independent and each is
    /// deterministic, so flattening them costs nothing and the order they
    /// finish in cannot change an answer.
    /// </summary>
    private IReadOnlyList<ShowMeSample> SolveGrid(
        DesignSpace space,
        IEditableField field,
        IReadOnlyList<double> values,
        FieldEditor editor,
        CancellationToken cancellation)
    {
        var low = values[0];
        var high = values[^1];

        // Materialise first, serially: it is a save/load round trip per value
        // and it is where an out-of-range or structurally invalid variant is
        // caught, before anything expensive has been spent on it.
        var variants = new EngineModelDocument?[values.Count];
        var failures = new string?[values.Count];

        for (var i = 0; i < values.Count; i++)
        {
            var coordinate = high > low ? (values[i] - low) / (high - low) : 0.0;

            try
            {
                var variant = new DesignPoint(space, [coordinate]).Materialise(document);
                var errors = variant.Validate()
                    .Where(issue => issue.Severity == ModelIssueSeverity.Error)
                    .Select(issue => issue.Message)
                    .ToList();

                if (errors.Count > 0)
                {
                    failures[i] = string.Join("; ", errors);
                }
                else
                {
                    variants[i] = Fidelity == EvaluationFidelity.Surrogate
                        ? SweepEvaluator.Coarsen(variant, SurrogateCellScale, SurrogateMaxCycles)
                        : variant;
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                failures[i] = e.Message;
            }
        }

        var points = new OperatingPointResult?[values.Count, Speeds.Count];
        var grid = Enumerable.Range(0, values.Count)
            .Where(i => variants[i] is not null)
            .SelectMany(i => Enumerable.Range(0, Speeds.Count).Select(j => (Value: i, Speed: j)))
            .ToList();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            CancellationToken = cancellation,
        };

        Parallel.ForEach(grid, options, cell =>
        {
            try
            {
                points[cell.Value, cell.Speed] =
                    OperatingPointRunner.Run(variants[cell.Value]!, Speeds[cell.Speed]);
            }
            catch (Exception e) when (e is ArithmeticException or InvalidOperationException or ArgumentException)
            {
                // One speed failing is not the whole value failing, but a
                // partial curve is not a curve — the sample is reported as
                // failed with the reason, rather than drawn with a hole in it.
                failures[cell.Value] ??= $"{Speeds[cell.Speed]:F0} rpm: {e.Message}";
            }
        });

        var samples = new List<ShowMeSample>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var display = editor.Format(field, values[i]);
            var sweep = new List<OperatingPointResult>(Speeds.Count);

            for (var j = 0; j < Speeds.Count; j++)
            {
                if (points[i, j] is { } point)
                {
                    sweep.Add(point);
                }
            }

            // Non-finite anywhere means the solve went non-physical, and an
            // area under a curve with a NaN in it is not a smaller area — it
            // is no area at all. Same rule the optimiser's evaluator applies.
            if (failures[i] is null && sweep.Any(p => !double.IsFinite(p.TorqueNm)))
            {
                failures[i] = "The solve produced a non-finite result — this value is outside what the model "
                              + "can describe.";
            }

            samples.Add(failures[i] is { } why
                ? new ShowMeSample(values[i], display, [], why)
                : new ShowMeSample(values[i], display, sweep.OrderBy(p => p.Rpm).ToList()));
        }

        return samples;
    }

    /// <summary>
    /// Peak torque against the parameter, with the speed it happens at on the
    /// right axis.
    ///
    /// The second series is the teaching one. "Longer runner, more torque" is
    /// only half of what a runner does, and a chart showing peak alone invites
    /// the reader to keep lengthening it; the rpm trace is what shows the
    /// torque being MOVED rather than made.
    /// </summary>
    private PlotModel ResponsePlot(
        IEditableField field,
        string unit,
        IReadOnlyList<ShowMeSample> samples,
        FieldEditor editor,
        double baseline)
    {
        var solved = samples.Where(s => !s.Failed).ToList();
        if (solved.Count == 0)
        {
            return Empty($"What {field.Label} does",
                "No value in the swept range produced a solvable model. " + FailureNote(samples));
        }

        var x = solved.Select(s => editor.ToDisplay(field, s.Value)).ToList();
        var peak = solved.Select(s => s.PeakTorqueNm).ToList();
        var low = solved.Select(s => s.LowEndTorqueNm).ToList();
        var top = solved.Select(s => s.TopEndTorqueNm).ToList();
        var all = peak.Concat(low).Concat(top).ToList();

        var notes = new List<string>
        {
            $"One parameter swept over {solved.Count} values; every other field held at this model's own value.",

            // The three torque series answer "how much"; the ratio answers
            // "where", and a tuning parameter usually moves the second while
            // barely touching the first. A figure with only the peak on it
            // invites the reader to conclude that more is always better.
            $"Low-end is {Speeds[0]:F0} rpm and top-end {Speeds[^1]:F0} rpm. The GAP between those two is what a "
            + "tuning parameter moves; the peak alone hides it.",

            Fidelity == EvaluationFidelity.Surrogate
                ? "Surrogate fidelity — a coarser mesh and a shorter cycle budget. The TREND is the answer here; "
                  + "run the model itself for the numbers."
                : "Solved at the document's own solver settings.",
        };

        if (samples.Any(s => s.Failed))
        {
            notes.Add(FailureNote(samples));
        }

        return new PlotModel
        {
            Title = $"What {field.Label} does",
            Subtitle = $"torque at the ends of the range, and the peak, against {field.Label.ToLowerInvariant()}",
            // The x axis gets a margin too, so the "this model" marker at an
            // end of the swept range has room for its own label instead of
            // being clipped by the frame.
            XAxis = new PlotAxis(field.Label, Range(x).Min, Range(x).Max, unit),
            YAxis = new PlotAxis("Torque", Range(all).Min, Range(all).Max, "N·m"),
            Series =
            [
                new PlotSeries("Peak", x, peak, "Brush.Accent"),
                new PlotSeries($"At {Speeds[0]:F0} rpm", x, low, "Brush.Success", PlotSeriesKind.Dashed),
                new PlotSeries($"At {Speeds[^1]:F0} rpm", x, top, "Brush.Info", PlotSeriesKind.Dotted),
            ],
            Markers = [new PlotMarker(editor.ToDisplay(field, baseline), "this model", "Brush.Info")],
            Notes = notes,
        };
    }

    /// <summary>The torque curves themselves — what actually changed shape.</summary>
    private PlotModel CurvePlot(IEditableField field, string unit, IReadOnlyList<ShowMeSample> samples)
    {
        var solved = samples.Where(s => !s.Failed).ToList();
        if (solved.Count == 0)
        {
            return Empty($"Torque curves across {field.Label.ToLowerInvariant()}", FailureNote(samples));
        }

        var all = solved.SelectMany(s => s.Sweep.Select(p => p.TorqueNm)).ToList();

        // Line style varies with the value as well as colour, so the series
        // are separable without it (plan §8.11: never colour alone). These are
        // every style a curve can take; at the default four values each series
        // gets its own, which is why the default is four.
        PlotSeriesKind[] kinds =
        [
            PlotSeriesKind.Line, PlotSeriesKind.Dashed,
            PlotSeriesKind.Dotted, PlotSeriesKind.Scatter,
        ];

        // Tokens that EXIST. "Brush.Series1..6" looked plausible, resolved to
        // nothing, and every series on both figures rendered in the same
        // fallback grey — which no test caught, because a colour token is a
        // string the view models never resolve themselves. Rendering the
        // screen caught it; XamlTokenTests now does too.
        string[] tokens =
        [
            "Brush.Accent", "Brush.Success", "Brush.Info",
            "Brush.Warning", "Brush.Danger", "Brush.TextSecondary",
        ];

        var series = solved.Select((s, i) => new PlotSeries(
            $"{s.Display} {unit}".Trim(),
            s.Sweep.Select(p => p.Rpm).ToList(),
            s.Sweep.Select(p => p.TorqueNm).ToList(),
            tokens[i % tokens.Length],
            kinds[i % kinds.Length])).ToList();

        return new PlotModel
        {
            Title = $"Torque curves across {field.Label.ToLowerInvariant()}",
            Subtitle = "the same engine, one field changed",
            XAxis = new PlotAxis("Engine speed", Speeds.Min(), Speeds.Max(), "rpm"),
            YAxis = new PlotAxis("Torque", Range(all).Min, Range(all).Max, "N·m"),
            Series = series,
            Notes =
            [
                "Each curve is a full solve of the same model with only this one field changed.",
                ..(solved.Count > kinds.Length
                    ? new[]
                    {
                        $"With {solved.Count} values there are more curves than line styles, so some are told "
                        + "apart by colour — reduce the step count to keep every series distinguishable without it.",
                    }
                    : []),
            ],
        };
    }

    /// <summary>
    /// Plain-language reading of the figures — plan §8.9's "Explain this
    /// result", applied to the study the user just ran.
    ///
    /// Every number in the sentence comes from the samples. Nothing here is a
    /// stock phrase chosen by which way a slope points: a narration that could
    /// be written before the sweep ran would be teaching the user the tool's
    /// assumptions rather than their engine's behaviour.
    /// </summary>
    private static string Narrate(ShowMeStudy study, IEditableField field, FieldEditor editor)
    {
        var solved = study.Solved;
        if (solved.Count == 0)
        {
            return "Nothing in the swept range solved, so there is nothing to read from it yet.";
        }

        if (solved.Count == 1)
        {
            return $"Only one value of {field.Label.ToLowerInvariant()} solved, so there is no trend to read.";
        }

        if (study.Flat)
        {
            return $"Peak torque does not move across this range of {field.Label.ToLowerInvariant()}. "
                   + "On this engine, at these speeds, it is not what is limiting the answer.";
        }

        var best = solved.MaxBy(s => s.AreaUnderTorque)!;
        var first = solved[0];
        var last = solved[^1];
        var unit = string.IsNullOrWhiteSpace(study.Unit) ? "" : " " + study.Unit;
        var name = field.Label.ToLowerInvariant();

        // Read across every sample, not from the two ends. An endpoint
        // comparison calls a hump a rise and a dip a fall, which is exactly
        // the reading the figure exists to correct.
        var peaks = solved.Select(s => s.PeakTorqueNm).ToList();
        var most = solved.MaxBy(s => s.PeakTorqueNm)!;
        var shape = ReferenceEquals(most, last) ? $"rises across the whole range to {last.PeakTorqueNm:F1} N·m"
            : ReferenceEquals(most, first) ? $"falls across the whole range from {first.PeakTorqueNm:F1} N·m"
            : $"peaks in the middle of the range, at {most.PeakTorqueNm:F1} N·m around {most.Display}{unit}";

        // Where the torque sits, which is what a tuning parameter really
        // moves. Stated as a ratio because rpm-at-peak is quantised to the
        // speeds sampled and can only move in jumps that size.
        var moved = Math.Abs(last.LowToTopRatio - first.LowToTopRatio) > 0.01
            ? $" Torque also moves along the range: at {first.Display}{unit} the bottom end makes "
              + $"{first.LowToTopRatio:P0} of what the top end does, and at {last.Display}{unit} it makes "
              + $"{last.LowToTopRatio:P0}."
            : " Where the torque sits in the rev range barely changes.";

        var here = solved.MinBy(s => Math.Abs(s.Value - study.Baseline))!;
        var atBaseline = editor.Format(field, study.Baseline);
        var exact = Math.Abs(here.Value - study.Baseline) < 1e-9;
        var nearest = exact
            ? $"this model sits at {atBaseline}{unit}"
            : $"this model sits at {atBaseline}{unit}, between the values tried — the nearest is {here.Display}{unit}";

        return $"Peak torque ranges from {peaks.Min():F1} to {peaks.Max():F1} N·m across this range of {name}, "
               + $"and {shape}.{moved} The widest torque curve of the {solved.Count} tried is at "
               + $"{best.Display}{unit}; {nearest}"
               + (ReferenceEquals(here, best)
                   ? exact ? ", which is that value." : ", which is the best of those tried."
                   : $", worth {Percent(here.AreaUnderTorque, best.AreaUnderTorque)} of it under the curve.")
               + " One parameter at a time, so an interaction with anything else is not in this figure.";
    }

    private static string Percent(double value, double of) =>
        of is 0 || !double.IsFinite(of) || !double.IsFinite(value) ? "an unknown share" : $"{value / of:P1}";

    private static string FailureNote(IReadOnlyList<ShowMeSample> samples)
    {
        var failed = samples.Where(s => s.Failed).ToList();
        return failed.Count == 0
            ? ""
            : $"{failed.Count} of {samples.Count} values did not solve: "
              + string.Join("; ", failed.Select(f => $"{f.Display} — {f.Failure}").Distinct().Take(2));
    }

    private static PlotModel Empty(string title, string why) => new()
    {
        Title = title,
        Subtitle = "nothing to draw",
        XAxis = new PlotAxis("", 0, 1),
        YAxis = new PlotAxis("", 0, 1),
        Notes = [why],
    };

    /// <summary>
    /// Axis ends, padded by a fraction of the SPAN rather than of the value.
    ///
    /// Padding by a fraction of the value is what flattened the first draft of
    /// the response figure: torque running 47–52 N·m padded by ±10% of the
    /// VALUE gives an axis of 42–57, so the variation the figure exists to
    /// show occupied a third of the height. A margin belongs to the range, not
    /// to where the range happens to sit on the number line.
    /// </summary>
    private static (double Min, double Max) Range(IReadOnlyList<double> values, double margin = 0.08)
    {
        var finite = values.Where(double.IsFinite).ToList();
        var min = finite.DefaultIfEmpty(0).Min();
        var max = finite.DefaultIfEmpty(1).Max();
        var span = max - min;

        if (span < 1e-9)
        {
            // A flat series still needs an axis to sit in, and a zero-height
            // one draws the line along the frame.
            var pad = Math.Max(1e-6, Math.Abs(max) * 0.05);
            return (min - pad, max + pad);
        }

        return (min - (span * margin), max + (span * margin));
    }
}
