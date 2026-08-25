namespace WaveBench.Core.Components;

/// <summary>
/// Steady loss coefficients for a 90° T-junction after Idelchik ("Handbook of
/// Hydraulic Resistance", diagrams 7-1/7-2), in the formulation documented by
/// the Deltares WANDA manual (§4.29). Coefficients are referenced to the
/// combined-leg dynamic head. Legs: combined (carries the total flow),
/// straight-through, side branch.
///
/// Branch-angle-dependent coefficients (Bassett, Winterbone &amp; Pearson 2001)
/// are the planned extension for collector work; this 90° tee model is the
/// pressure-loss option the plan's §2.7 junction table requires alongside the
/// constant-pressure model.
/// </summary>
public static class TeeJunctionLoss
{
    /// <summary>
    /// Combining flow, straight leg → combined leg: ξ ≈ 1.55·q − q², with
    /// q = Q_side/Q_combined.
    /// </summary>
    public static double CombiningStraight(double sideFlowFraction)
    {
        var q = Math.Clamp(sideFlowFraction, 0.0, 1.0);
        return 1.55 * q - q * q;
    }

    /// <summary>
    /// Combining flow, side branch → combined leg:
    /// ξ = A·[1 + (q·A_c/A_s)² − 2·(1 − q)²], q = Q_side/Q_combined.
    /// </summary>
    public static double CombiningBranch(double sideFlowFraction, double sideToCombinedAreaRatio)
    {
        var q = Math.Clamp(sideFlowFraction, 0.0, 1.0);
        var a = CoefficientA(sideToCombinedAreaRatio, q);
        var term = q / sideToCombinedAreaRatio;
        return a * (1.0 + term * term - 2.0 * (1.0 - q) * (1.0 - q));
    }

    /// <summary>
    /// Dividing flow, combined leg → side branch:
    /// ξ = A′·[1 + k·(q·A_c/A_s)²], k = 1 for A_s/A_c ≤ 2/3, 0.3 at equal
    /// areas (welded tee), A′ = 1.
    /// </summary>
    public static double DividingBranch(double sideFlowFraction, double sideToCombinedAreaRatio)
    {
        var q = Math.Clamp(sideFlowFraction, 0.0, 1.0);
        var term = q / sideToCombinedAreaRatio;
        var k = sideToCombinedAreaRatio <= 2.0 / 3.0
            ? 1.0
            : 0.3 + (1.0 - 0.3) * (1.0 - sideToCombinedAreaRatio) / (1.0 - 2.0 / 3.0);
        return 1.0 + k * term * term;
    }

    /// <summary>
    /// Dividing flow, combined leg → straight leg: small; Idelchik tabulates
    /// τ·q² with τ ≈ 0.4 for equal-area welded tees.
    /// </summary>
    public static double DividingStraight(double sideFlowFraction)
    {
        var q = Math.Clamp(sideFlowFraction, 0.0, 1.0);
        return 0.4 * q * q;
    }

    private static double CoefficientA(double areaRatio, double q)
    {
        if (areaRatio <= 0.35)
        {
            return 1.0;
        }

        return q <= 0.4 ? 0.9 * (1.0 - q) : 0.55;
    }
}
