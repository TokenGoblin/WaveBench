using FluentAssertions;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 13 gate: <i>"the ... twin-scroll pairing validation case passes"</i>.
///
/// Validation case 16: <i>"correct 360°-apart pairing must show near-zero scroll
/// overlap ... derived from firing order alone."</i>
///
/// That last clause is the design constraint, and it is why this is arithmetic
/// on the firing order and the cam rather than a simulation result: a wrong
/// pairing should be caught before anybody runs anything, and the penalty it
/// carries should have a reason attached and not just a number.
/// </summary>
public class TwinScrollPairingTests(ITestOutputHelper output)
{
    private static readonly int[] I4 = [1, 3, 4, 2];

    private static readonly int[] I6 = [1, 5, 3, 6, 2, 4];

    private static readonly int[] CrossplaneV8 = [1, 8, 7, 3, 6, 5, 4, 2];

    [Fact]
    public void Firing_angles_come_out_evenly_spaced_across_the_cycle()
    {
        var angles = ScrollPairing.FiringAngles(I4);

        angles[1].Should().Be(0.0);
        angles[3].Should().Be(180.0);
        angles[4].Should().Be(360.0);
        angles[2].Should().Be(540.0);

        // The pairing rule in one line: 1 and 4 are 360° apart, and so are 3 and 2.
        (angles[4] - angles[1]).Should().Be(360.0);
        (angles[2] - angles[3]).Should().Be(360.0);
    }

    [Fact]
    public void Gate_the_recommended_pairing_is_the_360_degree_apart_one_for_a_four_cylinder()
    {
        var (a, b) = ScrollPairing.Recommend(I4);

        a.Should().BeEquivalentTo(new[] { 1, 4 });
        b.Should().BeEquivalentTo(new[] { 3, 2 });

        output.WriteLine($"I4 firing 1-3-4-2 -> scroll A {{{string.Join(", ", a)}}}, scroll B {{{string.Join(", ", b)}}}");
    }

    [Fact]
    public void Gate_correct_pairing_shows_near_zero_scroll_overlap_and_incorrect_pairing_does_not()
    {
        var correct = ScrollPairing.Separation(
            I4, [("A", [1, 4]), ("B", [3, 2])]);

        // The classic mistake: pairing cylinders that are adjacent in the firing
        // order. They are 180° apart, so one's blowdown lands squarely in the
        // other's exhaust stroke.
        var wrong = ScrollPairing.Separation(
            I4, [("A", [1, 3]), ("B", [4, 2])]);

        Report("correct (1&4 / 3&2)", correct);
        Report("wrong   (1&3 / 4&2)", wrong);

        correct.Should().AllSatisfy(s => s.SeparationIndex.Should().Be(0.0,
            "cylinders 360° apart cannot interfere — that is the whole point of the rule"));

        wrong.Should().AllSatisfy(s => s.SeparationIndex.Should().BeGreaterThan(0.5,
            "cylinders 180° apart put most of one blowdown inside the other's exhaust stroke"));
    }

    [Fact]
    public void Gate_the_six_cylinder_pairing_falls_out_of_the_firing_order_too()
    {
        var (a, b) = ScrollPairing.Recommend(I6);
        output.WriteLine($"I6 firing 1-5-3-6-2-4 -> scroll A {{{string.Join(", ", a)}}}, scroll B {{{string.Join(", ", b)}}}");

        // The plan states the answer for this engine: 1/2/3 against 4/5/6.
        a.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        b.Should().BeEquivalentTo(new[] { 4, 5, 6 });

        var correct = ScrollPairing.Separation(I6, [("A", a), ("B", b)]);
        Report("I6 correct", correct);
        correct.Should().AllSatisfy(s => s.SeparationIndex.Should().Be(0.0));

        // Grouping consecutive firings instead puts cylinders 120° apart on one
        // scroll, which is the worst arrangement available.
        var wrong = ScrollPairing.Separation(I6, [("A", [1, 5, 3]), ("B", [6, 2, 4])]);
        Report("I6 wrong", wrong);
        wrong.Should().AllSatisfy(s => s.SeparationIndex.Should().BeGreaterThan(0.9));
    }

