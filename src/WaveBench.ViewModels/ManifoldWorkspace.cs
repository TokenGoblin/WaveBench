using System.Globalization;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>Something the palette can place or apply.</summary>
/// <param name="Id">Component kind name, or a collector configuration id.</param>
/// <param name="Label">Shown on the palette.</param>
/// <param name="Description">One line of help.</param>
/// <param name="Kind">Set for single components; null for a whole configuration.</param>
public sealed record PaletteItem(string Id, string Label, string Description, ManifoldNodeKind? Kind);

/// <summary>
/// A design warning about the geometry, with the source it comes from.
///
/// A warning without a citation is an opinion. The plan's §8.4 example is
/// explicit about the shape — <i>"Diffuser half-angle 11°: separation likely
/// (SAE 2006-01-3654). Suggested ≤ 7°."</i> — so every warning here carries
/// what is wrong, what to do instead, and who says so.
/// </summary>
/// <param name="NodeId">Which component, or null for the manifold as a whole.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Suggestion">What to do instead.</param>
/// <param name="Citation">Where the limit comes from.</param>
/// <param name="CrossLink">Another workspace that shows the consequence (plan §8.3).</param>
public sealed record DesignWarning(
    string? NodeId,
    string Message,
    string? Suggestion = null,
    string? Citation = null,
    string? CrossLink = null);

/// <summary>Live geometry readout for the whole manifold (plan §8.4).</summary>
public sealed record GeometrySummary(IReadOnlyList<DerivedReadout> Readouts);

/// <summary>
/// The Manifold canvas (plan Phase 18, §8.4): palette, selection, drag with
/// snap, auto-layout, copy/paste, the live geometry summary and the inline
/// design warnings.
///
/// Contains no UI-framework types. Everything the canvas does to the model
/// happens here and goes through <see cref="ProjectSession"/>, so the view is
/// left drawing rectangles and forwarding gestures — which is what makes the
/// Phase 18 behaviour testable without a window.
/// </summary>
public sealed class ManifoldWorkspace
{
    private readonly ProjectSession _session;
    private readonly HashSet<string> _selection = new(StringComparer.Ordinal);
    private List<ManifoldNode> _clipboardNodes = [];
    private List<ManifoldConnection> _clipboardConnections = [];

    public ManifoldWorkspace(ProjectSession session, UserPreferences? preferences = null)
    {
        _session = session;
        Preferences = preferences ?? new UserPreferences();
    }

    public UserPreferences Preferences { get; }

    /// <summary>Canvas grid, in the same units node positions use.</summary>
    public double GridSize { get; set; } = 0.5;

    public bool SnapToGrid { get; set; } = true;

    /// <summary>
    /// The manifold being edited. Null until the user places something —
    /// a model without a collector keeps its simple runner, and creating an
    /// empty graph would silently switch the solver over to a topology with
    /// no pipes in it.
    /// </summary>
    public ManifoldSpec? Manifold => _session.Document.ExhaustManifold;

    public IReadOnlySet<string> Selection => _selection;

    public static IReadOnlyList<PaletteItem> Components { get; } =
    [
        new("Pipe", "Pipe", "Straight or tapered. Unequal ends make it a taper.", ManifoldNodeKind.Pipe),
        new("Junction", "Junction", "2–6 legs. The branch angle drives the loss coefficients.", ManifoldNodeKind.Junction),
        new("Plenum", "Plenum", "0D volume with multiple ports — airbox, gallery, collector can.", ManifoldNodeKind.Plenum),
        new("Port", "Cylinder port", "Where the graph meets a cylinder.", ManifoldNodeKind.Port),
        new("Atmosphere", "Open end", "Exit to atmosphere, or a turbine scroll entry.", ManifoldNodeKind.Atmosphere),
    ];

    /// <summary>Whole configurations, from the §2.8 library.</summary>
    public static IReadOnlyList<PaletteItem> Configurations { get; } =
        CollectorLibrary.Configurations
            .Select(c => new PaletteItem(c.Id, c.Name, c.Description, null))
            .ToList();

    // ---- Building ------------------------------------------------------------

    /// <summary>
    /// Replace the manifold with a named configuration from the library. This
    /// is the one-action path the Phase 18 gate is really about: a user picks
    /// "4-2-1" rather than placing sixteen nodes.
    /// </summary>
    public void ApplyConfiguration(string configurationId, CollectorGeometry? geometry = null)
    {
        var g = geometry ?? DefaultGeometry();
        var spec = CollectorLibrary.Build(configurationId, g);
        _session.EditByUser("ExhaustManifold", spec);
        _selection.Clear();
    }

