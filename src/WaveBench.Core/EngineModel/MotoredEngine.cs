using WaveBench.Core.Components;
using WaveBench.Core.Solver;

namespace WaveBench.Core.EngineModel;

/// <summary>
/// Motored (no combustion) engine network: ducts, junctions, plenums,
/// orifice connectors, valve connections and cylinders stepped on a common
/// CFL-limited timestep in a deterministic fixed order (plan §5.7) —
/// junctions, valves, connectors, ducts, plenums, cylinders. Crank speed is
/// prescribed.
/// </summary>
public sealed class MotoredEngine
{
    public List<DuctSolver> Ducts { get; } = [];

    public List<Junction> Junctions { get; } = [];

    public List<PlenumVolume> Plenums { get; } = [];

    public List<OrificeConnector> Connectors { get; } = [];

    public List<ValveConnection> Valves { get; } = [];

    public List<Cylinder> Cylinders { get; } = [];

    public required double Rpm { get; set; }

    /// <summary>Engine crank angle, deg (grows without wrap).</summary>
    public double Angle { get; private set; }

    public double Time { get; private set; }

    public double Omega => Rpm * 2.0 * Math.PI / 60.0;

    /// <summary>Net work done BY the gas on the pistons since start, J.</summary>
    public double CumulativePistonWork => Cylinders.Sum(c => c.CumulativeWork);

    public void Step()
    {
        var dt = Ducts.Min(d => d.StableTimestep());

        foreach (var junction in Junctions)
        {
            junction.Update();
        }

        foreach (var valve in Valves)
        {
            valve.Update(dt, Angle);
        }

        foreach (var connector in Connectors)
        {
            connector.Update(dt);
        }

        foreach (var duct in Ducts)
        {
            duct.Step(dt);
        }

        foreach (var plenum in Plenums)
        {
            plenum.Commit();
        }

        foreach (var cylinder in Cylinders)
        {
            cylinder.Step(dt, Angle, Omega);
        }

        var dTheta = Omega * dt * 180.0 / Math.PI;
        Angle += dTheta;
        Time += dt;
    }

    /// <summary>Advance exactly one 720° cycle, integrating per-valve flows.</summary>
    public CycleResult RunCycle()
    {
        var start = Angle;
        var intakeIn = new double[Valves.Count];
        var previousDt = 0.0;

        while (Angle - start < 720.0)
        {
            var tBefore = Time;
            Step();
            previousDt = Time - tBefore;
            for (var v = 0; v < Valves.Count; v++)
            {
                intakeIn[v] += Valves[v].MassFlow * previousDt;
            }
        }

        return new CycleResult
        {
            NetValveMass = intakeIn,
            EndAngle = Angle,
        };
    }

    /// <summary>
    /// Run cycles until the chosen metric is periodic (plan §5.4): relative
    /// change below tolerance between successive cycles, minimum/maximum
    /// cycle counts. Returns the converged cycle's result and the count.
    /// </summary>
    public (CycleResult Result, int Cycles) RunToConvergence(
        Func<CycleResult, double> metric,
        double tolerance = 1e-3,
        int minCycles = 5,
        int maxCycles = 40)
    {
        CycleResult last = RunCycle();
        var lastMetric = metric(last);
        for (var cycle = 2; cycle <= maxCycles; cycle++)
        {
            var result = RunCycle();
            var value = metric(result);
            var change = Math.Abs(value - lastMetric) / Math.Max(Math.Abs(value), 1e-12);
            last = result;
            lastMetric = value;
            if (cycle >= minCycles && change < tolerance)
            {
                return (last, cycle);
            }
        }

        return (last, maxCycles);
    }

    /// <summary>Total gas mass in ducts, plenums and cylinders, kg.</summary>
    public double TotalMass() =>
        Ducts.Sum(d => d.ConservedTotals().Mass)
        + Plenums.Sum(p => p.Mass)
        + Cylinders.Sum(c => c.Mass);
}

public sealed class CycleResult
{
    /// <summary>Net mass through each valve (into the cylinder), kg, in valve order.</summary>
    public required double[] NetValveMass { get; init; }

    public required double EndAngle { get; init; }
}

/// <summary>
/// Instant first-cut analytics computed with the current gas state, never STP
/// (plan §2.10). These seed the 1D model and sit beside its results in the UI
/// so users learn where simple theory breaks down.
/// </summary>
public static class QuickEstimate
{
    /// <summary>
    /// Organ-pipe wave-return tuning: L = a·Δθ/(12·N) (plan §2.10; L m,
    /// a m/s, N rpm, Δθ crank degrees of the out-and-back window).
    /// </summary>
    public static double OrganPipeTunedLength(double soundSpeed, double windowDeg, double rpm) =>
        soundSpeed * windowDeg / (12.0 * rpm);

    public static double OrganPipeTunedRpm(double soundSpeed, double windowDeg, double length) =>
        soundSpeed * windowDeg / (12.0 * length);

    /// <summary>Helmholtz resonance f = (a/2π)·√(A/(V·L_eff)) (plan §2.10).</summary>
    public static double HelmholtzFrequency(double soundSpeed, double neckArea, double volume, double effectiveNeckLength) =>
        soundSpeed / (2.0 * Math.PI) * Math.Sqrt(neckArea / (volume * effectiveNeckLength));

    /// <summary>
    /// The intake wave-return window for the organ-pipe estimate, computed
    /// from geometry rather than assumed: the suction wave is launched at
    /// maximum piston speed after overlap TDC, and the returning compression
    /// must arrive by the EFFECTIVE intake closing — the angle where lift
    /// falls to a quarter of maximum, beyond which the valve no longer
    /// dominates the flow. Validated against the solved VE peak in the
    /// verification suite.
    /// </summary>
    public static double IntakeWaveReturnWindowDeg(CrankGeometry crank, CamProfile intakeCam)
    {
        var launch = 360.0 + crank.MaxPistonSpeedAngle();
        var effectiveClose = intakeCam.ClosingAngleAtFraction(0.25);
        return effectiveClose - launch;
    }
}
