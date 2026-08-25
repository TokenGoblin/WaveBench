using FluentAssertions;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 6 verification: fired single-cylinder behaviour, the knock-ranking
/// gate (RON95 &lt; E85 &lt; M100 in knock resistance at fixed geometry), and
/// deterministic cycle-to-cycle variability.
/// </summary>
public class FiredEngineTests(ITestOutputHelper output)
{
    private static readonly PerfectGas Gas = new(1.4, 287.05);

    private static readonly CrankGeometry Crank = new()
    {
        Bore = 0.086,
        Stroke = 0.062,
        RodLength = 0.107,
        CompressionRatio = 11.0,
    };

    private const double AmbientP = 1.0e5;
    private const double AmbientT = 300.0;

    private static EngineSimulator BuildFired(
        double rpm, double? octane, CycleVariability? variability = null, bool adiabatic = false)
    {
        var gasModel = new PerfectGasModel(Gas);
        var rho0 = AmbientP / (Gas.SpecificGasConstant * AmbientT);

        var intake = new DuctSolver(DuctGeometry.Uniform(0.60, 100, 0.038), gasModel);
        var exhaust = new DuctSolver(DuctGeometry.Uniform(0.20, 34, 0.035), gasModel);
        foreach (var duct in new[] { intake, exhaust })
        {
            for (var i = 0; i < duct.CellCount; i++)
            {
                duct.SetState(i, new PrimitiveState(rho0, 0.0, AmbientP));
            }
        }

        intake.LeftBoundary = BoundaryKind.External;
        intake.LeftEnd = new ReservoirBoundary { StagnationPressure = AmbientP, StagnationTemperature = AmbientT };
        exhaust.RightBoundary = BoundaryKind.External;
        exhaust.RightEnd = new ReservoirBoundary { StagnationPressure = AmbientP, StagnationTemperature = AmbientT };

        var cylinder = new Cylinder(gasModel, Crank, 0.0, AmbientP, AmbientT)
        {
            // Premixed stoichiometric gasoline charge (perfect-gas energy
            // accounting; species-resolved charge arrives with the fired
            // multi-species runs).
            Combustion = new WiebeCombustion(StartAngleDeg: -15.0, DurationDeg: 55.0),
            FuelLowerHeatingValue = 44.0e6,
            FuelChargeFraction = 1.0 / (1.0 + 14.6),
            HeatTransfer = adiabatic ? null : HeatTransferCorrelation.Woschni,
            WallTemperature = 420.0,
            KnockOctaneNumber = octane,
            Variability = variability,
        };

        var engine = new EngineSimulator { Rpm = rpm };
        engine.Ducts.Add(intake);
        engine.Ducts.Add(exhaust);
        engine.Cylinders.Add(cylinder);
        engine.Valves.Add(new ValveConnection(
            cylinder, intake, ductLeftEnd: false,
            CamProfile.Harmonic(340.0, 580.0, 0.010),
            new ValveGeometry { HeadDiameter = 0.031, ValveCount = 2 }));
        engine.Valves.Add(new ValveConnection(
            cylinder, exhaust, ductLeftEnd: true,
            CamProfile.Harmonic(140.0, 380.0, 0.010),
            new ValveGeometry { HeadDiameter = 0.026, ValveCount = 2 }));
        return engine;
    }

