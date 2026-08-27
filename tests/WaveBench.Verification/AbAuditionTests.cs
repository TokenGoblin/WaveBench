using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Auralisation;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 20 gate: <i>"A/B audition is gapless and level-matched."</i>
///
/// Both halves are checkable and both matter for a different reason.
/// <b>Level-matched</b>, because a listener asked to choose between two
/// exhausts will pick the louder one essentially every time — an unmatched A/B
/// measures which design is louder, not which sounds better. <b>Gapless</b>,
/// because a switch that restarts B from the beginning compares a different
/// part of the cycle, and one that inserts silence or a click makes the
/// listener hear the join instead of the design.
///
/// The pair under test is the plan's own M50 comparison, rendered from the
/// collector pulse train — real signals, not noise.
/// </summary>
public class AbAuditionTests(ITestOutputHelper output)
{
    private const double Rpm = 5000.0;
    private const double Seconds = 2.0;

    private static AudioStem Render(ExhaustSoundDesign design)
    {
        var timing = CollectorTiming.Analyze(design.Branches, Rpm);
        var amplitudes = Enumerable.Range(0, design.Branches.Count).Select(design.AmplitudeOf).ToArray();
        var samples = CollectorPulseTrain.Render(
            timing, Rpm, Seconds, Loudness.SupportedSampleRate, 18.0, amplitudes);
        return new AudioStem(design.Name, samples, Loudness.SupportedSampleRate);
    }

    private static AbAudition Pair() => new(Render(SoundCases.M50Factory()), Render(SoundCases.M50EqualLength()));

    [Fact]
    public void Gate_both_designs_are_brought_to_the_same_loudness_and_the_real_difference_is_kept()
    {
        var rawA = Render(SoundCases.M50Factory());
        var rawB = Render(SoundCases.M50EqualLength());

        var beforeA = Loudness.IntegratedLufs(rawA.Samples, rawA.SampleRate);
        var beforeB = Loudness.IntegratedLufs(rawB.Samples, rawB.SampleRate);

        var ab = Pair();
        var afterA = Loudness.IntegratedLufs(ab.A.Samples, ab.A.SampleRate);
        var afterB = Loudness.IntegratedLufs(ab.B.Samples, ab.B.SampleRate);

        output.WriteLine($"before: A {beforeA:F2} LUFS, B {beforeB:F2} LUFS ({beforeA - beforeB:+0.00;-0.00} LU apart)");
        output.WriteLine($"after:  A {afterA:F2} LUFS, B {afterB:F2} LUFS");
        output.WriteLine($"gains applied: A {ab.GainDbA:+0.00;-0.00} dB, B {ab.GainDbB:+0.00;-0.00} dB");

        afterA.Should().BeApproximately(-23.0, 0.15);
        afterB.Should().BeApproximately(-23.0, 0.15);
        afterA.Should().BeApproximately(afterB, 0.2, "matched means matched");

        // The real difference is REPORTED, not discarded: how loud a design is
        // remains a fact about it, and hiding it would be its own distortion.
        ab.TrueDifferenceLu.Should().BeApproximately(beforeA - beforeB, 0.05);
    }

