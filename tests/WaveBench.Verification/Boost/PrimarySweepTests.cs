using FluentAssertions;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 13 gate: <i>"pulse-energy-delivery and manifold-volume-ratio metrics
/// respond correctly to a primary-diameter sweep."</i>
///
/// Plan §4.6.1 states the trade the sweep must show: <i>"too small chokes and
/// raises pumping loss; too large dissipates the pulse into manifold volume."</i>
/// So "correctly" means three things at once, and all three are asserted —
/// manifold volume ratio climbing with diameter, pulse energy delivery falling
/// as the pulse is smeared into that volume, and breathing falling off at the
/// small end where the primary starts to choke.
///
/// This runs a real four-cylinder engine with real blowdown pulses into a real
/// turbine boundary. Nothing about the turbine is prescribed: the expansion
/// ratio, the flow and the work all come out of what the manifold delivered.
/// </summary>
public class PrimarySweepTests(ITestOutputHelper output)
{
    // High in the rev range on purpose. The choking half of the trade only
    // appears where there is enough flow to choke on: at 4500 rpm this engine
    // draws 0.09 kg/s and a Ø26 primary is not a restriction at all, so the
    // sweep reads as "narrower is always better" — which is true right up until
    // it very much is not.
    private const double Rpm = 6800.0;

    private static readonly double[] Diameters = [20.0, 24.0, 28.0, 32.0, 38.0, 44.0, 50.0];

    [Fact]
    public void Gate_a_primary_diameter_sweep_moves_both_metrics_the_way_the_physics_says()
    {
        var points = new List<PrimarySweepPoint>();

        foreach (var diameter in Diameters)
        {
            var rig = new TurbochargedEngineRig(diameter, Rpm);
            points.Add(rig.Run());
        }

        output.WriteLine(
            "  Ø mm   volume ratio   pulse delivery   peak/mean p   turbine kW      VE    IMEP bar   BSR");
        foreach (var p in points)
        {
            output.WriteLine(
                $"{p.PrimaryDiameterMm,6:F0}   {p.Metrics.ManifoldVolumeRatio,12:F2}   "
                + $"{p.Metrics.PulseEnergyDelivery,14:P1}   {p.Metrics.PressureRatioAmplitude,11:F3}   "
                + $"{p.MeanTurbinePowerW / 1000,10:F2}   {p.VolumetricEfficiency,5:F3}   "
                + $"{p.ImepPa / 1e5,8:F2}   {p.Metrics.MeanBladeSpeedRatio,5:F2}");
        }

        // 1. Manifold volume ratio is geometry and must rise monotonically. It
        //    is the axis of Watson & Janota's pulse-versus-constant-pressure
        //    distinction, so it has to be reported even though it is not solved.
        points.Select(p => p.Metrics.ManifoldVolumeRatio).Should().BeInAscendingOrder(
            "a wider primary is more manifold volume per exhaust event");

        // 2. Pulse energy delivery has an INTERIOR maximum, and that is the
        //    whole point of the sweep. Widen past it and the blowdown is
        //    dissipated into manifold volume until the turbine is running at
        //    constant pressure (the metric tends to 1). Narrow past it and the
        //    primary chokes, mean manifold pressure rises, and the pulse
        //    disappears into the raised mean instead.
        var bestDelivery = points.MaxBy(p => p.Metrics.PulseEnergyDelivery)!;
        bestDelivery.PrimaryDiameterMm.Should().NotBe(Diameters[0]);
        bestDelivery.PrimaryDiameterMm.Should().NotBe(Diameters[^1]);

        points.Should().AllSatisfy(p => p.Metrics.PulseEnergyDelivery.Should().BeGreaterThan(0.99,
            "a pulsating feed is never worth less than the same flow arriving steadily"));

        points[^1].Metrics.PulseEnergyDelivery.Should().BeLessThan(1.02,
            "the widest primary must have flattened the pulse into constant-pressure operation");

        // 3. The pulse the turbine sees, measured independently of the delivery
        //    metric, flattens the same way at the wide end.
        points[^1].Metrics.PressureRatioAmplitude.Should().BeLessThan(
            bestDelivery.Metrics.PressureRatioAmplitude,
            "peak-over-mean turbine inlet pressure is the pulse, and volume flattens it");

        // 4. The choking side of the trade. Without this the sweep would read as
        //    "narrower is always better", which is how someone ends up with a
        //    header that makes boost and no power.
        var best = points.MaxBy(p => p.VolumetricEfficiency)!;
        best.PrimaryDiameterMm.Should().NotBe(Diameters[0],
            "the narrowest primary must not also be the best-breathing one, or there is no trade to show");

        points[0].VolumetricEfficiency.Should().BeLessThan(best.VolumetricEfficiency * 0.98,
            "the narrowest primary must cost real volumetric efficiency, not a rounding error");

        points[0].ImepPa.Should().BeLessThan(best.ImepPa,
            "and the choked primary must cost load, which is where the pumping penalty shows up");

        // 5. Turbine power alone would pick the choked header, because a high
        //    mean back-pressure feeds the turbine well while strangling the
        //    engine. Reporting it beside VE is what stops the optimiser doing
        //    exactly that.
        points.Select(p => p.MeanTurbinePowerW).Should().BeInDescendingOrder(
            "a narrower primary always gives the turbine more, whatever it costs the engine");

        output.WriteLine("");
        output.WriteLine(
            $"best breathing at Ø{best.PrimaryDiameterMm:F0} mm (VE {best.VolumetricEfficiency:F3}); "
            + $"best pulse delivery at Ø{points.OrderByDescending(p => p.Metrics.PulseEnergyDelivery).First().PrimaryDiameterMm:F0} mm; "
            + $"most turbine power at Ø{points.OrderByDescending(p => p.MeanTurbinePowerW).First().PrimaryDiameterMm:F0} mm");
    }

