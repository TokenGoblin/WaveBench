using WaveBench.Core.Components;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Model;

namespace WaveBench.Core.Solver;

/// <summary>Where a cylinder's port attaches to the manifold.</summary>
/// <param name="Duct">The pipe the valve opens into.</param>
/// <param name="AtLeftEnd">Which end of that pipe the cylinder sits at.</param>
public sealed record PortAttachment(DuctSolver Duct, bool AtLeftEnd);

/// <summary>What the assembler built, and what it had to say about it.</summary>
/// <param name="Ports">Cylinder number (1-based) to its attachment.</param>
/// <param name="Notes">
/// Modelling decisions the caller should be able to surface — a junction that
/// had to fall back to the constant-pressure model, for instance. Silence
/// here would mean the user believes they got the pressure-loss model when
/// they did not.
/// </param>
public sealed record AssembledManifold(
    IReadOnlyDictionary<int, PortAttachment> Ports,
    IReadOnlyList<string> Notes);

/// <summary>
/// Turns a <see cref="ManifoldSpec"/> graph into solver objects — a
/// <see cref="DuctSolver"/> per pipe, a <see cref="Junction"/> per junction,
/// a <see cref="PlenumVolume"/> per plenum — and reports where each cylinder
/// port ended up so the valves can be attached.
///
/// <b>Pipes separate everything.</b> Two junctions cannot touch directly, nor
/// a port a junction: a 1-D solver marches state along a mesh, and a
/// zero-length connection between two boundary conditions has nowhere to put
/// that state. The collector library always inserts a pipe; anything else is
/// rejected with a reason rather than silently collapsed.
/// </summary>
public static class ManifoldAssembler
{
    /// <summary>
    /// Builds the graph into <paramref name="engine"/>.
    /// </summary>
    /// <param name="spec">Topology.</param>
    /// <param name="engine">Simulator to add ducts, junctions and plenums to.</param>
    /// <param name="gas">Gas model, shared by every component.</param>
    /// <param name="cellSize">Target cell size, m.</param>
    /// <param name="limiter">Slope limiter.</param>
    /// <param name="density">Initial density, kg/m³.</param>
    /// <param name="pressure">Initial and ambient pressure, Pa.</param>
    /// <param name="temperature">Ambient stagnation temperature, K.</param>
    /// <param name="cfl">CFL number.</param>
    public static AssembledManifold Build(
        ManifoldSpec spec,
        EngineSimulator engine,
        PerfectGasModel gas,
        double cellSize,
        SlopeLimiterKind limiter,
        double density,
        double pressure,
        double temperature,
        double cfl)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(engine);

        var errors = spec.Validate().Where(i => i.Severity == ModelIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Manifold cannot be built: " + string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
        }

        var notes = new List<string>();
        var ducts = new Dictionary<string, DuctSolver>(StringComparer.Ordinal);
        var plenums = new Dictionary<string, PlenumVolume>(StringComparer.Ordinal);

