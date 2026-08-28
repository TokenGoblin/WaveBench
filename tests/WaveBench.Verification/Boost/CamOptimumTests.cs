using FluentAssertions;
using WaveBench.Boost.Engine;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 14 gate, first half: <i>"the turbo-vs-NA cam optimum validation case
/// passes; the NA and turbo cam optima diverge and the reason is derivable from
/// the scavenging-pressure output."</i>
///
/// Validation case 17. Plan §4.0 states the claim being tested as bluntly as it
/// can be put: <i>"If the optimiser recommends the same 700 mm equal-length
/// primaries for a turbo build that it recommends NA, it is wrong."</i> The cam
/// is the same argument in a different component.
/// </summary>
public class CamOptimumTests(ITestOutputHelper output)
{
    // Low in the rev range on purpose. Scavenging is a low-speed tool — it is
    // where boost is scarce, where the turbine most needs the extra flow, and
    // where an NA engine's reversion is worst. A cam comparison at peak power
    // would be asking the question where it matters least.
    private const double Rpm = 2200.0;

    private static readonly double[] LobeCentreAngles = [115.0, 110.0, 105.0, 100.0, 95.0, 90.0];

    private static IReadOnlyList<CamPoint> Sweep(
        bool turbocharged, InjectionSystem injection, double shortCircuit = 0.0)
    {
        var points = new List<CamPoint>();
        foreach (var lca in LobeCentreAngles)
        {
            var rig = new CamSweepRig(lca, Rpm, turbocharged) { ShortCircuitFraction = shortCircuit };
            points.Add(rig.Run(injection));
        }

        return points;
    }

    /// <summary>
    /// Points that actually have an overlap window. At the widest lobe centres
    /// the two valves never leave their seats together, so the scavenging
    /// pressure ratio is not small — it is undefined, and reporting it as NaN
    /// is right. Those points are excluded from ratio assertions rather than
    /// having a number invented for them.
    /// </summary>
    private static IEnumerable<CamPoint> WithOverlap(IEnumerable<CamPoint> points) =>
        points.Where(p => double.IsFinite(p.MeanScavengingPressureRatio));

    private void Report(string label, IReadOnlyList<CamPoint> points)
    {
        output.WriteLine("");
        output.WriteLine(label);
        output.WriteLine("   LCA   overlap   p_int/p_exh   torque N·m   net N·m      VE   blow-through");
        foreach (var p in points)
        {
            output.WriteLine(
                $"{p.LobeCentreAngleDeg,6:F0}   {p.OverlapDeg,7:F0}   {p.MeanScavengingPressureRatio,11:F3}   "
                + $"{p.TorqueNm,10:F1}   {p.NetTorqueNm,7:F1}   {p.VolumetricEfficiency,5:F3}   "
                + $"{p.BlowThroughFraction,12:P1}");
        }
    }

    [Fact]
    public void Gate_the_naturally_aspirated_and_boosted_cam_optima_diverge()
    {
        var na = Sweep(turbocharged: false, InjectionSystem.Direct);
        var turbo = Sweep(turbocharged: true, InjectionSystem.Direct);

        Report("Naturally aspirated", na);
        Report("Boosted, 2.0 bar plenum", turbo);

        var naBest = na.MaxBy(p => p.TorqueNm)!;
        var turboBest = turbo.MaxBy(p => p.TorqueNm)!;

        output.WriteLine("");
        output.WriteLine(
            $"NA optimum at LCA {naBest.LobeCentreAngleDeg:F0}° ({naBest.OverlapDeg:F0}° overlap), "
            + $"boosted optimum at LCA {turboBest.LobeCentreAngleDeg:F0}° ({turboBest.OverlapDeg:F0}° overlap)");

        turboBest.LobeCentreAngleDeg.Should().BeLessThan(naBest.LobeCentreAngleDeg,
            "a boosted engine wants a tighter lobe centre — more overlap — than the same engine NA");

        // "Meaningfully different", not different by one step of the sweep.
        (naBest.LobeCentreAngleDeg - turboBest.LobeCentreAngleDeg).Should().BeGreaterThanOrEqualTo(10.0,
            "the two optima must be far enough apart that the recommendation actually changes");
    }