    [Fact]
    public void Fired_cycle_produces_plausible_performance_numbers()
    {
        var engine = BuildFired(5000.0, octane: null);
        var (result, cycles) = engine.RunToConvergence(r => r.Imep[0], tolerance: 2e-3, minCycles: 5, maxCycles: 30);

        var imep = result.Imep[0];
        var peak = result.PeakPressure[0];
        var cm = Crank.MeanPistonSpeed(5000.0);
        var friction = new ChenFlynnFriction();
        var bmep = PerformanceMetrics.Bmep(imep, friction, peak, cm);
        var torque = PerformanceMetrics.Torque(bmep, Crank.DisplacedVolume);
        var power = PerformanceMetrics.Power(torque, 5000.0);
        var bsfcGPerKwh = PerformanceMetrics.Bsfc(result.FuelMass[0], result.CycleDuration, power) * 3.6e9;

        output.WriteLine(
            $"IMEP {imep / 1e5:F2} bar, peak {peak / 1e5:F1} bar, BMEP {bmep / 1e5:F2} bar, " +
            $"torque {torque:F1} Nm, power {power / 1000:F1} kW, BSFC {bsfcGPerKwh:F0} g/kWh ({cycles} cycles)");

        // The perfect-gas cycle overestimates efficiency (γ = 1.4 in the
        // burned gas instead of ~1.27, no dissociation) — the documented
        // reason plan §2.2 mandates species-resolved burned-gas properties.
        // Bounds accept the tuned NA range up to the known γ-1.4 optimism.
        imep.Should().BeInRange(8e5, 24e5, "net IMEP of a tuned NA engine at stoich (γ=1.4 optimistic)");
        peak.Should().BeInRange(40e5, 110e5, "firing peak pressure");
        bmep.Should().BeLessThan(imep).And.BeGreaterThan(imep - 4e5);
        torque.Should().BeInRange(20.0, 62.0, "360 cc single");
        bsfcGPerKwh.Should().BeInRange(130.0, 420.0,
            "BSFC band widened low for the perfect-gas efficiency optimism");

        // Energy sanity: indicated work below released fuel energy.
        var fuelEnergy = result.FuelMass[0] * 44.0e6;
        (imep * Crank.DisplacedVolume).Should().BeLessThan(fuelEnergy * 0.55);
    }

    [Fact]
    public void Wall_heat_transfer_costs_indicated_work()
    {
        var adiabatic = BuildFired(5000.0, octane: null, adiabatic: true);
        var cooled = BuildFired(5000.0, octane: null);

        var (ra, _) = adiabatic.RunToConvergence(r => r.Imep[0], 2e-3, 5, 20);
        var (rc, _) = cooled.RunToConvergence(r => r.Imep[0], 2e-3, 5, 20);

        rc.Imep[0].Should().BeLessThan(ra.Imep[0] * 0.97,
            "Woschni wall losses must take a visible bite out of IMEP");
    }

    [Fact]
    public void Gate_knock_model_ranks_ron95_e85_m100_correctly()
    {
        // Fixed geometry and operating point; only the octane number changes
        // (RON95 gasoline 95, E85 106, M100 109 — library values). Correct
        // qualitative ranking: RON95 closest to knock.
        double Knock(double octane)
        {
            var engine = BuildFired(5000.0, octane);
            var (result, _) = engine.RunToConvergence(r => r.Imep[0], 2e-3, 5, 20);
            return result.KnockIntegral[0];
        }

        var ron95 = Knock(95.0);
        var e85 = Knock(106.0);
        var m100 = Knock(109.0);
        output.WriteLine($"Livengood-Wu integrals: RON95 {ron95:F3}, E85 {e85:F3}, M100 {m100:F3}");

        ron95.Should().BeGreaterThan(e85, "gate: RON95 knocks before E85");
        e85.Should().BeGreaterThan(m100, "gate: E85 knocks before M100");
        ron95.Should().BeGreaterThan(0.05, "the integral must actually accumulate at CR 11");
    }

    [Fact]
    public void Variability_produces_reproducible_scatter()
    {
        double[] ImepSeries(ulong seed)
        {
            var engine = BuildFired(5000.0, octane: null, new CycleVariability(seed));
            engine.RunToConvergence(r => r.Imep[0], 5e-3, 5, 8);
            var series = new List<double>();
            for (var i = 0; i < 10; i++)
            {
                series.Add(engine.RunCycle().Imep[0]);
            }

            return series.ToArray();
        }

        var first = ImepSeries(42);
        var second = ImepSeries(42);
        second.Should().Equal(first, "same seed → bit-identical stochastic cycles (plan §3.4)");

        var different = ImepSeries(43);
        different.Should().NotEqual(first, "a different seed draws different cycles");

        var mean = first.Average();
        var cov = Math.Sqrt(first.Sum(x => (x - mean) * (x - mean)) / first.Length) / mean;
        output.WriteLine($"IMEP CoV over 10 cycles: {cov:P2}");
        cov.Should().BeInRange(0.002, 0.08, "IMEP CoV in the configured neighbourhood (1–3% typical)");
    }
}
