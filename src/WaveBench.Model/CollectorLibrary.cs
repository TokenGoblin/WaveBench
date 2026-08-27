namespace WaveBench.Model;

/// <summary>
/// The geometry a collector needs, in one place, so building any §2.8
/// configuration is choosing a shape and filling this in rather than placing
/// nodes by hand.
/// </summary>
/// <param name="Cylinders">Cylinder count the header serves.</param>
/// <param name="PrimaryLengthMm">Primary pipe length.</param>
/// <param name="PrimaryDiameterMm">Primary pipe bore.</param>
/// <param name="SecondaryLengthMm">Secondary length where the shape has one (4-2-1, tri-Y).</param>
/// <param name="SecondaryDiameterMm">Secondary bore.</param>
/// <param name="CollectorLengthMm">Length after the final merge.</param>
/// <param name="CollectorDiameterMm">Bore after the final merge.</param>
/// <param name="MergeAngleDeg">Branch angle at every merge; 10–30° is a real collector.</param>
/// <param name="TailLengthMm">Tailpipe after the collector.</param>
/// <param name="TailDiameterMm">Tailpipe bore.</param>
public sealed record CollectorGeometry(
    int Cylinders = 4,
    double PrimaryLengthMm = 450,
    double PrimaryDiameterMm = 38,
    double SecondaryLengthMm = 300,
    double SecondaryDiameterMm = 45,
    double CollectorLengthMm = 250,
    double CollectorDiameterMm = 54,
    double MergeAngleDeg = 15,
    double TailLengthMm = 600,
    double TailDiameterMm = 60);

/// <summary>How the cylinders are grouped onto the first merge.</summary>
/// <param name="Name">Shown in the UI.</param>
/// <param name="Groups">Each group is a set of 1-based cylinder numbers merging together.</param>
/// <param name="Rationale">Why this pairing, in one line.</param>
public sealed record CylinderPairing(string Name, IReadOnlyList<IReadOnlyList<int>> Groups, string Rationale);

/// <summary>
/// Builders for the collector configurations the plan §2.8 requires the
/// editor to "express trivially", plus the §4.6.2 twin-scroll pairings.
///
/// The Phase 18 gate asks that a new user can build each of these in under
/// two minutes. The way to make that true is not a faster canvas — it is to
/// stop requiring a user to hand-place fifteen nodes for a shape that has a
/// name. Each builder here is one palette action followed by editing the
/// numbers, and <see cref="OperationCount"/> reports the manual placements it
/// saved so the claim is measurable rather than asserted.
/// </summary>
public static class CollectorLibrary
{
    /// <summary>Every configuration §2.8 names, with the builder for each.</summary>
    public static IReadOnlyList<(string Id, string Name, string Description)> Configurations { get; } =
    [
        ("4-1", "4-1 header", "All four primaries into one collector. Strongest single tuned peak, narrowest band."),
        ("4-2-1", "4-2-1 header", "Paired primaries then a second merge. Broader torque, two weaker peaks."),
        ("tri-y", "Tri-Y", "A 4-2-1 with unequal secondaries — the classic broad-torque header."),
        ("individual", "Individual runners", "No collector at all: each cylinder straight to atmosphere."),
        ("log", "Log manifold", "Primaries into a common gallery. Cheap, compact, poor scavenging."),
        ("180-crossover", "180° crossover", "V8 bank-crossing header that evens the firing intervals per collector."),
        ("x-pipe", "X-pipe", "Two banks crossed once — equalises pressure between them."),
        ("h-pipe", "H-pipe", "Two banks joined by a short balance tube."),
        ("twin-scroll", "Twin-scroll divided", "Two isolated scrolls, cylinders paired 360° apart (§4.6.2)."),
    ];

    /// <summary>
    /// Twin-scroll pairings from plan §4.6.2. Cylinders sharing a scroll must
    /// be 360° apart in the firing order so one cylinder's blowdown never
    /// lands in its scroll-mate's exhaust stroke.
    /// </summary>
    public static IReadOnlyList<CylinderPairing> TwinScrollPairings { get; } =
    [
        new("I4, firing 1-3-4-2", [[1, 4], [2, 3]],
            "1 and 4 are 360° apart, as are 2 and 3 — neither pair collides."),
        new("I6, firing 1-5-3-6-2-4", [[1, 2, 3], [4, 5, 6]],
            "The two triples are each evenly spaced at 240°, so no scroll ever sees overlapping blowdowns."),
        new("V8, bank pairing", [[1, 2, 3, 4], [5, 6, 7, 8]],
            "Simplest to plumb: each bank keeps its own scroll, at the cost of uneven intervals on a crossplane crank."),
        new("V8, cross-bank", [[1, 4, 6, 7], [2, 3, 5, 8]],
            "Crosses the banks to even the intervals per scroll — the reason a 180° crossover exists."),
    ];

