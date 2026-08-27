using FluentAssertions;
using WaveBench.Analysis;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 19 gate: <i>"wave decomposition correctly identifies the reflected
/// expansion arrival in a known textbook case"</i>.
///
/// The textbook case is the one every gas-dynamics course opens with: a
/// pressure pulse launched down a pipe reflects off an OPEN end as an
/// expansion and off a CLOSED end as a compression, and the reflection returns
/// to a probe at t = 2L/a. Both the SIGN and the TIMING are known in advance,
/// which is what makes it a test rather than a demonstration.
///
/// Method under test is Blair's superposition decomposition (Blair, Design and
/// Simulation of Four-Stroke Engines, SAE 1999, §2.2–2.5).
/// </summary>
public class WaveDecompositionTests(ITestOutputHelper output)
{
    private const double ReferencePressure = 101_325.0;
    private const double ReferenceTemperature = 293.15;
    private static readonly double Gamma = PerfectGas.Air.Gamma;

    private static double ReferenceSoundSpeed =>
        Math.Sqrt(Gamma * PerfectGas.Air.SpecificGasConstant * ReferenceTemperature);

    // ---- The algebra, before any solver is involved ----------------------

    [Fact]
    public void Undisturbed_gas_decomposes_into_two_undisturbed_waves()
    {
        var w = WaveDecomposition.At(ReferencePressure, 0.0, ReferencePressure, ReferenceSoundSpeed, Gamma);

        w.RightwardRatio.Should().BeApproximately(1.0, 1e-12);
        w.LeftwardRatio.Should().BeApproximately(1.0, 1e-12);
        w.RightwardPressurePa.Should().BeApproximately(ReferencePressure, 1e-6);
        w.LeftwardPressurePa.Should().BeApproximately(ReferencePressure, 1e-6);
    }

