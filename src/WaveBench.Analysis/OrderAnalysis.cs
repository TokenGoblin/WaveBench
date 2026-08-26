namespace WaveBench.Analysis;

/// <summary>Order-domain spectrum: amplitude per (half-)engine order.</summary>
public sealed record OrderSpectrum(double[] Orders, double[] Amplitude)
{
    public double AmplitudeAt(double order)
    {
        var index = Array.FindIndex(Orders, o => Math.Abs(o - order) < 1e-9);
        return index >= 0 ? Amplitude[index] : 0.0;
    }

    public double Level(double order, double reference = 1.0) =>
        20.0 * Math.Log10(Math.Max(AmplitudeAt(order), 1e-300) / reference);
}

/// <summary>
/// Crank-synchronous order tracking (plan Phase 9). Engine order
/// o = f/(N/60) (§3.2). Signals are windowed to an integer number of 720°
/// cycles — with a whole number of cycles every half-order is exactly
/// periodic in the window, so single-bin projection (Goertzel-style) recovers
/// amplitudes without leakage. Varying speed is handled by resampling into
/// the crank-angle domain first.
/// </summary>
public static class OrderAnalysis
{
    /// <summary>
    /// Order spectrum of a constant-speed signal. Uses the largest whole
    /// number of cycles that fits; half-order resolution up to maxOrder.
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
        return Project(signal[..n], i => i / samplesPerCycle * 720.0, maxOrder, orderStep);
    }

    /// <summary>
    /// Order spectrum with a known crank-angle history (varying speed):
    /// resamples onto a uniform angle grid over whole cycles, then projects.
    /// </summary>
    public static OrderSpectrum WithAngleHistory(
        IReadOnlyList<double> times, IReadOnlyList<double> values, Func<double, double> angleDegAt,
        double maxOrder = 24.0, double orderStep = 0.5, int samplesPerCycle = 1440)
    {
        var startAngle = angleDegAt(times[0]);
        var endAngle = angleDegAt(times[^1]);
        var cycles = (int)((endAngle - startAngle) / 720.0);
        if (cycles < 1)
        {
            throw new ArgumentException("History shorter than one engine cycle.");
        }

        var total = cycles * samplesPerCycle;
        var resampled = new double[total];
        var timeIndex = 0;
        for (var i = 0; i < total; i++)
        {
            var targetAngle = startAngle + i * 720.0 / samplesPerCycle;
            while (timeIndex < times.Count - 2 && angleDegAt(times[timeIndex + 1]) < targetAngle)
            {
                timeIndex++;
            }

            var a0 = angleDegAt(times[timeIndex]);
            var a1 = angleDegAt(times[timeIndex + 1]);
            var w = a1 > a0 ? Math.Clamp((targetAngle - a0) / (a1 - a0), 0.0, 1.0) : 0.0;
            resampled[i] = values[timeIndex] + w * (values[timeIndex + 1] - values[timeIndex]);
        }

        return Project(resampled, i => i * 720.0 / samplesPerCycle, maxOrder, orderStep);
    }

    private static OrderSpectrum Project(
        ReadOnlySpan<double> signal, Func<double, double> angleOfSample, double maxOrder, double orderStep)
    {
        var count = (int)Math.Round(maxOrder / orderStep);
        var orders = new double[count];
        var amplitude = new double[count];
        var n = signal.Length;

        // Precompute angles once.
        var angleRad = new double[n];
        for (var i = 0; i < n; i++)
        {
            angleRad[i] = angleOfSample(i) * Math.PI / 180.0 / 2.0; // ÷2: order 1 = once per rev = twice per 720° cycle
        }

        for (var k = 0; k < count; k++)
        {
            var order = (k + 1) * orderStep;
            orders[k] = order;
            double re = 0, im = 0;
            for (var i = 0; i < n; i++)
            {
                var phase = 2.0 * order * angleRad[i];
                re += signal[i] * Math.Cos(phase);
                im += signal[i] * Math.Sin(phase);
            }

            amplitude[k] = 2.0 * Math.Sqrt(re * re + im * im) / n;
        }

        return new OrderSpectrum(orders, amplitude);
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

    /// <summary>Half-order ratio (§3.7): energy at half-integer orders ÷ integer orders — rumble/lope.</summary>
    public static double HalfOrderRatio(OrderSpectrum spectrum)
    {
        double half = 0, integer = 0;
        for (var i = 0; i < spectrum.Orders.Length; i++)
        {
            var order = spectrum.Orders[i];
            var energy = spectrum.Amplitude[i] * spectrum.Amplitude[i];
            if (Math.Abs(order - Math.Round(order)) < 1e-6)
            {
                integer += energy;
            }
            else
            {
                half += energy;
            }
        }

        return integer > 0 ? half / integer : 0.0;
    }

    /// <summary>
    /// Harmonic decay slope (§3.7), dB per order across the firing-order
    /// harmonics (least squares over the first `harmonics` multiples).
    /// Mellow ≈ steep negative; bright ≈ shallow.
    /// </summary>
    public static double HarmonicDecaySlope(OrderSpectrum spectrum, double firingOrder, int harmonics = 6)
    {
        var points = new List<(double X, double Db)>();
        for (var h = 1; h <= harmonics; h++)
        {
            var amplitude = spectrum.AmplitudeAt(h * firingOrder);
            if (amplitude > 0)
            {
                points.Add((h, 20.0 * Math.Log10(amplitude)));
            }
        }

        if (points.Count < 2)
        {
            return 0.0;
        }

        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Db);
        var slope = points.Sum(p => (p.X - mx) * (p.Db - my)) / points.Sum(p => (p.X - mx) * (p.X - mx));
        return slope;
    }

    /// <summary>Order-to-order variance (§3.7): σ of harmonic levels around the decay fit, dB.</summary>
    public static double OrderToOrderVariance(OrderSpectrum spectrum, double firingOrder, int harmonics = 6)
    {
        var slope = HarmonicDecaySlope(spectrum, firingOrder, harmonics);
        var points = new List<(double X, double Db)>();
        for (var h = 1; h <= harmonics; h++)
        {
            var amplitude = spectrum.AmplitudeAt(h * firingOrder);
            if (amplitude > 0)
            {
                points.Add((h, 20.0 * Math.Log10(amplitude)));
            }
        }

        if (points.Count < 2)
        {
            return 0.0;
        }

        var my = points.Average(p => p.Db);
        var mx = points.Average(p => p.X);
        var intercept = my - slope * mx;
        var residual = points.Sum(p => Math.Pow(p.Db - (slope * p.X + intercept), 2)) / points.Count;
        return Math.Sqrt(residual);
    }
}
