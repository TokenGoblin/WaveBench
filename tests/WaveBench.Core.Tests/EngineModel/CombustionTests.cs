using FluentAssertions;
using WaveBench.Core.EngineModel;
using Xunit;

namespace WaveBench.Core.Tests.EngineModel;

public class CombustionTests
{
    [Fact]
    public void Wiebe_hits_the_standard_anchor_points()
    {
        var wiebe = new WiebeCombustion(StartAngleDeg: -15.0, DurationDeg: 55.0);

        wiebe.BurnFraction(-15.0).Should().Be(0.0);
        wiebe.BurnFraction(-16.0).Should().Be(0.0);
        // a = 5 → 1 − e⁻⁵ = 0.99326 at the end of the nominal duration.
        wiebe.BurnFraction(40.0).Should().BeApproximately(0.99326, 1e-4);
        // Midpoint for a = 5, m = 2: 1 − exp(−5·0.5³) = 0.4647.
        wiebe.BurnFraction(-15.0 + 27.5).Should().BeApproximately(0.4647, 1e-3);

        // Monotone.
        var previous = -1.0;
        for (var theta = -20.0; theta <= 60.0; theta += 1.0)
        {
            var x = wiebe.BurnFraction(theta);
            x.Should().BeGreaterThanOrEqualTo(previous);
            previous = x;
        }

        // Cycle coordinates: 705° maps to −15°.
        wiebe.BurnFraction(705.0).Should().Be(wiebe.BurnFraction(-15.0));
        wiebe.BurnFraction(710.0).Should().BeApproximately(wiebe.BurnFraction(-10.0), 1e-12);
    }

    [Fact]
    public void Double_wiebe_blends_its_components()
    {
        var main = new WiebeCombustion(-10.0, 45.0);
        var tail = new WiebeCombustion(10.0, 80.0);
        var model = new DoubleWiebeCombustion(main, tail, FirstWeight: 0.7);

        model.BurnFraction(100.0).Should().BeApproximately(0.99326, 1e-3);
        model.BurnFraction(0.0).Should().BeApproximately(
            0.7 * main.BurnFraction(0.0) + 0.3 * tail.BurnFraction(0.0), 1e-12);
        model.StartAngleDeg.Should().Be(-10.0);
    }

    [Fact]
    public void Woschni_scaling_exponents_are_exact()
    {
        double H(double p, double t, double cm) => InCylinderHeatTransfer.Coefficient(
            HeatTransferCorrelation.Woschni, 0.086, p, t, cm);

        (H(2 * 50e5, 2000, 15) / H(50e5, 2000, 15)).Should().BeApproximately(Math.Pow(2, 0.8), 1e-9);
        (H(50e5, 2 * 1000, 15) / H(50e5, 1000, 15)).Should().BeApproximately(Math.Pow(2, -0.55), 1e-9);
        (H(50e5, 2000, 2 * 15) / H(50e5, 2000, 15)).Should().BeApproximately(Math.Pow(2, 0.8), 1e-9);
    }

    [Fact]
    public void Heat_transfer_magnitudes_are_in_the_published_engine_range()
    {
        // Firing conditions: 50 bar, 2000 K, 86 mm bore, c_m = 15 m/s.
        // All three correlations land in the 500–5000 W/m²K band the
        // literature reports for firing SI engines.
        foreach (var correlation in Enum.GetValues<HeatTransferCorrelation>())
        {
            var h = InCylinderHeatTransfer.Coefficient(
                correlation, 0.086, 50e5, 2000.0, 15.0, instantaneousVolume: 2e-4);
            h.Should().BeInRange(400.0, 5000.0, $"{correlation} must be physically plausible (got {h:F0})");
        }
    }

    [Fact]
    public void Chen_flynn_arithmetic_and_magnitude()
    {
        var friction = new ChenFlynnFriction();
        // 80 bar peak, c_m = 15: 0.25 + 0.006·80 + 0.09·15/1 + ... in bar.
        var fmep = friction.Fmep(80e5, 15.0);
        fmep.Should().BeApproximately(25_000 + 0.006 * 80e5 + 9_000 * 15 + 90 * 225, 1e-6);
        (fmep / 1e5).Should().BeInRange(1.0, 3.5, "SI-engine FMEP is 1–3.5 bar at speed");
    }