    [Fact]
    public void A_single_travelling_wave_decomposes_entirely_into_that_direction()
    {
        // The definitive check on the algebra. For a pure rightward wave the
        // pressure and velocity are not independent — Blair's relation ties
        // them — so feeding a consistent pair must give X_l exactly 1.
        const double xr = 1.15;
        var pressure = ReferencePressure * Math.Pow(xr, 2.0 * Gamma / (Gamma - 1.0));
        var velocity = 2.0 * ReferenceSoundSpeed / (Gamma - 1.0) * (xr - 1.0);

        var w = WaveDecomposition.At(pressure, velocity, ReferencePressure, ReferenceSoundSpeed, Gamma);

        output.WriteLine($"pure rightward X_r={xr}: p {pressure / 1000:F1} kPa, u {velocity:F1} m/s "
                         + $"-> X_r {w.RightwardRatio:F6}, X_l {w.LeftwardRatio:F6}");

        w.RightwardRatio.Should().BeApproximately(xr, 1e-12);
        w.LeftwardRatio.Should().BeApproximately(1.0, 1e-12,
            "a wave travelling one way must have no component running the other");

        // And the mirror image, with the velocity reversed.
        var mirror = WaveDecomposition.At(pressure, -velocity, ReferencePressure, ReferenceSoundSpeed, Gamma);
        mirror.LeftwardRatio.Should().BeApproximately(xr, 1e-12);
        mirror.RightwardRatio.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Superposing_two_known_waves_recovers_both()
    {
        // Build a superposition from known parts and check it comes back
        // apart. This is the property the UI depends on: at a point where an
        // outgoing pulse and a returning reflection overlap, the raw trace is
        // uninformative and only the split is not.
        const double xr = 1.20;
        const double xl = 0.85;

        var x = xr + xl - 1.0;
        var pressure = ReferencePressure * Math.Pow(x, 2.0 * Gamma / (Gamma - 1.0));
        var velocity = 2.0 * ReferenceSoundSpeed / (Gamma - 1.0) * (xr - xl);

        var w = WaveDecomposition.At(pressure, velocity, ReferencePressure, ReferenceSoundSpeed, Gamma);

        output.WriteLine($"superposed p {pressure / 1000:F1} kPa, u {velocity:F1} m/s "
                         + $"-> X_r {w.RightwardRatio:F6} (want {xr}), X_l {w.LeftwardRatio:F6} (want {xl})");

        w.RightwardRatio.Should().BeApproximately(xr, 1e-12);
        w.LeftwardRatio.Should().BeApproximately(xl, 1e-12);

        // The give-away that superposition is NOT simple pressure addition:
        // a 20% compression and a 15% expansion do not cancel to reference.
        pressure.Should().NotBeApproximately(ReferencePressure, 100.0);
    }

    // ---- The textbook case, through the actual solver --------------------

    /// <summary>
    /// Launch a compression pulse into a pipe and watch what comes back.
    /// The probe sits near the closed left end; the pulse runs right, reflects
    /// off the far termination and returns.
    /// </summary>
    private (List<double> Angle, List<float> Pressure, List<float> Velocity, double Length, double SoundSpeed)
        RunPulse(bool openEnd)
    {
        const double length = 2.0;
        const int cells = 400;
        var gas = new PerfectGasModel(PerfectGas.Air);
        var duct = new DuctSolver(DuctGeometry.Uniform(length, cells, 0.05, 0.0), gas)
        {
            Limiter = SlopeLimiterKind.VanLeer,
            Cfl = 0.8,
        };

        var rho0 = ReferencePressure / (PerfectGas.Air.SpecificGasConstant * ReferenceTemperature);
        for (var i = 0; i < cells; i++)
        {
            duct.SetState(i, new PrimitiveState(rho0, 0.0, ReferencePressure));
        }

        // Probe a quarter of the way along, and put the pulse ON it. Starting
        // the pulse anywhere else adds its own travel to the probe into the
        // arrival time, and the textbook formula 2(L−x)/a would then be
        // comparing against the wrong journey.
        var probeCell = cells / 4;

        // A compression bump at rest: it splits into equal waves running both
        // ways. The rightward half is the one whose return we time.
        //
        // 1.15× and not 1.5×: the textbook arrival time is the SMALL-SIGNAL
        // one, and a finite-amplitude front outruns it. Keeping the pulse weak
        // enough for linear acoustics to hold is what lets the timing be
        // asserted tightly instead of hidden behind a loose tolerance.
        const int pulseCells = 12;
        for (var i = probeCell - (pulseCells / 2); i < probeCell + (pulseCells / 2); i++)
        {
            duct.SetState(i, new PrimitiveState(rho0 * 1.104, 0.0, ReferencePressure * 1.15));
        }

        duct.LeftBoundary = BoundaryKind.Reflective;
        if (openEnd)
        {
            duct.RightBoundary = BoundaryKind.External;
            duct.RightEnd = new ReservoirBoundary
            {
                StagnationPressure = ReferencePressure,
                StagnationTemperature = ReferenceTemperature,
            };
        }
        else
        {
            duct.RightBoundary = BoundaryKind.Reflective;
        }

        var probeX = duct.CellCentre(probeCell);
        var soundSpeed = Math.Sqrt(Gamma * PerfectGas.Air.SpecificGasConstant * ReferenceTemperature);

        // Long enough for the far reflection to come back past the probe, and
        // short enough not to reach the SECOND return: the leftward half
        // bounces off the closed left end, runs the full pipe and comes back
        // at 2L/a + 2(L−x)/a, and searching past that would find it instead.
        var horizon = 1.9 * (length - probeX) / soundSpeed * 2.0;

        var times = new List<double>();
        var pressure = new List<float>();
        var velocity = new List<float>();

        var t = 0.0;
        while (t < horizon)
        {
            var dt = duct.StableTimestep();
            duct.Step(dt);
            t += dt;
            times.Add(t);
            pressure.Add((float)duct.GetPressure(probeCell));
            velocity.Add((float)duct.GetVelocity(probeCell));
        }

        _ = probeX;
        return (times, pressure, velocity, length, soundSpeed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gate_the_textbook_reflection_is_identified_with_the_right_sign_and_the_right_arrival(bool openEnd)
    {
        var (times, pressure, velocity, length, soundSpeed) = RunPulse(openEnd);

        var components = WaveDecomposition.Series(
            pressure.ToArray(), velocity.ToArray(), ReferencePressure, soundSpeed, Gamma);

        // Time stands in for crank angle here; the search is the same code.
        // Search only after the pulse has cleared the probe. At t = 0 the bump
        // splits into equal waves running BOTH ways, so its leftward half is a
        // leftward compression sitting right on the probe — and for the closed
        // end that is the same sign as the reflection being looked for. One
        // pulse width is 12 cells, 0.17 ms; 1 ms is six times that and still
        // eight times earlier than any reflection can return, so the window is
        // set by departure, not by the answer.
        //
        // Sliced rather than passed as a window, because Strongest's window is
        // cycle-degree arithmetic and this axis is seconds.
        var after = times.FindIndex(t => t >= 1e-3);
        var expectExpansion = openEnd;
        var arrival = WaveDecomposition.Strongest(
            components[after..], times[after..], WaveSense.Leftward, expansion: expectExpansion);

        arrival.Should().NotBeNull();

        // TIMING. The pulse starts at the probe, so the rightward half travels
        // (L − x) to the far end and (L − x) back — the textbook 2(L−x)/a.
        var probeX = (length / 4.0) + (0.5 * length / 400.0);
        var expected = 2.0 * (length - probeX) / soundSpeed;

        output.WriteLine(
            $"{(openEnd ? "open" : "closed")} end: strongest leftward {arrival!.Kind} at "
            + $"{arrival.AngleDeg * 1000:F3} ms, X {arrival.AmplitudeRatio:F4}; "
            + $"2(L−x)/a = {expected * 1000:F3} ms");

        // SIGN. This is the half the textbook is remembered for: an open end
        // reflects a compression as an EXPANSION, a closed end as a
        // COMPRESSION. Getting the sign backwards would invert every piece of
        // tuning advice the tool gives.
        if (openEnd)
        {
            arrival.IsExpansion.Should().BeTrue("an open end reflects a compression as an expansion");
            arrival.AmplitudeRatio.Should().BeLessThan(0.995);
        }
        else
        {
            arrival.IsExpansion.Should().BeFalse("a closed end reflects a compression as a compression");
            arrival.AmplitudeRatio.Should().BeGreaterThan(1.005);
        }

        // 5% of the transit. The pulse has finite width, and even at 1.15× its
        // front outruns the small-signal a slightly, so the peak cannot land
        // exactly on 2(L−x)/a — but it must land close enough that the answer
        // is the textbook one and not merely the right order of magnitude.
        arrival.AngleDeg.Should().BeApproximately(expected, 0.05 * expected);
    }

    [Fact]
    public void Gate_the_outgoing_pulse_and_its_reflection_are_told_apart()
    {
        // The reason for the whole method. At the probe the same trace carries
        // the outgoing pulse and, later, the reflection. The rightward search
        // must find the first and the leftward search the second, and they
        // must be separated by very nearly the round trip.
        var (times, pressure, velocity, length, soundSpeed) = RunPulse(openEnd: true);
        var components = WaveDecomposition.Series(
            pressure.ToArray(), velocity.ToArray(), ReferencePressure, soundSpeed, Gamma);

        var outgoing = WaveDecomposition.Strongest(
            components, times, WaveSense.Rightward, expansion: false);
        var returning = WaveDecomposition.Strongest(
            components, times, WaveSense.Leftward, expansion: true);

        outgoing.Should().NotBeNull();
        returning.Should().NotBeNull();

        var probeX = (length / 4.0) + (0.5 * length / 400.0);
        var separation = returning!.AngleDeg - outgoing!.AngleDeg;
        var roundTrip = 2.0 * (length - probeX) / soundSpeed;

        output.WriteLine(
            $"outgoing compression at {outgoing.AngleDeg * 1000:F3} ms (X {outgoing.AmplitudeRatio:F3}), "
            + $"returning expansion at {returning.AngleDeg * 1000:F3} ms (X {returning.AmplitudeRatio:F3}), "
            + $"separated by {separation * 1000:F3} ms against a {roundTrip * 1000:F3} ms round trip");

        separation.Should().BePositive("the reflection cannot precede the pulse that caused it");
        separation.Should().BeApproximately(roundTrip, 0.12 * roundTrip);

        // And the raw trace on its own cannot tell you this: at the moment the
        // reflection passes, the recorded pressure is not obviously anything.
        var index = times.FindIndex(t => t >= returning.AngleDeg);
        output.WriteLine($"raw pressure at that instant: {pressure[index] / 1000.0:F2} kPa "
                         + $"({100.0 * (pressure[index] / ReferencePressure - 1.0):+0.0;-0.0}% from reference)");
    }

    // ---- The annotation the plan asks for --------------------------------

    [Fact]
    public void The_annotation_reads_the_way_the_plan_writes_it()
    {
        // Plan §8.4 gives the exact phrasing it wants:
        //   "reflected expansion arrives 12° before EVC"
        var arrival = new WaveArrival(368.0, 0.912, 78_000.0, WaveSense.Leftward);
        var text = WaveDecomposition.Annotate(arrival, eventDeg: 380.0, eventName: "EVC");

        output.WriteLine(text);
        text.Should().StartWith("reflected expansion arrives 12° before EVC");

        // After the event, and the other sense, must both read correctly —
        // "12° before" and "12° after" are opposite engineering verdicts.
        WaveDecomposition.Annotate(arrival with { AngleDeg = 392.0 }, 380.0, "EVC")
            .Should().StartWith("reflected expansion arrives 12° after EVC");
        WaveDecomposition.Annotate(arrival with { AmplitudeRatio = 1.09 }, 380.0, "EVC")
            .Should().StartWith("reflected compression arrives 12° before EVC");
        WaveDecomposition.Annotate(arrival with { Sense = WaveSense.Rightward }, 380.0, "EVC")
            .Should().StartWith("expansion arrives 12° before EVC");
        WaveDecomposition.Annotate(arrival with { AngleDeg = 380.0 }, 380.0, "EVC")
            .Should().Be("reflected expansion arrives at EVC (X 0.912)");
    }

    [Fact]
    public void The_annotation_takes_the_short_way_round_the_cycle()
    {
        // An arrival at 10° against an EVC of 700° is 30° AFTER it, not 690°
        // before. Getting this wrong turns a correctly tuned header into a
        // reported disaster.
        var arrival = new WaveArrival(10.0, 0.9, 80_000.0, WaveSense.Leftward);
        WaveDecomposition.Annotate(arrival, 700.0, "EVC").Should().Contain("30° after EVC");

        var early = new WaveArrival(700.0, 0.9, 80_000.0, WaveSense.Leftward);
        WaveDecomposition.Annotate(early, 10.0, "EVC").Should().Contain("30° before EVC");
    }

    [Fact]
    public void A_search_window_wraps_around_the_end_of_the_cycle()
    {
        var angles = new List<double> { 0, 90, 180, 360, 540, 700, 710 };
        var components = angles
            .Select(a => new WaveComponents(1.0, a is 710 or 90 ? 0.8 : 1.0, 0, 0))
            .ToList();

        // A window from 690° to 30° spans the wrap. Both candidates are equal
        // depth, so only the window decides which is found.
        var wrapped = WaveDecomposition.Strongest(
            components, angles, WaveSense.Leftward, expansion: true, fromDeg: 690, toDeg: 30);
        wrapped!.AngleDeg.Should().Be(710);

        var plain = WaveDecomposition.Strongest(
            components, angles, WaveSense.Leftward, expansion: true, fromDeg: 60, toDeg: 200);
        plain!.AngleDeg.Should().Be(90);
    }

    [Fact]
    public void A_mismatched_history_is_rejected_rather_than_silently_misaligned()
    {
        // A one-sample offset between pressure and velocity is a spurious
        // wave, not a rounding error: the split is pointwise.
        var act = () => WaveDecomposition.Series(
            new float[10], new float[9], ReferencePressure, ReferenceSoundSpeed, Gamma);
        act.Should().Throw<ArgumentException>().WithMessage("*simultaneously sampled*");
    }
}
