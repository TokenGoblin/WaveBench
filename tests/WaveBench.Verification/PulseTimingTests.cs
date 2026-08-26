using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Metrics;
using WaveBench.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 9 gate: collector timing matches hand calculation exactly; the
/// crossplane-vs-flat-plane half-order test passes; order tracking recovers
/// known levels within 0.2 dB.
/// </summary>
public class PulseTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate_collector_timing_matches_hand_calculation_exactly()
    {
        // Three equal primaries: L = 0.9 m, a = 500 m/s → τ = 1.8 ms.
        // At 6000 rpm: shift = 6·6000·0.0018 = 64.8° for every branch.
        var equal = new[]
        {
            new CollectorBranch(1, 0.0, 0.9, 500.0),
            new CollectorBranch(2, 240.0, 0.9, 500.0),
            new CollectorBranch(3, 480.0, 0.9, 500.0),
        };
        var result = CollectorTiming.Analyze(equal, 6000.0);

        result.ArrivalDeg[0].Should().BeApproximately(64.8, 1e-9);
        result.ArrivalDeg[1].Should().BeApproximately(304.8, 1e-9);
        result.ArrivalDeg[2].Should().BeApproximately(544.8, 1e-9);
        result.SpacingDeg.Should().OnlyContain(s => Math.Abs(s - 240.0) < 1e-9,
            "equal lengths ⇒ exactly 720/m spacing at every rpm (§3.2)");
        result.MaxAbsTimingErrorDeg.Should().BeLessThan(1e-9);

        // One primary 0.1 m longer: Δτ = 0.2 ms ⇒ error = 6·N·Δτ = 7.2° at
        // 6000 rpm, hand-exact; and linear in rpm (3.6° at 3000).
        var unequal = new[]
        {
            new CollectorBranch(1, 0.0, 0.9, 500.0),
            new CollectorBranch(2, 240.0, 1.0, 500.0),
            new CollectorBranch(3, 480.0, 0.9, 500.0),
        };
        CollectorTiming.Analyze(unequal, 6000.0).TimingErrorDeg[1].Should().BeApproximately(7.2, 1e-9);
        CollectorTiming.Analyze(unequal, 3000.0).TimingErrorDeg[1].Should().BeApproximately(3.6, 1e-9);

        // Wall-temperature effect (§3.2): same geometry, one cooler primary
        // (lower ā) also shows a timing error — geometrically equal is not
        // acoustically equal.
        var cooler = new[]
        {
            new CollectorBranch(1, 0.0, 0.9, 520.0),
            new CollectorBranch(2, 240.0, 0.9, 480.0),
            new CollectorBranch(3, 480.0, 0.9, 520.0),
        };
        var expected = 6.0 * 6000.0 * (0.9 / 480.0 - 0.9 / 520.0);
        CollectorTiming.Analyze(cooler, 6000.0).TimingErrorDeg[1].Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Gate_crossplane_shows_half_orders_and_flat_plane_does_not()
    {
        // One V8 bank, equal-length primaries into a 4-1 collector, identical
        // geometry — the firing intervals are the ONLY difference (§6.2 #8).
        const double rpm = 4000.0;
        static CollectorTimingResult Bank(double[] firingAngles) =>
            CollectorTiming.Analyze(
                firingAngles.Select((a, i) => new CollectorBranch(i + 1, a, 0.8, 550.0)).ToArray(), rpm);

        OrderSpectrum Spectrum(double[] firingAngles)
        {
            var cycle = CollectorPulseTrain.SynthesizeCycle(Bank(firingAngles));
            var (signal, rate) = CollectorPulseTrain.Repeat(cycle, 16, rpm);
            return OrderAnalysis.AtConstantSpeed(signal, rate, rpm, maxOrder: 12.0);
        }

        var flat = Spectrum([0.0, 180.0, 360.0, 540.0]);            // even 180° per bank
        var cross = Spectrum([0.0, 90.0, 270.0, 540.0]);            // 90-180-270-180 pattern

        var flatHalf = CharacterMetrics.HalfOrderRatio(flat);
        var crossHalf = CharacterMetrics.HalfOrderRatio(cross);
        var flatOpi = CharacterMetrics.OrderPurityIndex(flat, firingOrder: 2.0);
        var crossOpi = CharacterMetrics.OrderPurityIndex(cross, firingOrder: 2.0);

        output.WriteLine($"half-order ratio: flat {flatHalf:E3}, crossplane {crossHalf:F3}");
        output.WriteLine($"OPI: flat {flatOpi:F4}, crossplane {crossOpi:F4}");

        flatHalf.Should().BeLessThan(1e-6, "even spacing cancels every non-multiple order (§3.2)");
        crossHalf.Should().BeGreaterThan(0.2, "gate: strong half-order content is THE crossplane signature");
        crossHalf.Should().BeGreaterThan(1000.0 * Math.Max(flatHalf, 1e-12));
        flatOpi.Should().BeGreaterThan(0.999);
        crossOpi.Should().BeLessThan(0.8);
    }

    [Fact]
    public void The_180_crossover_restores_the_flat_plane_fingerprint()
    {
        // §6.2 #9: pairing across banks restores even spacing → rumble
        // becomes howl. Modelled as the corrected arrival set.
        const double rpm = 4000.0;
        var crossover = CollectorTiming.Analyze(
            new[] { 0.0, 180.0, 360.0, 540.0 }
                .Select((a, i) => new CollectorBranch(i + 1, a, 0.8, 550.0)).ToArray(), rpm);
        var cycle = CollectorPulseTrain.SynthesizeCycle(crossover);
        var (signal, rate) = CollectorPulseTrain.Repeat(cycle, 16, rpm);
        var spectrum = OrderAnalysis.AtConstantSpeed(signal, rate, rpm, maxOrder: 12.0);
        CharacterMetrics.HalfOrderRatio(spectrum).Should().BeLessThan(1e-6);
    }

    [Fact]
    public void Gate_order_tracking_recovers_known_levels_within_0_2_db()
    {
        // Synthetic: orders 1.0 (amplitude 1.0), 2.5 (0.3), 4.0 (0.1) at
        // 4000 rpm, 48 kHz, 20 cycles.
        const double rpm = 4000.0;
        const double rate = 48_000.0;
        var revPerSec = rpm / 60.0;
        var n = (int)(20 * 120.0 / rpm * rate);
        var signal = new double[n];
        for (var i = 0; i < n; i++)
        {
            var revAngle = 2.0 * Math.PI * revPerSec * i / rate;
            signal[i] = 1.0 * Math.Cos(1.0 * revAngle + 0.3)
                        + 0.3 * Math.Cos(2.5 * revAngle + 1.1)
                        + 0.1 * Math.Cos(4.0 * revAngle + 2.0);
        }

        var spectrum = OrderAnalysis.AtConstantSpeed(signal, rate, rpm, maxOrder: 8.0);
        foreach (var (order, amplitude) in new[] { (1.0, 1.0), (2.5, 0.3), (4.0, 0.1) })
        {
            var recovered = spectrum.AmplitudeAt(order);
            var errorDb = Math.Abs(20.0 * Math.Log10(recovered / amplitude));
            errorDb.Should().BeLessThan(0.2, $"gate: order {order} within 0.2 dB (got {recovered:F5})");
        }

        // Absent orders stay far below the present ones.
        spectrum.AmplitudeAt(3.0).Should().BeLessThan(0.003);
    }

    [Fact]
    public void Order_tracking_follows_a_speed_sweep_via_the_angle_domain()
    {
        // Linear sweep 3000 → 3600 rpm; order-3 tone locked to the crank.
        var times = new List<double>();
        var values = new List<double>();
        const double dt = 1.0 / 48_000.0;
        double AngleDeg(double t)
        {
            var rpm0 = 3000.0;
            const double slope = 600.0 / 2.0; // rpm per second over 2 s
            return 6.0 * (rpm0 * t + 0.5 * slope * t * t);
        }

        for (var t = 0.0; t < 2.0; t += dt)
        {
            times.Add(t);
            values.Add(0.7 * Math.Cos(3.0 * AngleDeg(t) * Math.PI / 180.0 + 0.4));
        }

        var spectrum = OrderAnalysis.WithAngleHistory(times, values, AngleDeg, maxOrder: 8.0);
        var errorDb = Math.Abs(20.0 * Math.Log10(spectrum.AmplitudeAt(3.0) / 0.7));
        errorDb.Should().BeLessThan(0.2, "crank-synchronous tracking is immune to the sweep");
        spectrum.AmplitudeAt(2.0).Should().BeLessThan(0.02);
    }

    [Fact]
    public void Scroll_separation_ranks_correct_and_incorrect_pairing()
    {
        // I4 firing 1-3-4-2 (0/180/360/540), EVO 130°, EVC 380° local.
        // Correct twin-scroll pairing (plan §4.6.2): A = {1,4}, B = {2,3} —
        // mates 360° apart ⇒ zero blowdown-into-exhaust-stroke overlap.
        ScrollSeparation.Index([0.0, 360.0], 130.0, 380.0).Should().Be(0.0);
        ScrollSeparation.Index([540.0, 180.0], 130.0, 380.0).Should().Be(0.0);

        // Incorrect pairing {1,2} (540° apart): large overlap.
        var wrong = ScrollSeparation.Index([0.0, 540.0], 130.0, 380.0);
        wrong.Should().BeGreaterThan(0.3,
            $"wrong pairing must show large overlap (index {wrong:F3})");
    }
}
