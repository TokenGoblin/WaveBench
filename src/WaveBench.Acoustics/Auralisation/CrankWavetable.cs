namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// One 720° cycle of source pressure at one engine speed, indexed by crank
/// angle (plan §3.6 step 2). This is the unit the synthesiser reads: because
/// the index is crank angle rather than time, playing it at a different
/// speed changes pitch and timing together, exactly as an engine does.
/// </summary>
public sealed class CrankWavetable
{
    private readonly float[] _samples;

    public CrankWavetable(double rpm, float[] samplesOverCycle, double load = 1.0)
    {
        if (samplesOverCycle.Length < 16)
        {
            throw new ArgumentException("Wavetable needs a meaningful number of samples.", nameof(samplesOverCycle));
        }

        Rpm = rpm;
        Load = load;
        _samples = samplesOverCycle;
    }

    public double Rpm { get; }

    /// <summary>
    /// Load this table was captured at — intake manifold pressure as a
    /// fraction of ambient, 1.0 being wide-open throttle. Defaults to 1.0 so
    /// a single-load bank behaves exactly as before.
    /// </summary>
    public double Load { get; }

    public int Length => _samples.Length;

    public ReadOnlySpan<float> Samples => _samples;

    /// <summary>Linear-interpolated sample at a crank angle (any value; wrapped to 720°).</summary>
    public double SampleAt(double crankAngleDeg)
    {
        var position = crankAngleDeg / 720.0 * _samples.Length;
        position -= Math.Floor(position / _samples.Length) * _samples.Length;

        var i0 = (int)position;
        var frac = position - i0;
        var i1 = i0 + 1 == _samples.Length ? 0 : i0 + 1;
        return _samples[i0] + frac * (_samples[i1] - _samples[i0]);
    }

    public double Mean
    {
        get
        {
            double sum = 0;
            foreach (var s in _samples)
            {
                sum += s;
            }

            return sum / _samples.Length;
        }
    }

    /// <summary>Copy with the DC component removed — the acoustic part of the signal.</summary>
    public CrankWavetable WithoutMean()
    {
        var mean = Mean;
        var output = new float[_samples.Length];
        for (var i = 0; i < _samples.Length; i++)
        {
            output[i] = (float)(_samples[i] - mean);
        }

        return new CrankWavetable(Rpm, output, Load);
    }

    /// <summary>
    /// Average of k consecutive cycles from a crank-angle capture, which is
    /// the natural way to turn <c>ProbeCapture.ResampleToCrankAngle</c> output
    /// into a wavetable: cycle-to-cycle scatter is re-injected at synthesis
    /// time from a seeded distribution (§3.4), not baked into the table.
    /// </summary>
    public static CrankWavetable FromCapture(
        double rpm, ReadOnlySpan<float> resampled, int samplesPerCycle, double load = 1.0)
    {
        var cycles = resampled.Length / samplesPerCycle;
        if (cycles < 1)
        {
            throw new ArgumentException("Capture is shorter than one cycle.", nameof(resampled));
        }

        var table = new float[samplesPerCycle];
        for (var c = 0; c < cycles; c++)
        {
            for (var i = 0; i < samplesPerCycle; i++)
            {
                table[i] += resampled[c * samplesPerCycle + i];
            }
        }

        for (var i = 0; i < samplesPerCycle; i++)
        {
            table[i] /= cycles;
        }

        return new CrankWavetable(rpm, table, load);
    }
}

/// <summary>
/// Wavetables for one source across an rpm × load grid (plan §3.6: the rpm
/// grid at a 250 rpm default spacing, and at least two load lines,
/// interpolated on both axes).
///
/// Every interpolation happens in the CRANK-ANGLE domain — the same angle is
/// read from all four bracketing tables and the values are blended — so the
/// result stays phase-coherent. Blending in the time domain (cross-fading
/// audio) is what the plan forbids: it destroys phase and sounds like a pitch
/// shift rather than an engine.
///
/// A bank built at one load behaves exactly as a 1-D bank did, so the load
/// axis costs nothing where it is not used.
/// </summary>
public sealed class WavetableBank
{
    // Load line → tables at that load, each sorted by rpm. Sorted by load.
    private readonly SortedList<double, List<CrankWavetable>> _byLoad = [];

    public string SourceName { get; }

