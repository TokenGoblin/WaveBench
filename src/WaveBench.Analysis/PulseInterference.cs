using WaveBench.Model;

namespace WaveBench.Analysis;

/// <summary>One cylinder's blowdown pulse, as it reaches a junction.</summary>
/// <param name="Cylinder">1-based cylinder number.</param>
/// <param name="FiringAngleDeg">Where this cylinder fires in the 720° cycle.</param>
/// <param name="BlowdownAngleDeg">Cycle angle at which its exhaust valve cracks open.</param>
/// <param name="PathLengthMm">Distance from its port to the junction.</param>
/// <param name="TransitDeg">Crank degrees the pulse takes to cover that distance.</param>
/// <param name="ArrivalAngleDeg">Cycle angle at which the pulse reaches the junction.</param>
public sealed record PulseArrival(
    int Cylinder,
    double FiringAngleDeg,
    double BlowdownAngleDeg,
    double PathLengthMm,
    double TransitDeg,
    double ArrivalAngleDeg);

/// <summary>Two pulses landing at the junction close enough to fight.</summary>
/// <param name="First">Earlier cylinder.</param>
/// <param name="Second">Later cylinder.</param>
/// <param name="SeparationDeg">Crank degrees between their arrivals, shortest way round the cycle.</param>
/// <param name="Severity">0 to 1; 1 is a simultaneous arrival.</param>
public sealed record PulseCollision(int First, int Second, double SeparationDeg, double Severity);

/// <summary>
/// The pulse-interference diagram (plan §2.8, called out as a REQUIRED
/// artefact: "the most useful visual for choosing cylinder pairing and no
/// consumer tool presents it well").
///
/// For each cylinder it answers one question: when does this cylinder's
/// blowdown pulse arrive at the collector, in crank degrees, and does it land
/// on top of another cylinder's? Two pulses arriving together at a junction
/// push against each other instead of scavenging, which is precisely what
/// cylinder pairing exists to avoid.
///
/// Transit time is <c>L / a</c> with <b>the actual local sound speed</b>, not
/// a nominal — the plan is explicit about this, and it matters: exhaust at
/// 900 K carries a pulse at about 600 m/s against 343 at ambient, so a
/// nominal-speed diagram puts every arrival in the wrong place by nearly a
/// factor of two.
/// </summary>
public static class PulseInterference
{
    /// <summary>
    /// Pulse arrivals at <paramref name="junctionId"/>.
    ///
    /// Engine speed is required, not optional: transit becomes a crank angle
    /// only once a speed is chosen, which is exactly why a collector that
    /// scavenges cleanly at 7000 rpm can be fighting itself at 3000.
    /// </summary>
    /// <param name="manifold">The topology.</param>
    /// <param name="junctionId">Node to measure arrivals at.</param>
    /// <param name="firingOrder">1-based cylinder numbers in firing sequence.</param>
    /// <param name="exhaustValveOpenDeg">EVO in cycle degrees, 0 = firing TDC of cylinder 1.</param>
    /// <param name="soundSpeed">Local exhaust sound speed, m/s — from the solved gas state.</param>
    /// <param name="rpm">Engine speed.</param>
    public static IReadOnlyList<PulseArrival> Arrivals(
        ManifoldSpec manifold,
        string junctionId,
        IReadOnlyList<int> firingOrder,
        double exhaustValveOpenDeg,
        double soundSpeed,
        double rpm)
    {
        ArgumentNullException.ThrowIfNull(manifold);
        if (soundSpeed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(soundSpeed), soundSpeed, "Sound speed must be positive.");
        }

