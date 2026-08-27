namespace WaveBench.Model;

/// <summary>Component kinds the manifold canvas can place (plan §2.7).</summary>
public enum ManifoldNodeKind
{
    /// <summary>A cylinder's exhaust or intake port — where the graph meets the engine.</summary>
    Port,

    /// <summary>Straight or tapered pipe. A taper is simply unequal end diameters.</summary>
    Pipe,

    /// <summary>2–6 branch junction. Branch angle drives the loss coefficients.</summary>
    Junction,

    /// <summary>0D volume with multiple ports — airbox, plenum, collector can.</summary>
    Plenum,

    /// <summary>Open end to atmosphere.</summary>
    Atmosphere,
}

/// <summary>
/// One component on the manifold graph.
///
/// Geometry fields that do not apply to a kind are simply unused — a node is
/// a small record rather than a class hierarchy, because the canvas, the
/// serialiser and the solver all want to treat them uniformly and a
/// discriminated hierarchy would need three visitors to do the same work.
/// Which fields matter per kind is stated on each one and enforced by
/// <see cref="ManifoldSpec.Validate"/>.
/// </summary>
public sealed record ManifoldNode
{
    /// <summary>Stable identity. Connections reference this, not an index.</summary>
    public required string Id { get; set; }

    public required ManifoldNodeKind Kind { get; set; }

    /// <summary>Shown on the canvas; free text.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Pipe: length, mm.</summary>
    public double LengthMm { get; set; }

    /// <summary>Pipe: inlet diameter, mm. Also the single diameter of a plain pipe.</summary>
    public double DiameterMm { get; set; }

    /// <summary>
    /// Pipe: outlet diameter, mm. Zero means "same as inlet" — a plain pipe.
    /// Anything else is a taper, which the solver meshes as variable area.
    /// </summary>
    public double OutletDiameterMm { get; set; }

    /// <summary>Pipe: absolute wall roughness ε, mm.</summary>
    public double RoughnessMm { get; set; }

    /// <summary>Plenum: volume, litres.</summary>
    public double VolumeLitres { get; set; }

    /// <summary>
    /// Junction: angle between each side branch and the combined-leg axis,
    /// degrees. 90° is a plain tee; a merge collector is typically 10–30°.
    /// </summary>
    public double BranchAngleDeg { get; set; } = 90.0;

    /// <summary>Port: which cylinder, 1-based.</summary>
    public int Cylinder { get; set; }

    /// <summary>Canvas position, in grid units. View state, carried in the document so a layout survives a save.</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>A directed connection between two nodes.</summary>
/// <param name="From">Upstream node id (toward the cylinder for an exhaust).</param>
/// <param name="To">Downstream node id.</param>
public sealed record ManifoldConnection(string From, string To);

/// <summary>
/// A manifold as a node graph (plan §2.8, Phase 18).
///
/// This is the model the canvas edits and the solver builds from. It is
/// OPTIONAL on the document: a model with no manifold keeps the single
/// intake/exhaust runner pair, so every existing project, template and test
/// keeps working unchanged and the graph is something a user opts into when
/// they need a collector.
/// </summary>
public sealed record ManifoldSpec
{
    public List<ManifoldNode> Nodes { get; set; } = [];

    public List<ManifoldConnection> Connections { get; set; } = [];

    /// <summary>What this topology is, for the UI and reports (e.g. "4-2-1").</summary>
    public string Configuration { get; set; } = string.Empty;

    public ManifoldNode? Node(string id) =>
        Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

    public IEnumerable<string> Downstream(string id) =>
        Connections.Where(c => c.From == id).Select(c => c.To);

    public IEnumerable<string> Upstream(string id) =>
        Connections.Where(c => c.To == id).Select(c => c.From);

