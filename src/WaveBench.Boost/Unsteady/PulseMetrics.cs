namespace WaveBench.Boost.Unsteady;

/// <summary>
/// What a turbine manifold actually delivered, over one cycle.
/// </summary>
/// <param name="PulseEnergyDelivery">
/// What the pulse was worth: mean turbine power divided by the power the same
/// mean mass flow would have produced arriving steadily at the cycle-mean
/// pressure.
///
/// <b>Above 1 the pulse paid for itself</b> — energy arriving concentrated at
/// high pressure is worth more than the same energy arriving smoothly, because
/// the available work goes as <c>1 − ER^(−(γ−1)/γ)</c> and that is a concave
/// function of pressure ratio the wrong way round for averaging. At 1 the
/// manifold has flattened the pulse into constant-pressure operation and the
/// volume has eaten the whole benefit. This IS the pulse-versus-constant-
/// pressure axis of Watson &amp; Janota, expressed as a single number.
///
/// The first definition tried here — the fraction of delivered power arriving
/// while the pressure was above its own cycle mean — turned out not to
/// discriminate: it sat between 60% and 67% for every manifold in a
/// primary-diameter sweep and was not even monotone, because a smooth trace
/// spends about as long above its mean as a peaky one does.
/// </param>
/// <param name="ManifoldVolumeRatio">Manifold volume ÷ displacement per exhaust event.</param>
/// <param name="MeanPressurePa">Cycle-mean turbine inlet total pressure.</param>
/// <param name="PeakPressurePa">Peak turbine inlet total pressure.</param>
/// <param name="PressureRatioAmplitude">Peak ÷ mean. The pulse the turbine actually sees.</param>
/// <param name="MeanBladeSpeedRatio">
/// Flow-weighted mean BSR. Peak η_ts is near 0.65–0.70; a manifold delivering
/// its pulse far from that is delivering it where the turbine cannot use it.
/// </param>
/// <param name="EnergyWeightedEfficiency">η_ts weighted by instantaneous power, not by time.</param>
public readonly record struct TurbineDeliveryMetrics(
    double PulseEnergyDelivery,
    double ManifoldVolumeRatio,
    double MeanPressurePa,
    double PeakPressurePa,
    double PressureRatioAmplitude,
    double MeanBladeSpeedRatio,
    double EnergyWeightedEfficiency);