    [Fact]
    public void A_crossplane_v8_cannot_separate_two_scrolls_cleanly_and_the_index_says_so()
    {
        // A finding, not a failure. Eight cylinders in two scrolls means four
        // events per scroll per cycle, so the best spacing available inside a
        // scroll is 180° — and a blowdown starting at 140° always lands inside a
        // mate's exhaust stroke 180° later. No two-scroll V8 escapes it.
        //
        // Which is why the overlap index alone is not enough here: it saturates
        // at 1 for every arrangement. What still ranks them is the minimum
        // spacing, and that is what the recommendation maximises.
        var (a, b) = ScrollPairing.Recommend(CrossplaneV8);
        var alternating = ScrollPairing.Separation(CrossplaneV8, [("A", a), ("B", b)]);

        output.WriteLine($"V8 firing 1-8-7-3-6-5-4-2 -> A {{{string.Join(", ", a)}}}, B {{{string.Join(", ", b)}}}");
        Report("V8 alternating", alternating);

        alternating.Should().AllSatisfy(s => s.SeparationIndex.Should().BeGreaterThan(0.0,
            "four cylinders on one scroll cannot be 360° apart"));

        alternating.Should().AllSatisfy(s => s.MinimumSpacingDeg.Should().Be(180.0,
            "alternating the firing order is the widest spacing eight cylinders allow"));

        // Grouping consecutive firings puts cylinders 90° apart, which is the
        // worst arrangement available and reads as such.
        var consecutive = ScrollPairing.Separation(
            CrossplaneV8, [("A", [1, 8, 7, 3]), ("B", [6, 5, 4, 2])]);
        Report("V8 consecutive", consecutive);

        consecutive.Max(s => s.MinimumSpacingDeg).Should().BeLessThan(
            alternating.Min(s => s.MinimumSpacingDeg),
            "the recommendation must beat the obvious wrong answer even where neither is clean");

        // And bank-per-scroll on a crossplane: the left bank fires 1-7-5-3 at
        // 0°/180°/450°/270°... uneven, which is the whole reason crossplane V8
        // headers need a 180° crossover to sound and breathe evenly.
        var byBank = ScrollPairing.Separation(
            CrossplaneV8, [("left", [1, 3, 5, 7]), ("right", [2, 4, 6, 8])]);
        Report("V8 by bank", byBank);

        byBank.Min(s => s.MinimumSpacingDeg).Should().BeLessThan(180.0,
            "a bank of a crossplane V8 is not evenly spaced in the firing order");
    }

    [Fact]
    public void A_longer_blowdown_makes_the_pairing_rule_matter_more()
    {
        // The separation index is not a property of the firing order alone: a
        // late EVO or a high engine speed lengthens the blowdown in crank
        // degrees and starts to eat into a neighbour's stroke. Correct pairing
        // has enough margin to absorb it; incorrect pairing does not.
        output.WriteLine("  blowdown°   correct   wrong");

        foreach (var blowdown in new[] { 40.0, 60.0, 90.0, 120.0 })
        {
            var cam = new ExhaustEventTiming(BlowdownDurationDeg: blowdown);
            var correct = ScrollPairing.Separation(I4, [("A", [1, 4])], cam)[0].SeparationIndex;
            var wrong = ScrollPairing.Separation(I4, [("A", [1, 3])], cam)[0].SeparationIndex;

            output.WriteLine($"{blowdown,11:F0}   {correct,7:F2}   {wrong,5:F2}");
            correct.Should().Be(0.0, $"a 360°-apart pair still has room at a {blowdown}° blowdown");
        }
    }

    [Fact]
    public void An_odd_cylinder_engine_is_refused_with_the_reason()
    {
        var act = () => ScrollPairing.Recommend([1, 3, 5, 2, 4]);
        act.Should().Throw<ArgumentException>().WithMessage("*even number of cylinders*");
    }

    [Fact]
    public void Partial_admission_gives_the_rotor_to_whichever_scroll_is_actually_pulsing()
    {
        // Plan §4.3: a twin-scroll turbine is generally NOT at full admission
        // through an in-phase pulse and IS at partial admission for most of an
        // out-of-phase one. Both limits have to come out of the same rule.
        var map = SyntheticTurbo.Turbine();
        var a = new RotorNozzleBoundary(map, 8.0e-4) { AdmissionFraction = 0.5 };
        var b = new RotorNozzleBoundary(map, 8.0e-4) { AdmissionFraction = 0.5 };

        // Both scrolls delivering equally: even split, no partial-admission loss.
        SetLastFlow(a, 0.10);
        SetLastFlow(b, 0.10);
        TwinScrollTurbine.Redistribute(a, b);

        a.AdmissionFraction.Should().BeApproximately(0.5, 1e-9);
        a.EfficiencyScale.Should().BeApproximately(1.0, 1e-9);

        // One scroll pulsing alone: it takes nearly the whole rotor, and pays
        // for it in efficiency.
        SetLastFlow(a, 0.20);
        SetLastFlow(b, 0.002);
        TwinScrollTurbine.Redistribute(a, b);

        a.AdmissionFraction.Should().BeGreaterThan(0.9);
        b.AdmissionFraction.Should().BeLessThan(0.1);
        a.EfficiencyScale.Should().BeLessThan(0.90);

        output.WriteLine(
            $"one scroll pulsing alone: admission {a.AdmissionFraction:P0} / {b.AdmissionFraction:P0}, "
            + $"efficiency scale {a.EfficiencyScale:F3}");
    }

    /// <summary>
    /// Set what a rotor last swallowed, scaled by its current admission so the
    /// redistribution sees the offer it is meant to see.
    /// </summary>
    private static void SetLastFlow(RotorNozzleBoundary rotor, double fullAdmissionFlow)
    {
        typeof(RotorNozzleBoundary)
            .GetProperty(nameof(RotorNozzleBoundary.Last))!
            .SetValue(rotor, new RotorState(
                fullAdmissionFlow * rotor.AdmissionFraction, 0, 0, 0, 0, 0, 0, default));
    }

    private void Report(string label, IReadOnlyList<ScrollSeparation> separation)
    {
        foreach (var s in separation)
        {
            output.WriteLine(
                $"{label,-20} scroll {s.Scroll}: {{{string.Join(", ", s.Cylinders)}}} "
                + $"index {s.SeparationIndex:F3}, min spacing {s.MinimumSpacingDeg:F0}°"
                + (s.SeparationIndex > 0 ? $" (cylinder {s.WorstPair.From} into cylinder {s.WorstPair.Into})" : ""));
        }
    }
}
