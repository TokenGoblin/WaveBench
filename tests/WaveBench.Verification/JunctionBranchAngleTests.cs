using FluentAssertions;
using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 4 §2.7 verification of branch-angle-dependent junction loss.
///
/// The angle matters because exhaust collectors merge primaries at 10–30°,
/// and charging them a right-angle tee coefficient overstates the loss
/// substantially. The checks here are limits and identities rather than fits:
/// exact reduction to the previous right-angle model, the analytic zero-loss
/// case, monotonicity, and the correct direction against area ratio.
/// </summary>
public class JunctionBranchAngleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0.1, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.9, 0.5)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.25, 0.35)]
    [InlineData(1.0, 1.0)]
    public void Gate_ninety_degrees_reproduces_the_right_angle_model_exactly(double q, double areaRatio)
    {
        // Adding an axis must not move the case that was already verified.
        // At α = 90° every cos term is zero, so this is an identity, not a
        // tolerance.
        var combining = TeeJunctionLoss.CombiningBranch(q, areaRatio, 90.0);
        var sigma = 1.0 / areaRatio;
        var a = areaRatio <= 0.35 ? 1.0 : q <= 0.4 ? 0.9 * (1.0 - q) : 0.55;
        var expected = a * (1.0 + Math.Pow(q * sigma, 2) - (2.0 * Math.Pow(1.0 - q, 2)));

        combining.Should().BeApproximately(expected, 1e-12);

        var dividing = TeeJunctionLoss.DividingBranch(q, areaRatio, 90.0);
        var k = areaRatio <= 2.0 / 3.0 ? 1.0 : 0.3 + (0.7 * (1.0 - areaRatio) / (1.0 - (2.0 / 3.0)));
        dividing.Should().BeApproximately(1.0 + (k * Math.Pow(q / areaRatio, 2)), 1e-12);
    }

    [Fact]
    public void Gate_an_aligned_equal_area_full_flow_merge_loses_exactly_nothing()
    {
        // The identity that no curve fit can fake: if all the flow arrives
        // through a branch that is aligned with the outlet and the same size,
        // the junction IS a straight pipe.
        TeeJunctionLoss.CombiningBranch(1.0, 1.0, 0.0).Should().BeApproximately(0.0, 1e-12);

        // And it is approached smoothly rather than jumped to.
        var near = TeeJunctionLoss.CombiningBranch(1.0, 1.0, 5.0);
        near.Should().BeGreaterThan(0.0).And.BeLessThan(0.02);
        output.WriteLine($"aligned merge: 0° -> {TeeJunctionLoss.CombiningBranch(1.0, 1.0, 0.0):F4}, 5° -> {near:F4}");
    }

    [Fact]
    public void Gate_loss_falls_monotonically_as_the_merge_angle_closes()
    {
        // The design point of the whole change: a shallower collector is a
        // lower-loss collector.
        var previous = double.MaxValue;
        foreach (var angle in new[] { 90.0, 75.0, 60.0, 45.0, 30.0, 15.0, 0.0 })
        {
            var xi = TeeJunctionLoss.CombiningBranch(0.5, 1.0, angle);
            output.WriteLine($"{angle,5:F0}°: ξ = {xi:F4}");
            xi.Should().BeLessThan(previous, $"{angle:F0}° must lose less than the angle above it");
            previous = xi;
        }
    }

    [Fact]
    public void Gate_a_shallow_collector_recovers_pressure_where_a_tee_loses_it()
    {
        // A 4-1 collector at 15°, each primary carrying a quarter of the flow
        // into a collector twice a primary's area. The primary DECELERATES
        // into the collector and is dragged along by the combined stream, so
        // its branch coefficient goes negative: it gains total pressure at the
        // other streams' expense, which is the scavenging a collector exists
        // to produce. Idelchik's converging-wye tables carry negative branch
        // coefficients for exactly this regime.
        const double q = 0.25;
        const double areaRatio = 0.5; // A_side / A_combined

        var tee = TeeJunctionLoss.CombiningBranch(q, areaRatio, 90.0);
        var collector = TeeJunctionLoss.CombiningBranch(q, areaRatio, 15.0);

        output.WriteLine($"4-1 collector, q=0.25, A_s/A_c=0.5: 90° tee ξ={tee:F3}, 15° merge ξ={collector:F3}");
        tee.Should().BeGreaterThan(0.0, "a right-angle merge dissipates");
        collector.Should().BeLessThan(0.0, "a shallow merge into a larger collector recovers");
        collector.Should().BeGreaterThan(-1.0, "recovery is bounded by the combined-leg dynamic head");
    }

    [Fact]
    public void Gate_recovery_is_bounded_by_one_dynamic_head_without_clamping()
    {
        // A leg pair may recover — only the junction as a whole must
        // dissipate — but it may not invent energy without limit. No clamp is
        // applied anywhere: the bound falls out of the formula, because the
        // bracket's minimum is −1 (at q = 0) and A ≤ 1. If it ever needed a
        // clamp, the formula would be wrong rather than the clamp right.
        var lowest = double.MaxValue;
        for (var angle = 0.0; angle <= 90.0; angle += 2.5)
        {
            for (var q = 0.0; q <= 1.0; q += 0.02)
            {
                for (var ratio = 0.2; ratio <= 1.5; ratio += 0.05)
                {
                    var combining = TeeJunctionLoss.CombiningBranch(q, ratio, angle);
                    combining.Should().BeGreaterThanOrEqualTo(-1.0);
                    lowest = Math.Min(lowest, combining);

                    // A dividing branch is always a restriction. The welded-tee
                    // correction fading with angle is what keeps this true.
                    TeeJunctionLoss.DividingBranch(q, ratio, angle)
                        .Should().BeGreaterThanOrEqualTo(0.0,
                            $"dividing at q={q:F2}, A_s/A_c={ratio:F2}, α={angle:F1}°");
                }
            }
        }

        output.WriteLine($"deepest recovery found across the sweep: {lowest:F4}");
        lowest.Should().BeLessThan(0.0, "recovery must actually occur somewhere, or the term does nothing");
    }

    [Fact]
    public void Angles_past_ninety_degrees_are_held_rather_than_extrapolated()
    {
        // Beyond 90° the branch points back into the oncoming flow. Idelchik's
        // wye diagrams do not cover that, so the coefficient must hold at its
        // right-angle value instead of continuing into a regime nobody
        // measured — extrapolating would make cos α negative and invent a
        // loss increase with no basis.
        var tee = TeeJunctionLoss.CombiningBranch(0.5, 1.0, 90.0);
        TeeJunctionLoss.CombiningBranch(0.5, 1.0, 120.0).Should().Be(tee);
        TeeJunctionLoss.CombiningBranch(0.5, 1.0, 180.0).Should().Be(tee);
    }

    [Fact]
    public void A_smaller_branch_into_the_same_collector_loses_more()
    {
        // Squeezing the same flow through a smaller primary raises its
        // velocity relative to the collector, and the loss with it.
        var wide = TeeJunctionLoss.CombiningBranch(0.5, 1.0, 30.0);
        var narrow = TeeJunctionLoss.CombiningBranch(0.5, 0.4, 30.0);

        output.WriteLine($"at 30°: A_s/A_c = 1.0 -> {wide:F3}, 0.4 -> {narrow:F3}");
        narrow.Should().BeGreaterThan(wide);
    }

    [Fact]
    public void The_junction_rejects_a_physically_meaningless_angle()
    {
        var gas = new PerfectGasModel(PerfectGas.Air);
        var junction = new Junction(gas);
        var duct = new DuctSolver(DuctGeometry.Uniform(0.5, 20, 0.040), gas);

        var act = () => junction.Connect(duct, leftEnd: true, isSideBranch: true, branchAngleDeg: 200.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
