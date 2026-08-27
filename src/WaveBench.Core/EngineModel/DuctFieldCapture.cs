using WaveBench.Core.Solver;

namespace WaveBench.Core.EngineModel;

/// <summary>What a field capture records at every cell.</summary>
public enum FieldQuantity
{
    /// <summary>Static pressure, Pa.</summary>
    Pressure,

    /// <summary>Signed axial particle velocity, m/s.</summary>
    Velocity,

    /// <summary>Local Mach number, signed with the flow.</summary>
    Mach,

    /// <summary>Static temperature, K.</summary>
    Temperature,
}

/// <summary>
/// The x–t wave field of one pipe (plan §8.4, Phase 19): a quantity sampled
/// across every cell, at a fixed crank-angle spacing, for a whole run.
///
/// This is the data behind the wave diagram — the heat map over distance and
/// crank angle that a race engine developer reads to answer <i>"does the
/// returning expansion arrive before EVC?"</i>. A probe answers that question
/// at one point; this answers it everywhere at once, which is what makes the
/// reflection visible as a diagonal streak rather than a bump in a trace.
///
/// <b>Sampled on ANGLE, not on solver steps.</b> Steps are CFL-limited, so
/// recording every one would give a frame rate that varies with the local
/// wave speed — dense where the gas is hot, sparse where it is not — and an
/// animation played back at constant rate would appear to speed up and slow
/// down for no physical reason. Decimating on crank angle also bounds the
/// memory exactly, which matters: this is the one structure in the product
/// whose size is cells × frames.
/// </summary>
public sealed class DuctFieldCapture
{
    private readonly DuctSolver _duct;
    private readonly List<double> _angles = [];
    private float[] _frames;
    private int _frameCount;
    private double _nextAngle = double.NaN;

    /// <param name="duct">Pipe to record.</param>
    /// <param name="name">Shown on the diagram; usually the graph node id.</param>
    /// <param name="quantity">What to record.</param>
    /// <param name="samplesPerCycle">
    /// Frames per 720°. 720 is half a degree, which resolves a wave crossing a
    /// 600 mm primary at 8000 rpm into about 40 frames.
    /// </param>
    /// <param name="expectedCycles">Capacity hint only; the buffer grows if exceeded.</param>
    public DuctFieldCapture(
        DuctSolver duct,
        string name,
        FieldQuantity quantity = FieldQuantity.Pressure,
        int samplesPerCycle = 720,
        int expectedCycles = 4)
    {
        ArgumentNullException.ThrowIfNull(duct);
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesPerCycle, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedCycles, 1);

