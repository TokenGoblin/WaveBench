using WaveBench.Core.Solver;

namespace WaveBench.Optimize;

/// <summary>Which way is better.</summary>
public enum ObjectiveSense
{
    Maximise,
    Minimise,
}

/// <summary>
/// Something the optimiser is trying to move (plan §9.2).
///
/// Every objective returns its value in its OWN units — newton-metre-rpm,
/// kilowatts, seconds, decibels — and never a normalised score. The search
/// normalises when it has to; a Pareto front drawn in the objectives' own
/// units is the one a user can read.
/// </summary>
public interface IObjective
{
    string Name { get; }

    string Unit { get; }

    ObjectiveSense Sense { get; }

    /// <summary>
    /// The value, or NaN when this evaluation cannot answer — a surrogate
    /// asked for a quantity only a solve produces, say. NaN is handled as
    /// "unknown", never as "bad": scoring an unknown as a loss teaches the
    /// search to avoid the region rather than to evaluate it properly.
    /// </summary>
    double Value(DesignEvaluation evaluation);
}

/// <summary>
/// The plan's default objective (§9.2): <i>"weighted area under the torque
/// curve over a user-defined rpm band — the correct objective for a race
/// car"</i>.
///
/// Area rather than peak, because a peak is one point and a lap is the whole
/// band: an engine that makes 5 N·m more at its peak and 15 less either side
/// is slower everywhere that matters, and a peak-torque objective would
/// choose it.
///
/// Integrated by the trapezium rule over the sweep points inside the band,
/// with linear interpolation at the band edges so that moving the band by a
/// few rpm moves the objective smoothly. A step there would make the search
/// surface discontinuous for no physical reason.
/// </summary>
/// <param name="FromRpm">Lower edge of the band.</param>
/// <param name="ToRpm">Upper edge.</param>
/// <param name="Weight">
/// Optional weight against rpm, for a user who cares more about part of the
/// band. Null weights every rpm equally.
/// </param>
public sealed record AreaUnderTorque(double FromRpm, double ToRpm, Func<double, double>? Weight = null) : IObjective
{
    public string Name => $"Area under torque {FromRpm:F0}–{ToRpm:F0} rpm";

    public string Unit => "N·m·rpm";

    public ObjectiveSense Sense => ObjectiveSense.Maximise;

    public double Value(DesignEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return Integrate(evaluation.Sweep, FromRpm, ToRpm, p => p.TorqueNm, Weight);
    }

    /// <summary>
    /// Trapezium integration of a sweep quantity over a band, with linear
    /// interpolation at both edges.
    /// </summary>
    internal static double Integrate(
        IReadOnlyList<OperatingPointResult> sweep,
        double fromRpm,
        double toRpm,
        Func<OperatingPointResult, double> select,
        Func<double, double>? weight)
    {
        if (sweep.Count < 2 || toRpm <= fromRpm)
        {
            return double.NaN;
        }

        var points = sweep.OrderBy(p => p.Rpm).ToList();

        // Sample the curve on its own points plus the two band edges, so the
        // band's ends contribute their real partial trapezia.
        var samples = new List<(double Rpm, double Value)>();

        void AddAt(double rpm)
        {
            var value = Interpolate(points, select, rpm);
            if (double.IsFinite(value))
            {
                samples.Add((rpm, value));
            }
        }

        AddAt(fromRpm);
        foreach (var point in points.Where(p => p.Rpm > fromRpm && p.Rpm < toRpm))
        {
            var value = select(point);
            if (double.IsFinite(value))
            {
                samples.Add((point.Rpm, value));
            }
        }

        AddAt(toRpm);

        if (samples.Count < 2)
        {
            return double.NaN;
        }

        var area = 0.0;
        for (var i = 1; i < samples.Count; i++)
        {
            var (r0, v0) = samples[i - 1];
            var (r1, v1) = samples[i];
            var w0 = weight?.Invoke(r0) ?? 1.0;
            var w1 = weight?.Invoke(r1) ?? 1.0;
            area += 0.5 * ((v0 * w0) + (v1 * w1)) * (r1 - r0);
        }

        return area;
    }

    private static double Interpolate(
        IReadOnlyList<OperatingPointResult> points, Func<OperatingPointResult, double> select, double rpm)
    {
        if (points.Count == 0)
        {
            return double.NaN;
        }

        // Outside the swept range the curve is unknown, not zero. Clamping to
        // the end value would let a design win by simply not being swept where
        // it is bad.
        if (rpm <= points[0].Rpm)
        {
            return Math.Abs(rpm - points[0].Rpm) < 1e-9 ? select(points[0]) : double.NaN;
        }

        if (rpm >= points[^1].Rpm)
        {
            return Math.Abs(rpm - points[^1].Rpm) < 1e-9 ? select(points[^1]) : double.NaN;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (rpm <= points[i].Rpm)
            {
                var t = (rpm - points[i - 1].Rpm) / (points[i].Rpm - points[i - 1].Rpm);
                return select(points[i - 1]) + (t * (select(points[i]) - select(points[i - 1])));
            }
        }

        return double.NaN;
    }
}

