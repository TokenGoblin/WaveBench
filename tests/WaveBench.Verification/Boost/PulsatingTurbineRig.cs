using WaveBench.Boost;
using WaveBench.Boost.Unsteady;
using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;

namespace WaveBench.Verification.Boost;

/// <summary>One sample of the turbine entry over a pulsating cycle.</summary>
/// <param name="Time">Seconds.</param>
/// <param name="ExpansionRatio">p₀/p₄ at the turbine entry flange.</param>
/// <param name="MassFlowParameter">ṁ√T₀/p₀ at the same place.</param>
/// <param name="PowerW">Instantaneous shaft power.</param>
public readonly record struct RigSample(
    double Time, double ExpansionRatio, double MassFlowParameter, double PowerW);

/// <summary>
/// A pulsating turbine gas stand, in software.
///
/// A pipe fed by a pulsating reservoir, terminated by the turbine — which is
/// how the hysteresis of plan §4.3 is measured in reality (Dale &amp; Watson,
/// Winterbone &amp; Pearson, Szymko/Martinez-Botas). The rig exists so the two
/// turbine models can be compared on identical gas dynamics with nothing but
/// the model differing, and so pulse frequency and amplitude can be swept
/// independently, which an engine cannot do.
///
/// Shaft speed is held, exactly as a dynamometer holds it: the measurement is
/// of the turbine, not of a turbocharger.
/// </summary>
internal sealed class PulsatingTurbineRig
{
    private readonly DuctSolver _manifold;
    private readonly ReservoirBoundary _source;
    private readonly TurbineStage _stage;
    private readonly List<Junction> _junctions;

    public PulsatingTurbineRig(
        TurbineModelKind kind,
        TurbineMap map,
        double shaftRpm,
        double meanPressurePa,
        double pulseAmplitudePa,
        double pulseFrequencyHz,
        double inletTemperatureK = 1050.0,
        int manifoldCells = 40,
        VoluteGeometry? volute = null,
        double manifoldLengthM = 0.35)
    {
        MeanPressurePa = meanPressurePa;
        PulseAmplitudePa = pulseAmplitudePa;
        PulseFrequencyHz = pulseFrequencyHz;
        InletTemperatureK = inletTemperatureK;

        // Exhaust products rather than air: γ = 1.33 changes both the wave
        // speeds and the work available in an expansion, and using 1.4 here
        // would flatter every turbine result by several percent.
        var gas = new PerfectGasModel(new PerfectGas(1.33, 287.0));
        Gas = gas;

        _manifold = new DuctSolver(DuctGeometry.Uniform(manifoldLengthM, manifoldCells, 0.040), gas) { Cfl = 0.8 };

        var rho = meanPressurePa / (287.0 * inletTemperatureK);
        for (var i = 0; i < _manifold.CellCount; i++)
        {
            _manifold.SetState(i, new PrimitiveState(rho, 0.0, meanPressurePa));
        }

        _source = new ReservoirBoundary
        {
            StagnationPressure = meanPressurePa,
            StagnationTemperature = inletTemperatureK,
        };
        _manifold.LeftBoundary = BoundaryKind.External;
        _manifold.LeftEnd = _source;

        var shaft = new TurboShaft(3.1e-6, shaftRpm);

        _stage = TurbineStage.Build(
            kind, map, shaft,
            [(_manifold, false, "single")],
            volute ?? new VoluteGeometry(0.150, Math.PI * 0.040 * 0.040 / 4.0, 8.0e-4, 12),
            gas);

        foreach (var duct in _stage.OwnedDucts)
        {
            for (var i = 0; i < duct.CellCount; i++)
            {
                duct.SetState(i, new PrimitiveState(rho, 0.0, meanPressurePa));
            }
        }

        _junctions = _stage.OwnedJunctions.ToList();
    }

    public IGasModel Gas { get; }

    public double MeanPressurePa { get; }

    public double PulseAmplitudePa { get; }

    public double PulseFrequencyHz { get; }

    public double InletTemperatureK { get; }

    public TurbineStage Stage => _stage;

    public double Time { get; private set; }

    public List<RigSample> Samples { get; } = [];

