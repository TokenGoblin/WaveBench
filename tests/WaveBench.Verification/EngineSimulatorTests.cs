using FluentAssertions;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 5 gate: a motored single-cylinder engine produces a sensible VE
/// curve with a visible tuning peak near the organ-pipe estimate; mass and
/// energy conserve; repeated runs are bit-identical.
/// </summary>
public class EngineSimulatorTests(ITestOutputHelper output)
{
    private static readonly PerfectGas Gas = new(1.4, 287.05);

    // A sane short-stroke single (360 cc, 86×62): mean piston speed stays
    // below 18 m/s across the sweep, so breathing limits don't mask tuning.
    private static readonly CrankGeometry Crank = new()
    {
        Bore = 0.086,
        Stroke = 0.062,
        RodLength = 0.107,
        CompressionRatio = 11.0,
    };

    private const double RunnerLength = 0.60;
    private const double AmbientP = 1.0e5;
    private const double AmbientT = 300.0;
    private const double IntakeOpen = 340.0;
    private const double IntakeClose = 580.0;

    private static EngineSimulator BuildEngine(double rpm, bool sealedEnds = false)
    {
        var gasModel = new PerfectGasModel(Gas);
        var rho0 = AmbientP / (Gas.SpecificGasConstant * AmbientT);

        var intake = new DuctSolver(DuctGeometry.Uniform(RunnerLength, 100, 0.038), gasModel);
        var exhaust = new DuctSolver(DuctGeometry.Uniform(0.20, 34, 0.035), gasModel);
        foreach (var duct in new[] { intake, exhaust })
        {
            for (var i = 0; i < duct.CellCount; i++)
            {
                duct.SetState(i, new PrimitiveState(rho0, 0.0, AmbientP));
            }
        }

        if (sealedEnds)
        {
            intake.LeftBoundary = BoundaryKind.Reflective;
            exhaust.RightBoundary = BoundaryKind.Reflective;
        }
        else
        {
            intake.LeftBoundary = BoundaryKind.External;
            intake.LeftEnd = new ReservoirBoundary { StagnationPressure = AmbientP, StagnationTemperature = AmbientT };
            exhaust.RightBoundary = BoundaryKind.External;
            exhaust.RightEnd = new ReservoirBoundary { StagnationPressure = AmbientP, StagnationTemperature = AmbientT };
        }

        var cylinder = new Cylinder(gasModel, Crank, 0.0, AmbientP, AmbientT);

        // 4-valve pent-roof sizing for an 86 mm bore: 2×31 mm intake,
        // 2×26 mm exhaust, 10 mm lift.
        var intakeValve = new ValveConnection(
            cylinder, intake, ductLeftEnd: false,
            CamProfile.Harmonic(IntakeOpen, IntakeClose, 0.010),
            new ValveGeometry { HeadDiameter = 0.031, ValveCount = 2 });
        var exhaustValve = new ValveConnection(
            cylinder, exhaust, ductLeftEnd: true,
            CamProfile.Harmonic(140.0, 380.0, 0.010),
            new ValveGeometry { HeadDiameter = 0.026, ValveCount = 2 });

        var engine = new EngineSimulator { Rpm = rpm };
        engine.Ducts.Add(intake);
        engine.Ducts.Add(exhaust);
        engine.Cylinders.Add(cylinder);
        engine.Valves.Add(intakeValve);   // index 0 = intake
        engine.Valves.Add(exhaustValve);
        return engine;
    }

    private static double RunToVe(double rpm, out int cycles)
    {
        var engine = BuildEngine(rpm);
        var rhoRef = AmbientP / (Gas.SpecificGasConstant * AmbientT);
        var (result, n) = engine.RunToConvergence(r => r.NetValveMass[0], tolerance: 1e-3, minCycles: 5, maxCycles: 25);
        cycles = n;
        return result.NetValveMass[0] / (rhoRef * Crank.DisplacedVolume);
    }

