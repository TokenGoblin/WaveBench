using WaveBench.Core.Numerics;

namespace WaveBench.Acoustics.Metrics;

/// <summary>
/// Order-domain spectrum on a uniform order grid: amplitude at
/// <c>OrderStep, 2·OrderStep, …</c>. The spectrum knows its own grid, so
/// metrics interrogate it instead of re-deriving the layout with per-call
/// epsilons.
/// </summary>
public sealed class OrderSpectrum
{
    public OrderSpectrum(double orderStep, double[] amplitude)
    {
        if (orderStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderStep));
        }

        OrderStep = orderStep;
        Amplitude = amplitude;
        Orders = new double[amplitude.Length];
        for (var i = 0; i < amplitude.Length; i++)
        {
            Orders[i] = (i + 1) * orderStep;
        }
    }

    public double OrderStep { get; }

    public double[] Orders { get; }

    public double[] Amplitude { get; }

    public double MaxOrder => Orders.Length > 0 ? Orders[^1] : 0.0;

    /// <summary>True when the order lies on this spectrum's grid and within range.</summary>
    public bool Contains(double order)
    {
        var index = order / OrderStep - 1.0;
        return order > 0
               && order <= MaxOrder + 1e-9
               && Math.Abs(index - Math.Round(index)) < 1e-6;
    }

    /// <summary>
    /// Amplitude at an order. O(1) grid lookup. Throws for orders the
    /// spectrum does not represent — silently returning zero would let
    /// metrics quietly become different metrics (a half-order requested from
    /// an integer-step spectrum, or a harmonic beyond maxOrder).
    /// </summary>
    public double AmplitudeAt(double order)
    {
        if (!Contains(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order),
                $"Order {order} is not on this spectrum's grid (step {OrderStep}, max {MaxOrder}).");
        }

        return Amplitude[(int)Math.Round(order / OrderStep) - 1];
    }

    /// <summary>Amplitude if present, otherwise null — for callers that legitimately probe.</summary>
    public double? TryAmplitudeAt(double order) =>
        Contains(order) ? Amplitude[(int)Math.Round(order / OrderStep) - 1] : null;

    public double Level(double order, double reference = 1.0) =>
        20.0 * Math.Log10(Math.Max(AmplitudeAt(order), 1e-300) / reference);

    /// <summary>True when the grid index of this order is a whole engine order.</summary>
    public bool IsIntegerOrder(int index) =>
        Math.Abs(Orders[index] - Math.Round(Orders[index])) < 1e-9;

    /// <summary>True when the order is a half-integer (0.5, 1.5, …) — the rumble carriers.</summary>
    public bool IsHalfOrder(int index)
    {
        var twice = 2.0 * Orders[index];
        return !IsIntegerOrder(index) && Math.Abs(twice - Math.Round(twice)) < 1e-9;
    }
}

/// <summary>
/// Crank-synchronous order tracking (plan Phase 9). Engine order
/// o = f/(N/60) (§3.2). Signals are windowed to an integer number of 720°
/// cycles — with a whole number of cycles every half-order is exactly
/// periodic in the window, so single-bin projection recovers amplitudes
/// without leakage. Varying speed is handled by resampling into the
/// crank-angle domain first.
/// </summary>
public static class OrderAnalysis
{
    /// <summary>
    /// Order spectrum of a constant-speed signal. Uses the largest whole
    /// number of cycles that fits.
    /// </summary>
    public static OrderSpectrum AtConstantSpeed(
        ReadOnlySpan<double> signal, double sampleRate, double rpm,
        double maxOrder = 24.0, double orderStep = 0.5)
    {
        var cycleSeconds = 120.0 / rpm;
        var samplesPerCycle = cycleSeconds * sampleRate;
        var cycles = (int)(signal.Length / samplesPerCycle);
        if (cycles < 1)
        {
            throw new ArgumentException("Signal shorter than one engine cycle.");
        }

        var n = (int)Math.Round(cycles * samplesPerCycle);

        // Angle per sample is uniform: 720°/samplesPerCycle.
        return Project(signal[..n], 720.0 / samplesPerCycle, maxOrder, orderStep);
    }