        // 1. Every pipe becomes a meshed duct.
        foreach (var node in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Pipe))
        {
            var duct = MakeDuct(node, gas, cellSize, limiter, density, pressure, cfl);
            ducts[node.Id] = duct;
            engine.Ducts.Add(duct);
        }

        // 2. Plenums.
        foreach (var node in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Plenum))
        {
            var plenum = new PlenumVolume(gas, node.VolumeLitres * 1e-3, pressure, temperature);
            plenums[node.Id] = plenum;
            engine.Plenums.Add(plenum);
        }

        // 3. Junctions, connecting the pipes either side.
        foreach (var node in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Junction))
        {
            var junction = new Junction(gas);
            var legs = 0;

            // Upstream pipes meet the junction at their downstream (right) end.
            foreach (var upstreamId in spec.Upstream(node.Id))
            {
                junction.Connect(RequirePipe(ducts, spec, upstreamId, node.Id), leftEnd: false,
                    isSideBranch: legs == 0, branchAngleDeg: node.BranchAngleDeg);
                legs++;
            }

            foreach (var downstreamId in spec.Downstream(node.Id))
            {
                junction.Connect(RequirePipe(ducts, spec, downstreamId, node.Id), leftEnd: true,
                    branchAngleDeg: node.BranchAngleDeg);
                legs++;
            }

            // The Idelchik pair-coefficient model is defined for a three-leg
            // tee. A 4-into-1 has five legs and there is no published
            // coefficient set for it here, so it falls back to Benson
            // constant-pressure — and SAYS so, because plan §2.7 defaults to
            // the loss model and a user is entitled to know when they did not
            // get it.
            if (legs == 3)
            {
                junction.Model = JunctionModel.TeeWithLosses;
            }
            else
            {
                junction.Model = JunctionModel.ConstantPressure;
                notes.Add(
                    $"Junction '{node.Id}' has {legs} legs: using the constant-pressure model. "
                    + "The branch-angle loss coefficients are defined for three-leg junctions only "
                    + "(plan §2.7); a multi-way merge is modelled without them.");
            }

            engine.Junctions.Add(junction);
        }

        // 4. Pipes in series. Two pipes joined directly — a collector into a
        //    tailpipe, or any stepped header — still need something to carry
        //    state across the seam: without it neither end has a boundary
        //    condition and the solve walks straight to NaN, which is exactly
        //    what a 4-1 did before this existed. Plan §2.7 names the answer:
        //    "stepped header: sequence of pipes with area-change junctions".
        foreach (var connection in spec.Connections)
        {
            if (!ducts.TryGetValue(connection.From, out var upstream) ||
                !ducts.TryGetValue(connection.To, out var downstream))
            {
                continue;
            }

            var seam = new Junction(gas) { Model = JunctionModel.ConstantPressure };
            seam.Connect(upstream, leftEnd: false);
            seam.Connect(downstream, leftEnd: true);
            engine.Junctions.Add(seam);
        }

        // 5. Open ends.
        foreach (var node in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Atmosphere))
        {
            var neighbourId = spec.Upstream(node.Id).Concat(spec.Downstream(node.Id)).Single();
            var duct = RequirePipe(ducts, spec, neighbourId, node.Id);
            var boundary = new ReservoirBoundary { StagnationPressure = pressure, StagnationTemperature = temperature };

            // The open end is whichever end of that pipe faces the node.
            if (spec.Downstream(neighbourId).Contains(node.Id))
            {
                duct.RightBoundary = BoundaryKind.External;
                duct.RightEnd = boundary;
            }
            else
            {
                duct.LeftBoundary = BoundaryKind.External;
                duct.LeftEnd = boundary;
            }
        }

        // 6. Cylinder ports.
        var ports = new Dictionary<int, PortAttachment>();
        foreach (var node in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Port))
        {
            var neighbourId = spec.Upstream(node.Id).Concat(spec.Downstream(node.Id)).Single();
            var duct = RequirePipe(ducts, spec, neighbourId, node.Id);

            // The cylinder sits at whichever end of the pipe faces the port.
            var atLeftEnd = spec.Downstream(node.Id).Contains(neighbourId);
            ports[node.Cylinder] = new PortAttachment(duct, atLeftEnd);
        }

        // 7. Plenum ports. A plenum joins its pipes through orifices sized to
        //    the pipe, which is the §2.7 "0D volume with multiple ports".
        foreach (var (id, plenum) in plenums)
        {
            foreach (var neighbourId in spec.Upstream(id).Concat(spec.Downstream(id)))
            {
                var duct = RequirePipe(ducts, spec, neighbourId, id);
                var atPlenumEnd = spec.Downstream(neighbourId).Contains(id);
                var area = atPlenumEnd ? duct.Geometry.FaceArea[^1] : duct.Geometry.FaceArea[0];

                // The port is the pipe's own cross-section: a log manifold's
                // stub opens straight into the gallery, it is not throttled.
                engine.Connectors.Add(new OrificeConnector(
                    new DuctEndpoint(duct, leftEnd: !atPlenumEnd),
                    new PlenumEndpoint(plenum, gas))
                {
                    EffectiveArea = area,
                });
            }
        }

        return new AssembledManifold(ports, notes);
    }

    private static DuctSolver RequirePipe(
        IReadOnlyDictionary<string, DuctSolver> ducts, ManifoldSpec spec, string id, string neighbourOf)
    {
        if (ducts.TryGetValue(id, out var duct))
        {
            return duct;
        }

        var kind = spec.Node(id)?.Kind.ToString() ?? "missing";
        throw new InvalidOperationException(
            $"'{neighbourOf}' connects directly to '{id}' ({kind}). Components must be separated by a pipe — "
            + "a 1-D solver has nowhere to hold state across a zero-length connection.");
    }

    private static DuctSolver MakeDuct(
        ManifoldNode node, PerfectGasModel gas, double cellSize, SlopeLimiterKind limiter,
        double density, double pressure, double cfl)
    {
        var length = node.LengthMm * 1e-3;
        var inlet = node.DiameterMm * 1e-3;
        var outlet = node.OutletDiameterMm > 0 ? node.OutletDiameterMm * 1e-3 : inlet;
        var cells = Math.Max(6, (int)Math.Round(length / cellSize)); // plan §5.3: ≥ 6 cells per pipe

        var geometry = Math.Abs(outlet - inlet) < 1e-12
            ? DuctGeometry.Uniform(length, cells, inlet, node.RoughnessMm * 1e-3)
            : DuctGeometry.Taper(length, cells, inlet, outlet, node.RoughnessMm * 1e-3);

        var duct = new DuctSolver(geometry, gas) { Limiter = limiter, Cfl = cfl };
        for (var i = 0; i < duct.CellCount; i++)
        {
            duct.SetState(i, new PrimitiveState(density, 0.0, pressure));
        }

        return duct;
    }
}
