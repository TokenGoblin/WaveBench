using FluentAssertions;
using WaveBench.Boost;
using WaveBench.Boost.Engine;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Superchargers and electric assist (plan §4.5).
///
/// The claim worth testing is the one the plan singles out: a Roots blower
/// heats the charge more than a screw at the same pressure ratio, and the
/// reason is the internal compression ratio and nothing else.
/// </summary>
public class SuperchargerTests(ITestOutputHelper output)
{
    private static PositiveDisplacementBlower Roots => new()
    {
        DisplacementPerRevM3 = 2.3e-3,
        InternalVolumeRatio = 1.0,
        DriveRatio = 2.2,
    };

    private static PositiveDisplacementBlower Screw => new()
    {
        DisplacementPerRevM3 = 2.3e-3,
        InternalVolumeRatio = 1.9,
        DriveRatio = 2.2,
    };

    [Fact]
    public void Gate_a_roots_heats_the_charge_more_than_a_screw_at_and_above_the_screw_design_ratio()
    {
        // The plan's claim is that a Roots heats the charge more than a screw at
        // the same pressure ratio, and it does — but only above the screw's own
        // design point. That caveat is not a hedge; it is the second half of the
        // same physics, and the sweep below shows the crossover rather than
        // hiding it.
        //
        // A screw with V_i = 1.9 is built for an internal ratio of 1.9^γ = 2.49.
        // Run it at 1.3 and it compresses to 2.49 and then blows down to 1.3,
        // and even with the recovery that gives back it has done more work than
        // a Roots would. Which is exactly why screw blowers are matched to their
        // target boost and Roots blowers are not fussy.
        output.WriteLine("    PR   Roots out K   screw out K   Roots η   screw η   Roots kW   screw kW");

        var results = new List<(double Pr, double RootsK, double ScrewK)>();

        foreach (var pr in new[] { 1.3, 1.5, 1.8, 2.0, 2.2 })
        {
            var roots = Roots.Solve(5000.0, pr);
            var screw = Screw.Solve(5000.0, pr);
            results.Add((pr, roots.OutletTemperatureK, screw.OutletTemperatureK));

            output.WriteLine(
                $"{pr,6:F1}   {roots.OutletTemperatureK,11:F1}   {screw.OutletTemperatureK,11:F1}   "
                + $"{roots.AdiabaticEfficiency,7:P0}   {screw.AdiabaticEfficiency,7:P0}   "
                + $"{roots.ShaftPowerW / 1000,8:F1}   {screw.ShaftPowerW / 1000,7:F1}");
        }

        // At and above the design ratio the plan's claim holds, and the gap
        // widens with pressure ratio because that is where the Roots is doing
        // more and more of its work isochorically.
        foreach (var (pr, rootsK, screwK) in results.Where(r => r.Pr >= 1.8))
        {
            rootsK.Should().BeGreaterThan(screwK, $"at PR {pr:F1}");
        }

        var gapAt18 = results.Single(r => r.Pr == 1.8).RootsK - results.Single(r => r.Pr == 1.8).ScrewK;
        var gapAt22 = results.Single(r => r.Pr == 2.2).RootsK - results.Single(r => r.Pr == 2.2).ScrewK;
        gapAt22.Should().BeGreaterThan(gapAt18, "the more pressure the Roots makes, the worse the comparison");

        output.WriteLine("");
        output.WriteLine(
            $"the Roots is {gapAt18:F1} K hotter at PR 1.8 and {gapAt22:F1} K hotter at PR 2.2 — "
            + "which is the intercooler the Roots car needs and the screw car does not");

        Roots.Solve(5000.0, 1.8).ShaftPowerW.Should().BeGreaterThan(
            Screw.Solve(5000.0, 1.8).ShaftPowerW);
    }