        _duct = duct;
        Name = name;
        Quantity = quantity;
        SamplesPerCycle = samplesPerCycle;
        CellCount = duct.CellCount;
        _frames = new float[(long)samplesPerCycle * expectedCycles * CellCount is var n && n < int.MaxValue
            ? (int)n
            : throw new ArgumentOutOfRangeException(nameof(expectedCycles), "Requested capture is too large.")];
    }

    public string Name { get; }

    public FieldQuantity Quantity { get; }

    public int SamplesPerCycle { get; }

    public int CellCount { get; }

    /// <summary>Frames recorded.</summary>
    public int FrameCount => _frameCount;

    /// <summary>Cycle angle of each frame, degrees, monotonically increasing.</summary>
    public IReadOnlyList<double> FrameAngles => _angles;

    /// <summary>Cell centre positions, m — the diagram's x axis.</summary>
    public double[] CellCentres { get; private set; } = [];

    /// <summary>Pipe length, m.</summary>
    public double Length => _duct.Geometry.Length;

    /// <summary>Bytes the frame buffer currently holds.</summary>
    public long Bytes => (long)_frames.Length * sizeof(float);

    /// <summary>
    /// What a capture of this shape would cost, so a caller can decide before
    /// committing to it rather than after running out of memory.
    /// </summary>
    public static long EstimateBytes(int cells, int samplesPerCycle, int cycles) =>
        (long)cells * samplesPerCycle * cycles * sizeof(float);

    /// <summary>One frame: the quantity across every cell at that angle.</summary>
    public ReadOnlySpan<float> Frame(int index)
    {
        if ((uint)index >= (uint)_frameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "No such frame.");
        }

        return _frames.AsSpan(index * CellCount, CellCount);
    }

    /// <summary>
    /// Min and max across every frame recorded — the colour scale. Computed
    /// once over the whole capture rather than per frame, because a scale that
    /// re-normalises every frame makes a decaying wave look constant and hides
    /// exactly what the diagram is for.
    /// </summary>
    public (float Min, float Max) Range()
    {
        if (_frameCount == 0)
        {
            return (0f, 0f);
        }

        var min = float.MaxValue;
        var max = float.MinValue;
        var span = _frames.AsSpan(0, _frameCount * CellCount);
        foreach (var v in span)
        {
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }
        }

        return (min, max);
    }

    /// <summary>
    /// The frame nearest a cycle angle, for scrubbing.
    ///
    /// Binary search, not a linear scan: this runs on every frame of a drag,
    /// and on the difference rather than the value it would also have to cope
    /// with <c>Math.Abs(x − double.MaxValue)</c> overflowing to infinity for
    /// every candidate at once — which silently returns frame 0.
    /// </summary>
    public int FrameAt(double angleDeg)
    {
        if (_frameCount == 0)
        {
            return -1;
        }

        var lo = 0;
        var hi = _frameCount - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_angles[mid] < angleDeg)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // lo is the first frame at or past the angle; the nearest is that one
        // or the one before it.
        if (lo > 0 && angleDeg - _angles[lo - 1] < _angles[lo] - angleDeg)
        {
            return lo - 1;
        }

        return lo;
    }

    public void Clear()
    {
        _frameCount = 0;
        _angles.Clear();
        _nextAngle = double.NaN;
    }

    /// <summary>
    /// Offered a step. Records only when the crank has advanced a whole
    /// sample interval, so the frame spacing is uniform in angle whatever the
    /// timestep is doing.
    /// </summary>
    internal void Offer(double angleDeg)
    {
        var interval = 720.0 / SamplesPerCycle;

        if (double.IsNaN(_nextAngle))
        {
            _nextAngle = angleDeg;
        }
        else if (angleDeg < _nextAngle)
        {
            return;
        }

        Record(angleDeg);

        // Advance past the angle just taken. A single += would fall behind if
        // one step ever crossed more than one interval, and the frames after
        // it would then be spaced by the timestep rather than by the interval.
        do
        {
            _nextAngle += interval;
        }
        while (_nextAngle <= angleDeg);
    }

    private void Record(double angleDeg)
    {
        var offset = _frameCount * CellCount;
        if (offset + CellCount > _frames.Length)
        {
            Array.Resize(ref _frames, Math.Max(_frames.Length * 2, offset + CellCount));
        }

        if (CellCentres.Length != CellCount)
        {
            CellCentres = new double[CellCount];
            for (var i = 0; i < CellCount; i++)
            {
                CellCentres[i] = _duct.CellCentre(i);
            }
        }

        var span = _frames.AsSpan(offset, CellCount);
        switch (Quantity)
        {
            case FieldQuantity.Pressure:
                for (var i = 0; i < CellCount; i++)
                {
                    span[i] = (float)_duct.GetPressure(i);
                }

                break;

            case FieldQuantity.Velocity:
                for (var i = 0; i < CellCount; i++)
                {
                    span[i] = (float)_duct.GetVelocity(i);
                }

                break;

            case FieldQuantity.Mach:
                for (var i = 0; i < CellCount; i++)
                {
                    var state = _duct.GetState(i);
                    span[i] = (float)(state.U / state.SoundSpeed);
                }

                break;

            case FieldQuantity.Temperature:
                for (var i = 0; i < CellCount; i++)
                {
                    span[i] = (float)_duct.GetState(i).T;
                }

                break;

            default:
                throw new InvalidOperationException($"Unhandled field quantity {Quantity}.");
        }

        _angles.Add(angleDeg);
        _frameCount++;
    }
}