    [Fact]
    public void Gate_the_scavenging_pressure_output_explains_the_divergence()
    {
        // The gate asks that the reason be DERIVABLE from the scavenging-pressure
        // output, not merely that the optima differ. So: the boosted engine must
        // show pressure across the engine in the scavenging direction and the NA
        // engine must not, at the same cam.
        var na = Sweep(turbocharged: false, InjectionSystem.Direct);
        var turbo = Sweep(turbocharged: true, InjectionSystem.Direct);

        output.WriteLine("   LCA   NA p_int/p_exh   boosted p_int/p_exh   NA positive°   boosted positive°");
        for (var i = 0; i < na.Count; i++)
        {
            var naPositive = na[i].Scavenging.Average(s => s.PositiveScavengingDeg);
            var turboPositive = turbo[i].Scavenging.Average(s => s.PositiveScavengingDeg);

            output.WriteLine(
                $"{na[i].LobeCentreAngleDeg,6:F0}   {na[i].MeanScavengingPressureRatio,13:F3}   "
                + $"{turbo[i].MeanScavengingPressureRatio,19:F3}   {naPositive,12:F1}   {turboPositive,17:F1}");
        }

        // Every boosted point must sit above every NA point on scavenging
        // pressure. That is the mechanism, and it is a property of the pressures
        // and not of the cam.
        WithOverlap(turbo).Min(p => p.MeanScavengingPressureRatio).Should().BeGreaterThan(
            WithOverlap(na).Max(p => p.MeanScavengingPressureRatio),
            "boost puts the intake above the exhaust across the whole cam sweep, and that is the reason "
            + "overlap changes from a liability into an asset");

        WithOverlap(turbo).Should().AllSatisfy(p => p.MeanScavengingPressureRatio.Should().BeGreaterThan(1.0,
            "positive scavenging pressure is what makes overlap useful"));

        WithOverlap(na).Should().AllSatisfy(p => p.MeanScavengingPressureRatio.Should().BeLessThan(1.0,
            "an NA engine's exhaust sits above its intake during overlap, which is why overlap costs it"));

        // And the window itself opens up as the lobe centres tighten, which is
        // what "more overlap" has to mean if the metric is measuring anything.
        WithOverlap(turbo).OrderByDescending(p => p.LobeCentreAngleDeg)
            .Select(p => p.Scavenging.Average(s => s.PositiveScavengingDeg))
            .Should().BeInAscendingOrder("a tighter lobe centre must open a longer positive-pressure window");

        // And the consequence: opening the overlap up moves the two engines in
        // opposite directions.
        var naWide = na.Single(p => p.LobeCentreAngleDeg == 90.0);
        var naTight = na.Single(p => p.LobeCentreAngleDeg == 115.0);
        var turboWide = turbo.Single(p => p.LobeCentreAngleDeg == 90.0);
        var turboTight = turbo.Single(p => p.LobeCentreAngleDeg == 115.0);

        output.WriteLine("");
        output.WriteLine(
            $"going from 0° to 50° of overlap: NA {naTight.TorqueNm:F1} → {naWide.TorqueNm:F1} N·m, "
            + $"boosted {turboTight.TorqueNm:F1} → {turboWide.TorqueNm:F1} N·m");

        (naWide.TorqueNm - naTight.TorqueNm).Should().BeLessThan(
            turboWide.TorqueNm - turboTight.TorqueNm,
            "the same increase in overlap must be worth more to the boosted engine than to the NA one");
    }

    [Fact]
    public void Gate_blow_through_appears_only_where_the_pressure_allows_it_and_grows_with_overlap()
    {
        var turbo = Sweep(turbocharged: true, InjectionSystem.Direct);
        var na = Sweep(turbocharged: false, InjectionSystem.Direct);

        Report("Boosted — blow-through against overlap", turbo);

        // Blow-through needs both a pressure difference and a window for it to
        // act through. The boosted engine has the pressure, so opening the
        // window must produce more of it.
        turbo.OrderByDescending(p => p.LobeCentreAngleDeg).Select(p => p.BlowThroughFraction)
            .Should().BeInAscendingOrder("more overlap under positive pressure means more blow-through");

        turbo.Max(p => p.BlowThroughFraction).Should().BeGreaterThan(0.005,
            "a boosted engine with 50° of overlap must blow measurable charge straight through");

        na.Max(p => p.BlowThroughFraction).Should().BeLessThan(
            turbo.Max(p => p.BlowThroughFraction),
            "an NA engine cannot blow through what it has no pressure to push");

        // Trapping efficiency is the complement and must agree with it.
        foreach (var point in turbo)
        {
            foreach (var cylinder in point.Scavenging)
            {
                (cylinder.BlowThroughFraction + cylinder.TrappingEfficiency)
                    .Should().BeApproximately(1.0, 1e-9);
            }
        }
    }