    /// <summary>
    /// Structural checks. These are about the GRAPH being solvable — a
    /// dangling pipe or a cycle is not a design opinion, it is a topology the
    /// solver cannot build. Plausibility warnings about geometry belong with
    /// the design warnings the canvas shows.
    /// </summary>
    public IReadOnlyList<ModelIssue> Validate(string prefix = "manifold")
    {
        var issues = new List<ModelIssue>();
        void Error(string path, string message) => issues.Add(new ModelIssue(ModelIssueSeverity.Error, path, message));
        void Warn(string path, string message) => issues.Add(new ModelIssue(ModelIssueSeverity.Warning, path, message));

        if (Nodes.Count == 0)
        {
            return issues;
        }

        var duplicates = Nodes.GroupBy(n => n.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var id in duplicates)
        {
            Error($"{prefix}.nodes", $"Duplicate node id '{id}'.");
        }

        foreach (var connection in Connections)
        {
            if (Node(connection.From) is null)
            {
                Error($"{prefix}.connections", $"Connection from unknown node '{connection.From}'.");
            }

            if (Node(connection.To) is null)
            {
                Error($"{prefix}.connections", $"Connection to unknown node '{connection.To}'.");
            }

            if (connection.From == connection.To)
            {
                Error($"{prefix}.connections", $"Node '{connection.From}' is connected to itself.");
            }
        }

        foreach (var node in Nodes)
        {
            var path = $"{prefix}.{node.Id}";
            var inputs = Upstream(node.Id).Count();
            var outputs = Downstream(node.Id).Count();

            switch (node.Kind)
            {
                case ManifoldNodeKind.Pipe:
                    if (node.LengthMm <= 0 || node.DiameterMm <= 0)
                    {
                        Error(path, "Pipe length and diameter must be positive.");
                    }

                    if (inputs != 1 || outputs != 1)
                    {
                        Error(path, $"A pipe needs exactly one connection each side; found {inputs} in, {outputs} out.");
                    }

                    break;

                case ManifoldNodeKind.Junction:
                    var legs = inputs + outputs;
                    if (legs is < 3 or > 6)
                    {
                        Error(path, $"A junction takes 3–6 legs (plan §2.7); found {legs}.");
                    }

                    if (node.BranchAngleDeg is < 0 or > 180)
                    {
                        Error(path, "Branch angle must be between 0° and 180°.");
                    }

                    break;

                case ManifoldNodeKind.Plenum:
                    if (node.VolumeLitres <= 0)
                    {
                        Error(path, "Plenum volume must be positive.");
                    }

                    if (inputs + outputs < 2)
                    {
                        Error(path, "A plenum needs at least two ports to be worth having.");
                    }

                    break;

                case ManifoldNodeKind.Port:
                    if (node.Cylinder < 1)
                    {
                        Error(path, "A port must name a cylinder (1-based).");
                    }

                    if (inputs + outputs != 1)
                    {
                        Error(path, "A cylinder port attaches to exactly one component.");
                    }

                    break;

                case ManifoldNodeKind.Atmosphere:
                    if (inputs + outputs != 1)
                    {
                        Error(path, "An open end attaches to exactly one component.");
                    }

                    break;
            }
        }

        // Every port must be able to reach atmosphere, or that cylinder is
        // plumbed into a dead end and the solver would trap it.
        var atmospheres = Nodes.Where(n => n.Kind == ManifoldNodeKind.Atmosphere).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (atmospheres.Count == 0)
        {
            Error(prefix, "The manifold has no open end — nothing connects it to atmosphere.");
        }
        else
        {
            foreach (var port in Nodes.Where(n => n.Kind == ManifoldNodeKind.Port))
            {
                if (!Reaches(port.Id, atmospheres))
                {
                    Error($"{prefix}.{port.Id}", $"Cylinder {port.Cylinder} cannot reach an open end.");
                }
            }
        }

        if (HasCycle())
        {
            Error(prefix, "The manifold contains a loop. An X- or H-pipe is a junction pair, not a cycle.");
        }

        var orphans = Nodes.Where(n => !Connections.Any(c => c.From == n.Id || c.To == n.Id)).ToList();
        foreach (var orphan in orphans)
        {
            Warn($"{prefix}.{orphan.Id}", "Not connected to anything.");
        }

        return issues;
    }

    /// <summary>Undirected reachability — flow direction reverses during reversion.</summary>
    private bool Reaches(string from, IReadOnlySet<string> targets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(from);
        seen.Add(from);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (targets.Contains(id))
            {
                return true;
            }

            foreach (var next in Downstream(id).Concat(Upstream(id)))
            {
                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    private bool HasCycle()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        bool Visit(string id)
        {
            if (state.TryGetValue(id, out var mark))
            {
                return mark == 1; // grey: back edge
            }

            state[id] = 1;
            foreach (var next in Downstream(id))
            {
                if (Visit(next))
                {
                    return true;
                }
            }

            state[id] = 2;
            return false;
        }

        return Nodes.Any(n => Visit(n.Id));
    }

    /// <summary>
    /// Total path length from a cylinder port to a node, mm, following the
    /// graph and summing pipe lengths. Null when there is no path.
    /// </summary>
    public double? PathLengthMm(string fromId, string toId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, double Length)>();
        queue.Enqueue((fromId, 0.0));
        seen.Add(fromId);

        while (queue.Count > 0)
        {
            var (id, length) = queue.Dequeue();
            if (id == toId)
            {
                return length;
            }

            foreach (var next in Downstream(id))
            {
                if (!seen.Add(next))
                {
                    continue;
                }

                var node = Node(next);
                var extra = node?.Kind == ManifoldNodeKind.Pipe ? node.LengthMm : 0.0;
                queue.Enqueue((next, length + extra));
            }
        }

        return null;
    }
}
