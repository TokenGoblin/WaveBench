namespace WaveBench.Core.EngineModel;

/// <summary>
/// Cycle-to-cycle combustion variability (plan §3.4): per-cylinder,
/// per-cycle perturbations of combustion phasing, duration and released
/// energy, drawn from a DETERMINISTIC seeded stream so results stay
/// reproducible (fixed, user-visible seed — plan Part 0 rule 6). Defaults:
/// CA50 jitter σ 1.2° and IMEP-scale energy CoV 2% (typical mid-load SI
/// values; higher at idle — user-adjustable).
/// </summary>
public sealed record CycleVariability(
    ulong Seed,
    double PhasingSigmaDeg = 1.2,
    double DurationSigmaFraction = 0.04,
    double EnergyCoV = 0.02)
{
    /// <summary>Draw the perturbation triple for one cylinder-cycle.</summary>
    public (double PhaseShiftDeg, double DurationScale, double EnergyScale) Draw(int cylinderIndex, long cycleNumber)
    {
        var stream = Hash(Seed, (ulong)cylinderIndex * 0x9E3779B97F4A7C15UL + (ulong)cycleNumber);
        var g1 = Gaussian(ref stream);
        var g2 = Gaussian(ref stream);
        var g3 = Gaussian(ref stream);
        return (
            g1 * PhasingSigmaDeg,
            Math.Max(0.5, 1.0 + g2 * DurationSigmaFraction),
            Math.Max(0.5, 1.0 + g3 * EnergyCoV));
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
        // xorshift64*
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        var value = state * 0x2545F4914F6CDD1DUL;
        return (value >> 11) * (1.0 / (1UL << 53));
    }

    private static double Gaussian(ref ulong state)
    {
        // Box–Muller; clamp away from 0 for the log.
        var u1 = Math.Max(NextUniform(ref state), 1e-12);
        var u2 = NextUniform(ref state);
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