    /// <summary>
    /// Order spectrum with a known crank-angle history (varying speed):
    /// resamples onto a uniform angle grid over whole cycles, then projects.
    /// </summary>
    public static OrderSpectrum WithAngleHistory(
        IReadOnlyList<double> times, IReadOnlyList<double> values, Func<double, double> angleDegAt,
        double maxOrder = 24.0, double orderStep = 0.5, int samplesPerCycle = 1440)
    {
        // Evaluate the angle history ONCE (the delegate may be an expensive
        // interpolation of solver output).
        var angles = new double[times.Count];
        for (var i = 0; i < times.Count; i++)
        {
            angles[i] = angleDegAt(times[i]);
        }

        return WithAngleSamples(angles, values, maxOrder, orderStep, samplesPerCycle);
    }

    /// <summary>Order spectrum from paired (crank angle, value) samples — the capture path.</summary>
    public static OrderSpectrum WithAngleSamples(
        IReadOnlyList<double> anglesDeg, IReadOnlyList<double> values,
        double maxOrder = 24.0, double orderStep = 0.5, int samplesPerCycle = 1440)
    {
        var startAngle = anglesDeg[0];
        var cycles = (int)((anglesDeg[^1] - startAngle) / 720.0);
        if (cycles < 1)
        {
            throw new ArgumentException("History shorter than one engine cycle.");
        }

        var total = cycles * samplesPerCycle;
        var resampled = new double[total];
        var index = 0;
        for (var i = 0; i < total; i++)
        {
            var target = startAngle + i * 720.0 / samplesPerCycle;
            while (index < anglesDeg.Count - 2 && anglesDeg[index + 1] < target)
            {
                index++;
            }

            var a0 = anglesDeg[index];
            var a1 = anglesDeg[index + 1];
            var w = a1 > a0 ? Math.Clamp((target - a0) / (a1 - a0), 0.0, 1.0) : 0.0;
            resampled[i] = values[index] + w * (values[index + 1] - values[index]);
        }

        return Project(resampled, 720.0 / samplesPerCycle, maxOrder, orderStep);
    }

    /// <summary>
    /// Single-bin projection on a uniform angle grid. The phase advance per
    /// sample is constant for each order, so the complex exponential is
    /// carried by a rotation recurrence (two multiply-adds per sample, no
    /// transcendentals in the loop) instead of a cos/sin pair per sample.
    /// The recurrence is re-synchronised exactly every
    /// <see cref="ResyncInterval"/> samples so phase cannot drift over the
    /// 10⁵-sample captures this is built for.
    /// </summary>
    private const int ResyncInterval = 1024;

    private static OrderSpectrum Project(
        ReadOnlySpan<double> signal, double angleStepDeg, double maxOrder, double orderStep)
    {
        var count = (int)Math.Round(maxOrder / orderStep);
        var amplitude = new double[count];
        var n = signal.Length;

        for (var k = 0; k < count; k++)
        {
            var order = (k + 1) * orderStep;

            // Order 1 = one cycle per crank revolution = 360°, so the phase
            // per sample is order·angleStep in degrees of that revolution.
            var deltaPhase = order * angleStepDeg * Math.PI / 180.0;
            var cosDelta = Math.Cos(deltaPhase);
            var sinDelta = Math.Sin(deltaPhase);

            double re = 0, im = 0;
            double cos = 1.0, sin = 0.0;
            for (var i = 0; i < n; i++)
            {
                re += signal[i] * cos;
                im += signal[i] * sin;

                if ((i & (ResyncInterval - 1)) == ResyncInterval - 1)
                {
                    var exact = (i + 1) * deltaPhase;
                    cos = Math.Cos(exact);
                    sin = Math.Sin(exact);
                }
                else
                {
                    var nextCos = cos * cosDelta - sin * sinDelta;
                    sin = cos * sinDelta + sin * cosDelta;
                    cos = nextCos;
                }
            }

            amplitude[k] = 2.0 * Math.Sqrt(re * re + im * im) / n;
        }

        return new OrderSpectrum(orderStep, amplitude);
    }
}