    public WavetableBank(string sourceName) => SourceName = sourceName;

    /// <summary>Every table, ordered by load then rpm.</summary>
    public IReadOnlyList<CrankWavetable> Tables => _byLoad.Values.SelectMany(t => t).ToList();

    /// <summary>The load lines present, ascending.</summary>
    public IReadOnlyList<double> Loads => _byLoad.Keys.ToList();

    public double MinRpm => _byLoad.Count > 0 ? _byLoad.Values.Min(t => t[0].Rpm) : 0.0;

    public double MaxRpm => _byLoad.Count > 0 ? _byLoad.Values.Max(t => t[^1].Rpm) : 0.0;

    public double MinLoad => _byLoad.Count > 0 ? _byLoad.Keys[0] : 0.0;

    public double MaxLoad => _byLoad.Count > 0 ? _byLoad.Keys[^1] : 0.0;

    public void Add(CrankWavetable table)
    {
        if (!_byLoad.TryGetValue(table.Load, out var line))
        {
            line = [];
            _byLoad.Add(table.Load, line);
        }

        line.Add(table);
        line.Sort((a, b) => a.Rpm.CompareTo(b.Rpm));
    }

    /// <summary>
    /// Sample at (rpm, crank angle) on the highest load line — the wide-open
    /// pull, and the behaviour of a single-load bank.
    /// </summary>
    public double SampleAt(double rpm, double crankAngleDeg) =>
        SampleAt(rpm, crankAngleDeg, MaxLoad);

    /// <summary>
    /// Sample at (rpm, load, crank angle), bilinear across the two axes.
    /// Outside the grid the nearest line is held rather than extrapolated:
    /// a wavetable extrapolated past its captured range is not a prediction,
    /// and <see cref="Covers"/> lets a caller check before trusting one.
    /// </summary>
    public double SampleAt(double rpm, double crankAngleDeg, double load)
    {
        if (_byLoad.Count == 0)
        {
            throw new InvalidOperationException($"Wavetable bank '{SourceName}' is empty.");
        }

        if (_byLoad.Count == 1)
        {
            return SampleLine(_byLoad.Values[0], rpm, crankAngleDeg);
        }

        var loads = _byLoad.Keys;
        if (load <= loads[0])
        {
            return SampleLine(_byLoad.Values[0], rpm, crankAngleDeg);
        }

        if (load >= loads[^1])
        {
            return SampleLine(_byLoad.Values[^1], rpm, crankAngleDeg);
        }

        var upper = 1;
        while (upper < loads.Count - 1 && loads[upper] < load)
        {
            upper++;
        }

        var loLoad = loads[upper - 1];
        var hiLoad = loads[upper];
        var w = (load - loLoad) / (hiLoad - loLoad);

        return (1.0 - w) * SampleLine(_byLoad.Values[upper - 1], rpm, crankAngleDeg)
               + w * SampleLine(_byLoad.Values[upper], rpm, crankAngleDeg);
    }

    /// <summary>
    /// Whether (rpm, load) is inside the captured grid rather than being held
    /// at an edge. The synthesiser reports this so a render cannot silently
    /// present held-edge audio as a solved result.
    /// </summary>
    public bool Covers(double rpm, double load) =>
        _byLoad.Count > 0
        && rpm >= MinRpm - 1e-9 && rpm <= MaxRpm + 1e-9
        && load >= MinLoad - 1e-9 && load <= MaxLoad + 1e-9;

    /// <summary>Linear blend between the two bracketing rpm tables of one load line.</summary>
    private static double SampleLine(List<CrankWavetable> line, double rpm, double crankAngleDeg)
    {
        if (line.Count == 1 || rpm <= line[0].Rpm)
        {
            return line[0].SampleAt(crankAngleDeg);
        }

        if (rpm >= line[^1].Rpm)
        {
            return line[^1].SampleAt(crankAngleDeg);
        }

        var upper = 1;
        while (upper < line.Count - 1 && line[upper].Rpm < rpm)
        {
            upper++;
        }

        var lo = line[upper - 1];
        var hi = line[upper];
        var w = (rpm - lo.Rpm) / (hi.Rpm - lo.Rpm);
        return (1.0 - w) * lo.SampleAt(crankAngleDeg) + w * hi.SampleAt(crankAngleDeg);
    }
}