        if (rpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rpm), rpm, "Engine speed must be positive.");
        }

        if (firingOrder.Count == 0)
        {
            return [];
        }

        var interval = 720.0 / firingOrder.Count;
        var degreesPerSecond = 6.0 * rpm;
        var arrivals = new List<PulseArrival>();

        for (var i = 0; i < firingOrder.Count; i++)
        {
            var cylinder = firingOrder[i];
            var port = manifold.Nodes.FirstOrDefault(n =>
                n.Kind == ManifoldNodeKind.Port && n.Cylinder == cylinder);
            if (port is null)
            {
                continue;
            }

            if (manifold.PathLengthMm(port.Id, junctionId) is not { } path)
            {
                continue;
            }

            var firing = i * interval;
            var blowdown = Wrap(firing + exhaustValveOpenDeg);

            // The pulse covers L at a, while the crank turns at 6·N deg/s.
            var transitDeg = path * 1e-3 / soundSpeed * degreesPerSecond;

            arrivals.Add(new PulseArrival(
                cylinder, firing, blowdown, path, transitDeg, Wrap(blowdown + transitDeg)));
        }

        return arrivals;
    }

    /// <summary>
    /// Collisions among a set of arrivals. <paramref name="windowDeg"/> is
    /// how close two arrivals must be to interfere — a blowdown pulse is not
    /// an instant, it has width, and that width is the window.
    /// </summary>
    public static IReadOnlyList<PulseCollision> Collisions(
        IReadOnlyList<PulseArrival> arrivals, double windowDeg = 60.0)
    {
        var collisions = new List<PulseCollision>();

        for (var i = 0; i < arrivals.Count; i++)
        {
            for (var j = i + 1; j < arrivals.Count; j++)
            {
                var separation = Separation(arrivals[i].ArrivalAngleDeg, arrivals[j].ArrivalAngleDeg);
                if (separation >= windowDeg)
                {
                    continue;
                }

                collisions.Add(new PulseCollision(
                    arrivals[i].Cylinder,
                    arrivals[j].Cylinder,
                    separation,
                    1.0 - (separation / windowDeg)));
            }
        }

        return collisions.OrderByDescending(c => c.Severity).ToList();
    }

    /// <summary>
    /// How evenly the arrivals are spread, 0 to 1, where 1 is perfectly
    /// even. This is the single number that ranks one pairing against
    /// another: a 4-1 collector fed by evenly spaced pulses scavenges; one
    /// fed by pulses bunched together does not.
    /// </summary>
    public static double Evenness(IReadOnlyList<PulseArrival> arrivals)
    {
        if (arrivals.Count < 2)
        {
            return 1.0;
        }

        var angles = arrivals.Select(a => a.ArrivalAngleDeg).Order().ToList();
        var ideal = 720.0 / angles.Count;

        var gaps = new List<double>();
        for (var i = 0; i < angles.Count; i++)
        {
            var next = i == angles.Count - 1 ? angles[0] + 720.0 : angles[i + 1];
            gaps.Add(next - angles[i]);
        }

        // Mean absolute deviation from the ideal gap, normalised so that the
        // worst case — every pulse simultaneous — scores 0.
        var deviation = gaps.Average(g => Math.Abs(g - ideal));
        var worst = ideal * 2.0 * (angles.Count - 1) / angles.Count;
        return Math.Clamp(1.0 - (deviation / worst), 0.0, 1.0);
    }

    /// <summary>
    /// Scroll separation index (plan §4.6.2), defined as the plan defines it:
    /// <i>"the overlap between one cylinder's blowdown pressure history and
    /// its scroll-mate's exhaust-stroke window"</i>. 0 is clean separation;
    /// 1 means a cylinder's whole blowdown lands inside a mate's exhaust
    /// stroke, where it pushes back against that piston.
    ///
    /// This is NOT the same as two blowdowns arriving together at a merge
    /// (<see cref="Collisions"/>). Cylinders 1 and 3 of a 1-3-4-2 four fire
    /// 180° apart — far enough that their pulses never coincide, but close
    /// enough that one lands squarely in the other's 250°-wide exhaust
    /// window. Measuring arrival proximity alone reports that pairing as
    /// fine, which is exactly the mistake §4.6.2 exists to prevent.
    ///
    /// The plan calls this a validation test, not just a display.
    /// </summary>
    /// <param name="manifold">The topology; each open end is treated as a scroll.</param>
    /// <param name="firingOrder">1-based cylinder numbers in firing sequence.</param>
    /// <param name="exhaustValveOpenDeg">EVO in cycle degrees, 0 = firing TDC of cylinder 1.</param>
    /// <param name="exhaustValveCloseDeg">EVC in cycle degrees; with EVO this is the exhaust window.</param>
    /// <param name="soundSpeed">Local exhaust sound speed, m/s.</param>
    /// <param name="rpm">Engine speed.</param>
    /// <param name="blowdownWidthDeg">Angular width of the blowdown pulse itself.</param>
    public static IReadOnlyList<(string Scroll, double Index)> ScrollSeparation(
        ManifoldSpec manifold,
        IReadOnlyList<int> firingOrder,
        double exhaustValveOpenDeg,
        double exhaustValveCloseDeg,
        double soundSpeed,
        double rpm,
        double blowdownWidthDeg = 60.0)
    {
        var results = new List<(string, double)>();
        var interval = firingOrder.Count > 0 ? 720.0 / firingOrder.Count : 720.0;

        // Each scroll runs to its own termination, so the terminations ARE
        // the scrolls — which is also why a shared outlet is not twin-scroll.
        foreach (var outlet in manifold.Nodes.Where(n => n.Kind == ManifoldNodeKind.Atmosphere))
        {
            var name = outlet.Label.Length > 0 ? outlet.Label : outlet.Id;
            var arrivals = Arrivals(manifold, outlet.Id, firingOrder, exhaustValveOpenDeg, soundSpeed, rpm);

            if (arrivals.Count < 2)
            {
                results.Add((name, 0.0));
                continue;
            }

            var worst = 0.0;
            foreach (var pulse in arrivals)
            {
                foreach (var mate in arrivals)
                {
                    if (mate.Cylinder == pulse.Cylinder)
                    {
                        continue;
                    }

                    // The mate's exhaust stroke, in cycle degrees.
                    var open = Wrap(mate.FiringAngleDeg + exhaustValveOpenDeg);
                    var duration = exhaustValveCloseDeg - exhaustValveOpenDeg;

                    var overlap = WindowOverlap(
                        pulse.ArrivalAngleDeg, blowdownWidthDeg, open, duration);
                    worst = Math.Max(worst, overlap / blowdownWidthDeg);
                }
            }

            results.Add((name, Math.Clamp(worst, 0.0, 1.0)));
        }

        _ = interval;
        return results;
    }

    /// <summary>
    /// Degrees of a pulse starting at <paramref name="pulseStart"/> that fall
    /// inside a window, both on the 720° cycle.
    /// </summary>
    private static double WindowOverlap(double pulseStart, double pulseWidth, double windowStart, double windowWidth)
    {
        // Walk the pulse in small steps and count what lands inside. Cheap,
        // and immune to the wrap-around arithmetic that makes interval
        // intersection on a cycle so easy to get subtly wrong.
        const int samples = 360;
        var inside = 0;
        for (var i = 0; i < samples; i++)
        {
            var angle = Wrap(pulseStart + (pulseWidth * i / samples));
            var offset = Wrap(angle - windowStart);
            if (offset < windowWidth)
            {
                inside++;
            }
        }

        return pulseWidth * inside / samples;
    }

    /// <summary>Shortest angular distance between two cycle angles, 0–360.</summary>
    private static double Separation(double a, double b)
    {
        var d = Math.Abs(Wrap(a) - Wrap(b));
        return Math.Min(d, 720.0 - d);
    }

    private static double Wrap(double angle)
    {
        var a = angle % 720.0;
        return a < 0 ? a + 720.0 : a;
    }
}