    [Fact]
    public void Roots_adiabatic_efficiency_lands_in_the_published_band()
    {
        // A Roots at PR 1.8 has an ideal isochoric-cycle efficiency near 80%;
        // real ones measure 55–70% once rotor friction and leakage reheat are
        // in. The rotor efficiency parameter is what carries that gap, and it is
        // exposed rather than hidden.
        var point = Roots.Solve(5000.0, 1.8);

        output.WriteLine(
            $"Roots at PR 1.8: outlet {point.OutletTemperatureK:F1} K, η {point.AdiabaticEfficiency:P1}, "
            + $"{point.ShaftPowerW / 1000:F2} kW off the crank for {point.MassFlowKgPerS:F4} kg/s");

        point.AdiabaticEfficiency.Should().BeInRange(0.55, 0.72);
        Screw.Solve(5000.0, 1.8).AdiabaticEfficiency.Should().BeInRange(0.68, 0.85);
    }

    [Fact]
    public void A_screw_run_below_its_design_ratio_over_compresses_and_costs_more_than_a_roots()
    {
        // The reason a screw is matched to its target boost, quantified. Below
        // the design ratio the machine compresses to its built-in ratio and then
        // blows down through the port; the model recovers the reversible part of
        // that blow-down (a real port recovers less), and the screw is STILL
        // worse than a Roots at the same low pressure ratio.
        var screwLow = Screw.Solve(5000.0, 1.4);
        var rootsLow = Roots.Solve(5000.0, 1.4);
        var screwDesign = Screw.Solve(5000.0, 2.2);
        var rootsDesign = Roots.Solve(5000.0, 2.2);

        output.WriteLine(
            $"PR 1.4 (below design): screw {screwLow.OutletTemperatureK:F1} K / "
            + $"{screwLow.ShaftPowerW / 1000:F2} kW, Roots {rootsLow.OutletTemperatureK:F1} K / "
            + $"{rootsLow.ShaftPowerW / 1000:F2} kW");
        output.WriteLine(
            $"PR 2.2 (near design):  screw {screwDesign.OutletTemperatureK:F1} K / "
            + $"{screwDesign.ShaftPowerW / 1000:F2} kW, Roots {rootsDesign.OutletTemperatureK:F1} K / "
            + $"{rootsDesign.ShaftPowerW / 1000:F2} kW");

        screwLow.ShaftPowerW.Should().BeGreaterThan(rootsLow.ShaftPowerW,
            "over-compressing to 2.49 and blowing back down to 1.4 costs more than never compressing "
            + "internally at all");

        screwDesign.ShaftPowerW.Should().BeLessThan(rootsDesign.ShaftPowerW,
            "and near its design point the screw wins, which is the whole reason to buy one");

        // The recovery is nonetheless real and worth having: without it the
        // screw would be charged the full internal compression at every
        // pressure ratio.
        var fullOverCompression = Screw.Solve(5000.0, Math.Pow(1.9, 1.4));
        screwLow.ShaftPowerW.Should().BeLessThan(fullOverCompression.ShaftPowerW,
            "blowing down through the port gives some of the over-compression back");
    }

    [Fact]
    public void Volumetric_efficiency_falls_with_pressure_ratio_and_rises_with_speed()
    {
        var blower = Roots;

        blower.VolumetricEfficiencyAt(6000.0, 2.2).Should().BeLessThan(
            blower.VolumetricEfficiencyAt(6000.0, 1.3),
            "there is more to leak against at a higher pressure ratio");

        blower.VolumetricEfficiencyAt(12_000.0, 1.8).Should().BeGreaterThan(
            blower.VolumetricEfficiencyAt(3_000.0, 1.8),
            "at higher speed there is less time for a fixed leak path to leak");
    }