    [Fact]
    public void The_manifold_volume_ratio_is_the_geometry_it_claims_to_be()
    {
        // The metric is a ratio of two volumes and must be checkable by hand,
        // or it is just a number that moves in the right direction.
        var rig = new TurbochargedEngineRig(38.0, Rpm);
        var point = rig.Run(warmupCycles: 1);

        var swept = rig.Engine.Cylinders[0].Geometry.DisplacedVolume;
        var manifold = point.Metrics.ManifoldVolumeRatio * swept;

        output.WriteLine(
            $"one cylinder sweeps {swept * 1e6:F1} cm³; the manifold holds {manifold * 1e6:F1} cm³; "
            + $"ratio {point.Metrics.ManifoldVolumeRatio:F2}");

        // Four 300 mm Ø38 primaries, a 120 mm Ø50 collector and an 80 mm Ø52
        // tail come to about 1.7 litres against a 0.5 litre cylinder.
        point.Metrics.ManifoldVolumeRatio.Should().BeInRange(2.0, 5.0,
            "a 4-1 header on a 500 cm³ cylinder is a few cylinder volumes of pipe");
    }

    [Fact]
    public void The_volute_resolved_model_reports_a_different_answer_on_the_same_engine()
    {
        // Plan §4.3: "Report the difference on every run. Where it is large, the
        // manifold volume is doing something the user should know about."
        var quasi = new TurbochargedEngineRig(34.0, Rpm).Run();
        var resolved = new TurbochargedEngineRig(34.0, Rpm, TurbineModelKind.VoluteResolved).Run();

        var difference = Math.Abs(resolved.MeanTurbinePowerW - quasi.MeanTurbinePowerW)
                         / Math.Max(quasi.MeanTurbinePowerW, 1.0);

        output.WriteLine(
            $"quasi-steady    : {quasi.MeanTurbinePowerW / 1000:F2} kW, delivery {quasi.Metrics.PulseEnergyDelivery:P1}, "
            + $"volume ratio {quasi.Metrics.ManifoldVolumeRatio:F2}");
        output.WriteLine(
            $"volute-resolved : {resolved.MeanTurbinePowerW / 1000:F2} kW, delivery {resolved.Metrics.PulseEnergyDelivery:P1}, "
            + $"volume ratio {resolved.Metrics.ManifoldVolumeRatio:F2}");
        output.WriteLine($"difference in mean turbine power: {difference:P1}");

        resolved.Metrics.ManifoldVolumeRatio.Should().BeGreaterThan(quasi.Metrics.ManifoldVolumeRatio,
            "resolving the volute adds its volume to the manifold, which the quasi-steady model does not count");

        difference.Should().BeGreaterThan(0.01,
            "if the two models agreed exactly there would be no reason to have both");
    }
}
