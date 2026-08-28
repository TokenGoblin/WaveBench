using FluentAssertions;
using WaveBench.Boost;
using WaveBench.Boost.Control;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Shaft dynamics, wastegate, charge air cooler and boost control — the
/// remaining §4.1 and §4.4 components of Phase 13.
/// </summary>
public class ShaftAndControlTests(ITestOutputHelper output)
{
    // ---- Shaft dynamics (§4.1) --------------------------------------------

    [Fact]
    public void The_shaft_integrates_energy_so_a_stationary_turbo_is_a_starting_condition_and_not_a_singularity()
    {
        // Torque is P/ω and blows up as ω → 0; kinetic energy does not. A shaft
        // starting from rest is exactly the case a spool prediction has to
        // handle, so the formulation has to survive it.
        var shaft = new TurboShaft(3.1e-6, 0.0);

        shaft.Rpm.Should().Be(0.0);
        shaft.Step(1e-4, turbinePowerW: 2000.0, compressorPowerW: 0.0);

        shaft.Rpm.Should().BeGreaterThan(0.0);
        double.IsFinite(shaft.Rpm).Should().BeTrue();
    }

    [Fact]
    public void Spool_time_matches_the_closed_form_the_energy_formulation_implies()
    {
        // Constant net power against a constant inertia has an exact answer:
        // t = ΔE/P. Integrating it numerically must reproduce that, or the
        // integration is wrong somewhere the physics cannot show through.
        const double inertia = 3.1e-6;
        const double netPower = 3000.0;
        const double target = 120_000.0;

        var frictionless = new BearingFriction(Coefficient: 0.0);
        var shaft = new TurboShaft(inertia, 20_000.0, frictionless) { MechanicalEfficiency = 1.0 };

        var expected = shaft.TimeToReach(target, netPower);

        var t = 0.0;
        while (shaft.Rpm < target)
        {
            shaft.Step(1e-5, netPower, 0.0);
            t += 1e-5;
        }

        output.WriteLine($"20 000 → 120 000 rpm on {netPower / 1000:F1} kW: closed form {expected * 1000:F2} ms, "
                         + $"integrated {t * 1000:F2} ms");

        t.Should().BeApproximately(expected, 2e-5);
    }

    [Fact]
    public void Bearing_friction_dominates_at_low_speed_and_cold_oil_makes_it_worse()
    {
        var warm = new BearingFriction();
        var cold = warm with { OilViscosityRatio = 3.5 };

        output.WriteLine("  rpm      warm W    cold W");
        foreach (var rpm in new[] { 20_000.0, 60_000.0, 100_000.0, 150_000.0 })
        {
            output.WriteLine($"{rpm,7:N0}   {warm.PowerW(rpm),8:F0}   {cold.PowerW(rpm),7:F0}");
        }

        warm.PowerW(150_000).Should().BeInRange(500.0, 1500.0,
            "the default must put roughly a kilowatt into the bearings at rated speed");

        cold.PowerW(60_000).Should().BeApproximately(3.5 * warm.PowerW(60_000), 1e-9);

        // The loss is a far bigger share of what is available at low speed,
        // which is what makes it a first-order term in spool time rather than a
        // correction.
        (warm.PowerW(30_000) / warm.PowerW(150_000)).Should().BeLessThan(0.01);
    }

    [Fact]
    public void An_electric_assist_shows_up_as_shaft_power_and_nothing_else()
    {
        // Plan §4.5: an e-turbo lives entirely in one term of the shaft
        // equation. If it needed anything else, the shaft model would be wrong.
        var plain = new TurboShaft(3.1e-6, 40_000.0);
        var assisted = new TurboShaft(3.1e-6, 40_000.0) { AssistPowerW = 2_000.0 };

        for (var i = 0; i < 2000; i++)
        {
            plain.Step(1e-5, 1500.0, 500.0);
            assisted.Step(1e-5, 1500.0, 500.0);
        }

        assisted.Rpm.Should().BeGreaterThan(plain.Rpm);
        output.WriteLine($"after 20 ms: unassisted {plain.Rpm:N0} rpm, with 2 kW of assist {assisted.Rpm:N0} rpm");
    }

    // ---- Wastegate (§4.3) --------------------------------------------------

