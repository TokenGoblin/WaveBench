using WaveBench.Core.EngineModel;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// Builds wavetable banks from the solver (plan §3.6 steps 1–2): solve on an
/// rpm grid, capture converged cycles at each speed, and store each as a
/// crank-angle-indexed table. This is the seam between the physics and the
/// audio: everything downstream reads wavetables, never the solver.
/// </summary>
public static class AuralisationPipeline
{
    public sealed record SourceProbe(string Name, int DuctIndex, int Cell);

    /// <summary>Exhaust outlet and intake mouth — the two paths that radiate (plan §3.1).</summary>
    public static IReadOnlyList<SourceProbe> DefaultSources { get; } =
    [
        new("exhaust", 1, -1),  // −1 = last cell (outlet)
        new("intake", 0, 0),    // intake mouth
    ];

    /// <summary>
    /// Solve across an rpm grid and return one bank per source. Each point is
    /// converged, then <paramref name="captureCycles"/> cycles are captured
    /// and averaged into the table.
    /// </summary>
    public static IReadOnlyDictionary<string, WavetableBank> BuildBanks(
        EngineModelDocument document,
        IReadOnlyList<double> rpmGrid,
        IReadOnlyList<SourceProbe>? sources = null,
        int captureCycles = 4,
        int samplesPerCycle = 1440,
        Action<string>? progress = null)
    {
        sources ??= DefaultSources;
        var banks = sources.ToDictionary(s => s.Name, s => new WavetableBank(s.Name));

        foreach (var rpm in rpmGrid.OrderBy(r => r))
        {
            var engine = EngineBuilder.Build(document, rpm);
            var probes = new List<(SourceProbe Source, ProbeCapture Probe)>();
            foreach (var source in sources)
            {
                var duct = engine.Ducts[source.DuctIndex];
                var cell = source.Cell < 0 ? duct.CellCount + source.Cell : source.Cell;
                probes.Add((source, engine.AddProbe(duct, cell, source.Name)));
            }

            engine.RunToConvergence(
                r => r.NetValveMass.Length > 0 ? r.NetValveMass[0] : 0.0,
                document.Solver.ConvergenceTolerance, document.Solver.MinCycles, document.Solver.MaxCycles);
            engine.CaptureCycles(captureCycles);

            foreach (var (source, probe) in probes)
            {
                var resampled = probe.ResampleToCrankAngle(engine.Capture, samplesPerCycle);
                var table = CrankWavetable.FromCapture(rpm, resampled, samplesPerCycle).WithoutMean();
                banks[source.Name].Add(table);
            }

            progress?.Invoke($"{rpm,6:F0} rpm captured");
        }

        return banks;
    }

    /// <summary>
    /// Default rpm grid at the plan's 250 rpm spacing (§3.6 step 1).
    /// </summary>
    public static IReadOnlyList<double> Grid(double fromRpm, double toRpm, double step = 250.0)
    {
        var grid = new List<double>();
        for (var rpm = fromRpm; rpm <= toRpm + 1e-9; rpm += step)
        {
            grid.Add(rpm);
        }

        return grid;
    }
}
