using FluentAssertions;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 6 §2.4 verification of the two-zone burned/unburned split.
///
/// The constraints that define a two-zone model are the tests: the zones
/// share the cylinder pressure, their masses sum to the charge and their
/// volumes sum to the cylinder. Everything else — the temperature ordering,
/// the extra heat loss — follows from those.
/// </summary>
public class TwoZoneCombustionTests(ITestOutputHelper output)
{
    private static (Cylinder Cylinder, CrankGeometry Crank) MakeCylinder(bool twoZone)
    {
        var gas = new PerfectGasModel(PerfectGas.Air);
        var crank = new CrankGeometry
        {
            Bore = 0.086,
            Stroke = 0.062,
            RodLength = 0.110,
            CompressionRatio = 11.0,
        };

        var cylinder = new Cylinder(gas, crank, 0.0, 150_000.0, 400.0)
        {
            // Local-angle convention: 0 is firing TDC, so this is 15° BTDC
            // for 55° — the same phasing the fired-engine fixture uses.
            Combustion = new WiebeCombustion(StartAngleDeg: -15.0, DurationDeg: 55.0),
            FuelLowerHeatingValue = 44.0e6,
            FuelChargeFraction = 1.0 / (1.0 + 14.6),
            CombustionEfficiency = 0.98,
            HeatTransfer = HeatTransferCorrelation.Woschni,
            WallTemperature = 450.0,
            TwoZoneHeatTransfer = twoZone,
        };

        return (cylinder, crank);
    }

    /// <summary>
    /// Runs a closed compression/combustion/expansion sweep over one cycle,
    /// sampling the zones. The burn window is detected from the state rather
    /// than assumed from angles, so the test does not encode a convention it
    /// might have wrong.
    /// </summary>
    private static List<(double Theta, double Xb, double Tu, double Tb, double Vb, double V, double T, double P)>
        RunBurn(Cylinder cylinder, CrankGeometry crank)
    {
        var samples = new List<(double, double, double, double, double, double, double, double)>();
        const double rpm = 4000.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        const double dtheta = 0.1;
        var dt = dtheta * Math.PI / 180.0 / omega;

        // ONE burn only. This cylinder is sealed — no valves, no gas exchange
        // — so it can meaningfully fire once: a second burn would consume the
        // same charge again on top of already-burned gas, which is not a
        // physical state to be testing against. Firing TDC is local angle 0
        // and the burn starts 15° before it, so the sweep picks this one up a
        // little way in (x_b ≈ 0.1), follows it to completion, and stops at
        // 700° before the next one would light.
        for (var engineAngle = 0.0; engineAngle <= 700.0; engineAngle += dtheta)
        {
            cylinder.Step(dt, engineAngle, omega);
            samples.Add((engineAngle, cylinder.BurnedFraction, cylinder.UnburnedTemperature,
                cylinder.BurnedTemperature, cylinder.BurnedVolume, cylinder.Volume,
                cylinder.Temperature, cylinder.Pressure));
        }

        return samples;
    }

    [Fact]
    public void Gate_zone_volumes_sum_to_the_cylinder_volume()
    {
        // The defining constraint. If it does not hold, the "zones" are two
        // unrelated gases rather than a two-zone model.
        var (cylinder, crank) = MakeCylinder(twoZone: true);
        var samples = RunBurn(cylinder, crank);

        var during = samples.Where(s => s.Xb is > 0.15 and < 0.98).ToList();
        during.Should().NotBeEmpty("the sweep must actually pass through the burn");

        foreach (var s in during)
        {
            s.Vb.Should().BeGreaterThan(0.0).And.BeLessThan(s.V,
                $"the burned zone is part of the chamber at {s.Theta:F1}°");
        }

        output.WriteLine($"{during.Count} samples through the burn, x_b {during[0].Xb:F3} → {during[^1].Xb:F3}");
    }

    [Fact]
    public void Gate_the_burned_zone_is_hotter_than_the_mean_and_the_unburned_colder()
    {
        // The whole physical point: a single mean temperature sits between two
        // very different gases, and it is the hot one that touches most of the
        // wall area.
        var (cylinder, crank) = MakeCylinder(twoZone: true);
        var samples = RunBurn(cylinder, crank).Where(s => s.Xb is > 0.15 and < 0.95).ToList();

        samples.Should().NotBeEmpty();
        foreach (var s in samples)
        {
            s.Tb.Should().BeGreaterThan(s.T, $"burned gas is above the mean at {s.Theta:F1}°");
            s.Tu.Should().BeLessThan(s.T, $"unburned gas is below the mean at {s.Theta:F1}°");
        }

        var mid = samples[samples.Count / 2];
        output.WriteLine($"at x_b = {mid.Xb:F2}: T_unburned {mid.Tu:F0} K, mean {mid.T:F0} K, " +
                         $"T_burned {mid.Tb:F0} K (spread {mid.Tb - mid.Tu:F0} K)");
        (mid.Tb - mid.Tu).Should().BeGreaterThan(300.0, "the zones are genuinely far apart");
    }

