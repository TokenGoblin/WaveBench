using FluentAssertions;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;

namespace WaveBench.Core.Tests.EngineModel;

public class EngineModelTests
{
    private static readonly CrankGeometry Crank = new()
    {
        Bore = 0.090,
        Stroke = 0.090,
        RodLength = 0.150,
        CompressionRatio = 10.0,
    };

    [Fact]
    public void Crank_volume_hits_tdc_and_bdc_exactly()
    {
        Crank.Volume(0.0).Should().BeApproximately(Crank.ClearanceVolume, Crank.ClearanceVolume * 1e-9);
        Crank.Volume(180.0).Should().BeApproximately(
            Crank.ClearanceVolume + Crank.DisplacedVolume, Crank.DisplacedVolume * 1e-9);
        Crank.Volume(360.0).Should().BeApproximately(Crank.ClearanceVolume, Crank.ClearanceVolume * 1e-9);

        // 90 mm bore/stroke → 572.6 cc.
        (Crank.DisplacedVolume * 1e6).Should().BeApproximately(572.6, 0.5);
        (Crank.DisplacedVolume / (Crank.Volume(180.0) / Crank.Volume(0.0) - 1.0))
            .Should().BeApproximately(Crank.ClearanceVolume, Crank.ClearanceVolume * 1e-9,
                "compression ratio definition is consistent");
    }

    [Fact]
    public void Rod_ratio_skews_mid_stroke_volume()
    {
        // With a finite rod the piston is below mid-stroke at 90°: V(90) is
        // more than half-displacement.
        var vMid = Crank.Volume(90.0) - Crank.ClearanceVolume;
        vMid.Should().BeGreaterThan(Crank.DisplacedVolume / 2.0);
    }

    [Fact]
    public void Pin_offset_shifts_effective_tdc()
    {
        var offset = Crank with { PinOffset = 0.010 };
        // True TDC sits at sinθ = e/(l+a) → ≈ +2.9° for 10 mm offset.
        var v0 = offset.Volume(0.0);
        var vNear = offset.Volume(2.9);
        vNear.Should().BeLessThan(v0, "true TDC moves off θ=0 with wrist-pin offset");
    }

    [Fact]
    public void Volume_derivative_integrates_back_to_displacement()
    {
        var sum = 0.0;
        const int n = 7200;
        for (var i = 0; i < n; i++)
        {
            var theta = 360.0 * i / n;
            sum += Math.Abs(Crank.VolumeDerivative(theta)) * (360.0 / n * Math.PI / 180.0);
        }

        // ∮|dV| over a revolution = 2·Vd.
        sum.Should().BeApproximately(2.0 * Crank.DisplacedVolume, Crank.DisplacedVolume * 1e-3);
    }

    [Fact]
    public void Harmonic_cam_opens_closes_and_peaks_correctly()
    {
        var cam = CamProfile.Harmonic(340.0, 580.0, 0.009);
        cam.MaxLift.Should().BeApproximately(0.009, 1e-5);
        cam.Lift(460.0).Should().BeApproximately(0.009, 1e-4, "peak at the centre");
        cam.Lift(200.0).Should().Be(0.0);
        cam.Lift(340.0 + 720.0).Should().BeApproximately(cam.Lift(340.0), 1e-12, "cycle-periodic");
        cam.OpeningAngle().Should().BeInRange(340.0, 350.0);
        cam.ClosingAngle().Should().BeInRange(570.0, 580.0);
    }

    [Fact]
    public void Cam_csv_import_infers_millimetres()
    {
        const string csv = "# deg, lift mm\n300,0\n330,4.5\n360,9.0\n390,4.5\n420,0\n";
        var cam = CamProfile.FromCsv(new StringReader(csv));
        cam.IsGeneric.Should().BeFalse();
        cam.MaxLift.Should().BeApproximately(0.009, 1e-9);
        cam.Lift(345.0).Should().BeApproximately(0.00675, 1e-6, "linear interpolation");
    }

    [Fact]
    public void Valve_effective_area_is_curtain_at_low_lift_and_throat_at_high_lift()
    {
        var valve = new ValveGeometry { HeadDiameter = 0.032 };
        var lowLift = 0.002;
        valve.EffectiveArea(lowLift).Should().BeApproximately(Math.PI * 0.032 * lowLift, 1e-9);
        valve.EffectiveArea(0.012).Should().Be(valve.ThroatArea, "curtain exceeds throat at high lift");
    }

    [Fact]
    public void Generic_cd_map_falls_with_lift_and_is_flagged_generic()
    {
        ValveCdMap.Generic.IsGeneric.Should().BeTrue();
        var low = ValveCdMap.Generic.Cd(0.05, 1.0);
        var high = ValveCdMap.Generic.Cd(0.30, 1.0);
        low.Should().BeGreaterThan(high);
        high.Should().BeInRange(0.4, 0.7);
    }

    [Fact]
    public void Sealed_cylinder_conserves_mass_exactly_and_energy_via_work()
    {
        // Closed valves: adiabatic motored compression/expansion. Mass exact;
        // after a full cycle the state returns (reversible) and the energy
        // budget closes against ∫p·dV to better than 0.1% of the work swing.
        var gas = new PerfectGasModel(new PerfectGas(1.4, 287.05));
        // Start at BDC (phase 180): the motored cycle then compresses to
        // ~p0·CR^γ ≈ 25 bar at TDC.
        var cylinder = new Cylinder(gas, Crank, 180.0, 1.0e5, 320.0);
        var m0 = cylinder.Mass;
        var e0 = cylinder.Energy;
        var p0 = cylinder.Pressure;

        const double rpm = 6000.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        const int steps = 20_000;
        var dt = 720.0 / 6.0 / rpm / steps; // one 720° cycle
        var angle = 0.0;
        var peakP = 0.0;
        for (var i = 0; i < steps; i++)
        {
            cylinder.Step(dt, angle, omega);
            angle += omega * dt * 180.0 / Math.PI;
            peakP = Math.Max(peakP, cylinder.Pressure);
        }

        cylinder.Mass.Should().Be(m0, "no ports, no mass change");
        peakP.Should().BeGreaterThan(15.0 * p0, "CR 10 adiabatic compression peaks well above 15 bar");

        // Energy budget: ΔU = −∫p·dV exactly (adiabatic).
        (cylinder.Energy - e0 + cylinder.CumulativeWork).Should().BeApproximately(
            0.0, Math.Abs(cylinder.CumulativeWork) * 1e-6 + Math.Abs(e0) * 1e-9);

        // Reversibility: back at TDC after 720°, state ≈ initial.
        cylinder.Pressure.Should().BeApproximately(p0, p0 * 1e-3);
    }

    [Fact]
    public void Quick_estimate_formulas()
    {
        // L = a·Δθ/(12·N): 347 m/s, 90°, 7435 rpm → 0.35 m.
        QuickEstimate.OrganPipeTunedLength(347.0, 90.0, 7435.0).Should().BeApproximately(0.35, 0.001);
        QuickEstimate.OrganPipeTunedRpm(347.0, 90.0, 0.35).Should().BeApproximately(7435.0, 5.0);

        // Helmholtz: A = 2e-3 m², V = 2e-3 m³, L_eff = 0.1 m:
        // f = (a/2π)·√(A/(V·L)) = 55.23·√10 ≈ 174.6 Hz.
        QuickEstimate.HelmholtzFrequency(347.0, 2e-3, 2e-3, 0.1)
            .Should().BeApproximately(347.0 / (2 * Math.PI) * Math.Sqrt(10.0), 1e-6);
    }
}