    [Fact]
    public void The_blow_through_answer_is_a_bracket_and_the_tool_reports_it_as_one()
    {
        // The single-zone cylinder mixes perfectly, which is the LOWER bound on
        // blow-through: on this engine it reports under a percent where a
        // measured DI turbo at the same overlap and scavenging pressure shows
        // several. Perfect displacement is the upper bound. Where a real engine
        // sits between them is port and chamber geometry a 1D solver cannot
        // resolve — so the tool brackets it instead of picking a number.
        const double lca = 90.0;

        var mixed = new CamSweepRig(lca, Rpm, turbocharged: true) { ShortCircuitFraction = 0.0 }
            .Run(InjectionSystem.PortUpstreamOfValve);
        var partial = new CamSweepRig(lca, Rpm, turbocharged: true) { ShortCircuitFraction = 0.35 }
            .Run(InjectionSystem.PortUpstreamOfValve);
        var displaced = new CamSweepRig(lca, Rpm, turbocharged: true) { ShortCircuitFraction = 1.0 }
            .Run(InjectionSystem.PortUpstreamOfValve);

        output.WriteLine("  short-circuit   blow-through   fuel penalty   net N·m   TIT rise");
        foreach (var (label, point) in new[]
                 {
                     ("0.00 (mixed)", mixed), ("0.35", partial), ("1.00 (displaced)", displaced),
                 })
        {
            output.WriteLine(
                $"  {label,-13}   {point.BlowThroughFraction,12:P1}   {point.Cost.FuelPenaltyFraction,12:P1}   "
                + $"{point.NetTorqueNm,7:F1}   {point.Cost.TurbineInletRiseK,7:F1} K");
        }

        mixed.BlowThroughFraction.Should().BeLessThan(partial.BlowThroughFraction);
        partial.BlowThroughFraction.Should().BeLessThan(displaced.BlowThroughFraction);

        // The bracket has to bite where the plan says it must: on the number the
        // optimiser is maximising. A blow-through estimate that changed nothing
        // would not stop anyone exploiting free scavenging.
        displaced.NetTorqueNm.Should().BeLessThan(mixed.NetTorqueNm,
            "more short-circuiting is more fuel out of the exhaust, and it has to cost net torque");

        // Indicated torque is untouched: the bracket is a reporting parameter
        // and does not reach back into the gas dynamics. Saying so here keeps
        // that honest.
        displaced.TorqueNm.Should().BeApproximately(mixed.TorqueNm, 1e-6,
            "the short-circuit fraction re-attributes charge; it does not re-solve the flow");
    }

    [Fact]
    public void Gate_port_injection_is_charged_for_the_fuel_it_blows_through_and_direct_injection_is_not()
    {
        // Plan §4.6.3: "Do not let the optimiser exploit free scavenging that
        // the modelled injection system cannot actually have." The optimiser
        // maximises NET torque, so the fuel has to be charged to it.
        var direct = Sweep(turbocharged: true, InjectionSystem.Direct);
        var port = Sweep(turbocharged: true, InjectionSystem.PortUpstreamOfValve);

        output.WriteLine("   LCA   overlap   blow-through   DI net N·m   PI net N·m   fuel lost   TIT rise");
        for (var i = 0; i < direct.Count; i++)
        {
            output.WriteLine(
                $"{direct[i].LobeCentreAngleDeg,6:F0}   {direct[i].OverlapDeg,7:F0}   "
                + $"{direct[i].BlowThroughFraction,12:P1}   {direct[i].NetTorqueNm,10:F1}   "
                + $"{port[i].NetTorqueNm,10:F1}   {port[i].Cost.FuelPenaltyFraction,9:P1}   "
                + $"{port[i].Cost.TurbineInletRiseK,7:F1} K");
        }

        // Direct injection pays nothing for blow-through.
        direct.Should().AllSatisfy(p =>
        {
            p.Cost.FuelLostKgPerCycle.Should().Be(0.0);
            p.NetTorqueNm.Should().BeApproximately(p.TorqueNm, 1e-9);
        });

        // Port injection pays, and pays more the more it blows through.
        var widest = port.Single(p => p.LobeCentreAngleDeg == 90.0);
        widest.Cost.FuelLostKgPerCycle.Should().BeGreaterThan(0.0);
        widest.NetTorqueNm.Should().BeLessThan(widest.TorqueNm);

        port.OrderByDescending(p => p.LobeCentreAngleDeg).Select(p => p.Cost.FuelPenaltyFraction)
            .Should().BeInAscendingOrder("more blow-through is more fuel out of the exhaust valve");

        // The unburnt fuel that reaches the port burns there and shows up as
        // turbine inlet temperature — a hard material limit, so it belongs in
        // the report and eventually in the optimiser's constraints.
        widest.Cost.TurbineInletRiseK.Should().BeGreaterThan(0.0);

        // And the lambda story is the other way round: DI blow-through is pure
        // air, so the sensor reads lean on an engine that is not.
        direct.Single(p => p.LobeCentreAngleDeg == 90.0).Cost.MeasuredLambdaRatio
            .Should().BeGreaterThan(1.0, "blow-through air makes a wideband read lean under DI");

        widest.Cost.MeasuredLambdaRatio.Should().BeApproximately(1.0, 1e-9,
            "port-injected blow-through carries its own fuel, so the sensor is not fooled — the fuel bill is");
    }
}
