using FluentAssertions;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// The x–t wave field behind the Phase 19 wave diagram (plan §8.4).
///
/// A field capture is only worth its memory if the thing it is supposed to
/// show is actually in it. What the diagram shows is a wave as a DIAGONAL
/// STREAK across distance and crank angle, whose slope is the wave speed and
/// whose reflection at a termination is a visible change of direction. These
/// check exactly that, by measuring the slope out of the recorded frames.
/// </summary>
public class DuctFieldCaptureTests(ITestOutputHelper output)
{
    private static EngineModelDocument FourCylinder() => new()
    {
        Name = "field capture",
        Engine = new EngineSpec
        {
            BoreMm = 82, StrokeMm = 56.5, RodLengthMm = 100, CompressionRatio = 12, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec
        {
            HeadDiameterMm = 29, Count = 2, MaxLiftMm = 9.5, OpenDeg = 340, CloseDeg = 590,
        },
        ExhaustValves = new ValveTrainSpec
        {
            HeadDiameterMm = 24, Count = 2, MaxLiftMm = 9.0, OpenDeg = 130, CloseDeg = 380,
        },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 36, RoughnessMm = 0.045 },
        ExhaustRunner = new DuctSpec { LengthMm = 600, DiameterMm = 34, RoughnessMm = 0.045 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
        Solver = new SolverSpec { CellSizeMm = 10.0, MinCycles = 5, MaxCycles = 12 },
    };

    [Fact]
    public void Frames_are_evenly_spaced_in_crank_angle_not_in_solver_steps()
    {
        // The reason for decimating on angle. Timesteps are CFL-limited, so
        // they shorten wherever the gas is hot — during and after blowdown.
        // Frames recorded per step would bunch there, and an animation played
        // at a constant rate would appear to slow down for no physical reason.
        var document = FourCylinder();
        var engine = EngineBuilder.Build(document, 6000.0);
        var exhaust = engine.Ducts.First(d => d.Geometry.Length > 0.5);

        const int samplesPerCycle = 720;
        var field = engine.AddFieldCapture(exhaust, "pri1", FieldQuantity.Pressure, samplesPerCycle, 2);

        engine.RunToConvergence(r => r.NetValveMass[0], 1e-3, 5, 12);
        engine.CaptureCycles(2);

        field.FrameCount.Should().BeInRange(samplesPerCycle * 2 - 4, (samplesPerCycle * 2) + 4);

        var gaps = new List<double>();
        for (var i = 1; i < field.FrameCount; i++)
        {
            gaps.Add(field.FrameAngles[i] - field.FrameAngles[i - 1]);
        }

        var nominal = 720.0 / samplesPerCycle;
        output.WriteLine($"{field.FrameCount} frames, gap {gaps.Min():F4}–{gaps.Max():F4}° against a nominal {nominal:F4}°");
        output.WriteLine($"buffer {field.Bytes / 1024.0:F0} KiB for {field.CellCount} cells");

        gaps.Should().OnlyContain(g => g > 0, "angle must increase monotonically");

        // Every gap within one timestep's worth of the nominal spacing. It
        // cannot be exact — a frame lands on the first step at or past the
        // boundary — but it must not drift or bunch.
        gaps.Max().Should().BeLessThan(nominal * 1.6);
        gaps.Average().Should().BeApproximately(nominal, nominal * 0.02);
    }

    [Fact]
    public void Gate_a_wave_appears_as_a_diagonal_whose_slope_is_the_wave_speed()
    {
        // The diagram's whole content. Track the pressure peak's position
        // frame by frame in a plain pipe and the trace must be a straight line
        // in x–t whose gradient is the local wave speed — that diagonal IS the
        // wave, and reading its slope is how a user checks a return.
        const double length = 2.0;
        const int cells = 400;
        const double t0 = 293.15;
        const double p0 = 101_325.0;

        var gas = new PerfectGasModel(PerfectGas.Air);
        var duct = new DuctSolver(DuctGeometry.Uniform(length, cells, 0.05, 0.0), gas)
        {
            Limiter = SlopeLimiterKind.VanLeer,
            Cfl = 0.8,
        };

        var rho0 = p0 / (PerfectGas.Air.SpecificGasConstant * t0);
        for (var i = 0; i < cells; i++)
        {
            duct.SetState(i, new PrimitiveState(rho0, 0.0, p0));
        }

        for (var i = 20; i < 32; i++)
        {
            duct.SetState(i, new PrimitiveState(rho0 * 1.104, 0.0, p0 * 1.15));
        }

        duct.LeftBoundary = BoundaryKind.Reflective;
        duct.RightBoundary = BoundaryKind.Reflective;

        var soundSpeed = Math.Sqrt(PerfectGas.Air.Gamma * PerfectGas.Air.SpecificGasConstant * t0);

        // Drive the capture by hand: this is a bare duct, not an engine, so
        // there is no crank. Feed it a synthetic angle at 6000 rpm.
        var field = new DuctFieldCapture(duct, "pipe", FieldQuantity.Pressure, samplesPerCycle: 3600);
        var offer = typeof(DuctFieldCapture).GetMethod(
            "Offer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        const double degPerSecond = 6.0 * 6000.0;
        var t = 0.0;
        var horizon = 0.9 * length / soundSpeed;
        while (t < horizon)
        {
            var dt = duct.StableTimestep();
            duct.Step(dt);
            t += dt;
            offer.Invoke(field, [t * degPerSecond]);
        }

        field.FrameCount.Should().BeGreaterThan(20);

        // Peak position per frame, in metres and seconds.
        var xs = new List<double>();
        var ts = new List<double>();
        for (var f = 0; f < field.FrameCount; f++)
        {
            var frame = field.Frame(f);
            var peak = 0;
            for (var i = 1; i < frame.Length; i++)
            {
                if (frame[i] > frame[peak])
                {
                    peak = i;
                }
            }

            xs.Add(field.CellCentres[peak]);
            ts.Add(field.FrameAngles[f] / degPerSecond);
        }

        // Least-squares gradient dx/dt over the middle of the run, clear of
        // the launch transient and of the far wall.
        var from = field.FrameCount / 5;
        var to = field.FrameCount * 4 / 5;
        var n = to - from;
        var meanT = ts.GetRange(from, n).Average();
        var meanX = xs.GetRange(from, n).Average();
        var sxy = 0.0;
        var sxx = 0.0;
        for (var i = from; i < to; i++)
        {
            sxy += (ts[i] - meanT) * (xs[i] - meanX);
            sxx += (ts[i] - meanT) * (ts[i] - meanT);
        }

        var speed = sxy / sxx;

        // The comparator is the FINITE-AMPLITUDE speed, not the small-signal
        // a. A peak rides on gas the wave has itself compressed and set in
        // motion, so it travels at a_local + u, and Blair gives both from the
        // pressure amplitude ratio (Design and Simulation of Four-Stroke
        // Engines, §2.2):
        //
        //   X = (p/p_ref)^((γ−1)/2γ),  a_local = a_ref·X,  u = 2a_ref(X−1)/(γ−1)
        //
        // Measuring 387 m/s against a = 343 is not a 13% error, it is a
        // correctly resolved finite-amplitude wave — which is the entire
        // reason this project runs a nonlinear solver instead of an acoustic
        // one, and it has to be visible in the diagram.
        var gamma = PerfectGas.Air.Gamma;
        var x = Math.Pow(1.15, (gamma - 1.0) / (2.0 * gamma));
        var localSoundSpeed = soundSpeed * x;
        var particleVelocity = 2.0 * soundSpeed * (x - 1.0) / (gamma - 1.0);
        var expected = localSoundSpeed + particleVelocity;

        output.WriteLine(
            $"{field.FrameCount} frames; peak travels at {speed:F1} m/s. "
            + $"Small-signal a = {soundSpeed:F1}; finite-amplitude a+u = {localSoundSpeed:F1} + "
            + $"{particleVelocity:F1} = {expected:F1} m/s "
            + $"({100.0 * (speed / expected - 1.0):+0.0;-0.0}% from it)");

        speed.Should().BeApproximately(expected, 0.03 * expected,
            "the diagonal's gradient in x–t is the wave speed, which is what makes the diagram readable");
        speed.Should().BeGreaterThan(soundSpeed * 1.05,
            "a finite-amplitude compression outruns the small-signal sound speed, and the diagram must show that");
        speed.Should().BePositive("this wave runs toward +x");
    }

    [Fact]
    public void The_colour_scale_spans_the_whole_capture_not_one_frame()
    {
        // A scale re-normalised per frame makes a decaying wave look constant
        // and hides exactly what the diagram exists to show.
        var document = FourCylinder();
        var engine = EngineBuilder.Build(document, 6000.0);
        var exhaust = engine.Ducts.First(d => d.Geometry.Length > 0.5);
        var field = engine.AddFieldCapture(exhaust, "pri1", FieldQuantity.Pressure, 360, 2);

        engine.RunToConvergence(r => r.NetValveMass[0], 1e-3, 5, 12);
        engine.CaptureCycles(1);

        var (min, max) = field.Range();
        output.WriteLine($"pressure over the whole cycle: {min / 1000:F1}–{max / 1000:F1} kPa");

        max.Should().BeGreaterThan(min);
        min.Should().BeGreaterThan(0f, "an absolute pressure cannot be negative");

        // Blowdown puts the exhaust well above atmospheric at some point in
        // the cycle and the wave action pulls it below at another. A range
        // that failed to span 101 kPa would mean the capture missed the event.
        max.Should().BeGreaterThan(150_000f);
        min.Should().BeLessThan(101_325f);

        // Every frame must lie inside the global range, which is the property
        // a renderer relies on to map colour without clipping.
        for (var f = 0; f < field.FrameCount; f++)
        {
            foreach (var v in field.Frame(f))
            {
                v.Should().BeInRange(min, max);
            }
        }
    }

    [Fact]
    public void Scrubbing_lands_on_the_nearest_frame()
    {
        var document = FourCylinder();
        var engine = EngineBuilder.Build(document, 6000.0);
        var exhaust = engine.Ducts.First(d => d.Geometry.Length > 0.5);
        var field = engine.AddFieldCapture(exhaust, "pri1", FieldQuantity.Pressure, 360, 2);

        engine.RunToConvergence(r => r.NetValveMass[0], 1e-3, 5, 12);
        engine.CaptureCycles(1);

        var target = field.FrameAngles[field.FrameCount / 3];
        var index = field.FrameAt(target);
        field.FrameAngles[index].Should().BeApproximately(target, 1e-9);

        // Off-grid angles land on the nearest frame, not the next one.
        var between = (field.FrameAngles[10] + field.FrameAngles[11]) / 2.0 - 0.01;
        field.FrameAt(between).Should().Be(10);

        field.FrameAt(double.MinValue).Should().Be(0);
        field.FrameAt(double.MaxValue).Should().Be(field.FrameCount - 1);
    }

    [Fact]
    public void The_memory_cost_is_stated_before_it_is_paid()
    {
        // Cells × frames × 4 bytes is the one structure in the product whose
        // size can surprise, so a caller must be able to ask first.
        DuctFieldCapture.EstimateBytes(400, 720, 30).Should().Be(400L * 720 * 30 * 4);

        var mib = DuctFieldCapture.EstimateBytes(400, 720, 30) / 1024.0 / 1024.0;
        output.WriteLine($"400 cells, 720 frames/cycle, 30 cycles = {mib:F1} MiB per pipe per quantity");
        mib.Should().BeLessThan(64.0, "a 30-cycle capture must stay inside a sane working set");
    }

    [Fact]
    public void A_capture_can_only_be_attached_to_a_duct_of_this_engine()
    {
        var engine = EngineBuilder.Build(FourCylinder(), 6000.0);
        var stranger = new DuctSolver(
            DuctGeometry.Uniform(0.5, 10, 0.04, 0.0), new PerfectGasModel(PerfectGas.Air));

        var act = () => engine.AddFieldCapture(stranger, "nope");
        act.Should().Throw<ArgumentException>().WithMessage("*not part of this engine*");
    }
}
