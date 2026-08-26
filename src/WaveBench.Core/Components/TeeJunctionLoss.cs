namespace WaveBench.Core.Components;

/// <summary>
/// Steady loss coefficients for a pipe junction after Idelchik ("Handbook of
/// Hydraulic Resistance", diagrams 7-1/7-2 for the 90° tee and the converging
/// / diverging wye diagrams for other angles), in the formulation documented
/// by the Deltares WANDA manual (§4.29). Coefficients are referenced to the
/// combined-leg dynamic head. Legs: combined (carries the total flow),
/// straight-through, side branch.
///
/// <b>Branch angle.</b> The angle is measured between the side branch and the
/// combined-leg axis: 90° is a tee, 0° a fully aligned merge. It matters:
/// exhaust collectors merge primaries at 10–30°, and charging them the
/// right-angle coefficient overstates the loss substantially. The angle
/// enters through the cos α terms of Idelchik's wye formulas, which vanish at
/// 90° — so every coefficient here reduces EXACTLY to the previous
/// right-angle model, and a test pins that.
///
/// The combining form also satisfies an analytic limit that no fit can fake:
/// with all flow through an aligned, equal-area branch the junction is just a
/// straight pipe, and ξ comes out exactly zero.
///
/// <b>Not implemented:</b> the unsteady-flow junction coefficients of
/// Bassett, Winterbone &amp; Pearson (2001) and Bassett et al.
/// (SAE 2003-01-0370), which the plan names. These are steady-flow
/// coefficients applied quasi-steadily; see docs/physics.md.
/// </summary>
public static class TeeJunctionLoss
{
    /// <summary>A plain tee — the angle these coefficients reduce to.</summary>
    public const double RightAngleDeg = 90.0;

    /// <summary>
    /// Combining flow, straight leg → combined leg: ξ ≈ 1.55·q − q², with
    /// q = Q_side/Q_combined.
    ///
    /// The straight leg carries no angle dependence: it is collinear with the
    /// combined leg by definition, and Idelchik's empirical straight-leg fit
    /// is for the flow division rather than the branch geometry.
    /// </summary>
    public static double CombiningStraight(double sideFlowFraction)
    {
        var q = Math.Clamp(sideFlowFraction, 0.0, 1.0);
        return 1.55 * q - q * q;
    }

    /// <summary>
    /// Combining flow, side branch → combined leg:
    /// ξ = A·[1 + (q·σ)² − 2·(1 − q)² − 2·q²·σ·cos α], σ = A_c/A_s,
    /// q = Q_side/Q_combined, α the branch angle. At α = 90° the last term
    /// vanishes and this is the tee formula.
    /// </summary>
    public static double CombiningBranch(
        double sideFlowFraction, double sideToCombinedAreaRatio, double branchAngleDeg = RightAngleDeg)
    {
        var q = Math.Clamp(sideFlowFraction, 0.0, 1.0);
        var a = CoefficientA(sideToCombinedAreaRatio, q);
        var sigma = 1.0 / sideToCombinedAreaRatio;
        var term = q * sigma;
        // This coefficient MAY be negative, and that is not a bug to clamp
        // away — the right-angle model already returned negative values at
        // small q. A branch merging into a larger collector decelerates and is
        // dragged along by the faster combined stream: it gains total pressure
        // at the other streams' expense, as an ejector does. Idelchik's
        // converging-wye tables carry negative branch coefficients for exactly
        // this, and it is the scavenging a shallow collector exists to
        // produce. Only the junction as a whole must dissipate, not each leg
        // pair.
        //
        // No clamp is needed: the bracket is ≥ −1 everywhere (its minimum is
        // −1 at q = 0) and A ≤ 1, so the coefficient cannot fall below one
        // combined-leg dynamic head on its own.
        return a * (1.0 + (term * term)
                        - (2.0 * (1.0 - q) * (1.0 - q))
                        - (2.0 * q * q * sigma * CosAngle(branchAngleDeg)));
    }

    /// <summary>
    /// Dividing flow, combined leg → side branch:
    /// ξ = 1 + k·(q·σ)² − 2·(q·σ)·cos α, σ = A_c/A_s, k = 1 for
    /// A_s/A_c ≤ 2/3 and 0.3 at equal areas (welded tee).
    ///
    /// The −2(q·σ)cos α term is Idelchik's diverging-wye angle dependence,
    /// zero at 90°.
    ///
    /// The welded-tee k &lt; 1 is an empirical RIGHT-ANGLE correction, so it
    /// is faded out as the branch aligns: k_eff = k + (1 − k)·cos α. That
    /// leaves 90° exactly as before and recovers the pure wye form (k = 1) at
    /// 0°. It is not cosmetic — combining a fixed k &lt; 1 with the angle term
    /// drives the coefficient negative at shallow angles, which a dividing
    /// branch cannot be. With the fade, the minimum over flow split is
    /// 1 − cos²α/k_eff ≥ 0 everywhere, so no floor is needed.
    /// </summary>
    public static double DividingBranch(
        double sideFlowFraction, double sideToCombinedAreaRatio, double branchAngleDeg = RightAngleDeg)
    {
        var q = Math.Clamp(sideFlowFraction, 0.0, 1.0);
        var term = q / sideToCombinedAreaRatio;
        var cos = CosAngle(branchAngleDeg);
        var k = sideToCombinedAreaRatio <= 2.0 / 3.0
            ? 1.0
            : 0.3 + (1.0 - 0.3) * (1.0 - sideToCombinedAreaRatio) / (1.0 - 2.0 / 3.0);
        var effectiveK = k + ((1.0 - k) * cos);
        return 1.0 + (effectiveK * term * term) - (2.0 * term * cos);
    }

    /// <summary>
    /// cos of the branch angle, with the angle clamped to the range these
    /// correlations were established over. Idelchik's wye diagrams cover
    /// 0–90°; beyond 90° the branch points backwards into the oncoming flow
    /// and the formulas are not valid, so the coefficient is held at its
    /// right-angle value rather than extrapolated into a regime nobody
    /// measured.
    /// </summary>
    private static double CosAngle(double branchAngleDeg) =>
        Math.Cos(Math.Clamp(branchAngleDeg, 0.0, 90.0) * Math.PI / 180.0);

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
