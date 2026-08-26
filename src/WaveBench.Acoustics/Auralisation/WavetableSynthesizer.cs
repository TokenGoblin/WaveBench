namespace WaveBench.Acoustics.Auralisation;

/// <summary>Per-cycle stochastic variation applied at synthesis (plan §3.4/§3.6 step 4).</summary>
public sealed record SynthesisVariation(
    double AmplitudeCoV = 0.02,
    double PhaseJitterDeg = 1.2)
{
    public static SynthesisVariation None { get; } = new(0.0, 0.0);
}

/// <summary>One rendered layer of the mix.</summary>
public sealed record AudioStem(string Name, float[] Samples, double SampleRate)
{
    public double Duration => Samples.Length / SampleRate;
}

/// <summary>
/// Crank-angle wavetable synthesis (plan §3.6 steps 3–4). A phase
/// accumulator advances the crank angle by the instantaneous engine speed
/// each sample; the wavetable bank is read at that angle and blended between
/// adjacent rpm tables IN THE CRANK-ANGLE DOMAIN. Nothing is time-stretched,
/// so a sweep stays phase-coherent from idle to redline.
///
/// Per-cycle variation is applied as an amplitude scale and a small crank
/// offset that change only at cycle boundaries, drawn from a seeded
/// deterministic stream — same seed, bit-identical render (§3.6 gate).
/// </summary>
public sealed class WavetableSynthesizer(ulong seed = 20260825)
{
    public ulong Seed { get; } = seed;

    /// <summary>Render one source layer over the profile.</summary>
    public AudioStem Render(
        WavetableBank bank,
        RpmProfile profile,
        double sampleRate = 48_000.0,
        SynthesisVariation? variation = null,
        double startAngleDeg = 0.0)
    {
        var v = variation ?? new SynthesisVariation();
        var count = (int)Math.Round(profile.Duration * sampleRate);
        var output = new float[count];

        var angle = startAngleDeg;
        var cycleIndex = 0L;
        var (amplitude, phaseOffset) = DrawCycle(cycleIndex, v);

        for (var i = 0; i < count; i++)
        {
            var time = i / sampleRate;
            var rpm = profile.RpmAt(time);

            // Crank advance: 6·N degrees per second.
            angle += 6.0 * rpm / sampleRate;
            if (angle >= 720.0)
            {
                angle -= 720.0;
                cycleIndex++;
                (amplitude, phaseOffset) = DrawCycle(cycleIndex, v);
            }

            output[i] = (float)(amplitude * bank.SampleAt(rpm, angle + phaseOffset));
        }

        return new AudioStem(bank.SourceName, output, sampleRate);
    }

    /// <summary>Deterministic per-cycle draw: amplitude scale and crank offset.</summary>
    private (double Amplitude, double PhaseOffsetDeg) DrawCycle(long cycle, SynthesisVariation v)
    {
        if (v.AmplitudeCoV == 0.0 && v.PhaseJitterDeg == 0.0)
        {
            return (1.0, 0.0);
        }

        var state = Hash(Seed, (ulong)cycle);
        var g1 = Gaussian(ref state);
        var g2 = Gaussian(ref state);
        return (Math.Max(0.2, 1.0 + g1 * v.AmplitudeCoV), g2 * v.PhaseJitterDeg);
    }