    [Fact]
    public void Gate_ve_curve_has_a_tuning_peak_near_the_organ_pipe_estimate()
    {
        var rpms = new List<double>();
        var ves = new List<double>();
        for (var rpm = 4500.0; rpm <= 8500.0; rpm += 500.0)
        {
            var ve = RunToVe(rpm, out var cycles);
            rpms.Add(rpm);
            ves.Add(ve);
            output.WriteLine($"{rpm,6:F0} rpm  VE = {ve:F4}  ({cycles} cycles)");
        }

        ves.Should().OnlyContain(v => v > 0.5 && v < 1.35, "VE must be physically sensible");

        var peakIndex = ves.IndexOf(ves.Max());
        peakIndex.Should().BeInRange(1, rpms.Count - 2, "the tuning peak must lie inside the sweep");

        var peakRpm = rpms[peakIndex];
        var edgeMin = Math.Min(ves[0], ves[^1]);
        ves[peakIndex].Should().BeGreaterThan(edgeMin + 0.02, "the peak must be a visible tuning feature");

        // Organ-pipe estimate (plan §2.10) with the geometry-derived window:
        // launch at max piston speed after overlap TDC, return by the
        // effective intake closing (25% lift).
        var window = QuickEstimate.IntakeWaveReturnWindowDeg(
            Crank, CamProfile.Harmonic(IntakeOpen, IntakeClose, 0.010));
        var a0 = Gas.SoundSpeed(AmbientP / (Gas.SpecificGasConstant * AmbientT), AmbientP);
        var estimate = QuickEstimate.OrganPipeTunedRpm(a0, window, RunnerLength);
        output.WriteLine($"peak {peakRpm:F0} rpm; organ-pipe estimate {estimate:F0} rpm (window {window:F0}°)");

        // Gate: within ~5% (half the sweep step gives ±250 rpm resolution).
        Math.Abs(peakRpm - estimate).Should().BeLessThan(estimate * 0.05 + 250.0,
            $"gate: tuning peak ({peakRpm:F0}) within ~5% of the organ-pipe estimate ({estimate:F0})");
    }

    [Fact]
    public void Gate_sealed_engine_conserves_mass_and_closes_the_energy_budget()
    {
        var engine = BuildEngine(8000.0, sealedEnds: true);
        var mass0 = engine.TotalMass();
        var energy0 = engine.Ducts.Sum(d => d.ConservedTotals().Energy) + engine.Cylinders[0].Energy;

        engine.RunCycle();
        engine.RunCycle();

        var mass1 = engine.TotalMass();
        var energy1 = engine.Ducts.Sum(d => d.ConservedTotals().Energy) + engine.Cylinders[0].Energy;
        var work = engine.CumulativePistonWork;

        mass1.Should().BeApproximately(mass0, mass0 * 1e-6,
            $"gate: mass conserved (Δ = {(mass1 - mass0) / mass0:E2} relative)");

        // Adiabatic sealed system: ΔE = −(work done by gas on piston).
        var closure = Math.Abs(energy1 - energy0 + work) / Math.Max(Math.Abs(work), 1.0);
        closure.Should().BeLessThan(1e-3,
            $"gate: energy budget closes to 0.1% (ΔE = {energy1 - energy0:F2} J, work = {work:F2} J)");
    }

    [Fact]
    public void Gate_repeated_runs_are_bit_identical()
    {
        double[] Run()
        {
            var engine = BuildEngine(9000.0);
            engine.RunCycle();
            engine.RunCycle();
            engine.RunCycle();

            var cylinder = engine.Cylinders[0];
            var probes = new List<double> { cylinder.Mass, cylinder.Energy, cylinder.Pressure };
            foreach (var duct in engine.Ducts)
            {
                for (var i = 0; i < duct.CellCount; i += 7)
                {
                    var w = duct.GetPrimitive(i);
                    probes.Add(w.Rho);
                    probes.Add(w.U);
                    probes.Add(w.P);
                }
            }

            return probes.ToArray();
        }

        var first = Run();
        var second = Run();

        second.Should().Equal(first, "gate: same input → bit-identical results (plan Part 0 rule 6)");
    }
}
