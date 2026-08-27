using WaveBench.Core.EngineModel;

namespace WaveBench.Core.Solver;

/// <summary>
/// Reads the SOLVED gas state of a built manifold, so the pulse-interference
/// diagram (plan §2.8) can use the sound speed the solver actually computed
/// rather than a nominal one handed in by the caller.
///
/// The plan is explicit that transit is <c>L / a</c> with the actual local
/// sound speed, and the difference is not a detail: exhaust at 900 K carries a
/// pulse at roughly 600 m/s against 343 at ambient, so a nominal-speed diagram
/// places every arrival wrong by nearly a factor of two — and the diagram
/// exists precisely to say which arrivals land on top of each other.
///
/// It matters per pipe, too. A primary sees gas straight out of the port; a
/// tailpipe two metres downstream sees it several hundred kelvin cooler. One
/// number for the whole manifold is the same mistake at a smaller scale.
/// </summary>
public static class ManifoldPulseState
{
    /// <summary>
    /// Time-mean sound speed in each pipe of the manifold, m/s, keyed by graph
    /// node id.
    ///
    /// <b>This advances the engine by one full cycle.</b> Sampling has to
    /// happen while the solution moves — a single snapshot catches whatever
    /// happened to be in the pipe at that instant, which for a primary is a
    /// blowdown pulse or nothing at all depending on where in the cycle you
    /// look. Call it on an already-converged engine, where one more cycle is
    /// just another converged cycle.
    ///
    /// The mean is <b>mass-weighted across the cells</b>. A length mean gives
    /// a cool, nearly empty stretch of pipe the same say as a dense slug of
    /// hot gas, and it is the hot gas the pulse is travelling through.
    /// </summary>
    /// <param name="engine">A converged engine, built from the same manifold.</param>
    /// <param name="pipes">Node id to duct, from <see cref="AssembledManifold.Pipes"/>.</param>
    /// <param name="samples">Sample points across the cycle; more costs nothing but time.</param>
    public static IReadOnlyDictionary<string, double> MeanSoundSpeed(
        EngineSimulator engine,
        IReadOnlyDictionary<string, DuctSolver> pipes,
        int samples = 360)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pipes);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);

        var totals = pipes.Keys.ToDictionary(id => id, _ => 0.0, StringComparer.Ordinal);
        var taken = 0;

        // 720° of crank, sampled at a fixed angular spacing. Stepping is
        // CFL-limited and so not uniform in angle; sampling on angle rather
        // than on step count keeps the mean a time mean of the cycle and not
        // a mean weighted by wherever the timestep happened to be small.
        var start = engine.Angle;
        var nextSampleAt = 0.0;
        var span = 720.0;

        while (engine.Angle - start < span)
        {
            if (engine.Angle - start >= nextSampleAt)
            {
                foreach (var (id, duct) in pipes)
                {
                    totals[id] += MassWeightedSoundSpeed(duct);
                }

                taken++;
                nextSampleAt += span / samples;
            }

            engine.Step();
        }

        if (taken == 0)
        {
            // Cannot happen with samples ≥ 1, but a divide by zero here would
            // surface as an infinite sound speed and a zero transit time,
            // which is a much harder thing to notice than an exception.
            throw new InvalidOperationException("The cycle produced no samples.");
        }

        return totals.ToDictionary(kv => kv.Key, kv => kv.Value / taken, StringComparer.Ordinal);
    }

    /// <summary>Mass-weighted mean sound speed across one duct's cells, m/s.</summary>
    public static double MassWeightedSoundSpeed(DuctSolver duct)
    {
        ArgumentNullException.ThrowIfNull(duct);

        var weighted = 0.0;
        var mass = 0.0;

        for (var i = 0; i < duct.CellCount; i++)
        {
            var cellMass = duct.GetPrimitive(i).Rho * duct.Geometry.CellArea[i] * duct.Geometry.CellSize;
            weighted += duct.GetState(i).SoundSpeed * cellMass;
            mass += cellMass;
        }

        return mass > 0 ? weighted / mass : 0.0;
    }
}
