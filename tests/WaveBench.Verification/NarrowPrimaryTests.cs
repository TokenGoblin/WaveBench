using FluentAssertions;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// OPEN DEFECT — a primary narrower than the valves feeding it aborts the run.
///
/// <b>These tests are skipped because the defect is real and unfixed.</b> They
/// are committed rather than deleted because the reproduction is the expensive
/// part: remove the Skip and they characterise the failure exactly.
///
/// <b>Symptom.</b> When the exhaust valve throats total more area than the
/// primary they open into, duct cell 0 reaches a non-positive density in the
/// conservative update. It used to propagate as a silent NaN all the way to a
/// reported torque figure; <c>DuctSolver.UpdateConserved</c> now throws with
/// the pipe named, which is the only part of this so far that IS fixed.
///
/// <b>Threshold.</b> Sharp, and independent of both mesh and speed. On an
/// 82 mm bore four with two 28 mm exhaust valves: Ø34 mm works, Ø33 fails, at
/// every mesh from 5 to 20 mm and every speed from 2000 to 7000 rpm. Two
/// throats at 0.85 × 28 mm are 890 mm²; a Ø34 pipe is 908 mm² and a Ø33 is
/// 855. It breaks exactly where the valve can outflow the pipe.
///
/// <b>Mesh independence is the strongest clue.</b> dt scales with dx, so the
/// fraction of a cell removed per step is the same however fine the mesh.
/// That rules out the timestep and points at the imposed flux itself.
///
/// <b>Three hypotheses tried and falsified</b> — recorded so the next attempt
/// does not repeat them:
/// <list type="number">
/// <item>Bounding dt by the imposed end flux. No effect: the network sets the
/// flux override AFTER asking the duct for dt, so at valve opening the value
/// in hand is the previous step's, which is zero.</item>
/// <item>Clamping the face state to sonic when the face-pressure bracket
/// degenerates. No effect, so that branch is not the path being taken.</item>
/// <item>Clamping the face state to sonic AFTER the bisection, whenever the
/// solved face would be supersonic into the duct. This made things WORSE — it
/// engaged on Ø34, which had been solving correctly, and broke it. So either
/// the sonic pressure derived from R₋ is wrong, or a supersonic solved face is
/// not the mechanism.</item>
/// </list>
/// </summary>
public class NarrowPrimaryTests(ITestOutputHelper output)
{
    private const string Open =
        "Open solver defect: a primary narrower than its valve throats drives duct cell 0 to a "
        + "non-positive density. See the class remarks for the threshold and for three falsified "
        + "hypotheses.";

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

    [Fact(Skip = Open)]
    public void A_primary_narrower_than_its_valves_should_still_solve()
    {
        var results = new List<(double Diameter, double Ve)>();

        foreach (var diameter in new[] { 38.0, 34, 33, 31, 28, 26, 24 })
        {
            var result = OperatingPointRunner.Run(FourCylinder(diameter), 5500.0);
            results.Add((diameter, result.VolumetricEfficiency));

            output.WriteLine($"Ø{diameter,5:F1} mm  VE {result.VolumetricEfficiency:F4}  "
                             + $"torque {result.TorqueNm,7:F2} N·m");

            double.IsFinite(result.VolumetricEfficiency).Should().BeTrue("a solve must produce a number");
        }

        // And the physics has to be right, not merely finite: a primary
        // narrower than the valves feeding it is a restriction, so breathing
        // must fall away as it closes down.
        results[^1].Ve.Should().BeLessThan(results[0].Ve,
            "a 24 mm primary must breathe measurably worse than a 38 mm one");
    }

    [Theory(Skip = Open)]
    [InlineData(5.0)]
    [InlineData(20.0)]
    public void The_failure_is_independent_of_mesh_size(double cellMm)
    {
        var result = OperatingPointRunner.Run(FourCylinder(30.9, cellMm), 5500.0);
        output.WriteLine($"{cellMm,4:F0} mm cells  VE {result.VolumetricEfficiency:F4}");
        double.IsFinite(result.VolumetricEfficiency).Should().BeTrue();
    }

    [Theory(Skip = Open)]
    [InlineData(2000.0)]
    [InlineData(7000.0)]
    public void The_failure_is_independent_of_speed(double rpm)
    {
        var result = OperatingPointRunner.Run(FourCylinder(30.9), rpm);
        output.WriteLine($"{rpm,5:F0} rpm  VE {result.VolumetricEfficiency:F4}");
        double.IsFinite(result.VolumetricEfficiency).Should().BeTrue();
    }

    /// <summary>
    /// NOT skipped. The boundary of the defect is itself worth pinning: a
    /// primary comfortably wider than its valves must keep solving, and must
    /// keep returning the same answer, whatever is eventually done about the
    /// narrow case. A robustness fix that quietly moves every other result is
    /// not a robustness fix.
    /// </summary>
    [Fact]
    public void An_ordinary_primary_solves_and_its_answer_is_pinned()
    {
        var result = OperatingPointRunner.Run(FourCylinder(38.0), 5500.0);

        output.WriteLine($"Ø38 mm: VE {result.VolumetricEfficiency:F6}, torque {result.TorqueNm:F6} N·m");

        result.VolumetricEfficiency.Should().BeApproximately(1.060378, 1e-5);
        result.TorqueNm.Should().BeApproximately(219.969680, 1e-4);
    }

    /// <summary>
    /// Also not skipped: the failure must remain LOUD. A silent NaN reaching a
    /// reported torque figure is the part of this that was genuinely
    /// dangerous, and it must not come back.
    /// </summary>
    [Fact]
    public void The_narrow_case_fails_loudly_rather_than_returning_a_number()
    {
        var act = () => OperatingPointRunner.Run(FourCylinder(30.9), 5500.0);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-physical density*")
            .WithMessage("*Ø30.9 mm*", "the message has to name the geometry that caused it");
    }
}