    [Fact]
    public void Gate_an_internal_wastegate_partly_defeats_the_twin_scroll_division_as_it_opens()
    {
        // Plan §4.3 names this explicitly: "Model the loss of scroll division at
        // an internally-gated port — it partly defeats twin-scroll pairing at
        // high load, and omitting it overstates the twin-scroll benefit."
        var internalGate = new Wastegate { FullOpenAreaM2 = 4.0e-4 };
        var externalGate = new Wastegate
        {
            FullOpenAreaM2 = 4.0e-4,
            Placement = WastegatePlacement.External,
        };

        output.WriteLine("  position   internal division   external division");
        foreach (var position in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            internalGate.Position = position;
            externalGate.Position = position;
            output.WriteLine(
                $"{position,10:F2}   {internalGate.ScrollDivisionRetained,17:P0}   "
                + $"{externalGate.ScrollDivisionRetained,17:P0}");
        }

        internalGate.Position = 0.0;
        internalGate.ScrollDivisionRetained.Should().Be(1.0, "a shut gate connects nothing");

        internalGate.Position = 1.0;
        internalGate.ScrollDivisionRetained.Should().BeLessThan(0.5,
            "a fully open internal port has most of the division gone");

        externalGate.Position = 1.0;
        externalGate.ScrollDivisionRetained.Should().Be(1.0,
            "an external gate on its own take-off never joins the scrolls");
    }

    [Fact]
    public void An_opening_wastegate_takes_flow_away_from_the_rotor_by_area()
    {
        var gate = new Wastegate { FullOpenAreaM2 = 4.0e-4 };
        const double rotorArea = 5.5e-4;

        gate.Position = 0.0;
        gate.DivertedFlow(0.20, rotorArea).Should().Be(0.0);

        gate.Position = 1.0;
        var diverted = gate.DivertedFlow(0.20, rotorArea);

        // Both paths see the same pressure ratio, so the split is by effective
        // area: 0.78 × 4.0 cm² against 5.5 cm².
        var expected = 0.20 * gate.OpenAreaM2 / (rotorArea + gate.OpenAreaM2);
        diverted.Should().BeApproximately(expected, 1e-12);

        output.WriteLine($"fully open: {diverted / 0.20:P0} of the exhaust bypasses the rotor");
    }

    [Fact]
    public void A_blow_off_valve_opens_progressively_above_its_cracking_pressure()
    {
        var valve = new BlowOffValve { FullOpenAreaM2 = 3.0e-4 };

        valve.Position(20_000).Should().Be(0.0, "below cracking it stays shut");
        valve.Position(55_000).Should().BeApproximately(0.5, 0.01);
        valve.Position(120_000).Should().Be(1.0, "past full-open it cannot open further");
    }

    // ---- Charge air cooler (§4.4) -----------------------------------------

    [Fact]
    public void Gate_a_soaked_charge_cooler_gives_a_hotter_charge_than_a_steady_model_predicts()
    {
        // Plan §4.4 asks for thermal mass "so a repeated-run transient shows the
        // IAT climb a steady-state model hides". This is that transient: five
        // pulls with a short cool-down between them, ON A DYNO — no ram air
        // through the core, which is the whole reason the effect is a dyno
        // phenomenon before it is a road one.
        var cooler = new ChargeAirCooler().OnADyno();
        cooler.Reset(298.15);

        const double inlet = 400.0;
        const double flow = 0.18;
        const double ambient = 303.0;

        var steady = cooler.SteadyOutletK(inlet, flow, ambient);
        var endOfPull = new List<double>();

        for (var pull = 0; pull < 5; pull++)
        {
            ChargeCoolerState state = default;
            for (var t = 0.0; t < 12.0; t += 0.05)
            {
                state = cooler.Step(0.05, inlet, flow, ambient);
            }

            endOfPull.Add(state.OutletTemperatureK);
            output.WriteLine(
                $"pull {pull + 1}: outlet {state.OutletTemperatureK:F1} K, core {state.CoreTemperatureK:F1} K, "
                + $"rejecting {state.HeatRejectedW / 1000:F1} kW");

            // Idle back to the pits for thirty seconds.
            for (var t = 0.0; t < 30.0; t += 0.1)
            {
                cooler.Step(0.1, 320.0, 0.03, ambient);
            }
        }

        output.WriteLine($"a steady ε-NTU model would say {steady:F1} K on every pull");

        endOfPull.Should().BeInAscendingOrder("each pull must leave the core hotter than the last");
        endOfPull[0].Should().BeGreaterThan(steady,
            "even the first pull heats the core above ambient, so the charge leaves hotter than steady");
        (endOfPull[^1] - endOfPull[0]).Should().BeGreaterThan(2.0,
            "the last pull must be measurably worse than the first, which is the point of the thermal mass");
    }

