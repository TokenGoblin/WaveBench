namespace WaveBench.Core.EngineModel;

/// <summary>Mass-fraction-burned schedule over the cycle.</summary>
public interface ICombustionModel
{
    /// <summary>Burn start, deg in cycle coordinates (0 = TDC firing).</summary>
    double StartAngleDeg { get; }

    /// <summary>Nominal 0–1 burn duration, deg.</summary>
    double DurationDeg { get; }

    /// <summary>Mass fraction burned at local cycle angle (monotone 0→1).</summary>
    double BurnFraction(double localAngleDeg);
}

/// <summary>
/// Wiebe function (plan §2.5 Level 1):
///   x_b = 1 − exp(−a·((θ−θ0)/Δθ)^(m+1)),  a = 5, m = 2 defaults.
/// The a = 5 convention burns 99.33% at θ0 + Δθ. Burn duration scaling with
/// engine speed and residual fraction is applied by the caller via
/// <see cref="WithDuration"/> (documented approximation until the Level-3
/// predictive model lands).
/// </summary>
public sealed record WiebeCombustion(
    double StartAngleDeg,
    double DurationDeg,
    double A = 5.0,
    double M = 2.0) : ICombustionModel
{
    public double BurnFraction(double localAngleDeg)
    {
        var x = (Normalize(localAngleDeg) - StartAngleDeg) / DurationDeg;
        if (x <= 0)
        {
            return 0.0;
        }

        if (x >= 1)
        {
            return 1.0 - Math.Exp(-A);
        }

        return 1.0 - Math.Exp(-A * Math.Pow(x, M + 1.0));
    }

    private double Normalize(double angle)
    {
        // Combustion window sits near TDC firing; map angles near 720 into
        // negative territory so a start before TDC (θ0 < 0) works.
        var a = angle % 720.0;
        if (a > 360.0)
        {
            a -= 720.0;
        }

        return a;
    }

    public WiebeCombustion WithDuration(double durationDeg) => this with { DurationDeg = durationDeg };
}

/// <summary>
/// Double Wiebe: weighted sum of two schedules (e.g. flame-kernel + main
/// burn, or main + late diffusion tail).
/// </summary>
public sealed record DoubleWiebeCombustion(
    WiebeCombustion First,
    WiebeCombustion Second,
    double FirstWeight) : ICombustionModel
{
    public double StartAngleDeg => Math.Min(First.StartAngleDeg, Second.StartAngleDeg);

    public double DurationDeg =>
        Math.Max(First.StartAngleDeg + First.DurationDeg, Second.StartAngleDeg + Second.DurationDeg)
        - StartAngleDeg;

    public double BurnFraction(double localAngleDeg) =>
        FirstWeight * First.BurnFraction(localAngleDeg)
        + (1.0 - FirstWeight) * Second.BurnFraction(localAngleDeg);
}