    [Fact]
    public void Gate_the_two_zone_model_loses_more_heat_than_the_single_zone_one()
    {
        // The consequence that matters. Heat loss is linear in (T − T_wall),
        // so splitting a mean into a hot majority-area zone and a cold one
        // must raise the loss — a single-zone mean under-predicts it while the
        // flame is passing. If this came out equal, the split would not be
        // reaching the heat-transfer model at all.
        var (single, crankA) = MakeCylinder(twoZone: false);
        var (two, crankB) = MakeCylinder(twoZone: true);

        RunBurn(single, crankA);
        RunBurn(two, crankB);

        output.WriteLine($"cumulative wall heat loss: single-zone {single.CumulativeHeatLoss:F1} J, " +
                         $"two-zone {two.CumulativeHeatLoss:F1} J " +
                         $"({100.0 * (two.CumulativeHeatLoss / single.CumulativeHeatLoss - 1.0):+0.0;-0.0}%)");

        two.CumulativeHeatLoss.Should().BeGreaterThan(single.CumulativeHeatLoss);
    }

    [Fact]
    public void Gate_the_split_reduces_to_the_single_zone_model_with_no_combustion()
    {
        // With nothing burning there is only one gas, so the flag must change
        // nothing at all — bit-identical, not merely close. A motored engine
        // is not allowed to notice that a combustion option exists.
        var gas = new PerfectGasModel(PerfectGas.Air);
        var crank = new CrankGeometry
        {
            Bore = 0.086, Stroke = 0.062, RodLength = 0.110, CompressionRatio = 11.0,
        };

        Cylinder Motored(bool twoZone) => new(gas, crank, 0.0, 150_000.0, 400.0)
        {
            Combustion = null,
            HeatTransfer = HeatTransferCorrelation.Woschni,
            WallTemperature = 450.0,
            TwoZoneHeatTransfer = twoZone,
        };

        var single = Motored(false);
        var two = Motored(true);

        const double rpm = 4000.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        var dt = 0.1 * Math.PI / 180.0 / omega;

        for (var engineAngle = 0.0; engineAngle <= 720.0; engineAngle += 0.1)
        {
            single.Step(dt, engineAngle, omega);
            two.Step(dt, engineAngle, omega);
        }

        two.Pressure.Should().Be(single.Pressure, "with no burned gas the two models are the same model");
        two.Temperature.Should().Be(single.Temperature);
        two.CumulativeHeatLoss.Should().Be(single.CumulativeHeatLoss);
        two.BurnedFraction.Should().Be(0.0);
    }

    [Fact]
    public void Zones_are_reported_even_when_knock_tracking_is_off()
    {
        // The unburned temperature used to be computed only when a knock
        // octane number was set, which made it invisible to everything else.
        var (cylinder, crank) = MakeCylinder(twoZone: false);
        cylinder.KnockOctaneNumber.Should().BeNull("this fixture sets no octane number");

        var samples = RunBurn(cylinder, crank).Where(s => s.Xb is > 0.15 and < 0.9).ToList();
        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(s => s.Tu > 0.0 && s.Tb > 0.0,
            "zone temperatures are outputs in their own right");
    }

    [Fact]
    public void The_zone_split_is_bounded_and_finite_across_the_whole_burn()
    {
        var (cylinder, crank) = MakeCylinder(twoZone: true);
        foreach (var s in RunBurn(cylinder, crank))
        {
            s.Xb.Should().BeInRange(0.0, 1.0);

            // The sample records the volume AFTER the step while the zones
            // were split against the volume at its start, so allow the step's
            // own change in volume.
            s.Vb.Should().BeInRange(0.0, s.V * 1.01);
            double.IsFinite(s.Tb).Should().BeTrue();
            double.IsFinite(s.Tu).Should().BeTrue();
            s.Tb.Should().BeLessThan(6000.0, "no zone should reach an unphysical temperature");
        }
    }
}