/// <summary>
/// Records the turbine inlet history over a cycle and reduces it to the two
/// metrics plan §4.6.1 names, plus the BSR trace §4.3 asks for.
///
/// <b>Why these two.</b> A turbocharged exhaust manifold is designed to a
/// different objective than an NA header: deliver the blowdown pulse to the
/// turbine with its amplitude intact, rather than create a returning expansion
/// at the valve. Pulse energy delivery measures whether the pulse survived;
/// manifold volume ratio places the design on the pulse-versus-constant-pressure
/// axis of Watson &amp; Janota. Together they are what makes a primary-diameter
/// sweep readable: too small chokes and raises pumping loss, too large
/// dissipates the pulse into manifold volume.
/// </summary>
/// <param name="manifoldVolumeM3">Volume from the exhaust valve to the rotor face.</param>
/// <param name="displacementPerEventM3">One cylinder's swept volume.</param>
/// <param name="gamma">
/// Ratio of specific heats for the gas the turbine is expanding, used only in
/// the constant-pressure reference. 1.33 for combustion products; pass 1.4 when
/// the surrounding simulation is running an air model, so the reference and the
/// measurement are computed on the same gas.
/// </param>
/// <param name="cp">
/// Specific heat of the same gas, J/kg·K. It cancels between the measured power
/// and the constant-pressure reference, so what matters is that it MATCHES the
/// gas model the run used — 1150 alongside γ 1.33, about 1005 alongside γ 1.4.
/// </param>
public sealed class TurbineDeliveryRecorder(
    double manifoldVolumeM3, double displacementPerEventM3, double gamma = 1.33, double cp = 1150.0)
{
    private readonly List<double> _time = [];
    private readonly List<double> _totalPressure = [];
    private readonly List<double> _totalTemperature = [];
    private readonly List<double> _power = [];
    private readonly List<double> _efficiency = [];
    private readonly List<double> _bsr = [];
    private readonly List<double> _massFlow = [];
    private readonly List<double> _expansionRatio = [];

    /// <summary>Manifold volume from the exhaust valve to the turbine inlet, m³.</summary>
    public double ManifoldVolumeM3 { get; } = manifoldVolumeM3;

    /// <summary>Swept volume displaced per exhaust event, m³ — one cylinder's displacement.</summary>
    public double DisplacementPerEventM3 { get; } = displacementPerEventM3;

    public int SampleCount => _time.Count;

    public void Clear()
    {
        _time.Clear();
        _totalPressure.Clear();
        _totalTemperature.Clear();
        _power.Clear();
        _efficiency.Clear();
        _bsr.Clear();
        _massFlow.Clear();
        _expansionRatio.Clear();
    }

    /// <summary>Record the rotor state at a solver time.</summary>
    public void Record(double time, in RotorState state)
    {
        _time.Add(time);
        _totalPressure.Add(state.TotalPressurePa);
        _totalTemperature.Add(state.TotalTemperatureK);
        _power.Add(state.PowerW);
        _efficiency.Add(state.Efficiency);
        _bsr.Add(state.BladeSpeedRatio);
        _massFlow.Add(state.MassFlowKgPerS);
        _expansionRatio.Add(state.ExpansionRatio);
    }

    /// <summary>Reduce the recorded cycle.</summary>
    public TurbineDeliveryMetrics Reduce()
    {
        if (_time.Count < 3)
        {
            throw new InvalidOperationException(
                $"A delivery metric needs a recorded cycle; {_time.Count} sample(s) were taken.");
        }

        var duration = _time[^1] - _time[0];
        if (duration <= 0)
        {
            throw new InvalidOperationException("The recorded samples span no time.");
        }

        var meanPressure = TimeMean(_totalPressure);
        var peakPressure = _totalPressure.Max();

        // BSR and efficiency weighted by power: an average over time would be
        // dominated by the long quiet stretch between pulses, where the turbine
        // is doing nothing and its operating point does not matter.
        double bsrWeighted = 0, etaWeighted = 0, weight = 0;
        for (var i = 1; i < _time.Count; i++)
        {
            var dt = _time[i] - _time[i - 1];
            var p = Math.Max(0.0, 0.5 * (_power[i] + _power[i - 1])) * dt;
            var bsr = 0.5 * (_bsr[i] + _bsr[i - 1]);

            weight += p;
            etaWeighted += p * 0.5 * (_efficiency[i] + _efficiency[i - 1]);
            if (double.IsFinite(bsr))
            {
                bsrWeighted += p * bsr;
            }
        }

        var meanEfficiency = weight > 0 ? etaWeighted / weight : 0.0;

        // The constant-pressure reference: the same mean mass flow, the same
        // mean total temperature and the same efficiency, but arriving steadily
        // at the cycle-mean expansion ratio instead of in pulses. c_p cancels
        // between this and the measured power, so it does not appear.
        var meanFlow = Math.Max(0.0, TimeMean(_massFlow));
        var meanTemperature = TimeMean(_totalTemperature);
        var meanExpansion = Math.Max(1.0, TimeMean(_expansionRatio));
        var idealAtMean = 1.0 - Math.Pow(meanExpansion, -(gamma - 1.0) / gamma);

        var steadyEquivalent = meanFlow * cp * meanTemperature * meanEfficiency * idealAtMean;
        var delivery = steadyEquivalent > 0 ? TimeMean(_power) / steadyEquivalent : double.NaN;

        return new TurbineDeliveryMetrics(
            delivery,
            DisplacementPerEventM3 > 0 ? ManifoldVolumeM3 / DisplacementPerEventM3 : double.NaN,
            meanPressure,
            peakPressure,
            meanPressure > 0 ? peakPressure / meanPressure : double.NaN,
            weight > 0 ? bsrWeighted / weight : double.NaN,
            meanEfficiency);
    }

    /// <summary>Mean mass flow through the turbine over the recorded cycle, kg/s.</summary>
    public double MeanMassFlow() => TimeMean(_massFlow);

    /// <summary>Mean shaft power over the recorded cycle, W.</summary>
    public double MeanPowerW() => TimeMean(_power);

    private double TimeMean(List<double> series)
    {
        double sum = 0;
        for (var i = 1; i < _time.Count; i++)
        {
            sum += 0.5 * (series[i] + series[i - 1]) * (_time[i] - _time[i - 1]);
        }

        return sum / (_time[^1] - _time[0]);
    }
}