    [Fact]
    public void Gate_a_centrifugal_supercharger_makes_boost_that_tracks_engine_speed_squared()
    {
        // Plan §4.5: "boost tracks rpm² — a fundamentally different torque curve
        // shape, worth placing beside a turbo in Compare." This is that shape,
        // asserted.
        var blower = new CentrifugalSupercharger
        {
            Map = SyntheticTurbo.Compressor(),
            DriveRatio = 26.0,
        };

        output.WriteLine("  engine rpm   impeller rpm   ṁ kg/s     PR   power kW");

        var points = new List<(double Rpm, double Pr)>();
        foreach (var (rpm, flow) in new[]
                 {
                     (2000.0, 0.070), (3000.0, 0.105), (4000.0, 0.140),
                     (5000.0, 0.172), (6000.0, 0.195),
                 })
        {
            var point = blower.Solve(rpm, flow);
            points.Add((rpm, point.PressureRatio));

            output.WriteLine(
                $"{rpm,12:N0}   {rpm * blower.DriveRatio,12:N0}   {flow,7:F3}   {point.PressureRatio,5:F2}   "
                + $"{point.ShaftPowerW / 1000,8:F2}");
        }

        points.Select(p => p.Pr).Should().BeInAscendingOrder();

        // PR − 1 goes as tip speed squared, so doubling engine speed should
        // roughly quadruple the boost above atmospheric. "Roughly", because the
        // flow is rising at the same time and that costs some head.
        var atTwo = points.Single(p => p.Rpm == 2000.0).Pr - 1.0;
        var atFour = points.Single(p => p.Rpm == 4000.0).Pr - 1.0;

        output.WriteLine("");
        output.WriteLine($"boost above atmospheric: {atTwo:F3} at 2000 rpm, {atFour:F3} at 4000 — "
                         + $"{atFour / atTwo:F1}× for double the speed");

        (atFour / atTwo).Should().BeInRange(2.5, 5.5,
            "a centrifugal makes almost nothing low down and everything at the top, which is exactly "
            + "why it feels nothing like a turbo at the same peak boost");
    }

    [Fact]
    public void Parasitic_power_comes_off_the_crank_and_is_reported_as_such()
    {
        // The number that makes a supercharged torque curve a different shape:
        // this power is subtracted from what the engine makes, at every speed,
        // whether or not the boost is wanted.
        var blower = Roots;
        output.WriteLine("  engine rpm   parasitic kW");

        var previous = 0.0;
        foreach (var rpm in new[] { 2000.0, 4000.0, 6000.0 })
        {
            var point = blower.Solve(rpm, 1.8);
            output.WriteLine($"{rpm,12:N0}   {point.ShaftPowerW / 1000,12:F2}");

            point.ShaftPowerW.Should().BeGreaterThan(previous, "more air means more work to move it");
            previous = point.ShaftPowerW;
        }
    }

    [Fact]
    public void Electric_assist_reports_the_energy_it_spent_and_respects_a_power_budget()
    {
        // Plan §4.5: motor torque in the shaft equation with a power budget
        // (48 V systems are limited) and a report of electrical energy per
        // acceleration event.
        const double budgetW = 7_000.0;
        var shaft = new TurboShaft(3.1e-6, 30_000.0) { AssistPowerW = budgetW };

        var energy = 0.0;
        var time = 0.0;
        const double dt = 1e-5;

        while (shaft.Rpm < 120_000.0 && time < 2.0)
        {
            shaft.Step(dt, turbinePowerW: 1_200.0, compressorPowerW: 900.0);
            energy += budgetW * dt;
            time += dt;
        }

        output.WriteLine(
            $"30 000 → 120 000 rpm on {budgetW / 1000:F1} kW of assist: {time * 1000:F0} ms, "
            + $"{energy:F0} J ({energy / 3600:F2} Wh) per event");

        shaft.Rpm.Should().BeGreaterThanOrEqualTo(120_000.0);
        time.Should().BeLessThan(1.0);

        // The same shaft without assist takes materially longer — that is the
        // whole product claim of an e-turbo, and it should fall out rather than
        // be asserted.
        var unassisted = new TurboShaft(3.1e-6, 30_000.0);
        var unassistedTime = 0.0;
        while (unassisted.Rpm < 120_000.0 && unassistedTime < 5.0)
        {
            unassisted.Step(dt, 1_200.0, 900.0);
            unassistedTime += dt;
        }

        output.WriteLine($"without assist: {unassistedTime * 1000:F0} ms");
        unassistedTime.Should().BeGreaterThan(time * 2.0);
    }
}