    /// <summary>
    /// The blowdown pulse: a raised cosine over the first third of the period,
    /// then quiet. Sharper than a sinusoid on purpose — a real blowdown is a
    /// short, steep event, and the hysteresis under test grows with exactly
    /// that steepness.
    /// </summary>
    public double SourcePressure(double t)
    {
        var phase = (t * PulseFrequencyHz) % 1.0;
        const double width = 1.0 / 3.0;
        if (phase >= width)
        {
            return MeanPressurePa;
        }

        return MeanPressurePa + (PulseAmplitudePa * 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * phase / width)));
    }

    /// <summary>Run for a number of pulse periods, recording only the last one.</summary>
    public void Run(int periods, int recordLast = 1)
    {
        var period = 1.0 / PulseFrequencyHz;
        var recordFrom = (periods - recordLast) * period;

        while (Time < periods * period)
        {
            var dt = Math.Min(_manifold.StableTimestep(), MinimumOwnedTimestep());
            dt = Math.Min(dt, (periods * period) - Time);
            if (dt <= 0)
            {
                break;
            }

            _source.StagnationPressure = SourcePressure(Time);

            foreach (var junction in _junctions)
            {
                junction.Update();
            }

            _manifold.Step(dt);
            foreach (var volute in _stage.OwnedDucts)
            {
                volute.Step(dt);
            }

            _stage.IntegrateAtFixedSpeed(dt);
            Time += dt;

            if (Time >= recordFrom)
            {
                var (er, mfp, _) = _stage.Entries[0].InletSample();
                Samples.Add(new RigSample(Time, er, mfp, _stage.InstantaneousPowerW));
            }
        }
    }

    private double MinimumOwnedTimestep()
    {
        var dt = double.PositiveInfinity;
        foreach (var volute in _stage.OwnedDucts)
        {
            dt = Math.Min(dt, volute.StableTimestep());
        }

        return dt;
    }

    /// <summary>
    /// How far the (expansion ratio, mass flow parameter) trace is from being
    /// single-valued: at each expansion ratio, the spread between the filling
    /// and emptying branches, taken at its widest and divided by the mean mass
    /// flow parameter.
    ///
    /// <b>Not the enclosed area.</b> The obvious metric — shoelace area over
    /// the bounding box — measures the wrong thing, and measuring it produced
    /// the wrong answer: a bigger pulse grows the bounding box faster than it
    /// grows the enclosed area, so a box-normalised loop appears to SHRINK with
    /// amplitude. The literature's "wider loop" means the vertical opening at a
    /// given pressure ratio, which is what this returns.
    /// </summary>
    public double LoopOpenness(int bins = 40)
    {
        if (Samples.Count < 8)
        {
            return 0.0;
        }

        var erMin = Samples.Min(s => s.ExpansionRatio);
        var erMax = Samples.Max(s => s.ExpansionRatio);
        if (erMax - erMin <= 0)
        {
            return 0.0;
        }

        var mean = Samples.Average(s => Math.Abs(s.MassFlowParameter));
        if (mean <= 0)
        {
            return 0.0;
        }

        var lo = new double[bins];
        var hi = new double[bins];
        var seen = new int[bins];
        Array.Fill(lo, double.MaxValue);
        Array.Fill(hi, double.MinValue);

        foreach (var s in Samples)
        {
            var bin = Math.Clamp(
                (int)((s.ExpansionRatio - erMin) / (erMax - erMin) * bins), 0, bins - 1);
            lo[bin] = Math.Min(lo[bin], s.MassFlowParameter);
            hi[bin] = Math.Max(hi[bin], s.MassFlowParameter);
            seen[bin]++;
        }

        var widest = 0.0;
        for (var b = 0; b < bins; b++)
        {
            // A bin with a handful of samples is a corner of the trace, not a
            // branch separation; requiring a few keeps sampling noise out.
            if (seen[b] >= 4)
            {
                widest = Math.Max(widest, hi[b] - lo[b]);
            }
        }

        return widest / mean;
    }

    /// <summary>
    /// Peak minus trough expansion ratio actually delivered to the turbine
    /// entry. Not the same as the source amplitude — the manifold is a low-pass
    /// filter, and how much of the pulse survives it is exactly what plan §4.6.1
    /// is about.
    /// </summary>
    public double DeliveredExpansionRatioSpan() =>
        Samples.Count == 0 ? 0.0 : Samples.Max(s => s.ExpansionRatio) - Samples.Min(s => s.ExpansionRatio);

    /// <summary>Mean shaft power over the recorded period, W.</summary>
    public double MeanPowerW()
    {
        if (Samples.Count < 2)
        {
            return 0.0;
        }

        double sum = 0;
        for (var i = 1; i < Samples.Count; i++)
        {
            sum += 0.5 * (Samples[i].PowerW + Samples[i - 1].PowerW) * (Samples[i].Time - Samples[i - 1].Time);
        }

        return sum / (Samples[^1].Time - Samples[0].Time);
    }
}
