using FluentAssertions;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 10 §3.6 verification of the second wavetable axis: at least two load
/// lines, interpolated on both axes, in the crank-angle domain.
/// </summary>
public class LoadInterpolationTests(ITestOutputHelper output)
{
    /// <summary>A table whose samples are a constant, so blends are checkable by eye.</summary>
    private static CrankWavetable Flat(double rpm, double load, float value)
    {
        var samples = new float[720];
        Array.Fill(samples, value);
        return new CrankWavetable(rpm, samples, load);
    }

    /// <summary>A single harmonic, for phase-coherence checks.</summary>
    private static CrankWavetable Harmonic(double rpm, double load, double amplitude, int order)
    {
        var samples = new float[720];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * order * i / samples.Length));
        }

        return new CrankWavetable(rpm, samples, load);
    }

    [Fact]
    public void Gate_the_bank_interpolates_bilinearly_on_both_axes()
    {
        // Corners 10/20 at low load, 30/40 at high: the centre of the cell must
        // be the mean of all four, and each edge midpoint the mean of its two.
        var bank = new WavetableBank("test");
        bank.Add(Flat(2000.0, 0.4, 10f));
        bank.Add(Flat(4000.0, 0.4, 20f));
        bank.Add(Flat(2000.0, 1.0, 30f));
        bank.Add(Flat(4000.0, 1.0, 40f));

        bank.Loads.Should().Equal(0.4, 1.0);

        bank.SampleAt(2000.0, 0.0, 0.4).Should().BeApproximately(10.0, 1e-6);
        bank.SampleAt(4000.0, 0.0, 1.0).Should().BeApproximately(40.0, 1e-6);
        bank.SampleAt(3000.0, 0.0, 0.4).Should().BeApproximately(15.0, 1e-6, "midway in rpm on the low line");
        bank.SampleAt(2000.0, 0.0, 0.7).Should().BeApproximately(20.0, 1e-6, "midway in load at 2000 rpm");
        bank.SampleAt(3000.0, 0.0, 0.7).Should().BeApproximately(25.0, 1e-6, "the centre of the cell");
    }

    [Fact]
    public void Gate_load_blending_happens_in_the_crank_angle_domain()
    {
        // Two load lines carrying the same order at different amplitudes must
        // blend to a clean single harmonic of the intermediate amplitude — no
        // beating, no phase cancellation. Blending audio in the time domain
        // (which the plan forbids) would not do this.
        var bank = new WavetableBank("test");
        bank.Add(Harmonic(3000.0, 0.35, 1.0, order: 2));
        bank.Add(Harmonic(3000.0, 1.0, 3.0, order: 2));

        for (var angle = 0.0; angle < 720.0; angle += 7.0)
        {
            var expected = 2.0 * Math.Sin(2.0 * Math.PI * 2.0 * (angle / 720.0));
            bank.SampleAt(3000.0, angle, 0.675).Should().BeApproximately(expected, 1e-4);
        }
    }

    [Fact]
    public void Outside_the_grid_the_nearest_line_is_held_and_the_bank_says_so()
    {
        var bank = new WavetableBank("test");
        bank.Add(Flat(2000.0, 0.4, 10f));
        bank.Add(Flat(4000.0, 1.0, 40f));

        bank.Covers(3000.0, 0.7).Should().BeTrue();
        bank.Covers(9000.0, 0.7).Should().BeFalse("9000 rpm is past the grid");
        bank.Covers(3000.0, 0.1).Should().BeFalse("0.1 load is below the grid");

        // Held, never extrapolated: an extrapolated wavetable is not a result.
        bank.SampleAt(9000.0, 0.0, 1.0).Should().BeApproximately(40.0, 1e-6);
        bank.SampleAt(500.0, 0.0, 0.05).Should().BeApproximately(10.0, 1e-6);
    }

    [Fact]
    public void A_single_load_bank_behaves_exactly_as_before()
    {
        // The load axis must cost nothing where it is unused, or every existing
        // render changes.
        var bank = new WavetableBank("test");
        bank.Add(Flat(2000.0, 1.0, 10f));
        bank.Add(Flat(4000.0, 1.0, 20f));

        bank.SampleAt(3000.0, 0.0).Should().BeApproximately(15.0, 1e-6);
        bank.SampleAt(3000.0, 0.0, 0.2).Should().BeApproximately(15.0, 1e-6, "one line is used at every load");
        bank.MaxLoad.Should().Be(1.0);
    }

    [Fact]
    public void Gate_the_synthesiser_follows_a_load_profile()
    {
        // Amplitude must track the throttle track: quiet where the profile is
        // at cruise, loud where it is wide open.
        var bank = new WavetableBank("test");
        bank.Add(Harmonic(3000.0, 0.35, 0.2, order: 2));
        bank.Add(Harmonic(6000.0, 0.35, 0.2, order: 2));
        bank.Add(Harmonic(3000.0, 1.0, 1.0, order: 2));
        bank.Add(Harmonic(6000.0, 1.0, 1.0, order: 2));

        var rpm = RpmProfile.Steady(4500.0, 2.0);
        var lift = LoadProfile.LiftOff(2.0, liftAtSeconds: 1.0, cruiseLoad: 0.35);
        var stem = new WavetableSynthesizer(seed: 3).Render(
            bank, rpm, 48_000.0, SynthesisVariation.None, 0.0, lift);

        var open = Peak(stem, 0.0, 0.9);
        var cruise = Peak(stem, 1.3, 2.0);
        output.WriteLine($"wide open peak {open:F3}, after lift {cruise:F3}");

        open.Should().BeApproximately(1.0, 0.05);
        cruise.Should().BeApproximately(0.2, 0.05);

        static double Peak(AudioStem stem, double fromSeconds, double toSeconds)
        {
            var from = (int)(fromSeconds * stem.SampleRate);
            var to = Math.Min((int)(toSeconds * stem.SampleRate), stem.Samples.Length);
            var peak = 0.0;
            for (var i = from; i < to; i++)
            {
                peak = Math.Max(peak, Math.Abs(stem.Samples[i]));
            }

            return peak;
        }
    }

    [Fact]
    public void The_synthesiser_reports_how_much_of_a_render_was_held_at_an_edge()
    {
        var bank = new WavetableBank("test");
        bank.Add(Harmonic(3000.0, 1.0, 1.0, order: 2));
        bank.Add(Harmonic(4000.0, 1.0, 1.0, order: 2));

        var synth = new WavetableSynthesizer(seed: 5);

        synth.Render(bank, RpmProfile.Steady(3500.0, 0.5), 48_000.0, SynthesisVariation.None);
        synth.LastRenderHeldAtGridEdge.Should().Be(0.0, "3500 rpm is inside the grid");

        // Half the sweep sits above the grid.
        synth.Render(bank, RpmProfile.Sweep(3000.0, 5000.0, 0.5), 48_000.0, SynthesisVariation.None);
        output.WriteLine($"held at edge: {synth.LastRenderHeldAtGridEdge * 100:F0}%");
        synth.LastRenderHeldAtGridEdge.Should().BeApproximately(0.5, 0.02);
    }

    [Fact]
    public void Gate_a_lower_load_line_actually_solves_to_a_quieter_engine()
    {
        // The end-to-end claim: closing the throttle reduces trapped mass and
        // therefore the acoustic source. If the load fraction were not reaching
        // the solver this would come back identical.
        var document = ExampleModel();

        double TrappedMass(double load)
        {
            var engine = EngineBuilder.Build(document, 4000.0, cellSizeScale: null, intakeLoadFraction: load);
            var (result, _) = engine.RunToConvergence(
                r => r.NetValveMass.Length > 0 ? r.NetValveMass[0] : 0.0,
                document.Solver.ConvergenceTolerance, document.Solver.MinCycles, document.Solver.MaxCycles);
            return result.NetValveMass[0];
        }

        var wot = TrappedMass(1.0);
        var cruise = TrappedMass(0.35);

        output.WriteLine($"intake mass per cycle at 4000 rpm: WOT {wot * 1e6:F2} mg, 35% load {cruise * 1e6:F2} mg " +
                         $"(ratio {cruise / wot:F3})");

        cruise.Should().BeGreaterThan(0.0);
        cruise.Should().BeLessThan(wot * 0.6,
            "throttling to 35% manifold pressure must move much less air");
    }

    [Fact]
    public void Load_outside_the_physical_range_is_rejected_rather_than_clamped()
    {
        var document = ExampleModel();

        var tooHigh = () => EngineBuilder.Build(document, 3000.0, null, 1.5);
        tooHigh.Should().Throw<ArgumentOutOfRangeException>("boost is not a throttle position");

        var zero = () => EngineBuilder.Build(document, 3000.0, null, 0.0);
        zero.Should().Throw<ArgumentOutOfRangeException>();

        var profile = new LoadProfile();
        var bad = () => profile.Add(0.0, 0.0);
        bad.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static EngineModelDocument ExampleModel() => new()
    {
        Name = "load test single",
        Engine = new EngineSpec
        {
            BoreMm = 82, StrokeMm = 68, RodLengthMm = 120, CompressionRatio = 11, CylinderCount = 1,
        },
        IntakeValves = new ValveTrainSpec
        {
            HeadDiameterMm = 32, Count = 1, MaxLiftMm = 9.0, OpenDeg = 340, CloseDeg = 590,
        },
        ExhaustValves = new ValveTrainSpec
        {
            HeadDiameterMm = 28, Count = 1, MaxLiftMm = 8.5, OpenDeg = 130, CloseDeg = 380,
        },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 38 },
        ExhaustRunner = new DuctSpec { LengthMm = 500, DiameterMm = 36 },
        Solver = new SolverSpec { CellSizeMm = 10.0, MinCycles = 12, MaxCycles = 20 },
    };
}