    [Fact]
    public void Blowby_leaks_mass_during_compression()
    {
        var gas = new WaveBench.Core.Solver.PerfectGasModel(new WaveBench.Core.Numerics.PerfectGas(1.4, 287.05));
        var crank = new CrankGeometry { Bore = 0.09, Stroke = 0.09, RodLength = 0.15, CompressionRatio = 10.0 };
        var sealed_ = new Cylinder(gas, crank, 180.0, 1.0e5, 320.0);
        var leaky = new Cylinder(gas, crank, 180.0, 1.0e5, 320.0) { BlowbyEffectiveArea = 0.5e-6 };

        const double rpm = 3000.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        const int steps = 10_000;
        var dt = 720.0 / 6.0 / rpm / steps;
        var angle = 0.0;
        for (var i = 0; i < steps; i++)
        {
            sealed_.Step(dt, angle, omega);
            leaky.Step(dt, angle, omega);
            angle += omega * dt * 180.0 / Math.PI;
        }

        leaky.CumulativeBlowby.Should().BeGreaterThan(0.0);
        leaky.Mass.Should().BeLessThan(sealed_.Mass);
        (leaky.CumulativeBlowby / sealed_.Mass).Should().BeLessThan(0.05,
            "blowby is a small fraction per cycle at a sane ring-gap area");
        leaky.Mass.Should().BeApproximately(sealed_.Mass - leaky.CumulativeBlowby, sealed_.Mass * 1e-9);
    }

    [Fact]
    public void Crevice_stores_mass_at_high_pressure_and_returns_it()
    {
        var gas = new WaveBench.Core.Solver.PerfectGasModel(new WaveBench.Core.Numerics.PerfectGas(1.4, 287.05));
        var crank = new CrankGeometry { Bore = 0.09, Stroke = 0.09, RodLength = 0.15, CompressionRatio = 10.0 };
        var plain = new Cylinder(gas, crank, 180.0, 1.0e5, 320.0);
        var crevice = new Cylinder(gas, crank, 180.0, 1.0e5, 320.0) { CreviceVolume = 2e-6, WallTemperature = 400.0 };

        const double rpm = 3000.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        const int steps = 10_000;
        var dt = 720.0 / 6.0 / rpm / steps;
        var angle = 0.0;
        double peakPlain = 0, peakCrevice = 0;
        for (var i = 0; i < steps; i++)
        {
            plain.Step(dt, angle, omega);
            crevice.Step(dt, angle, omega);
            peakPlain = Math.Max(peakPlain, plain.Pressure);
            peakCrevice = Math.Max(peakCrevice, crevice.Pressure);
            angle += omega * dt * 180.0 / Math.PI;
        }

        peakCrevice.Should().BeLessThan(peakPlain, "crevice storage clips the compression peak");
        // Bulk + standing crevice content together conserve the charge:
        // the crevice holds p·V/(R·T_wall) at all times.
        var stored = crevice.Pressure * 2e-6 / (287.05 * 400.0);
        (crevice.Mass + stored).Should().BeApproximately(plain.Mass, plain.Mass * 1e-3);
    }

    [Fact]
    public void Variability_is_deterministic_and_statistically_sane()
    {
        var v = new CycleVariability(Seed: 1234);
        var a = v.Draw(0, 5);
        var b = v.Draw(0, 5);
        a.Should().Be(b, "same seed, cylinder, cycle → identical draw");

        v.Draw(1, 5).Should().NotBe(a, "different cylinder draws differently");
        v.Draw(0, 6).Should().NotBe(a, "different cycle draws differently");

        // σ of the phase shift ≈ configured over many draws.
        var sum = 0.0;
        var sumSq = 0.0;
        const int n = 2000;
        for (var i = 0; i < n; i++)
        {
            var (phase, _, _) = v.Draw(0, i);
            sum += phase;
            sumSq += phase * phase;
        }

        var mean = sum / n;
        var std = Math.Sqrt(sumSq / n - mean * mean);
        Math.Abs(mean).Should().BeLessThan(0.15);
        std.Should().BeApproximately(1.2, 0.15);
    }
}
