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
    /// <summary>
    /// One sampled step. <paramref name="VolumeBefore"/> and
    /// <paramref name="VolumeAfter"/> bracket the step, because the zones are
    /// split against the volume at its START while every public property is
    /// read after it — an undeclared skew here would quietly weaken the
    /// volume-sum assertion, which is the whole point of that test.
    /// </summary>
    private readonly record struct Sample(
        double Theta,
        double Xb,
        bool Resolved,
        double Tu,
        double Tb,
        double Vb,
        double Vu,
        double VolumeBefore,
        double VolumeAfter,
        double T,
        double P);

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
            // for 55° — the same phasing the fired-engine fixture uses. Note
            // this window STRADDLES local 720°→0°, which is exactly the case
            // that used to break the burn bookkeeping.
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
    /// Runs compression through combustion and expansion, sampling the zones.
    /// Starts at BDC on compression so the burn is approached cleanly, and
    /// covers one full cycle from there.
    /// </summary>
    private static List<Sample> RunBurn(Cylinder cylinder, CrankGeometry crank)
    {
        var samples = new List<Sample>();
        const double rpm = 4000.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        const double dtheta = 0.1;
        var dt = dtheta * Math.PI / 180.0 / omega;

        for (var engineAngle = 540.0; engineAngle <= 540.0 + 700.0; engineAngle += dtheta)
        {
            var local = engineAngle % 720.0;
            var before = cylinder.Volume;
            cylinder.Step(dt, local, omega);
            samples.Add(new Sample(
                local, cylinder.BurnedFraction, cylinder.ZonesResolved,
                cylinder.UnburnedTemperature, cylinder.BurnedTemperature,
                cylinder.BurnedVolume, cylinder.UnburnedVolume,
                before, cylinder.Volume, cylinder.Temperature, cylinder.Pressure));
        }

        return samples;
    }

    [Fact]
    public void Gate_zone_volumes_sum_to_the_cylinder_volume()
    {
        // The defining constraint, and now actually asserted: an earlier
        // version of this test only checked 0 < V_b < V, which the docs
        // nonetheless described as asserting the sum. Both zone volumes are
        // exposed so the sum can be checked rather than assumed.
        var (cylinder, crank) = MakeCylinder(twoZone: true);
        var resolved = RunBurn(cylinder, crank).Where(s => s.Resolved).ToList();

        resolved.Should().NotBeEmpty("the sweep must actually resolve the zones somewhere");

        foreach (var s in resolved)
        {
            var sum = s.Vb + s.Vu;
            var low = Math.Min(s.VolumeBefore, s.VolumeAfter);
            var high = Math.Max(s.VolumeBefore, s.VolumeAfter);
            sum.Should().BeInRange(low * (1.0 - 1e-9), high * (1.0 + 1e-9),
                $"V_b + V_u must be the cylinder volume at {s.Theta:F1}°");

            s.Vb.Should().BeGreaterThan(0.0);
            s.Vu.Should().BeGreaterThan(0.0);
        }

        output.WriteLine($"{resolved.Count} resolved samples, x_b {resolved[0].Xb:F3} → {resolved[^1].Xb:F3}");
    }

    [Fact]
    public void Gate_the_burned_zone_is_hotter_than_the_mean_and_the_unburned_colder()
    {
        // Not tautological: UpdateZones no longer clamps the burned
        // temperature up to the mean, it treats a burned zone that comes out
        // at or below the mean as an invalid split and falls back. So on the
        // resolved samples this is a genuine physical assertion, and the
        // comparison between the two ZONE temperatures carries no step skew
        // at all since both come from the same instant.
        var (cylinder, crank) = MakeCylinder(twoZone: true);
        var samples = RunBurn(cylinder, crank).Where(s => s.Resolved).ToList();

        samples.Should().NotBeEmpty();
        foreach (var s in samples)
        {
            s.Tb.Should().BeGreaterThan(s.Tu, $"burned gas is above unburned at {s.Theta:F1}°");
            s.Tb.Should().BeGreaterThan(s.T * 0.99, $"and above the mean at {s.Theta:F1}°");
            s.Tu.Should().BeLessThan(s.T * 1.01, $"unburned gas is below the mean at {s.Theta:F1}°");
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
    public void Gate_the_zones_close_when_the_burn_window_does()
    {
        // The Wiebe asymptote is 0.9933, never 1, so a "burned fraction
        // reached 1" test never fires and the split used to persist through
        // expansion and the whole exhaust stroke — carrying a fictitious 0.67%
        // unburned pocket that cooled isentropically to below wall
        // temperature and then fed heat back INTO the charge.
        var (cylinder, crank) = MakeCylinder(twoZone: true);
        var samples = RunBurn(cylinder, crank);

        // The burn runs from −15° to +40° about firing TDC.
        static double BurnAngle(double local) => local > 360.0 ? local - 720.0 : local;

        var afterBurn = samples.Where(s => BurnAngle(s.Theta) is > 60.0 and < 300.0).ToList();
        afterBurn.Should().NotBeEmpty();
        afterBurn.Should().OnlyContain(s => !s.Resolved,
            "once the burn window has closed there is only burned gas");
        afterBurn.Should().OnlyContain(s => s.Xb >= 1.0,
            "and the Wiebe tail is not a real unburned pocket");

        var beforeBurn = samples.Where(s => BurnAngle(s.Theta) < -30.0).ToList();
        beforeBurn.Should().NotBeEmpty();
        beforeBurn.Should().OnlyContain(s => !s.Resolved && s.Xb == 0.0,
            "and nothing is burned before the spark");
    }

    [Fact]
    public void Gate_no_phantom_burned_zone_appears_before_anything_burns()
    {
        // The zone split is gated on the start-of-combustion reference being
        // recorded, not on a per-cycle energy budget that stays non-zero for
        // the remainder of the cycle. Before any fuel burns there is no
        // burned zone to report.
        var (cylinder, crank) = MakeCylinder(twoZone: true);

        const double rpm = 4000.0;
        var omega = rpm * 2.0 * Math.PI / 60.0;
        var dt = 0.1 * Math.PI / 180.0 / omega;

        for (var engineAngle = 540.0; engineAngle <= 540.0 + 700.0; engineAngle += 0.1)
        {
            cylinder.Step(dt, engineAngle % 720.0, omega);
            if (cylinder.CumulativeFuelBurned <= 0.0)
            {
                cylinder.BurnedTemperature.Should().Be(0.0, "no fuel has burned yet");
                cylinder.BurnedFraction.Should().Be(0.0);
                cylinder.ZonesResolved.Should().BeFalse();
            }
        }
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

        var samples = RunBurn(cylinder, crank).Where(s => s.Resolved).ToList();
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
            s.Vb.Should().BeInRange(0.0, Math.Max(s.VolumeBefore, s.VolumeAfter) * (1.0 + 1e-9));
            s.Vu.Should().BeInRange(0.0, Math.Max(s.VolumeBefore, s.VolumeAfter) * (1.0 + 1e-9));
            double.IsFinite(s.Tb).Should().BeTrue();
            double.IsFinite(s.Tu).Should().BeTrue();
            s.Tb.Should().BeLessThan(6000.0, "no zone should reach an unphysical temperature");
        }
    }
}