    [Fact]
    public void Gate_a_switch_consumes_no_time_and_inserts_none()
    {
        var ab = Pair();
        var switches = new[] { 0.4, 0.9, 1.35 };
        var rendered = ab.Render(switches);

        rendered.Samples.Length.Should().Be(ab.Length,
            "a gapless switch is one that costs no samples — the output is the same length as either source");
        rendered.SampleRate.Should().Be(ab.SampleRate);

        // Away from the fades the output must BE one source or the other,
        // sample for sample, at the same position. Anything else means the
        // playback head moved.
        var fade = (int)Math.Round(ab.CrossfadeMs * 1e-3 * ab.SampleRate);
        var checkedSamples = 0;

        for (var i = 0; i < rendered.Samples.Length; i += 37)
        {
            var nearSwitch = switches.Any(s =>
            {
                var at = (int)Math.Round(s * ab.SampleRate);
                return i >= at - 1 && i < at + fade + 1;
            });

            if (nearSwitch)
            {
                continue;
            }

            var expected = ab.IsOnB(i / ab.SampleRate, switches) ? ab.B.Samples[i] : ab.A.Samples[i];
            rendered.Samples[i].Should().BeApproximately(expected, 1e-6f);
            checkedSamples++;
        }

        output.WriteLine($"{checkedSamples} sampled positions match their source exactly");
        checkedSamples.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void Gate_a_switch_does_not_click()
    {
        // A click is an isolated discontinuity — one first-difference far
        // larger than the material's own. Comparing against the SOURCES' own
        // maximum step is what makes this a test of the switch rather than of
        // how loud the engine is.
        var ab = Pair();
        var switches = new[] { 0.5, 1.0, 1.5 };
        var rendered = ab.Render(switches);

        var sourceStep = Math.Max(MaxStep(ab.A.Samples), MaxStep(ab.B.Samples));
        var renderedStep = MaxStep(rendered.Samples);

        output.WriteLine($"largest sample-to-sample step: sources {sourceStep:E3}, A/B render {renderedStep:E3} "
                         + $"({renderedStep / sourceStep:F3}×)");

        renderedStep.Should().BeLessThan(sourceStep * 1.05f,
            "a crossfaded switch must not introduce a step bigger than the material already has");

        // And the same signal switched with NO crossfade must fail that test,
        // or the assertion above is not measuring anything.
        var abrupt = new AbAudition(Render(SoundCases.M50Factory()), Render(SoundCases.M50EqualLength()))
        {
            CrossfadeMs = 1.0 / Loudness.SupportedSampleRate * 1000.0,
        };
        var abruptStep = MaxStep(abrupt.Render(switches).Samples);
        output.WriteLine($"with a one-sample switch instead: {abruptStep:E3} ({abruptStep / sourceStep:F3}×)");
        abruptStep.Should().BeGreaterThan(renderedStep,
            "the crossfade has to be doing something, or this test would pass without it");
    }

    [Fact]
    public void Gate_the_crossfade_does_not_dip_or_bump_the_level()
    {
        // Two renders of the same engine at the same speed are phase-locked to
        // the same crank, so they are correlated — which is why the default is
        // equal-GAIN. Equal-power would lift a correlated fade by up to 3 dB,
        // and the listener would hear the switch as a pulse of loudness.
        var ab = Pair();
        var switches = new[] { 1.0 };
        var rendered = ab.Render(switches);

        var at = (int)Math.Round(1.0 * ab.SampleRate);
        var fade = (int)Math.Round(ab.CrossfadeMs * 1e-3 * ab.SampleRate);
        var windowLength = fade * 4;

        var duringFade = Rms(rendered.Samples, at, fade);
        var before = Rms(rendered.Samples, at - windowLength, windowLength);
        var after = Rms(rendered.Samples, at + fade, windowLength);
        var reference = 0.5 * (before + after);

        var excursionDb = 20.0 * Math.Log10(duringFade / reference);
        output.WriteLine($"RMS before {before:E3}, during fade {duringFade:E3}, after {after:E3} "
                         + $"({excursionDb:+0.00;-0.00} dB across the fade)");

        Math.Abs(excursionDb).Should().BeLessThan(1.5,
            "the switch must not be audible as a change of level");
    }

    [Fact]
    public void An_ab_of_two_different_lengths_is_refused_rather_than_silently_truncated()
    {
        // A switch maps ONE playback position onto both streams. Unequal
        // lengths mean that position means two different moments in the cycle,
        // and the comparison stops being of the same thing.
        var a = Render(SoundCases.M50Factory());
        var shortB = a with { Samples = a.Samples.Take(a.Samples.Length / 2).ToArray() };

        var act = () => new AbAudition(a, shortB);
        act.Should().Throw<ArgumentException>().WithMessage("*same length*");

        var wrongRate = a with { SampleRate = 44_100.0 };
        var act2 = () => new AbAudition(a, wrongRate);
        act2.Should().Throw<ArgumentException>().WithMessage("*share a sample rate*");
    }

    [Fact]
    public void The_audible_source_follows_the_switch_timeline()
    {
        var ab = Pair();
        var switches = new[] { 0.5, 1.2 };

        ab.IsOnB(0.0, switches).Should().BeFalse("playback starts on A");
        ab.IsOnB(0.49, switches).Should().BeFalse();
        ab.IsOnB(0.51, switches).Should().BeTrue();
        ab.IsOnB(1.19, switches).Should().BeTrue();
        ab.IsOnB(1.21, switches).Should().BeFalse();
    }

    private static float MaxStep(ReadOnlySpan<float> samples)
    {
        var max = 0f;
        for (var i = 1; i < samples.Length; i++)
        {
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
        }

        return max;
    }

    private static double Rms(ReadOnlySpan<float> samples, int from, int count)
    {
        from = Math.Max(0, from);
        count = Math.Min(count, samples.Length - from);
        var sum = 0.0;
        for (var i = from; i < from + count; i++)
        {
            sum += samples[i] * (double)samples[i];
        }

        return Math.Sqrt(sum / Math.Max(1, count));
    }
}
