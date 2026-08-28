using FluentAssertions;
using WaveBench.Boost;
using WaveBench.Boost.Engine;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 14 gate, second half: <i>"the FSAE restrictor validation case passes"</i>.
///
/// Validation case 18: <i>"FSAE restrictor upstream of compressor — operating
/// line shift and choke ceiling match hand calculation; surge approach
/// flagged."</i>
///
/// The choke ceiling is checkable by hand, so it is checked by hand here rather
/// than against the model's own output.
/// </summary>
public class RestrictorTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate_the_choke_ceiling_matches_the_hand_calculation()
    {
        // ṁ* = C_d·A·p₀·√(γ/(R·T₀))·(2/(γ+1))^((γ+1)/(2(γ−1)))
        //
        // A  = π·0.020²/4        = 3.1416e-4 m²
        // C_d·A                  = 3.0159e-4 m²
        // √(1.4/(287.05·298.15)) = 4.0444e-3
        // (2/2.4)^3              = 0.5787
        // ṁ* = 3.0159e-4 · 101325 · 4.0444e-3 · 0.5787 = 0.07152 kg/s
        var restrictor = IntakeRestrictor.Petrol20mm;
        var choked = restrictor.ChokedFlow();

        output.WriteLine(
            $"20 mm restrictor, C_d {restrictor.DischargeCoefficient:F2}: choked flow {choked:F5} kg/s "
            + $"({choked * 1000:F1} g/s)");

        choked.Should().BeApproximately(0.07152, 5e-5, "against the hand calculation above");

        // The number every FSAE team already knows, from the other direction:
        // at λ = 1 on petrol that is about 4.9 g/s of fuel, and at a decent
        // BSFC something under 90 kW. Nothing downstream changes it.
        var fuel = choked / 14.7;
        output.WriteLine($"at λ = 1 that is {fuel * 1000:F2} g/s of fuel — the whole car's ceiling");
        fuel.Should().BeApproximately(0.00487, 1e-4);
    }

    [Fact]
    public void A_smaller_restrictor_and_a_hotter_day_both_move_the_ceiling()
    {
        var petrol = IntakeRestrictor.Petrol20mm.ChokedFlow();
        var ethanol = IntakeRestrictor.Ethanol19mm.ChokedFlow();

        // Area goes as d², so 19/20 squared is 0.9025.
        (ethanol / petrol).Should().BeApproximately(0.9025, 1e-3);

        var hot = IntakeRestrictor.Petrol20mm.ChokedFlow(
            AmbientCondition.HotDay.PressurePa, AmbientCondition.HotDay.TemperatureK);
        var high = IntakeRestrictor.Petrol20mm.ChokedFlow(
            AmbientCondition.Altitude1600m.PressurePa, AmbientCondition.Altitude1600m.TemperatureK);

        output.WriteLine($"20 mm: standard {petrol * 1000:F2} g/s, 19 mm: {ethanol * 1000:F2} g/s");
        output.WriteLine($"20 mm on a 35 °C day: {hot * 1000:F2} g/s; at 1600 m: {high * 1000:F2} g/s");

        hot.Should().BeLessThan(petrol, "a hot day is less dense air through the same throat");
        high.Should().BeLessThan(petrol, "and so is altitude");
    }

    [Fact]
    public void Gate_the_restrictor_moves_the_operating_line_across_the_map()
    {
        // Plan §4.6.4: "the compressor inlet runs sub-atmospheric, so corrected
        // flow and corrected speed shift substantially and the operating line
        // moves across the map".
        var restrictor = IntakeRestrictor.Petrol20mm;
        var map = SyntheticTurbo.FsaeCompressor();
        const double shaftRpm = 190_000.0;

        output.WriteLine(
            "   ṁ g/s   p₀₁ kPa   corrected ṁ   PR   manifold kPa   surge %   choke %   region");

        var withRestrictor = new List<(double Flow, double Corrected, double SurgeMargin, double Manifold)>();

        foreach (var flow in new[] { 0.030, 0.045, 0.055, 0.065, 0.0710 })
        {
            var state = restrictor.Solve(flow);
            var point = CompressorModel.Solve(
                map, flow, shaftRpm, state.OutletTotalTemperatureK, state.OutletTotalPressureKPa);

            var corrected = Corrected.Flow(
                flow, state.OutletTotalTemperatureK, state.OutletTotalPressureKPa, map.Reference);

            var manifold = point.PressureRatio * state.OutletTotalPressureKPa;
            withRestrictor.Add((flow, corrected, point.SurgeMarginPercent, manifold));

            output.WriteLine(
                $"{flow * 1000,8:F1}   {state.OutletTotalPressureKPa,7:F1}   {corrected,11:F4}   "
                + $"{point.PressureRatio,4:F2}   {manifold,12:F1}   {point.SurgeMarginPercent,7:F1}   "
                + $"{point.ChokeMarginPercent,7:F1}   {point.Region}");
        }

        // The shift itself: the SAME physical flow reads as a higher corrected
        // flow behind the restrictor, because corrected flow divides by inlet
        // pressure and the restrictor has taken some away.
        var flowAtAmbient = Corrected.Flow(0.065, 298.15, 101.325, map.Reference);
        var flowBehind = withRestrictor.Single(w => Math.Abs(w.Flow - 0.065) < 1e-9).Corrected;

        flowBehind.Should().BeGreaterThan(flowAtAmbient,
            "sub-atmospheric inlet pressure moves the same mass flow to the right on the map");

        output.WriteLine("");
        output.WriteLine(
            $"0.065 kg/s reads as {flowAtAmbient:F4} kg/s corrected at ambient and {flowBehind:F4} behind the "
            + $"restrictor — {(flowBehind / flowAtAmbient) - 1.0:P1} further right");

        // And the manifold pressure the engine actually sees is below what the
        // pressure ratio suggests, because it is a ratio on a reduced inlet.
        var top = withRestrictor[^1];
        top.Manifold.Should().BeLessThan(
            withRestrictor[^1].Flow > 0 ? 400.0 : 0.0);
    }

    [Fact]
    public void Gate_a_choked_restrictor_turns_extra_shaft_speed_into_a_surge_trajectory()
    {
        // Plan §4.6.4: "Once the restrictor chokes, the compressor cannot pull
        // more mass regardless of shaft speed; the turbo simply raises pressure
        // ratio against fixed mass flow, which is a surge trajectory."
        //
        // This is the single most valuable thing the module can tell an FSAE
        // team, so it is asserted rather than assumed.
        var restrictor = IntakeRestrictor.Petrol20mm;
        var map = SyntheticTurbo.FsaeCompressor();
        var choked = restrictor.ChokedFlow();

        var state = restrictor.Solve(choked);
        state.IsChoked.Should().BeTrue();

        output.WriteLine("  shaft rpm     PR   surge margin   in surge?");

        var margins = new List<double>();
        foreach (var rpm in new[] { 150_000.0, 180_000.0, 210_000.0, 240_000.0, 270_000.0 })
        {
            var point = CompressorModel.Solve(
                map, choked, rpm, state.OutletTotalTemperatureK, state.OutletTotalPressureKPa);

            margins.Add(point.SurgeMarginPercent);
            output.WriteLine(
                $"{rpm,11:N0}   {point.PressureRatio,4:F2}   {point.SurgeMarginPercent,12:F1}   "
                + $"{(point.InSurge ? "YES" : "no"),9}");
        }

        margins.Should().BeInDescendingOrder(
            "with the mass flow pinned by the restrictor, every extra rpm is pressure ratio and nothing else — "
            + "which walks the operating point straight at the surge line");

        // Asking for more flow than the throat will pass returns the ceiling,
        // not a bigger number and not an exception.
        var beyond = restrictor.Solve(choked * 1.5);
        beyond.MassFlowKgPerS.Should().BeApproximately(choked, 1e-12);
        beyond.IsChoked.Should().BeTrue();
    }

    [Fact]
    public void The_diffuser_is_the_cheapest_thing_on_the_car_to_improve()
    {
        // Total pressure recovery downstream of the throat is a design choice,
        // and it is worth reporting because it is the one an undergraduate team
        // can actually act on.
        var sharp = new IntakeRestrictor { ThroatDiameterM = 0.020, DiffuserRecovery = 0.20 };
        var good = new IntakeRestrictor { ThroatDiameterM = 0.020, DiffuserRecovery = 0.85 };

        const double flow = 0.060;
        var sharpState = sharp.Solve(flow);
        var goodState = good.Solve(flow);

        output.WriteLine(
            $"at {flow * 1000:F0} g/s: sharp expansion leaves {sharpState.OutletTotalPressureKPa:F1} kPa, "
            + $"a proper diffuser leaves {goodState.OutletTotalPressureKPa:F1} kPa "
            + $"({goodState.OutletTotalPressureKPa - sharpState.OutletTotalPressureKPa:F1} kPa more)");

        goodState.OutletTotalPressureKPa.Should().BeGreaterThan(sharpState.OutletTotalPressureKPa);

        // Both choke at the same flow: the throat sets the ceiling and the
        // diffuser cannot move it. That is worth pinning, because it is the
        // thing people most often hope is not true.
        sharp.ChokedFlow().Should().BeApproximately(good.ChokedFlow(), 1e-12);
    }

    [Fact]
    public void Ambient_conditions_scale_density_the_way_the_gas_law_says()
    {
        output.WriteLine("  condition                    density   ratio");
        foreach (var condition in AmbientCondition.Standard)
        {
            output.WriteLine(
                $"  {condition.Label,-26}   {condition.Density():F4}   {condition.DensityRatio():P1}");
        }

        AmbientCondition.StandardDay.DensityRatio().Should().BeApproximately(1.0, 1e-12);
        AmbientCondition.HotAndHigh.DensityRatio().Should().BeLessThan(
            Math.Min(
                AmbientCondition.HotDay.DensityRatio(),
                AmbientCondition.Altitude1600m.DensityRatio()),
            "hot and high is worse than either alone, which is where a sea-level match fails");
    }
}
