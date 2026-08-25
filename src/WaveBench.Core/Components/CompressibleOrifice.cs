namespace WaveBench.Core.Components;

/// <summary>
/// Quasi-steady compressible flow through a restriction (plan §2.6):
///   ṁ = C_d·A·(p0/√(R·T0))·Φ(p/p0, γ)
///   Φ = √( 2γ/(γ−1) · [ r^(2/γ) − r^((γ+1)/γ) ] ),  r = p/p0
/// choked at the critical pressure ratio r* = (2/(γ+1))^(γ/(γ−1)), where
/// Φ* = √γ·(2/(γ+1))^((γ+1)/(2(γ−1))) (= 0.6847 for γ = 1.4).
/// Upstream conditions are stagnation; downstream is static.
/// </summary>
public static class CompressibleOrifice
{
    public static double CriticalPressureRatio(double gamma) =>
        Math.Pow(2.0 / (gamma + 1.0), gamma / (gamma - 1.0));

    /// <summary>The dimensionless mass-flow function Φ (choke-limited).</summary>
    public static double FlowFunction(double pressureRatio, double gamma)
    {
        var rCrit = CriticalPressureRatio(gamma);
        var r = Math.Clamp(pressureRatio, rCrit, 1.0);
        var term = Math.Pow(r, 2.0 / gamma) - Math.Pow(r, (gamma + 1.0) / gamma);
        return Math.Sqrt(Math.Max(0.0, 2.0 * gamma / (gamma - 1.0) * term));
    }

    public static bool IsChoked(double pressureRatio, double gamma) =>
        pressureRatio <= CriticalPressureRatio(gamma);

    /// <summary>
    /// Mass flow, kg/s. p0/T0 upstream stagnation, p downstream static,
    /// R the upstream gas constant. Direction handling belongs to the caller.
    /// </summary>
    public static double MassFlow(
        double dischargeCoefficient, double area,
        double upstreamStagnationPressure, double upstreamStagnationTemperature,
        double downstreamStaticPressure, double gamma, double gasConstant)
    {
        if (downstreamStaticPressure >= upstreamStagnationPressure)
        {
            return 0.0;
        }

        var ratio = downstreamStaticPressure / upstreamStagnationPressure;
        return dischargeCoefficient * area
               * upstreamStagnationPressure
               / Math.Sqrt(gasConstant * upstreamStagnationTemperature)
               * FlowFunction(ratio, gamma);
    }
}