    /// <summary>Geometry seeded from the model, so a template is not a blank guess.</summary>
    public CollectorGeometry DefaultGeometry()
    {
        var document = _session.Document;
        var primary = document.ExhaustRunner.DiameterMm;
        return new CollectorGeometry(
            Cylinders: document.Engine.CylinderCount,
            PrimaryLengthMm: document.ExhaustRunner.LengthMm,
            PrimaryDiameterMm: primary,
            SecondaryDiameterMm: primary * 1.25,
            CollectorDiameterMm: primary * 1.5,
            TailDiameterMm: primary * 1.65);
    }

    /// <summary>Place a single component. Returns its new id.</summary>
    public string Add(ManifoldNodeKind kind, double x = 0, double y = 0)
    {
        var spec = Draft();
        var id = UniqueId(spec, kind.ToString().ToLowerInvariant());

        var node = new ManifoldNode
        {
            Id = id,
            Kind = kind,
            Label = kind.ToString(),
            X = Snap(x),
            Y = Snap(y),
        };

        // Sensible starting geometry: a component placed with zeros would be
        // invalid the instant it is connected, and a red graph is a poor
        // welcome.
        switch (kind)
        {
            case ManifoldNodeKind.Pipe:
                node.LengthMm = 300;
                node.DiameterMm = Math.Max(10, _session.Document.ExhaustRunner.DiameterMm);
                break;
            case ManifoldNodeKind.Plenum:
                node.VolumeLitres = 2.0;
                break;
            case ManifoldNodeKind.Port:
                node.Cylinder = NextFreeCylinder(spec);
                break;
        }

        spec.Nodes.Add(node);
        Commit(spec);
        return id;
    }

    public bool Connect(string fromId, string toId)
    {
        var spec = Draft();
        if (fromId == toId || spec.Node(fromId) is null || spec.Node(toId) is null)
        {
            return false;
        }

        if (spec.Connections.Any(c => (c.From == fromId && c.To == toId) || (c.From == toId && c.To == fromId)))
        {
            return false;
        }

        spec.Connections.Add(new ManifoldConnection(fromId, toId));
        Commit(spec);
        return true;
    }

    public bool Disconnect(string fromId, string toId)
    {
        var spec = Draft();
        if (spec.Connections.RemoveAll(c => c.From == fromId && c.To == toId) == 0)
        {
            return false;
        }

        Commit(spec);
        return true;
    }

    /// <summary>Delete the selection, taking its connections with it.</summary>
    public int DeleteSelected()
    {
        if (_selection.Count == 0)
        {
            return 0;
        }

        var spec = Draft();
        var removed = spec.Nodes.RemoveAll(n => _selection.Contains(n.Id));
        spec.Connections.RemoveAll(c => _selection.Contains(c.From) || _selection.Contains(c.To));
        _selection.Clear();
        Commit(spec);
        return removed;
    }

    /// <summary>
    /// Edit one node's geometry — the inspector's write path. Takes a
    /// mutation so the clone-and-commit stays in one place.
    /// </summary>
    public bool EditNode(string id, Action<ManifoldNode> change)
    {
        var spec = Draft();
        if (spec.Node(id) is not { } node)
        {
            return false;
        }

        change(node);
        Commit(spec);
        return true;
    }

    // ---- Selection -----------------------------------------------------------

    public void Select(string id, bool additive = false)
    {
        if (!additive)
        {
            _selection.Clear();
        }

        _selection.Add(id);
    }

    public void Toggle(string id)
    {
        if (!_selection.Remove(id))
        {
            _selection.Add(id);
        }
    }

    public void SelectAll()
    {
        _selection.Clear();
        foreach (var node in Manifold?.Nodes ?? [])
        {
            _selection.Add(node.Id);
        }
    }

    public void ClearSelection() => _selection.Clear();

    /// <summary>Everything inside a rubber-band rectangle.</summary>
    public void SelectInside(double x1, double y1, double x2, double y2, bool additive = false)
    {
        if (!additive)
        {
            _selection.Clear();
        }

        var left = Math.Min(x1, x2);
        var right = Math.Max(x1, x2);
        var top = Math.Min(y1, y2);
        var bottom = Math.Max(y1, y2);

        foreach (var node in Manifold?.Nodes ?? [])
        {
            if (node.X >= left && node.X <= right && node.Y >= top && node.Y <= bottom)
            {
                _selection.Add(node.Id);
            }
        }
    }

