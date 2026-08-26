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

    public CrankWavetable(double rpm, float[] samplesOverCycle)
    {
        if (samplesOverCycle.Length < 16)
        {
            throw new ArgumentException("Wavetable needs a meaningful number of samples.", nameof(samplesOverCycle));
        }

        Rpm = rpm;
        _samples = samplesOverCycle;
    }

    public double Rpm { get; }

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

        return new CrankWavetable(Rpm, output);
    }

    /// <summary>
    /// Average of k consecutive cycles from a crank-angle capture, which is
    /// the natural way to turn <c>ProbeCapture.ResampleToCrankAngle</c> output
    /// into a wavetable: cycle-to-cycle scatter is re-injected at synthesis
    /// time from a seeded distribution (§3.4), not baked into the table.
    /// </summary>
    public static CrankWavetable FromCapture(double rpm, ReadOnlySpan<float> resampled, int samplesPerCycle)
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

        return new CrankWavetable(rpm, table);
    }
}

/// <summary>
/// Wavetables for one source across an rpm grid (plan §3.6 default 250 rpm
/// spacing). Interpolation between adjacent speeds happens in the
/// CRANK-ANGLE domain — the same angle is read from both tables and the two
/// values are blended — so the result stays phase-coherent. Blending in the
/// time domain (i.e. cross-fading audio) is what the plan forbids: it
/// destroys phase and sounds like a pitch shift.
/// </summary>
public sealed class WavetableBank
{
    private readonly List<CrankWavetable> _tables = [];

    public string SourceName { get; }

    public WavetableBank(string sourceName) => SourceName = sourceName;

    public IReadOnlyList<CrankWavetable> Tables => _tables;

    public double MinRpm => _tables.Count > 0 ? _tables[0].Rpm : 0.0;

    public double MaxRpm => _tables.Count > 0 ? _tables[^1].Rpm : 0.0;

    public void Add(CrankWavetable table)
    {
        _tables.Add(table);
        _tables.Sort((a, b) => a.Rpm.CompareTo(b.Rpm));
    }

    /// <summary>
    /// Sample at (rpm, crank angle) with linear blending between the two
    /// bracketing rpm tables, evaluated at the SAME crank angle in both.
    /// Outside the grid the nearest table is held (and the caller should warn).
    /// </summary>
    public double SampleAt(double rpm, double crankAngleDeg)
    {
        if (_tables.Count == 0)
        {
            throw new InvalidOperationException($"Wavetable bank '{SourceName}' is empty.");
        }

        if (_tables.Count == 1 || rpm <= _tables[0].Rpm)
        {
            return _tables[0].SampleAt(crankAngleDeg);
        }

        if (rpm >= _tables[^1].Rpm)
        {
            return _tables[^1].SampleAt(crankAngleDeg);
        }

        var upper = 1;
        while (upper < _tables.Count - 1 && _tables[upper].Rpm < rpm)
        {
            upper++;
        }

        var lower = upper - 1;
        var lo = _tables[lower];
        var hi = _tables[upper];
        var w = (rpm - lo.Rpm) / (hi.Rpm - lo.Rpm);
        return (1.0 - w) * lo.SampleAt(crankAngleDeg) + w * hi.SampleAt(crankAngleDeg);
    }
}
