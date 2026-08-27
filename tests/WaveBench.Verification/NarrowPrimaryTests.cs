using FluentAssertions;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Regression suite for a defect that reached a reported torque figure as a
/// silent NaN.
///
/// <b>Symptom.</b> A four-cylinder engine whose exhaust primary was narrower
/// than the valve throats feeding it aborted with a non-positive density in
/// duct cell 0. Sharp threshold, independent of mesh and of speed: Ø34 mm
/// worked, Ø33 failed, at every mesh from 5 to 20 mm and every speed from 2000
/// to 7000 rpm. Two throats at 0.85 × 28 mm are 890 mm²; a Ø34 pipe is 908 mm²
/// and a Ø33 is 855, so it broke exactly where the valve could outflow the
/// pipe — which made a flow-limit explanation look obvious and cost three
/// wrong fixes.
///
/// <b>Cause, which was somewhere else entirely.</b> The Wiebe increment is
/// <c>xb − _previousBurnFraction</c>, and <c>_previousBurnFraction</c> started
/// at zero. A cylinder whose first step lands PAST its burn window therefore
/// saw xb ≈ 0.9933 against a stored zero and released the whole cycle's fuel
/// in one step. On a four-cylinder engine the cylinders start 180° apart, so
/// two of the four begin past the window on every run — one of them mid
/// exhaust-stroke with its valve open — and detonated into a cold pipe at 14
/// bar on the first degree of crank.
///
/// A wide pipe absorbed it and the transient washed out over the convergence
/// cycles, which is why it went unseen for so long. A narrow one did not.
///
/// <b>What identified it.</b> Not reasoning about the flux, which is where the
/// area coincidence pointed and where three attempted fixes failed. It was
/// noticing that one and two cylinders survived and four did not — the defect
/// was never about the pipe at all — and then that the failure happened at
/// 3.2° of crank, long before any exhaust valve of cylinder 1 opens.
/// </summary>
public class NarrowPrimaryTests(ITestOutputHelper output)
{
    private static EngineModelDocument FourCylinder(double primaryDiameterMm, double cellMm = 14.0) => new()
    {
        Name = "narrow primary",
        Engine = new EngineSpec
        {
            BoreMm = 82, StrokeMm = 78, RodLengthMm = 133, CompressionRatio = 10.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec
        {
            HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580,
        },
        ExhaustValves = new ValveTrainSpec
        {
            HeadDiameterMm = 28, Count = 2, MaxLiftMm = 9.5, OpenDeg = 140, CloseDeg = 370,
        },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
        ExhaustRunner = new DuctSpec { LengthMm = 500, DiameterMm = primaryDiameterMm },
        Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
        Solver = new SolverSpec { CellSizeMm = cellMm, MinCycles = 4, MaxCycles = 8 },
    };

    // ---- The cause --------------------------------------------------------

    [Fact]
    public void Gate_no_cylinder_burns_fuel_it_never_had()
    {
        // The root cause, tested where it lives. Every cylinder starts at
        // ambient with no compression behind it; none of them should release
        // any heat on the first step, whatever part of the cycle they happen
        // to begin in.
        var engine = EngineBuilder.Build(FourCylinder(33.0), 5500.0);

        var before = engine.Cylinders.Select(c => c.Pressure).ToList();
        engine.Step();

        for (var c = 0; c < engine.Cylinders.Count; c++)
        {
            var cylinder = engine.Cylinders[c];
            var local = cylinder.LocalAngle(engine.Angle);

            output.WriteLine(
                $"cylinder {c + 1}: local {local,6:F1}°  {before[c] / 1e5:F3} -> "
                + $"{cylinder.Pressure / 1e5:F3} bar, burned {cylinder.CumulativeFuelBurned * 1e6:F3} mg");

            cylinder.CumulativeFuelBurned.Should().Be(0.0,
                $"cylinder {c + 1} starts at local {local:F0}° with an uncompressed charge and must not fire");

            // Two of the four used to jump to 14 bar here.
            cylinder.Pressure.Should().BeLessThan(2e5,
                "nothing has compressed or burned yet, so nothing can be above about a bar");
        }
    }

    [Fact]
    public void A_cylinder_that_starts_past_its_burn_window_waits_for_the_next_one()
    {
        // Seeding the burn state must SUPPRESS the spurious release, not
        // suppress combustion outright. The cylinder that begins mid exhaust
        // stroke has to come round and fire normally on its next cycle.
        var engine = EngineBuilder.Build(FourCylinder(33.0), 5500.0);
        var midExhaust = engine.Cylinders[1]; // starts at local 180°

        engine.RunCycle();
        var afterFirst = midExhaust.CumulativeFuelBurned;

        engine.RunCycle();
        var afterSecond = midExhaust.CumulativeFuelBurned;

        output.WriteLine($"cylinder 2 burned {afterFirst * 1e6:F2} mg in cycle 1, "
                         + $"{(afterSecond - afterFirst) * 1e6:F2} mg in cycle 2");

        afterSecond.Should().BeGreaterThan(afterFirst,
            "a cylinder that starts past its window must still fire on the cycles that follow");
    }

    // ---- The symptom ------------------------------------------------------

    [Fact]
    public void Gate_a_primary_narrower_than_its_valves_solves()
    {
        var results = new List<(double Diameter, double Ve, double Torque)>();

        foreach (var diameter in new[] { 38.0, 34, 33, 31, 28, 26, 24 })
        {
            var result = OperatingPointRunner.Run(FourCylinder(diameter), 5500.0);
            results.Add((diameter, result.VolumetricEfficiency, result.TorqueNm));

            output.WriteLine($"Ø{diameter,5:F1} mm  VE {result.VolumetricEfficiency:F4}  "
                             + $"torque {result.TorqueNm,7:F2} N·m");

            double.IsFinite(result.VolumetricEfficiency).Should().BeTrue("a solve must produce a number");
            result.VolumetricEfficiency.Should().BeInRange(0.2, 2.0);
        }

        // The response is smooth and TURNS OVER rather than being monotone.
        // Narrowing a primary raises exhaust velocity and improves scavenging
        // before it starts to choke — which is why headers are sized rather
        // than simply made as large as will fit, and why an assertion that
        // "narrower must always breathe worse" would have been wrong.
        var best = results.MaxBy(r => r.Torque);
        output.WriteLine($"best torque at Ø{best.Diameter:F0} mm — an optimum, not an edge");

        best.Diameter.Should().BeLessThan(38.0).And.BeGreaterThan(24.0,
            "the optimum is interior, so the trend is not monotone in either direction");
        results[^1].Torque.Should().BeLessThan(best.Torque,
            "past the optimum the pipe becomes the restriction");
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(20.0)]
    public void It_solves_at_every_mesh_size(double cellMm)
    {
        var result = OperatingPointRunner.Run(FourCylinder(30.9, cellMm), 5500.0);
        output.WriteLine($"{cellMm,4:F0} mm cells  VE {result.VolumetricEfficiency:F4}");
        double.IsFinite(result.VolumetricEfficiency).Should().BeTrue();
        result.VolumetricEfficiency.Should().BeInRange(0.2, 2.0);
    }

    [Theory]
    [InlineData(2000.0)]
    [InlineData(7000.0)]
    public void It_solves_at_every_speed(double rpm)
    {
        var result = OperatingPointRunner.Run(FourCylinder(30.9), rpm);
        output.WriteLine($"{rpm,5:F0} rpm  VE {result.VolumetricEfficiency:F4}");
        double.IsFinite(result.VolumetricEfficiency).Should().BeTrue();
        result.VolumetricEfficiency.Should().BeInRange(0.2, 2.0);
    }

    [Fact]
    public void The_fix_barely_moves_a_converged_answer()
    {
        // The startup artefact was violent but brief, and a converged periodic
        // solution should not remember it. That it moved an ordinary Ø38
        // primary by eight parts per million is the evidence the fix is
        // surgical rather than a change of physics — and the reason no other
        // committed figure in the suite moved at all.
        var result = OperatingPointRunner.Run(FourCylinder(38.0), 5500.0);

        output.WriteLine($"Ø38 mm: VE {result.VolumetricEfficiency:F6}, torque {result.TorqueNm:F6} N·m");
        output.WriteLine("before the fix: VE 1.060378, torque 219.969680 N·m");

        result.VolumetricEfficiency.Should().BeApproximately(1.060383, 1e-5);
        result.TorqueNm.Should().BeApproximately(219.967828, 1e-4);

        // Within a hundredth of a percent of the pre-fix figure.
        Math.Abs((result.TorqueNm / 219.969680) - 1.0).Should().BeLessThan(1e-4);
    }
}