    /// <summary>Default firing order for a cylinder count, when the model does not state one.</summary>
    public static IReadOnlyList<int> DefaultFiringOrder(int cylinders) => cylinders switch
    {
        1 => [1],
        2 => [1, 2],
        3 => [1, 2, 3],
        4 => [1, 3, 4, 2],
        5 => [1, 2, 4, 5, 3],
        6 => [1, 5, 3, 6, 2, 4],
        8 => [1, 8, 4, 3, 6, 5, 7, 2],
        _ => Enumerable.Range(1, Math.Max(1, cylinders)).ToList(),
    };

    /// <summary>
    /// Builds a named configuration. Unknown ids throw rather than returning
    /// an empty manifold — a silently empty graph is worse than a stop.
    /// </summary>
    public static ManifoldSpec Build(string configurationId, CollectorGeometry geometry)
    {
        return configurationId.ToLowerInvariant() switch
        {
            "4-1" => IntoOne(geometry),
            "4-2-1" => IntoTwoIntoOne(geometry, unequalSecondaries: false),
            "tri-y" => IntoTwoIntoOne(geometry, unequalSecondaries: true),
            "individual" => Individual(geometry),
            "log" => Log(geometry),
            "180-crossover" => Crossover(geometry),
            "x-pipe" => TwoBanks(geometry, "x-pipe", balanceLengthMm: 0),
            "h-pipe" => TwoBanks(geometry, "h-pipe", balanceLengthMm: 120),
            "twin-scroll" => TwinScroll(geometry, TwinScrollPairings[0]),
            _ => throw new ArgumentException($"Unknown collector configuration '{configurationId}'.", nameof(configurationId)),
        };
    }

    /// <summary>
    /// Manual node placements this builder replaced — the measurable half of
    /// "buildable in under two minutes". One palette click stands in for this
    /// many drag-and-drops.
    /// </summary>
    public static int OperationCount(string configurationId, CollectorGeometry geometry) =>
        Build(configurationId, geometry) is { } spec ? spec.Nodes.Count + spec.Connections.Count : 0;

    // ---- Shapes -------------------------------------------------------------

    private static ManifoldSpec IntoOne(CollectorGeometry g)
    {
        var spec = new ManifoldSpec { Configuration = "4-1" };
        var junction = Add(spec, ManifoldNodeKind.Junction, "merge", "Collector", x: 4, y: 1.5,
            n => n.BranchAngleDeg = g.MergeAngleDeg);

        for (var c = 1; c <= g.Cylinders; c++)
        {
            var port = Port(spec, c, y: c - 1);
            var primary = Pipe(spec, $"pri{c}", $"Primary {c}", g.PrimaryLengthMm, g.PrimaryDiameterMm,
                g.PrimaryDiameterMm, x: 2, y: c - 1);
            Connect(spec, port, primary);
            Connect(spec, primary, junction);
        }

        var collector = Pipe(spec, "collector", "Collector", g.CollectorLengthMm, g.CollectorDiameterMm,
            g.TailDiameterMm, x: 6, y: 1.5);
        var tail = Pipe(spec, "tail", "Tailpipe", g.TailLengthMm, g.TailDiameterMm, g.TailDiameterMm, x: 8, y: 1.5);
        var air = Add(spec, ManifoldNodeKind.Atmosphere, "out", "Atmosphere", x: 10, y: 1.5);

        Connect(spec, junction, collector);
        Connect(spec, collector, tail);
        Connect(spec, tail, air);
        return spec;
    }

