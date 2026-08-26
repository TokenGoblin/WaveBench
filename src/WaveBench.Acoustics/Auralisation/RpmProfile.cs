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
