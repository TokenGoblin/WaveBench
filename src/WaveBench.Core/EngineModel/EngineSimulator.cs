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
public sealed class EngineSimulator
{
    public List<DuctSolver> Ducts { get; } = [];

    public List<Junction> Junctions { get; } = [];

    public List<PlenumVolume> Plenums { get; } = [];

    /// <summary>
    /// Modelling decisions taken while building this engine that the user
    /// should see — a junction that fell back to the constant-pressure model,
    /// for instance. Carried on the engine rather than on the builder because
    /// operating points are built in parallel.
    /// </summary>
    public List<string> Notes { get; } = [];

    /// <summary>
    /// Manifold graph node id to the duct built for it, empty when this engine
    /// has no manifold graph. Lets an analysis read the solved state of a pipe
    /// the user named on the canvas — see
    /// <see cref="Solver.ManifoldPulseState"/>.
    /// </summary>
    public IReadOnlyDictionary<string, Solver.DuctSolver> ManifoldPipes { get; set; } =
        new Dictionary<string, Solver.DuctSolver>(StringComparer.Ordinal);

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

    /// <summary>
    /// Opt-in wall-time breakdown of Step phases (dev/profiling aid; off by
    /// default, deterministic results unaffected).
    /// </summary>
    public static bool EnableProfiling;

    public static readonly long[] ProfileTicks = new long[5]; // valves, ducts, cylinders, junctions, other

    private int _totalCells;

    public void Step()
    {
        var t0 = EnableProfiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

        var dt = double.MaxValue;
        for (var d = 0; d < Ducts.Count; d++)
        {
            var candidate = Ducts[d].StableTimestep();
            if (candidate < dt)
            {
                dt = candidate;
            }
        }

        foreach (var junction in Junctions)
        {
            junction.Update();
        }

        var t1 = EnableProfiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

        foreach (var valve in Valves)
        {
            valve.Update(dt, Angle);
        }

        foreach (var connector in Connectors)
        {
            connector.Update(dt);
        }

        var t2 = EnableProfiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

        // Pipes stepped in parallel with the coupling barrier already passed
        // (plan §5.7). Each duct's arithmetic is independent within a step, so
        // scheduling cannot change any result — determinism holds bit-exactly.
        // Parallel dispatch only pays for itself on big meshes: below ~2000
        // total cells the per-step scheduling overhead exceeds the work
        // (measured), so small networks step sequentially.
        if (_totalCells == 0)
        {
            foreach (var duct in Ducts)
            {
                _totalCells += duct.CellCount;
            }
        }

        if (Ducts.Count >= 4 && _totalCells >= 2000)
        {
            Parallel.For(0, Ducts.Count, d => Ducts[d].Step(dt));
        }
        else
        {
            foreach (var duct in Ducts)
            {
                duct.Step(dt);
            }
        }

        var t3 = EnableProfiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

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
        RecordProbes();

        if (EnableProfiling)
        {
            var t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            ProfileTicks[3] += t1 - t0; // dt scan + junctions
            ProfileTicks[0] += t2 - t1; // valves + connectors
            ProfileTicks[1] += t3 - t2; // ducts
            ProfileTicks[2] += t4 - t3; // plenums + cylinders
        }
    }

    /// <summary>Advance exactly one 720° cycle, integrating per-valve flows and per-cylinder metrics.</summary>
    public CycleResult RunCycle()
    {
        var start = Angle;
        var startTime = Time;
        var intakeIn = new double[Valves.Count];
        var workBefore = Cylinders.Select(c => c.CumulativeWork).ToArray();
        var fuelBefore = Cylinders.Select(c => c.CumulativeFuelBurned).ToArray();
        var knockBefore = Cylinders.Select(c => c.CumulativeKnockIntegral).ToArray();
        foreach (var cyl in Cylinders)
        {
            cyl.ResetPeakPressure();
        }

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

        var imep = new double[Cylinders.Count];
        var peak = new double[Cylinders.Count];
        var fuel = new double[Cylinders.Count];
        var knock = new double[Cylinders.Count];
        for (var c = 0; c < Cylinders.Count; c++)
        {
            imep[c] = (Cylinders[c].CumulativeWork - workBefore[c]) / Cylinders[c].Geometry.DisplacedVolume;
            peak[c] = Cylinders[c].CyclePeakPressure;
            fuel[c] = Cylinders[c].CumulativeFuelBurned - fuelBefore[c];
            knock[c] = Cylinders[c].CumulativeKnockIntegral - knockBefore[c];
        }

        return new CycleResult
        {
            NetValveMass = intakeIn,
            EndAngle = Angle,
            CycleDuration = Time - startTime,
            Imep = imep,
            PeakPressure = peak,
            FuelMass = fuel,
            KnockIntegral = knock,
        };
    }