    private static ManifoldSpec IntoTwoIntoOne(CollectorGeometry g, bool unequalSecondaries)
    {
        var spec = new ManifoldSpec { Configuration = unequalSecondaries ? "tri-Y" : "4-2-1" };
        var pairing = FiringPairs(g.Cylinders);

        var secondaries = new List<string>();
        for (var pair = 0; pair < pairing.Count; pair++)
        {
            var junction = Add(spec, ManifoldNodeKind.Junction, $"merge{pair + 1}", $"Merge {pair + 1}",
                x: 4, y: pair * 2.0 + 0.5, n => n.BranchAngleDeg = g.MergeAngleDeg);

            foreach (var c in pairing[pair])
            {
                var port = Port(spec, c, y: c - 1);
                var primary = Pipe(spec, $"pri{c}", $"Primary {c}", g.PrimaryLengthMm, g.PrimaryDiameterMm,
                    g.PrimaryDiameterMm, x: 2, y: c - 1);
                Connect(spec, port, primary);
                Connect(spec, primary, junction);
            }

            // A tri-Y is a 4-2-1 whose secondaries are deliberately unequal —
            // that asymmetry is the whole reason it broadens the torque curve.
            var length = unequalSecondaries
                ? g.SecondaryLengthMm * (pair == 0 ? 0.8 : 1.25)
                : g.SecondaryLengthMm;

            var secondary = Pipe(spec, $"sec{pair + 1}", $"Secondary {pair + 1}", length,
                g.SecondaryDiameterMm, g.SecondaryDiameterMm, x: 6, y: pair * 2.0 + 0.5);
            Connect(spec, junction, secondary);
            secondaries.Add(secondary);
        }

        var final = Add(spec, ManifoldNodeKind.Junction, "final", "Final merge", x: 8, y: 1.5,
            n => n.BranchAngleDeg = g.MergeAngleDeg);
        foreach (var secondary in secondaries)
        {
            Connect(spec, secondary, final);
        }

        var collector = Pipe(spec, "collector", "Collector", g.CollectorLengthMm, g.CollectorDiameterMm,
            g.TailDiameterMm, x: 10, y: 1.5);
        var tail = Pipe(spec, "tail", "Tailpipe", g.TailLengthMm, g.TailDiameterMm, g.TailDiameterMm, x: 12, y: 1.5);
        var air = Add(spec, ManifoldNodeKind.Atmosphere, "out", "Atmosphere", x: 14, y: 1.5);

        Connect(spec, final, collector);
        Connect(spec, collector, tail);
        Connect(spec, tail, air);
        return spec;
    }

    private static ManifoldSpec Individual(CollectorGeometry g)
    {
        var spec = new ManifoldSpec { Configuration = "individual runners" };
        for (var c = 1; c <= g.Cylinders; c++)
        {
            var port = Port(spec, c, y: c - 1);
            var pipe = Pipe(spec, $"pri{c}", $"Runner {c}", g.PrimaryLengthMm + g.CollectorLengthMm,
                g.PrimaryDiameterMm, g.PrimaryDiameterMm, x: 2, y: c - 1);
            var air = Add(spec, ManifoldNodeKind.Atmosphere, $"out{c}", "Atmosphere", x: 4, y: c - 1);
            Connect(spec, port, pipe);
            Connect(spec, pipe, air);
        }

        return spec;
    }

    private static ManifoldSpec Log(CollectorGeometry g)
    {
        // A log is a plenum with the primaries stubbed into it — which is
        // exactly why it scavenges badly: there is no tuned length, just
        // volume.
        var spec = new ManifoldSpec { Configuration = "log manifold" };
        var gallery = Add(spec, ManifoldNodeKind.Plenum, "gallery", "Log gallery", x: 4, y: 1.5,
            n => n.VolumeLitres = Math.PI / 4.0 * Math.Pow(g.CollectorDiameterMm / 1000.0, 2)
                                  * (g.Cylinders * 0.10) * 1000.0);

        for (var c = 1; c <= g.Cylinders; c++)
        {
            var port = Port(spec, c, y: c - 1);
            var stub = Pipe(spec, $"pri{c}", $"Stub {c}", Math.Min(120.0, g.PrimaryLengthMm),
                g.PrimaryDiameterMm, g.PrimaryDiameterMm, x: 2, y: c - 1);
            Connect(spec, port, stub);
            Connect(spec, stub, gallery);
        }

        var tail = Pipe(spec, "tail", "Downpipe", g.TailLengthMm, g.TailDiameterMm, g.TailDiameterMm, x: 6, y: 1.5);
        var air = Add(spec, ManifoldNodeKind.Atmosphere, "out", "Atmosphere", x: 8, y: 1.5);
        Connect(spec, gallery, tail);
        Connect(spec, tail, air);
        return spec;
    }

