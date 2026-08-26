namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// Engine speed versus time for a render (plan §3.6: "support user-drawn rpm
/// profiles — idle → redline, limiter bounce, upshift cuts, decel with
/// burble"). Breakpoints are linearly interpolated.
/// </summary>
public sealed class RpmProfile
{
    private readonly List<(double Time, double Rpm)> _points = [];

    public IReadOnlyList<(double Time, double Rpm)> Points => _points;

    public double Duration => _points.Count > 0 ? _points[^1].Time : 0.0;

    public RpmProfile Add(double timeSeconds, double rpm)
    {
        if (_points.Count > 0 && timeSeconds < _points[^1].Time)
        {
            throw new ArgumentException("Profile points must be added in time order.", nameof(timeSeconds));
        }

        _points.Add((timeSeconds, rpm));
        return this;
    }

    public double RpmAt(double time)
    {
        if (_points.Count == 0)
        {
            throw new InvalidOperationException("Profile is empty.");
        }

        if (time <= _points[0].Time)
        {
            return _points[0].Rpm;
        }

        if (time >= _points[^1].Time)
        {
            return _points[^1].Rpm;
        }

        var i = 1;
        while (i < _points.Count - 1 && _points[i].Time < time)
        {
            i++;
        }

        var (t0, r0) = _points[i - 1];
        var (t1, r1) = _points[i];
        var w = (time - t0) / (t1 - t0);
        return r0 + w * (r1 - r0);
    }

    /// <summary>Steady speed for the given duration.</summary>
    public static RpmProfile Steady(double rpm, double seconds) =>
        new RpmProfile().Add(0.0, rpm).Add(seconds, rpm);

    /// <summary>Linear sweep — the classic pull.</summary>
    public static RpmProfile Sweep(double fromRpm, double toRpm, double seconds) =>
        new RpmProfile().Add(0.0, fromRpm).Add(seconds, toRpm);

    /// <summary>Pull to the limiter, bounce off it, then decel — exercises every path.</summary>
    public static RpmProfile PullAndDecel(double idleRpm, double limiterRpm, double pullSeconds)
    {
        var profile = new RpmProfile().Add(0.0, idleRpm).Add(pullSeconds, limiterRpm);
        var t = pullSeconds;
        for (var bounce = 0; bounce < 3; bounce++)
        {
            profile.Add(t + 0.06, limiterRpm - 350.0).Add(t + 0.12, limiterRpm);
            t += 0.12;
        }

        return profile.Add(t + pullSeconds * 0.8, idleRpm);
    }
}

/// <summary>
/// Throttle position against time, as intake manifold pressure fraction —
/// 1.0 wide open, ~0.35 a light cruise. The second axis of the §3.6
/// wavetable grid, and the difference between auditioning a pull and
/// auditioning the cruise drone that actually makes a car tiring to drive.
///
/// Deliberately the same shape as <see cref="RpmProfile"/>: piecewise-linear
/// breakpoints, held flat outside the range.
/// </summary>
public sealed class LoadProfile
{
    private readonly List<(double Time, double Load)> _points = [];

    public IReadOnlyList<(double Time, double Load)> Points => _points;

    public double Duration => _points.Count > 0 ? _points[^1].Time : 0.0;

    public LoadProfile Add(double timeSeconds, double load)
    {
        if (_points.Count > 0 && timeSeconds < _points[^1].Time)
        {
            throw new ArgumentException("Profile points must be added in time order.", nameof(timeSeconds));
        }

        if (load is <= 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(load), load, "Load is manifold pressure as a fraction of ambient, in (0, 1].");
        }

        _points.Add((timeSeconds, load));
        return this;
    }

    public double LoadAt(double time)
    {
        if (_points.Count == 0)
        {
            throw new InvalidOperationException("Profile is empty.");
        }

        if (time <= _points[0].Time)
        {
            return _points[0].Load;
        }

        if (time >= _points[^1].Time)
        {
            return _points[^1].Load;
        }

        var i = 1;
        while (i < _points.Count - 1 && _points[i].Time < time)
        {
            i++;
        }

        var (t0, l0) = _points[i - 1];
        var (t1, l1) = _points[i];
        return l0 + (time - t0) / (t1 - t0) * (l1 - l0);
    }

    /// <summary>Constant throttle — wide open by default, which is the classic pull.</summary>
    public static LoadProfile Constant(double load, double seconds) =>
        new LoadProfile().Add(0.0, load).Add(seconds, load);

    /// <summary>
    /// Lift off the throttle partway through: the transition that reveals
    /// overrun character, and where a drone shows up if it is going to.
    /// </summary>
    public static LoadProfile LiftOff(double seconds, double liftAtSeconds, double cruiseLoad = 0.35) =>
        new LoadProfile()
            .Add(0.0, 1.0)
            .Add(liftAtSeconds, 1.0)
            .Add(Math.Min(liftAtSeconds + 0.15, seconds), cruiseLoad)
            .Add(seconds, cruiseLoad);
}