/// <summary>Peak of a swept quantity (plan §9.2: peak power, peak torque).</summary>
public sealed record PeakOf(string Name, string Unit, Func<OperatingPointResult, double> Select) : IObjective
{
    public ObjectiveSense Sense => ObjectiveSense.Maximise;

    public double Value(DesignEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var values = evaluation.Sweep.Select(Select).Where(double.IsFinite).ToList();
        return values.Count > 0 ? values.Max() : double.NaN;
    }

    public static PeakOf Power { get; } = new("Peak power", "kW", p => p.PowerW / 1000.0);

    public static PeakOf Torque { get; } = new("Peak torque", "N·m", p => p.TorqueNm);
}

/// <summary>A swept quantity at one speed — "torque at 4000 rpm".</summary>
public sealed record ValueAtRpm(string Name, string Unit, double Rpm, Func<OperatingPointResult, double> Select)
    : IObjective
{
    public ObjectiveSense Sense { get; init; } = ObjectiveSense.Maximise;

    public double Value(DesignEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        var nearest = evaluation.Sweep
            .Where(p => double.IsFinite(Select(p)))
            .OrderBy(p => Math.Abs(p.Rpm - Rpm))
            .FirstOrDefault();

        return nearest is null ? double.NaN : Select(nearest);
    }
}

/// <summary>
/// Cylinder-to-cylinder VE spread (plan §9.2), worst over the band.
///
/// Worst rather than mean: a manifold that feeds the cylinders evenly at
/// 6000 rpm and badly at 3000 is not an even manifold, and averaging hides
/// exactly the speed the user would have wanted to know about.
/// </summary>
public sealed record VolumetricEfficiencySpread : IObjective
{
    public string Name => "Worst cylinder VE spread";

    public string Unit => "fraction of mean";

    public ObjectiveSense Sense => ObjectiveSense.Minimise;

    public double Value(DesignEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        var spreads = evaluation.Sweep
            .Where(p => p.PerCylinderVolumetricEfficiency.Length > 1)
            .Select(p => p.VolumetricEfficiencySpread)
            .Where(double.IsFinite)
            .ToList();

        return spreads.Count > 0 ? spreads.Max() : double.NaN;
    }
}

/// <summary>
/// An objective read from the evaluation's named metrics — what everything
/// the optimiser cannot compute from a sweep alone arrives as: Order Purity
/// Index, distance to a sound target, time-to-torque, surge margin, packaging
/// length.
/// </summary>
public sealed record MetricObjective(string Name, string Unit, string MetricKey, ObjectiveSense Sense) : IObjective
{
    public double Value(DesignEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return evaluation.Metric(MetricKey);
    }
}

/// <summary>
/// The objectives of one run, in order.
///
/// A single-objective optimiser takes the first and a weighted blend of the
/// rest; a multi-objective one takes them all. Keeping them in one list means
/// the same problem definition drives both, and a user can switch from CMA-ES
/// to NSGA-II without restating what they want.
/// </summary>
public sealed class ObjectiveSet
{
    public ObjectiveSet(IReadOnlyList<IObjective> objectives, IReadOnlyList<double>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(objectives);

        if (objectives.Count == 0)
        {
            throw new ArgumentException("An optimisation needs at least one objective.", nameof(objectives));
        }

        if (weights is not null && weights.Count != objectives.Count)
        {
            throw new ArgumentException("One weight per objective, or none at all.", nameof(weights));
        }

        if (objectives.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != objectives.Count)
        {
            throw new ArgumentException("Two objectives share a name; a front would be unreadable.", nameof(objectives));
        }

        Objectives = objectives;
        Weights = weights ?? Enumerable.Repeat(1.0, objectives.Count).ToList();
    }

    public IReadOnlyList<IObjective> Objectives { get; }

    public IReadOnlyList<double> Weights { get; }

    public int Count => Objectives.Count;

    public IReadOnlyList<double> Values(DesignEvaluation evaluation) =>
        Objectives.Select(o => o.Value(evaluation)).ToList();

    /// <summary>
    /// Every objective as a quantity to MINIMISE, which is the only form the
    /// search algorithms deal in. Stating the sense once, here, is what keeps
    /// a sign error out of four separate optimisers.
    /// </summary>
    public IReadOnlyList<double> Costs(DesignEvaluation evaluation) =>
        Objectives.Select(o =>
        {
            var value = o.Value(evaluation);
            return o.Sense == ObjectiveSense.Maximise ? -value : value;
        }).ToList();
}