    private static ManifoldSpec Crossover(CollectorGeometry g)
    {
        // 180° crossover: cylinders are grouped so each collector sees evenly
        // spaced pulses, which on a crossplane V8 means crossing the banks.
        var spec = new ManifoldSpec { Configuration = "180° crossover" };
        var groups = g.Cylinders == 8
            ? (IReadOnlyList<IReadOnlyList<int>>)[[1, 4, 6, 7], [2, 3, 5, 8]]
            : FiringPairs(g.Cylinders);

        var collectors = new List<string>();
        for (var i = 0; i < groups.Count; i++)
        {
            var junction = Add(spec, ManifoldNodeKind.Junction, $"merge{i + 1}", $"Collector {i + 1}",
                x: 5, y: i * 4.0 + 1.0, n => n.BranchAngleDeg = g.MergeAngleDeg);

            foreach (var c in groups[i])
            {
                var port = Port(spec, c, y: c - 1);
                var primary = Pipe(spec, $"pri{c}", $"Primary {c}", g.PrimaryLengthMm, g.PrimaryDiameterMm,
                    g.PrimaryDiameterMm, x: 2.5, y: c - 1);
                Connect(spec, port, primary);
                Connect(spec, primary, junction);
            }

            var secondary = Pipe(spec, $"sec{i + 1}", $"Secondary {i + 1}", g.SecondaryLengthMm,
                g.SecondaryDiameterMm, g.SecondaryDiameterMm, x: 7, y: i * 4.0 + 1.0);
            Connect(spec, junction, secondary);
            collectors.Add(secondary);
        }

        var final = Add(spec, ManifoldNodeKind.Junction, "final", "Final merge", x: 9, y: 3.0,
            n => n.BranchAngleDeg = g.MergeAngleDeg);
        foreach (var c in collectors)
        {
            Connect(spec, c, final);
        }

        var tail = Pipe(spec, "tail", "Tailpipe", g.TailLengthMm, g.TailDiameterMm, g.TailDiameterMm, x: 11, y: 3.0);
        var air = Add(spec, ManifoldNodeKind.Atmosphere, "out", "Atmosphere", x: 13, y: 3.0);
        Connect(spec, final, tail);
        Connect(spec, tail, air);
        return spec;
    }

    private static ManifoldSpec TwoBanks(CollectorGeometry g, string id, double balanceLengthMm)
    {
        // X- and H-pipes join two banks. The graph stays a tree: the balance
        // path is modelled as each bank teeing into a shared element, not as
        // a loop, because a loop is not something the 1-D solver can march.
        var spec = new ManifoldSpec { Configuration = id == "x-pipe" ? "X-pipe" : "H-pipe" };
        var perBank = Math.Max(1, g.Cylinders / 2);

        var bankOutlets = new List<string>();
        for (var bank = 0; bank < 2; bank++)
        {
            var junction = Add(spec, ManifoldNodeKind.Junction, $"bank{bank + 1}", $"Bank {bank + 1} collector",
                x: 4, y: bank * 4.0 + 1.0, n => n.BranchAngleDeg = g.MergeAngleDeg);

            for (var i = 0; i < perBank; i++)
            {
                var c = (bank * perBank) + i + 1;
                var port = Port(spec, c, y: c - 1);
                var primary = Pipe(spec, $"pri{c}", $"Primary {c}", g.PrimaryLengthMm, g.PrimaryDiameterMm,
                    g.PrimaryDiameterMm, x: 2, y: c - 1);
                Connect(spec, port, primary);
                Connect(spec, primary, junction);
            }

            bankOutlets.Add(junction);
        }

        var cross = Add(spec, ManifoldNodeKind.Junction, "cross", id == "x-pipe" ? "X" : "H balance",
            x: 7, y: 3.0, n => n.BranchAngleDeg = id == "x-pipe" ? g.MergeAngleDeg : 90.0);

        for (var bank = 0; bank < 2; bank++)
        {
            var length = balanceLengthMm > 0 ? g.SecondaryLengthMm : g.SecondaryLengthMm * 0.6;
            var pipe = Pipe(spec, $"mid{bank + 1}", $"Bank {bank + 1} mid-pipe", length,
                g.SecondaryDiameterMm, g.SecondaryDiameterMm, x: 5.5, y: bank * 4.0 + 1.0);
            Connect(spec, bankOutlets[bank], pipe);
            Connect(spec, pipe, cross);
        }

        for (var bank = 0; bank < 2; bank++)
        {
            var tail = Pipe(spec, $"tail{bank + 1}", $"Tailpipe {bank + 1}", g.TailLengthMm,
                g.TailDiameterMm, g.TailDiameterMm, x: 9, y: bank * 4.0 + 1.0);
            var air = Add(spec, ManifoldNodeKind.Atmosphere, $"out{bank + 1}", "Atmosphere",
                x: 11, y: bank * 4.0 + 1.0);
            Connect(spec, cross, tail);
            Connect(spec, tail, air);
        }

        return spec;
    }