    private static ulong Hash(ulong seed, ulong stream)
    {
        var x = seed ^ (stream * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x == 0 ? 0x9E3779B97F4A7C15UL : x;
    }

    private static double NextUniform(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return ((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
    }

    private static double Gaussian(ref ulong state)
    {
        var u1 = Math.Max(NextUniform(ref state), 1e-12);
        var u2 = NextUniform(ref state);
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>
/// Mixes rendered stems and applies the listener chain (plan §3.6 steps 5–6).
/// Stems stay separate all the way to export so exhaust, intake, broadband
/// and the (cosmetic, clearly-labelled) mechanical layer can be soloed.
/// </summary>
public static class StemMixer
{
    public static AudioStem Mix(string name, params (AudioStem Stem, double Gain)[] parts)
    {
        if (parts.Length == 0)
        {
            throw new ArgumentException("Nothing to mix.", nameof(parts));
        }

        var rate = parts[0].Stem.SampleRate;
        var length = parts.Max(p => p.Stem.Samples.Length);
        var output = new float[length];
        foreach (var (stem, gain) in parts)
        {
            if (Math.Abs(stem.SampleRate - rate) > 1e-9)
            {
                throw new ArgumentException("All stems must share a sample rate.");
            }

            for (var i = 0; i < stem.Samples.Length; i++)
            {
                output[i] += (float)(gain * stem.Samples[i]);
            }
        }

        return new AudioStem(name, output, rate);
    }

    /// <summary>
    /// Distance/Doppler for a moving source (plan §3.6 drive-by): time-varying
    /// propagation delay plus 1/r spreading, read with linear interpolation
    /// from the source stem. The Doppler shift emerges from the changing
    /// delay — it is never applied as a separate pitch shift.
    /// </summary>
    public static AudioStem DriveBy(
        AudioStem source, double speedMetresPerSecond, double closestApproachMetres,
        double soundSpeed = 343.2, double startDistanceMetres = 60.0)
    {
        var rate = source.SampleRate;
        var output = new float[source.Samples.Length];
        var passTime = startDistanceMetres / Math.Max(speedMetresPerSecond, 1e-6);

        for (var i = 0; i < output.Length; i++)
        {
            var t = i / rate;
            var along = (t - passTime) * speedMetresPerSecond;
            var distance = Math.Sqrt(along * along + closestApproachMetres * closestApproachMetres);
            var emissionTime = t - distance / soundSpeed;
            var position = emissionTime * rate;
            if (position < 0 || position >= source.Samples.Length - 1)
            {
                continue;
            }

            var i0 = (int)position;
            var frac = position - i0;
            var sample = source.Samples[i0] + frac * (source.Samples[i0 + 1] - source.Samples[i0]);
            output[i] = (float)(sample * closestApproachMetres / distance);
        }

        return new AudioStem($"{source.Name} (drive-by)", output, rate);
    }

    /// <summary>
    /// Overrun burble / crackle (plan §3.4): stochastic impulsive events on
    /// decel, seeded and reproducible. PHENOMENOLOGICAL — the rate and energy
    /// are user knobs, not predictions, and the UI must say so.
    /// </summary>
    public static AudioStem OverrunBurble(
        RpmProfile profile, double sampleRate, ulong seed,
        double eventsPerSecond = 25.0, double amplitude = 0.25, double decayMs = 12.0)
    {
        var count = (int)Math.Round(profile.Duration * sampleRate);
        var output = new float[count];
        var state = seed == 0 ? 1UL : seed;
        var decaySamples = decayMs * 1e-3 * sampleRate;

        for (var i = 0; i < count; i++)
        {
            var t = i / sampleRate;
            // Only on decel: rpm falling.
            var rpmNow = profile.RpmAt(t);
            var rpmNext = profile.RpmAt(Math.Min(t + 0.02, profile.Duration));
            if (rpmNext >= rpmNow)
            {
                continue;
            }

            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var u = ((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
            if (u > eventsPerSecond / sampleRate)
            {
                continue;
            }

            // Impulsive event with exponential decay.
            var energy = amplitude * (0.4 + 0.6 * u * sampleRate / eventsPerSecond);
            for (var k = 0; k < (int)(decaySamples * 4) && i + k < count; k++)
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                var noise = (((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53))) * 2.0 - 1.0;
                output[i + k] += (float)(energy * noise * Math.Exp(-k / decaySamples));
            }
        }

        return new AudioStem("burble", output, sampleRate);
    }
}