    // ---- Moving --------------------------------------------------------------

    /// <summary>
    /// Move the whole selection by a delta, snapping to the grid.
    ///
    /// Call this ONCE when a drag completes, not per frame: each call is an
    /// undo step, and a drag that fills the undo stack with sixty entries is
    /// worse than no undo at all. The view shows the in-progress offset
    /// itself and commits here on release.
    /// </summary>
    public void MoveSelection(double dx, double dy)
    {
        if (_selection.Count == 0)
        {
            return;
        }

        var spec = Draft();
        foreach (var node in spec.Nodes.Where(n => _selection.Contains(n.Id)))
        {
            node.X = Snap(node.X + dx);
            node.Y = Snap(node.Y + dy);
        }

        Commit(spec);
    }

    /// <summary>
    /// Arrange the graph left to right by distance from a cylinder port, so
    /// an imported or hand-built manifold reads as a flow rather than a pile.
    /// </summary>
    public void AutoLayout()
    {
        var spec = Draft();
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);

        // Ports are column 0; every other node sits one past its deepest
        // upstream neighbour.
        var queue = new Queue<string>();
        foreach (var port in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Port))
        {
            depth[port.Id] = 0;
            queue.Enqueue(port.Id);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var next in spec.Downstream(id))
            {
                var candidate = depth[id] + 1;
                if (!depth.TryGetValue(next, out var existing) || candidate > existing)
                {
                    depth[next] = candidate;
                    queue.Enqueue(next);
                }
            }
        }

        // Anything unreachable from a port still needs somewhere to go.
        foreach (var node in spec.Nodes.Where(n => !depth.ContainsKey(n.Id)))
        {
            depth[node.Id] = 0;
        }

        foreach (var column in depth.GroupBy(d => d.Value).OrderBy(g => g.Key))
        {
            var ordered = column.Select(c => c.Key).Order(StringComparer.Ordinal).ToList();
            for (var row = 0; row < ordered.Count; row++)
            {
                var node = spec.Node(ordered[row])!;
                node.X = column.Key * 2.0;
                node.Y = row * 1.5;
            }
        }

        Commit(spec);
    }

    // ---- Clipboard -----------------------------------------------------------

    /// <summary>
    /// Copy the selection. Connections are kept only when BOTH ends are
    /// selected — copying half a connection would paste a dangling one, and
    /// the plan asks for "copy/paste a whole bank", which is a subgraph.
    /// </summary>
    public int Copy()
    {
        var spec = Manifold;
        if (spec is null)
        {
            return 0;
        }

        _clipboardNodes = spec.Nodes.Where(n => _selection.Contains(n.Id)).Select(n => n with { }).ToList();
        _clipboardConnections = spec.Connections
            .Where(c => _selection.Contains(c.From) && _selection.Contains(c.To))
            .ToList();
        return _clipboardNodes.Count;
    }

    /// <summary>Paste the clipboard, offset, with fresh ids. Returns the new ids.</summary>
    public IReadOnlyList<string> Paste(double offsetX = 1.0, double offsetY = 1.0)
    {
        if (_clipboardNodes.Count == 0)
        {
            return [];
        }

        var spec = Draft();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var original in _clipboardNodes)
        {
            var id = UniqueId(spec, original.Id);
            map[original.Id] = id;

            var copy = original with
            {
                Id = id,
                X = Snap(original.X + offsetX),
                Y = Snap(original.Y + offsetY),
            };

            // A pasted cylinder port cannot keep its cylinder — two ports on
            // one cylinder is not a manifold, it is a mistake waiting to be
            // found at solve time.
            if (copy.Kind == ManifoldNodeKind.Port)
            {
                copy.Cylinder = NextFreeCylinder(spec);
            }

            spec.Nodes.Add(copy);
        }

        foreach (var connection in _clipboardConnections)
        {
            spec.Connections.Add(new ManifoldConnection(map[connection.From], map[connection.To]));
        }

        _selection.Clear();
        foreach (var id in map.Values)
        {
            _selection.Add(id);
        }

        Commit(spec);
        return map.Values.ToList();
    }

    // ---- Readouts ------------------------------------------------------------

    /// <summary>Live geometry summary (plan §8.4).</summary>
    public GeometrySummary Summary()
    {
        var spec = Manifold;
        if (spec is null || spec.Nodes.Count == 0)
        {
            return new GeometrySummary([
                new DerivedReadout("Manifold", "Single runner",
                    "No collector: each cylinder has its own pipe to atmosphere. Place a configuration to build one."),
            ]);
        }

        var readouts = new List<DerivedReadout>
        {
            new("Configuration", spec.Configuration.Length > 0 ? spec.Configuration : "custom"),
            new("Components", $"{spec.Nodes.Count} nodes, {spec.Connections.Count} connections"),
        };

        var ports = spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Port).OrderBy(n => n.Cylinder).ToList();
        var outlets = spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Atmosphere).ToList();

        if (ports.Count > 0 && outlets.Count > 0)
        {
            var paths = ports
                .Select(p => outlets.Select(o => spec.PathLengthMm(p.Id, o.Id)).FirstOrDefault(l => l.HasValue))
                .Where(l => l.HasValue)
                .Select(l => l!.Value)
                .ToList();

            if (paths.Count > 0)
            {
                var spread = paths.Max() - paths.Min();
                readouts.Add(new("Path length per cylinder",
                    $"{paths.Min():F0}–{paths.Max():F0} mm",
                    spread < 1.0 ? "Equal length." : $"{spread:F0} mm spread between the shortest and longest.",
                    spread > 100.0
                        ? "Over 100 mm of spread: the cylinders are tuned to noticeably different speeds."
                        : null));
            }
        }

        var primaries = spec.Nodes
            .Where(n => n.Kind == ManifoldNodeKind.Pipe && spec.Upstream(n.Id).Any(u =>
                spec.Node(u)?.Kind == ManifoldNodeKind.Port))
            .ToList();

        if (primaries.Count > 0)
        {
            readouts.Add(new("Primary length", $"{primaries.Min(p => p.LengthMm):F0} mm"));
            readouts.Add(new("Primary Ø", $"{primaries.Min(p => p.DiameterMm):F1} mm"));
        }

        var displacement = Displacement();
        foreach (var plenum in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Plenum))
        {
            var ratio = displacement > 0 ? plenum.VolumeLitres / displacement : 0.0;
            readouts.Add(new($"Plenum {plenum.Id}", $"{plenum.VolumeLitres:F2} L",
                $"{ratio:F2}× displacement."));
        }

        foreach (var taper in spec.Nodes.Where(n =>
                     n.Kind == ManifoldNodeKind.Pipe && n.OutletDiameterMm > 0))
        {
            var ratio = taper.OutletDiameterMm / taper.DiameterMm;
            readouts.Add(new($"Taper {taper.Id}", $"{ratio:F2}:1",
                $"{taper.DiameterMm:F1} → {taper.OutletDiameterMm:F1} mm over {taper.LengthMm:F0} mm."));
        }

        return new GeometrySummary(readouts);
    }

    /// <summary>
    /// Inline design warnings with citations (plan §8.4). Every one names a
    /// source, because a limit without one is just this tool's opinion.
    /// </summary>
    public IReadOnlyList<DesignWarning> Warnings()
    {
        var spec = Manifold;
        if (spec is null)
        {
            return [];
        }

        var warnings = new List<DesignWarning>();

        foreach (var node in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Pipe))
        {
            // Diffuser half-angle — the plan's own worked example.
            if (node.OutletDiameterMm > node.DiameterMm && node.LengthMm > 0)
            {
                var halfAngle = Math.Atan(
                    (node.OutletDiameterMm - node.DiameterMm) / 2.0 / node.LengthMm) * 180.0 / Math.PI;

                if (halfAngle > 7.0)
                {
                    warnings.Add(new(node.Id,
                        $"Diffuser half-angle {halfAngle:F1}°: separation likely.",
                        "Suggested ≤ 7° — lengthen the cone or reduce the exit diameter.",
                        "Claywell & Horkheimer, SAE 2006-01-3654",
                        "Results → Waves"));
                }
            }

            // A pipe too short to mesh is a pipe the solver will over-resolve
            // into six cells anyway, which is a silent change of geometry.
            if (node.LengthMm > 0 && node.DiameterMm > 0 && node.LengthMm / node.DiameterMm < 1.0)
            {
                warnings.Add(new(node.Id,
                    $"L/D of {node.LengthMm / node.DiameterMm:F2}: shorter than it is wide.",
                    "Plane-wave theory assumes L/D above about 1; below that this is an area change, not a pipe.",
                    "plan §5.3 (≥ 6 cells per pipe)"));
            }
        }

        foreach (var junction in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Junction))
        {
            var legs = spec.Upstream(junction.Id).Count() + spec.Downstream(junction.Id).Count();

            if (junction.BranchAngleDeg > 45.0 && legs >= 3)
            {
                warnings.Add(new(junction.Id,
                    $"Branch angle {junction.BranchAngleDeg:F0}° on a merge: high loss and poor scavenging.",
                    "A merge collector is usually 10–30°. A right angle is a plumbing tee, not a header.",
                    "Idelchik, Handbook of Hydraulic Resistance — converging wye"));
            }

            if (legs > 3)
            {
                warnings.Add(new(junction.Id,
                    $"{legs}-leg junction: the branch-angle loss coefficients do not cover it.",
                    "Solved with the constant-pressure model instead. Split into three-leg merges to use the loss model.",
                    "plan §2.7"));
            }

            // Area ratio through the merge: a collector much smaller than the
            // sum of its primaries chokes; much bigger dissipates the pulse.
            var inArea = spec.Upstream(junction.Id).Select(spec.Node).Where(n => n?.Kind == ManifoldNodeKind.Pipe)
                .Sum(n => Area(n!.OutletDiameterMm > 0 ? n.OutletDiameterMm : n.DiameterMm));
            var outArea = spec.Downstream(junction.Id).Select(spec.Node).Where(n => n?.Kind == ManifoldNodeKind.Pipe)
                .Sum(n => Area(n!.DiameterMm));

            if (inArea > 0 && outArea > 0)
            {
                var ratio = outArea / inArea;
                if (ratio < 0.6)
                {
                    warnings.Add(new(junction.Id,
                        $"Collector is {ratio:F2}× the combined primary area: restrictive.",
                        "Aim for roughly 0.7–1.0× — below that the merge raises pumping loss.",
                        "Blair, Design and Simulation of Four-Stroke Engines, ch. 6"));
                }
                else if (ratio > 1.6)
                {
                    warnings.Add(new(junction.Id,
                        $"Collector is {ratio:F2}× the combined primary area: the pulse will dissipate.",
                        "A large step toward constant-pressure operation costs the scavenging the header exists for.",
                        "Watson & Janota, Turbocharging the Internal Combustion Engine",
                        "Boost → Turbine"));
                }
            }
        }

        // Structural problems from the model itself, surfaced on the canvas.
        foreach (var issue in spec.Validate())
        {
            warnings.Add(new(
                issue.Path.Contains('.') ? issue.Path[(issue.Path.LastIndexOf('.') + 1)..] : null,
                issue.Message,
                issue.Severity == ModelIssueSeverity.Error ? "The manifold will not solve until this is fixed." : null));
        }

        return warnings;
    }

    // ---- Internals -----------------------------------------------------------

    private double Displacement()
    {
        var e = _session.Document.Engine;
        return Math.PI / 4.0 * Math.Pow(e.BoreMm / 1000.0, 2) * (e.StrokeMm / 1000.0) * e.CylinderCount * 1000.0;
    }

    private static double Area(double diameterMm) => Math.PI / 4.0 * diameterMm * diameterMm;

    private double Snap(double value) => SnapToGrid && GridSize > 0
        ? Math.Round(value / GridSize) * GridSize
        : value;

    private static int NextFreeCylinder(ManifoldSpec spec)
    {
        var used = spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Port).Select(n => n.Cylinder).ToHashSet();
        var c = 1;
        while (used.Contains(c))
        {
            c++;
        }

        return c;
    }

    private static string UniqueId(ManifoldSpec spec, string prefix)
    {
        var i = 1;
        string id;
        do
        {
            id = string.Create(CultureInfo.InvariantCulture, $"{prefix}{i++}");
        }
        while (spec.Node(id) is not null);

        return id;
    }

    /// <summary>
    /// A private copy of the graph to mutate. Nothing the caller does to it
    /// reaches the document until <see cref="Commit"/>, which is what makes
    /// an edit atomic and gives undo a distinct "before" to restore.
    ///
    /// Creating the manifold on first placement rather than on open means a
    /// model the user never touches keeps its simple runner: an empty graph
    /// in the document would switch the solver to a topology with no pipes.
    /// </summary>
    private ManifoldSpec Draft() => (_session.Document.ExhaustManifold ?? new ManifoldSpec()).DeepCopy();

    /// <summary>Write the edited graph back through the session.</summary>
    private void Commit(ManifoldSpec spec) => _session.EditByUser("ExhaustManifold", spec);
}