    /// <summary>Twin-scroll: two isolated collectors, no shared path (plan §4.6.2).</summary>
    public static ManifoldSpec TwinScroll(CollectorGeometry g, CylinderPairing pairing)
    {
        var spec = new ManifoldSpec { Configuration = $"twin-scroll ({pairing.Name})" };

        for (var s = 0; s < pairing.Groups.Count; s++)
        {
            var junction = Add(spec, ManifoldNodeKind.Junction, $"scroll{s + 1}", $"Scroll {(char)('A' + s)}",
                x: 4, y: s * 4.0 + 1.0, n => n.BranchAngleDeg = g.MergeAngleDeg);

            foreach (var c in pairing.Groups[s])
            {
                var port = Port(spec, c, y: c - 1);
                var primary = Pipe(spec, $"pri{c}", $"Primary {c}", g.PrimaryLengthMm, g.PrimaryDiameterMm,
                    g.PrimaryDiameterMm, x: 2, y: c - 1);
                Connect(spec, port, primary);
                Connect(spec, primary, junction);
            }

            // Each scroll stays isolated all the way to its own entry — that
            // separation IS the twin-scroll.
            var entry = Pipe(spec, $"entry{s + 1}", $"Scroll {(char)('A' + s)} entry", g.CollectorLengthMm,
                g.CollectorDiameterMm, g.CollectorDiameterMm, x: 6, y: s * 4.0 + 1.0);
            var air = Add(spec, ManifoldNodeKind.Atmosphere, $"turbine{s + 1}",
                $"Turbine scroll {(char)('A' + s)}", x: 8, y: s * 4.0 + 1.0);
            Connect(spec, junction, entry);
            Connect(spec, entry, air);
        }

        return spec;
    }

    // ---- Building blocks ----------------------------------------------------

    /// <summary>
    /// Groups cylinders into pairs that are as far apart in the firing order
    /// as possible — the same principle as §4.6.2, applied to a 4-2-1's first
    /// merge. Pairing adjacent firings is the classic mistake: their
    /// blowdowns collide in the secondary.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> FiringPairs(int cylinders)
    {
        var order = DefaultFiringOrder(cylinders);
        var half = order.Count / 2;
        if (half == 0)
        {
            return [[.. order]];
        }

        var groups = new List<IReadOnlyList<int>>();
        for (var i = 0; i < half; i++)
        {
            groups.Add([order[i], order[i + half]]);
        }

        return groups;
    }

    private static string Add(
        ManifoldSpec spec, ManifoldNodeKind kind, string id, string label,
        double x, double y, Action<ManifoldNode>? configure = null)
    {
        var node = new ManifoldNode { Id = id, Kind = kind, Label = label, X = x, Y = y };
        configure?.Invoke(node);
        spec.Nodes.Add(node);
        return id;
    }

    private static string Port(ManifoldSpec spec, int cylinder, double y) =>
        Add(spec, ManifoldNodeKind.Port, $"cyl{cylinder}", $"Cylinder {cylinder}", x: 0, y: y,
            n => n.Cylinder = cylinder);

    private static string Pipe(
        ManifoldSpec spec, string id, string label, double lengthMm,
        double inletMm, double outletMm, double x, double y) =>
        Add(spec, ManifoldNodeKind.Pipe, id, label, x, y, n =>
        {
            n.LengthMm = lengthMm;
            n.DiameterMm = inletMm;
            n.OutletDiameterMm = Math.Abs(outletMm - inletMm) < 1e-9 ? 0.0 : outletMm;
        });

    private static void Connect(ManifoldSpec spec, string from, string to) =>
        spec.Connections.Add(new ManifoldConnection(from, to));
}