    [Fact]
    public void Cooler_pressure_drop_goes_as_flow_squared()
    {
        var cooler = new ChargeAirCooler { RatedFlowKgPerS = 0.15, RatedPressureDropPa = 8_000.0 };
        cooler.Reset(300.0);

        var atRated = cooler.Step(1e-6, 380.0, 0.15, 300.0).PressureDropPa;
        var atDouble = cooler.Step(1e-6, 380.0, 0.30, 300.0).PressureDropPa;

        atRated.Should().BeApproximately(8_000.0, 1.0);
        atDouble.Should().BeApproximately(32_000.0, 10.0, "twice the flow is four times the drop");
    }

    // ---- Boost control (§4.4) ---------------------------------------------

    [Fact]
    public void The_actuator_opens_later_at_higher_duty_which_is_the_sign_that_catches_people_out()
    {
        var actuator = new PneumaticActuator();

        actuator.Reset(0.0);
        var atZeroDuty = actuator.Step(1.0, 100_000.0, 0.0);

        actuator.Reset(0.0);
        var atHighDuty = actuator.Step(1.0, 100_000.0, 0.9);

        atHighDuty.Should().BeLessThan(atZeroDuty,
            "the solenoid bleeds pressure away from the diaphragm, so more duty means a later opening "
            + "and more boost");

        output.WriteLine($"at 1.0 bar of boost: 0% duty opens to {atZeroDuty:P0}, 90% duty to {atHighDuty:P0}");
    }

    [Fact]
    public void The_actuator_cannot_slam_and_the_controller_does_not_wind_up_behind_it()
    {
        var actuator = new PneumaticActuator { SlewRatePerSecond = 8.0 };
        actuator.Reset(0.0);

        actuator.Step(0.01, 500_000.0, 0.0).Should().BeApproximately(0.08, 1e-9,
            "8 per second for 10 ms is 8% of stroke, however hard it is pushed");

        // Anti-windup: a long saturated stretch must not store demand the
        // actuator was never able to act on, or the overshoot the plan asks to
        // be reported is the controller's and not the turbo's.
        var controller = new BoostController();
        for (var i = 0; i < 500; i++)
        {
            controller.Update(0.01, 200_000.0, 101_325.0);
        }

        controller.Duty.Should().Be(1.0, "the controller must be asking for everything it has");

        // Now the boost arrives. A wound-up integrator would hold the duty at 1
        // for a long time after; a clamped one comes off quickly.
        var stepsToRelease = 0;
        while (controller.Duty > 0.5 && stepsToRelease < 500)
        {
            controller.Update(0.01, 200_000.0, 210_000.0);
            stepsToRelease++;
        }

        output.WriteLine($"duty came off maximum {stepsToRelease * 10} ms after the target was passed");
        stepsToRelease.Should().BeLessThan(100, "an anti-windup integrator releases within a second");
    }

    [Fact]
    public void Feed_forward_starts_the_controller_near_the_answer_instead_of_winding_up_to_it()
    {
        var plain = new BoostController();
        var fedForward = new BoostController { FeedForward = _ => 0.55 };

        // A small error on purpose: from a standing start both controllers
        // saturate and the comparison says nothing. Feed-forward earns its
        // place near the target, where it holds the duty the plant needs while
        // the integrator only trims.
        var plainFirst = plain.Update(0.01, 200_000.0, 185_000.0);
        var fedFirst = fedForward.Update(0.01, 200_000.0, 185_000.0);

        fedFirst.Should().BeGreaterThan(plainFirst);
        fedFirst.Should().BeLessThan(1.0, "and it must not be saturated either, or it is not trimming anything");
        output.WriteLine($"first update: without feed-forward {plainFirst:P0}, with it {fedFirst:P0}");
    }

    // ---- VGT (§4.3) --------------------------------------------------------

    [Fact]
    public void Closing_the_vanes_shrinks_the_swallowing_capacity_and_the_actuator_takes_time_to_do_it()
    {
        var vanes = new VariableGeometryActuator();

        vanes.Reset(1.0);
        var open = vanes.CapacityScale;

        vanes.Reset(0.0);
        var closed = vanes.CapacityScale;

        closed.Should().BeLessThan(open,
            "closed vanes are a smaller effective A/R, which is what makes a VGT spool");

        // And it cannot move instantly, which is why spool strategy depends on
        // the actuator as much as on the turbine.
        vanes.Reset(1.0);
        vanes.Step(0.05, 0.0).Should().BeApproximately(0.80, 1e-9, "4 per second for 50 ms is 20% of travel");

        output.WriteLine(
            $"capacity scale: vanes open {open:F2}, vanes closed {closed:F2} "
            + $"({(1.0 - (closed / open)):P0} less flow at the same pressure ratio)");
    }
}
