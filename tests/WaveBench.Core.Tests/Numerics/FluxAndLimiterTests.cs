using FluentAssertions;
using WaveBench.Core.Numerics;
using Xunit;

namespace WaveBench.Core.Tests.Numerics;

public class FluxAndLimiterTests
{
    private static readonly PerfectGas Gas = new(1.4, 287.0);

    [Fact]
    public void Hllc_is_consistent_with_the_physical_flux_on_uniform_data()
    {
        var w = new PrimitiveState(1.2, 45.0, 101_325.0);
        var (fRho, fMom, fEner) = HllcFlux.Compute(w, w, Gas);
        var (eRho, eMom, eEner) = EulerMath.Flux(w, Gas);

        fRho.Should().BeApproximately(eRho, Math.Abs(eRho) * 1e-12);
        fMom.Should().BeApproximately(eMom, Math.Abs(eMom) * 1e-12);
        fEner.Should().BeApproximately(eEner, Math.Abs(eEner) * 1e-12);
    }

    [Fact]
    public void Hllc_upwinds_supersonic_flow()
    {
        // Both states moving right far above the sound speed → flux is F(left).
        var left = new PrimitiveState(1.0, 1000.0, 100_000.0);
        var right = new PrimitiveState(0.5, 1000.0, 50_000.0);
        var flux = HllcFlux.Compute(left, right, Gas);
        var exact = EulerMath.Flux(left, Gas);
        flux.Should().Be(exact);
    }

    [Fact]
    public void Hllc_flux_approaches_the_godunov_flux_for_weak_waves()
    {
        // On a strong jump (raw Sod data) HLLC's PVRS wave-speed estimates put
        // the single-interface flux ~20% from the exact Godunov flux — that is
        // intrinsic to the approximation and the scheme still converges (see
        // the Sod L1 verification test). The property worth asserting is the
        // weak-wave limit, where HLLC must approach the exact flux.
        var left = new PrimitiveState(1.0, 0.0, 1.0);
        var right = new PrimitiveState(0.98, 0.01, 0.97);
        var exactState = new ExactRiemannSolver(left, right, Gas).Sample(0.0);
        var exactFlux = EulerMath.Flux(exactState, Gas);
        var (fRho, fMom, fEner) = HllcFlux.Compute(left, right, Gas);

        fMom.Should().BeApproximately(exactFlux.FMom, Math.Abs(exactFlux.FMom) * 0.01);
        fEner.Should().BeApproximately(exactFlux.FEner, Math.Abs(exactFlux.FEner) * 0.01 + 1e-4);
        fRho.Should().BeApproximately(exactFlux.FRho, 0.01);
    }

    [Theory]
    [InlineData(SlopeLimiterKind.Minmod)]
    [InlineData(SlopeLimiterKind.VanLeer)]
    [InlineData(SlopeLimiterKind.VanAlbada)]
    public void Limiters_vanish_at_extrema_and_are_symmetric(SlopeLimiterKind kind)
    {
        SlopeLimiters.Limit(kind, 1.0, -1.0).Should().Be(0.0, "opposite signs mark an extremum");
        SlopeLimiters.Limit(kind, -0.3, 0.7).Should().Be(0.0);
        SlopeLimiters.Limit(kind, 0.0, 1.0).Should().Be(0.0);

        var ab = SlopeLimiters.Limit(kind, 0.4, 0.9);
        var ba = SlopeLimiters.Limit(kind, 0.9, 0.4);
        ab.Should().BeApproximately(ba, 1e-15, "limiters must be symmetric");
    }

    [Theory]
    [InlineData(SlopeLimiterKind.Minmod)]
    [InlineData(SlopeLimiterKind.VanLeer)]
    [InlineData(SlopeLimiterKind.VanAlbada)]
    public void Limited_slope_is_bounded_by_the_input_slopes(SlopeLimiterKind kind)
    {
        foreach (var (a, b) in new[] { (0.2, 0.9), (1.5, 0.1), (0.5, 0.5), (-2.0, -0.4) })
        {
            var s = SlopeLimiters.Limit(kind, a, b);
            var bound = 2.0 * Math.Min(Math.Abs(a), Math.Abs(b));
            Math.Abs(s).Should().BeLessThanOrEqualTo(bound + 1e-15);
            Math.Sign(s).Should().Be(Math.Sign(a));
        }
    }

    [Fact]
    public void Limiters_reproduce_the_slope_on_linear_data()
    {
        // Equal one-sided differences → the exact central slope, second order.
        SlopeLimiters.VanLeer(0.6, 0.6).Should().BeApproximately(0.6, 1e-15);
        SlopeLimiters.Minmod(0.6, 0.6).Should().BeApproximately(0.6, 1e-15);
        SlopeLimiters.VanAlbada(0.6, 0.6).Should().BeApproximately(0.6, 1e-15);
    }
}