/// <summary>
/// Engine-specific character metrics on order spectra (plan §3.2/§3.7).
/// </summary>
public static class CharacterMetrics
{
    /// <summary>
    /// Order Purity Index (§3.2): Σ energy at integer multiples of the firing
    /// order ÷ Σ energy at all orders ≤ 12·o_fire. 1.0 = pure howl.
    /// </summary>
    public static double OrderPurityIndex(OrderSpectrum spectrum, double firingOrder)
    {
        double atHarmonics = 0, total = 0;
        for (var i = 0; i < spectrum.Orders.Length; i++)
        {
            var order = spectrum.Orders[i];
            if (order > 12.0 * firingOrder + 1e-9)
            {
                continue;
            }

            var energy = spectrum.Amplitude[i] * spectrum.Amplitude[i];
            total += energy;
            var multiple = order / firingOrder;
            if (Math.Abs(multiple - Math.Round(multiple)) < 1e-6)
            {
                atHarmonics += energy;
            }
        }

        return total > 0 ? atHarmonics / total : 1.0;
    }

    /// <summary>
    /// Half-order ratio (§3.7): energy at HALF-integer orders ÷ integer
    /// orders — rumble / lope. Uses the spectrum's own grid classification,
    /// so a finer analysis grid (quarter orders) does not silently inflate
    /// the metric: sub-half content is excluded from both sums.
    /// </summary>
    public static double HalfOrderRatio(OrderSpectrum spectrum)
    {
        double half = 0, integer = 0;
        for (var i = 0; i < spectrum.Orders.Length; i++)
        {
            var energy = spectrum.Amplitude[i] * spectrum.Amplitude[i];
            if (spectrum.IsIntegerOrder(i))
            {
                integer += energy;
            }
            else if (spectrum.IsHalfOrder(i))
            {
                half += energy;
            }
        }

        return integer > 0 ? half / integer : 0.0;
    }

    /// <summary>
    /// Harmonic decay slope (§3.7), dB per order across the firing-order
    /// harmonics. Mellow ≈ steep negative; bright ≈ shallow.
    /// </summary>
    public static double HarmonicDecaySlope(OrderSpectrum spectrum, double firingOrder, int harmonics = 6) =>
        Fit(spectrum, firingOrder, harmonics).Slope;

    /// <summary>Order-to-order variance (§3.7): σ of harmonic levels around the decay fit, dB.</summary>
    public static double OrderToOrderVariance(OrderSpectrum spectrum, double firingOrder, int harmonics = 6) =>
        Fit(spectrum, firingOrder, harmonics).Residual;

    /// <summary>
    /// Least-squares fit of harmonic level (dB) versus harmonic number, done
    /// once for both metrics. Harmonics the spectrum cannot represent are
    /// reported rather than silently dropped.
    /// </summary>
    private static (double Slope, double Residual, int Used) Fit(
        OrderSpectrum spectrum, double firingOrder, int harmonics)
    {
        Span<double> x = stackalloc double[harmonics];
        Span<double> db = stackalloc double[harmonics];
        var used = 0;
        for (var h = 1; h <= harmonics; h++)
        {
            var amplitude = spectrum.TryAmplitudeAt(h * firingOrder);
            if (amplitude is > 0)
            {
                x[used] = h;
                db[used] = 20.0 * Math.Log10(amplitude.Value);
                used++;
            }
        }

        if (used < 2)
        {
            return (0.0, 0.0, used);
        }

        double mx = 0, my = 0;
        for (var i = 0; i < used; i++)
        {
            mx += x[i];
            my += db[i];
        }

        mx /= used;
        my /= used;

        double num = 0, den = 0;
        for (var i = 0; i < used; i++)
        {
            num += (x[i] - mx) * (db[i] - my);
            den += (x[i] - mx) * (x[i] - mx);
        }

        var slope = den > 0 ? num / den : 0.0;
        var intercept = my - slope * mx;

        var residual = 0.0;
        for (var i = 0; i < used; i++)
        {
            var e = db[i] - (slope * x[i] + intercept);
            residual += e * e;
        }

        return (slope, Math.Sqrt(residual / used), used);
    }
}
