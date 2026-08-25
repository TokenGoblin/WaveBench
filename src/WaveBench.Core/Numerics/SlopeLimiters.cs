namespace WaveBench.Core.Numerics;

public enum SlopeLimiterKind
{
    /// <summary>Most dissipative, most robust.</summary>
    Minmod,

    /// <summary>Default (plan §5.1).</summary>
    VanLeer,

    /// <summary>Smooth limiter; least extremum clipping of the three.</summary>
    VanAlbada,
}

/// <summary>
/// Slope limiters for MUSCL reconstruction, in slope form: given the backward
/// and forward differences of a primitive variable, return the limited cell
/// slope. All three are symmetric and vanish at extrema (opposite-sign
/// differences), which is what keeps the scheme TVD.
/// </summary>
public static class SlopeLimiters
{
    public static double Limit(SlopeLimiterKind kind, double backward, double forward) => kind switch
    {
        SlopeLimiterKind.Minmod => Minmod(backward, forward),
        SlopeLimiterKind.VanLeer => VanLeer(backward, forward),
        SlopeLimiterKind.VanAlbada => VanAlbada(backward, forward),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static double Minmod(double a, double b)
    {
        if (a * b <= 0.0)
        {
            return 0.0;
        }

        return Math.Abs(a) < Math.Abs(b) ? a : b;
    }

    public static double VanLeer(double a, double b)
    {
        if (a * b <= 0.0)
        {
            return 0.0;
        }

        return 2.0 * a * b / (a + b);
    }

    public static double VanAlbada(double a, double b)
    {
        if (a * b <= 0.0)
        {
            return 0.0;
        }

        return a * b * (a + b) / (a * a + b * b);
    }
}
