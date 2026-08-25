using FluentAssertions;
using WaveBench.Core.Components;
using Xunit;

namespace WaveBench.Core.Tests.Numerics;

public class ComponentTests
{
    [Fact]
    public void Choked_flow_function_matches_the_published_constant()
    {
        // Φ* = √γ·(2/(γ+1))^((γ+1)/(2(γ−1))) = 0.6847 for γ = 1.4 (standard
        // compressible-flow tables).
        CompressibleOrifice.FlowFunction(0.0, 1.4).Should().BeApproximately(0.6847, 0.0002);
        CompressibleOrifice.CriticalPressureRatio(1.4).Should().BeApproximately(0.5283, 0.0001);
    }

    [Fact]
    public void Subcritical_flow_function_matches_hand_calculation()
    {
        // r = 0.9, γ = 1.4: Φ = √(7·(0.9^(2/1.4) − 0.9^(2.4/1.4))) = 0.4224.
        CompressibleOrifice.FlowFunction(0.9, 1.4).Should().BeApproximately(0.4224, 0.0005);
    }

    [Fact]
    public void Mass_flow_is_zero_for_adverse_pressure_and_clamps_at_choke()
    {
        CompressibleOrifice.MassFlow(0.8, 1e-4, 1e5, 300, 1.2e5, 1.4, 287).Should().Be(0.0);

        var atChoke = CompressibleOrifice.MassFlow(0.8, 1e-4, 1e5, 300, 0.5283e5, 1.4, 287);
        var deepVacuum = CompressibleOrifice.MassFlow(0.8, 1e-4, 1e5, 300, 0.1e5, 1.4, 287);
        deepVacuum.Should().BeApproximately(atChoke, atChoke * 1e-3, "choked flow is back-pressure independent");
    }

    [Fact]
    public void Tee_loss_coefficients_match_published_anchors()
    {
        // Dividing tee, equal areas, all flow to the branch: K = 1 + 0.3 = 1.3
        // (Idelchik welded tee; the classic Crane TP-410 value for a 90°
        // run-to-branch tee).
        TeeJunctionLoss.DividingBranch(1.0, 1.0).Should().BeApproximately(1.3, 0.01);

        // Small-area branch behaves like k = 1: K = 1 + (q/ar)².
        TeeJunctionLoss.DividingBranch(0.5, 0.5).Should().BeApproximately(1.0 + Math.Pow(0.5 / 0.5, 2), 0.01);

        // Combining straight leg: ξ = 1.55q − q² (Idelchik via WANDA §4.29).
        TeeJunctionLoss.CombiningStraight(0.0).Should().Be(0.0);
        TeeJunctionLoss.CombiningStraight(0.5).Should().BeApproximately(0.525, 1e-6);
        TeeJunctionLoss.CombiningStraight(1.0).Should().BeApproximately(0.55, 1e-6);

        // Combining branch, equal areas, q = 0.5 (> 0.4 → A = 0.55):
        // ξ = 0.55·(1 + 0.25 − 2·0.25) = 0.4125.
        TeeJunctionLoss.CombiningBranch(0.5, 1.0).Should().BeApproximately(0.4125, 1e-4);

        // No side flow → no straight-leg penalty.
        TeeJunctionLoss.DividingStraight(0.0).Should().Be(0.0);
    }

    [Fact]
    public void Throttle_area_grows_monotonically_with_angle()
    {
        var throttle = new ThrottleValve(0.06);
        var previous = 0.0;
        for (var angle = 0.0; angle <= 90.0; angle += 5.0)
        {
            var area = throttle.EffectiveArea(angle);
            area.Should().BeGreaterThanOrEqualTo(previous);
            previous = area;
        }

        throttle.EffectiveArea(90.0).Should().BeApproximately(throttle.BoreArea, throttle.BoreArea * 0.01,
            "wide open ≈ full bore");
        throttle.EffectiveArea(0.0).Should().BeApproximately(
            throttle.BoreArea * throttle.LeakageFraction, 1e-9, "closed plate leaks only");
    }
}
