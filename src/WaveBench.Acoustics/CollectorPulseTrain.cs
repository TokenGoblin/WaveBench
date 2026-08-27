namespace WaveBench.Acoustics;

/// <summary>
/// The §3.2 composite-spectrum machine in the crank-angle domain: each
/// cylinder contributes a blowdown pulse at its computed arrival phase, and
/// the superposition at the collector is what carries the order structure —
/// with m equal pulses at exactly even spacing the energy collapses onto the
/// firing order and its harmonics; any timing or amplitude error populates
/// the intermediate orders (crossplane rumble, UEL boxer, log-manifold
/// warble — one equation, plan §3.2).
/// </summary>
public static class CollectorPulseTrain
{
    /// <summary>
    /// One 720° cycle of collector pressure, arbitrary units: Gaussian pulses
    /// (σ = pulseWidthDeg) at each branch's arrival angle, cyclic-wrapped.
    /// Per-branch amplitudes model unequal scavenging/wall temperature
    /// (§3.2: amplitude mismatch breaks the cancellation just like timing).
    /// </summary>
    public static double[] SynthesizeCycle(
        CollectorTimingResult timing,
        double pulseWidthDeg = 18.0,
        int samplesPerCycle = 2880,
        IReadOnlyList<double>? amplitudes = null)
    {
        var signal = new double[samplesPerCycle];
        var degreesPerSample = 720.0 / samplesPerCycle;

        // Beyond 8σ the Gaussian is below 1e-13 — far under the 1e-6 order
        // thresholds these spectra are judged against — so only the window
        // around each arrival is evaluated.
        var window = 8.0 * pulseWidthDeg;
        var twoSigmaSquared = 2.0 * pulseWidthDeg * pulseWidthDeg;

        for (var b = 0; b < timing.ArrivalDeg.Count; b++)
        {
            var centre = timing.ArrivalDeg[b];
            var amplitude = amplitudes?[b] ?? 1.0;
            for (var i = 0; i < samplesPerCycle; i++)
            {
                var d = i * degreesPerSample - centre;
                d -= 720.0 * Math.Round(d / 720.0); // nearest cyclic image
                if (Math.Abs(d) > window)
                {
                    continue;
                }

                signal[i] += amplitude * Math.Exp(-d * d / twoSigmaSquared);
            }
        }

        return signal;
    }

    /// <summary>
    /// Render the pulse train straight to a time signal at an EXACT sample
    /// rate — the audition preview.
    ///
    /// Not <see cref="SynthesizeCycle"/> plus <see cref="Repeat"/>: that route
    /// fixes samples-per-cycle first, so the sample rate that falls out is
    /// whatever the rpm makes it, and rounding it back to 48 kHz would detune
    /// the engine. Evaluating on the sample clock and computing the crank
    /// angle per sample gets both exactly right.
    ///
    /// The mean is removed. A train of one-sided Gaussians has a large DC
    /// component which is inaudible, cannot be reproduced, and would eat the
    /// headroom that the audible part needs.
    /// </summary>
    /// <param name="timing">Arrival angles at the collector.</param>
    /// <param name="rpm">Engine speed.</param>
    /// <param name="seconds">Length to render.</param>
    /// <param name="sampleRate">Output rate, Hz.</param>
    /// <param name="pulseWidthDeg">Gaussian σ, crank degrees.</param>
    /// <param name="amplitudes">Per-branch amplitude, or null for equal.</param>
    public static float[] Render(
        CollectorTimingResult timing,
        double rpm,
        double seconds,
        double sampleRate,
        double pulseWidthDeg = 18.0,
        IReadOnlyList<double>? amplitudes = null)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rpm);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var count = Math.Max(1, (int)Math.Round(seconds * sampleRate));
        var signal = new float[count];
        var degreesPerSample = 6.0 * rpm / sampleRate;
        var window = 8.0 * pulseWidthDeg;
        var twoSigmaSquared = 2.0 * pulseWidthDeg * pulseWidthDeg;

        var sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            var angle = i * degreesPerSample;
            var value = 0.0;

            for (var b = 0; b < timing.ArrivalDeg.Count; b++)
            {
                var d = angle - timing.ArrivalDeg[b];
                d -= 720.0 * Math.Round(d / 720.0); // nearest cyclic image
                if (Math.Abs(d) > window)
                {
                    continue;
                }

                value += (amplitudes?[b] ?? 1.0) * Math.Exp(-d * d / twoSigmaSquared);
            }

            signal[i] = (float)value;
            sum += value;
        }

        var mean = (float)(sum / count);
        for (var i = 0; i < count; i++)
        {
            signal[i] -= mean;
        }

        return signal;
    }

    /// <summary>Repeat a cycle k times into a time signal at the given rpm; returns (signal, sampleRate).</summary>
    public static (double[] Signal, double SampleRate) Repeat(double[] cycle, int cycles, double rpm)
    {
        var signal = new double[cycle.Length * cycles];
        for (var c = 0; c < cycles; c++)
        {
            Array.Copy(cycle, 0, signal, c * cycle.Length, cycle.Length);
        }

        var cycleSeconds = 120.0 / rpm;
        return (signal, cycle.Length / cycleSeconds);
    }
}