    /// <summary>
    /// Wall temperature convergence band, K (plan §2.9: "iterate wall
    /// temperatures to convergence across cycles"). A converged gas state over
    /// a wall still climbing toward its own balance is not a converged
    /// operating point — the gas metric goes quiet long before the wall does,
    /// because the wall moves on a slower loop.
    /// </summary>
    public double WallConvergenceK { get; set; } = 0.5;

    /// <summary>Largest wall temperature change at the last cycle boundary, K.</summary>
    public double LastWallChangeK { get; private set; }

    /// <summary>
    /// Solve every cyclic-steady wall for its cycle-average balance and adopt
    /// the result. Called at each cycle boundary by
    /// <see cref="RunToConvergence"/>; returns the largest change in K, or
    /// zero when no duct has such a wall.
    /// </summary>
    public double SettleWalls()
    {
        var change = 0.0;
        foreach (var duct in Ducts)
        {
            if (duct.Wall is { Mode: Solver.WallUpdate.CyclicSteady } wall)
            {
                change = Math.Max(change, wall.SolveCyclicSteady());
            }
        }

        return LastWallChangeK = change;
    }

    /// <summary>
    /// Run cycles until the chosen metric is periodic (plan §5.4): relative
    /// change below tolerance between successive cycles, minimum/maximum
    /// cycle counts. Returns the converged cycle's result and the count.
    ///
    /// Pipe walls in cyclic-steady mode are settled at every cycle boundary
    /// and their temperature must also be periodic before the run is called
    /// converged.
    /// </summary>
    public (CycleResult Result, int Cycles) RunToConvergence(
        Func<CycleResult, double> metric,
        double tolerance = 1e-3,
        int minCycles = 5,
        int maxCycles = 40)
    {
        CycleResult last = RunCycle();
        SettleWalls();
        var lastMetric = metric(last);
        for (var cycle = 2; cycle <= maxCycles; cycle++)
        {
            var result = RunCycle();
            var wallChange = SettleWalls();
            var value = metric(result);
            var change = Math.Abs(value - lastMetric) / Math.Max(Math.Abs(value), 1e-12);
            last = result;
            lastMetric = value;
            if (cycle >= minCycles && change < tolerance && wallChange < WallConvergenceK)
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

    /// <summary>
    /// High-resolution capture (plan §3.4). The timeline (time, crank angle)
    /// is recorded ONCE here; probes hold only their own float32 pressure
    /// samples, which is the storage format the results store wants.
    /// </summary>
    public CaptureTimeline Capture { get; } = new();

    /// <summary>Probes attached to duct cells; add via <see cref="AddProbe"/>.</summary>
    public IReadOnlyList<ProbeCapture> Probes => _probes;

    private readonly List<ProbeCapture> _probes = [];

    public ProbeCapture AddProbe(Solver.DuctSolver duct, int cell, string name)
    {
        if (!Ducts.Contains(duct))
        {
            throw new ArgumentException("Probe duct must belong to this engine.", nameof(duct));
        }

        var probe = new ProbeCapture(duct, cell, name);
        _probes.Add(probe);
        return probe;
    }

    /// <summary>x–t field captures attached to ducts; add via <see cref="AddFieldCapture"/>.</summary>
    public IReadOnlyList<DuctFieldCapture> Fields => _fields;

    private readonly List<DuctFieldCapture> _fields = [];

    /// <summary>
    /// Record a pipe's whole field over crank angle (plan §8.4 wave diagram).
    ///
    /// Costs cells × frames × 4 bytes and nothing else, so it is opt-in per
    /// pipe: capturing every duct of a collector network at half a degree
    /// would be tens of megabytes for pipes nobody is looking at.
    /// </summary>
    public DuctFieldCapture AddFieldCapture(
        Solver.DuctSolver duct,
        string name,
        FieldQuantity quantity = FieldQuantity.Pressure,
        int samplesPerCycle = 720,
        int expectedCycles = 4)
    {
        if (!Ducts.Contains(duct))
        {
            throw new ArgumentException("Duct is not part of this engine.", nameof(duct));
        }

        var capture = new DuctFieldCapture(duct, name, quantity, samplesPerCycle, expectedCycles);
        _fields.Add(capture);
        return capture;
    }

    /// <summary>Drop all probes and field captures (they hold duct references).</summary>
    public void ClearProbes()
    {
        _probes.Clear();
        _fields.Clear();
        Capture.Clear();
    }

    /// <summary>
    /// Run <paramref name="cycles"/> cycles with capture on, discarding any
    /// earlier capture — the plan §3.4 "last k converged cycles" workflow:
    /// converge first with <see cref="RunToConvergence"/>, then call this.
    /// </summary>
    public CycleResult CaptureCycles(int cycles)
    {
        Capture.Clear();
        foreach (var probe in _probes)
        {
            probe.Clear();
        }

        foreach (var field in _fields)
        {
            field.Clear();
        }

        Capture.Enabled = true;
        try
        {
            // Record the state at t₀ BEFORE the first step: samples are taken
            // after each step, so without this the captured span falls one
            // step short of k whole cycles and the crank-angle resampler
            // would silently return k−1 cycles.
            RecordProbes();

            CycleResult last = null!;
            for (var i = 0; i < cycles; i++)
            {
                last = RunCycle();
            }

            return last;
        }
        finally
        {
            Capture.Enabled = false;
        }
    }

    private void RecordProbes()
    {
        if (!Capture.Enabled)
        {
            return;
        }

        Capture.Record(Time, Angle);
        foreach (var probe in _probes)
        {
            probe.Record();
        }

        // Field captures decimate themselves on angle, so they see every step
        // and keep only the ones that land on a frame boundary.
        foreach (var field in _fields)
        {
            field.Offer(Angle);
        }
    }
}

/// <summary>
/// The shared capture timeline: time and crank angle per recorded step,
/// stored once for all probes (they sample the same solver clock).
/// </summary>
public sealed class CaptureTimeline
{
    public bool Enabled { get; set; }

    public List<double> Times { get; } = [];

    public List<double> AnglesDeg { get; } = [];

    public int SampleCount => Times.Count;

    internal void Record(double time, double angleDeg)
    {
        Times.Add(time);
        AnglesDeg.Add(angleDeg);
    }

    public void Clear()
    {
        Times.Clear();
        AnglesDeg.Clear();
    }
}

/// <summary>
/// One capture probe: static pressure at a duct cell, float32 (plan §3.4
/// storage format), sampled on the shared <see cref="CaptureTimeline"/>.
/// </summary>
public sealed class ProbeCapture(Solver.DuctSolver duct, int cell, string name)
{
    private readonly Solver.DuctSolver _duct = duct;
    private readonly int _cell = cell;

    public string Name { get; } = name;

    /// <summary>Raw samples at solver steps (non-uniform Δt), Pa.</summary>
    public List<float> Pressure { get; } = [];

    /// <summary>
    /// Axial velocity at the same steps, m/s, signed. Broadband flow noise
    /// scales as a power of velocity (plan §3.4), so it needs this rather
    /// than pressure — the two are not interchangeable at a termination,
    /// where pressure goes to nearly zero exactly where velocity peaks.
    /// </summary>
    public List<float> Velocity { get; } = [];

    internal void Record()
    {
        Pressure.Add((float)_duct.GetPressure(_cell));
        Velocity.Add((float)_duct.GetVelocity(_cell));
    }

    public void Clear()
    {
        Pressure.Clear();
        Velocity.Clear();
    }

    /// <summary>
    /// Resample onto a uniform crank-angle grid over whole 720° cycles —
    /// the documented crank-angle basis (plan §3.4). Solver steps are
    /// CFL-limited and therefore non-uniform in time but monotone in angle,
    /// so linear interpolation on angle is the correct resampler; the result
    /// is what both the results store and the order/auralisation chain want.
    /// </summary>
    public float[] ResampleToCrankAngle(CaptureTimeline timeline, int samplesPerCycle = 1440) =>
        Resample(Pressure, timeline, samplesPerCycle);

    /// <summary>
    /// The same resampling applied to <see cref="Velocity"/> — the input the
    /// broadband flow-noise generator needs.
    /// </summary>
    public float[] ResampleVelocityToCrankAngle(CaptureTimeline timeline, int samplesPerCycle = 1440) =>
        Resample(Velocity, timeline, samplesPerCycle);

    private static float[] Resample(List<float> series, CaptureTimeline timeline, int samplesPerCycle)
    {
        if (timeline.SampleCount < 2 || series.Count != timeline.SampleCount)
        {
            throw new InvalidOperationException("Probe and timeline sample counts must match and be non-trivial.");
        }

        var start = timeline.AnglesDeg[0];
        var cycles = (int)((timeline.AnglesDeg[^1] - start) / 720.0);
        if (cycles < 1)
        {
            throw new InvalidOperationException("Capture is shorter than one 720° cycle.");
        }

        var total = cycles * samplesPerCycle;
        var output = new float[total];
        var index = 0;
        for (var i = 0; i < total; i++)
        {
            var target = start + i * 720.0 / samplesPerCycle;
            while (index < timeline.SampleCount - 2 && timeline.AnglesDeg[index + 1] < target)
            {
                index++;
            }

            var a0 = timeline.AnglesDeg[index];
            var a1 = timeline.AnglesDeg[index + 1];
            var w = a1 > a0 ? Math.Clamp((target - a0) / (a1 - a0), 0.0, 1.0) : 0.0;
            output[i] = (float)(series[index] + w * (series[index + 1] - series[index]));
        }

        return output;
    }

    /// <summary>Effective sample rate of the resampled grid at this engine speed, Hz.</summary>
    public static double ResampledSampleRate(double rpm, int samplesPerCycle = 1440) =>
        samplesPerCycle / (120.0 / rpm);
}

public sealed class CycleResult
{
    /// <summary>Net mass through each valve (into the cylinder), kg, in valve order.</summary>
    public required double[] NetValveMass { get; init; }

    public required double EndAngle { get; init; }

    /// <summary>Wall-clock duration of the cycle, s.</summary>
    public double CycleDuration { get; init; }

    /// <summary>Net indicated mean effective pressure per cylinder, Pa (full-cycle, pumping included).</summary>
    public double[] Imep { get; init; } = [];

    public double[] PeakPressure { get; init; } = [];

    public double[] FuelMass { get; init; } = [];

    public double[] KnockIntegral { get; init; } = [];
}

/// <summary>Brake-side performance from cycle metrics (plan §2.5).</summary>
public static class PerformanceMetrics
{
    /// <summary>Brake MEP = net IMEP − Chen–Flynn FMEP, Pa.</summary>
    public static double Bmep(double imep, ChenFlynnFriction friction, double peakPressure, double meanPistonSpeed) =>
        imep - friction.Fmep(peakPressure, meanPistonSpeed);

    /// <summary>Brake torque of a four-stroke, N·m: T = BMEP·V_d,total/(4π).</summary>
    public static double Torque(double bmep, double totalDisplacement) =>
        bmep * totalDisplacement / (4.0 * Math.PI);

    /// <summary>Brake power, W.</summary>
    public static double Power(double torque, double rpm) => torque * rpm * 2.0 * Math.PI / 60.0;

    /// <summary>Brake-specific fuel consumption, kg/J (multiply by 3.6e9 for g/kWh).</summary>
    public static double Bsfc(double fuelMassPerCycle, double cycleDuration, double brakePower) =>
        fuelMassPerCycle / cycleDuration / brakePower;
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
