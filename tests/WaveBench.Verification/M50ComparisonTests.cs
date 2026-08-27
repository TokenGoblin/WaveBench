using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 20 gate: <i>"the M50 factory-vs-6-1 comparison is reproducible end to
/// end."</i>
///
/// Plan §3.0 states the comparison as a table of claims and calls it <i>"the
/// worked example the module must nail"</i>, reproduced <b>from geometry and
/// firing order alone</b>. Each test below is one row of that table, asserted
/// against numbers that fall out of the pulse superposition rather than out of
/// a fitted constant:
///
/// <code>
///                       | Factory cast          | Equal-length 6-1
///  Runner lengths       | Unequal, short        | Equal
///  Arrival spacing      | Uneven, rpm-dependent | Exactly 120° at every rpm
///  Order content        | Leaks into 0.5–2.5    | On the 3rd and its multiples
///  Pulse amplitude      | Unequal               | Matched
/// </code>
///
/// The sentence the UI writes about all this is tested separately, next to the
/// code that writes it.
/// </summary>
public class M50ComparisonTests(ITestOutputHelper output)
{
    [Fact]
    public void Row1_the_factory_runners_are_unequal_and_short_and_the_6_1_is_equal()
    {
        var factory = SoundCases.M50Factory();
        var equal = SoundCases.M50EqualLength();

        var factoryLengths = factory.Branches.Select(b => b.PrimaryLength).ToList();
        var equalLengths = equal.Branches.Select(b => b.PrimaryLength).ToList();

        (factoryLengths.Max() - factoryLengths.Min()).Should().BeGreaterThan(0.1,
            "the cast manifold's runners differ by more than 100 mm");
        factoryLengths.Max().Should().BeLessThan(equalLengths[0], "and all of them are shorter than the 6-1's");

        equalLengths.Distinct().Should().ContainSingle("every primary of a 6-1 is the same length");

        output.WriteLine("factory: " + string.Join(", ", factoryLengths.Select(l => $"{l * 1000:F0} mm")));
        output.WriteLine("6-1:     " + string.Join(", ", equalLengths.Select(l => $"{l * 1000:F0} mm")));
    }

    [Fact]
    public void Gate_row2_the_6_1_spacing_is_exactly_120_degrees_at_every_rpm_and_the_factory_is_not()
    {
        // The strongest claim in the table, and the only one checkable at more
        // than one speed: equal lengths give equal transit, so the arrivals
        // inherit the firing spacing exactly, at ANY speed. Unequal lengths
        // give an error of 6·N·Δτ, which by construction grows with N.
        var factory = SoundCases.M50Factory();
        var equal = SoundCases.M50EqualLength();
        var errors = new List<double>();

        foreach (var rpm in new[] { 1500.0, 3000.0, 4500.0, 6000.0, 7200.0 })
        {
            var f = CollectorTiming.Analyze(factory.Branches, rpm);
            var e = CollectorTiming.Analyze(equal.Branches, rpm);
            errors.Add(f.MaxAbsTimingErrorDeg);

            output.WriteLine(
                $"{rpm,6:F0} rpm  factory spacing {f.SpacingDeg.Min():F1}–{f.SpacingDeg.Max():F1}° "
                + $"(worst error {f.MaxAbsTimingErrorDeg:F1}°)   "
                + $"6-1 spacing {e.SpacingDeg.Min():F1}–{e.SpacingDeg.Max():F1}° "
                + $"(worst error {e.MaxAbsTimingErrorDeg:F1}°)");

            e.SpacingDeg.Should().OnlyContain(s => Math.Abs(s - 120.0) < 1e-9,
                "equal lengths mean equal transit, so the arrivals keep the firing spacing exactly");
            e.MaxAbsTimingErrorDeg.Should().BeLessThan(1e-9);
        }

        errors.Should().OnlyContain(e => e > 1.0);
        for (var i = 1; i < errors.Count; i++)
        {
            errors[i].Should().BeGreaterThan(errors[i - 1],
                "a fixed transit mismatch is 6·N·Δτ crank degrees, so it grows with speed");
        }

        // And it is the SAME mismatch throughout: error against rpm is a
        // straight line through the origin, not merely an increasing one.
        (errors[^1] / errors[0]).Should().BeApproximately(7200.0 / 1500.0, 0.05);
    }

    [Fact]
    public void Gate_row3_the_factory_leaks_into_the_half_orders_and_the_6_1_does_not()
    {
        const double rpm = 4000.0;
        var factoryDesign = SoundCases.M50Factory();
        var equalDesign = SoundCases.M50EqualLength();

        var factory = CollectorSpectrum.At(factoryDesign, rpm);
        var equal = CollectorSpectrum.At(equalDesign, rpm);

        // A four-stroke six fires three times per revolution: the clean
        // spectrum is the 3rd order and its integer multiples, nothing else.
        output.WriteLine("order   factory      6-1");
        foreach (var order in new[] { 0.5, 1.0, 1.5, 2.0, 2.5 })
        {
            var f = factory.Level(order);
            var e = equal.Level(order);
            output.WriteLine($"{order,5:F1}  {f,8:F1} dB {e,8:F1} dB");

            // The factory's content has to be REAL before the comparison means
            // anything. Two designs both sitting at −300 dB satisfy "one is
            // 10 dB below the other" by round-off alone, and an early version
            // of this test passed exactly that way.
            f.Should().BeGreaterThan(-60.0,
                $"order {order} must carry actual energy on the cast manifold, not FFT noise");

            e.Should().BeLessThan(f - 10.0,
                $"the 6-1 must put far less into order {order} than the cast manifold does");
        }

        output.WriteLine("--- firing harmonics ---");
        foreach (var harmonic in new[] { 3.0, 6.0, 9.0 })
        {
            output.WriteLine($"{harmonic,5:F1}  {factory.Level(harmonic),8:F1} dB {equal.Level(harmonic),8:F1} dB");
        }

        var factoryLeak = CollectorSpectrum.NonFiringEnergyFraction(factoryDesign, rpm);
        var equalLeak = CollectorSpectrum.NonFiringEnergyFraction(equalDesign, rpm);
        output.WriteLine($"off-harmonic energy: factory {factoryLeak * 100:F1}%, 6-1 {equalLeak * 100:F1}%");

        equalLeak.Should().BeLessThan(0.02, "the 6-1's energy is concentrated on the 3rd and its multiples");
        factoryLeak.Should().BeGreaterThan(equalLeak * 5,
            "the cast manifold leaks into the intermediate orders by a wide margin");

        // Half-order content is the specific signature of a warble.
        CharacterMetrics.HalfOrderRatio(equal)
            .Should().BeLessThan(CharacterMetrics.HalfOrderRatio(factory) / 2.0);
    }

    [Fact]
    public void Row4_unequal_scavenging_breaks_the_cancellation_even_with_perfect_timing()
    {
        // Plan §3.2 is explicit that amplitude mismatch does this "just like
        // timing". Proving it needs the timing held perfect while only the
        // amplitudes vary — otherwise the two causes are confounded and the
        // claim is untested.
        const double rpm = 4000.0;
        var factory = SoundCases.M50Factory();
        var equal = SoundCases.M50EqualLength();

        var factoryAmps = Enumerable.Range(0, factory.Branches.Count).Select(factory.AmplitudeOf).ToList();
        Enumerable.Range(0, equal.Branches.Count).Select(equal.AmplitudeOf)
            .Distinct().Should().ContainSingle("a 6-1's cylinders scavenge alike");
        factoryAmps.Distinct().Should().HaveCountGreaterThan(1);

        var unevenAmplitudes = equal with
        {
            Name = "6-1 with uneven scavenging",
            Amplitudes = factoryAmps,
        };

        var leak = CollectorSpectrum.NonFiringEnergyFraction(unevenAmplitudes, rpm);
        var clean = CollectorSpectrum.NonFiringEnergyFraction(equal, rpm);

        output.WriteLine($"perfect timing, uneven amplitudes: {leak * 100:F3}% off-harmonic");
        output.WriteLine($"perfect timing, even amplitudes:   {clean * 100:F3}% off-harmonic");

        leak.Should().BeGreaterThan(clean,
            "unequal scavenging populates the intermediate orders even when every pulse arrives on time");
    }

    [Fact]
    public void Gate_the_timing_attribution_separates_length_from_temperature()
    {
        // A timing error has two independent causes and a builder can only act
        // on one at a time, so the attribution is what makes the diagnosis
        // useful. Each is isolated here in a design that has only that fault.
        var lengthOnly = Design("length only", [400, 400, 400, 400, 400, 520], Even(920));
        var temperatureOnly = Design("temperature only", Even(400), [920, 920, 920, 920, 920, 700]);

        var byLength = CollectorSpectrum.Attribute(lengthOnly, 6000.0)!;
        var byTemperature = CollectorSpectrum.Attribute(temperatureOnly, 6000.0)!;

        output.WriteLine($"length only:      cylinder {byLength.Cylinder}, error {byLength.ErrorDeg:F1}°, "
                         + $"from length {byLength.FromLengthDeg:F1}°, from temperature {byLength.FromTemperatureDeg:F1}°");
        output.WriteLine($"temperature only: cylinder {byTemperature.Cylinder}, error {byTemperature.ErrorDeg:F1}°, "
                         + $"from length {byTemperature.FromLengthDeg:F1}°, from temperature {byTemperature.FromTemperatureDeg:F1}°");

        // A design with no temperature spread must have nothing blamed on
        // temperature, and vice versa.
        Math.Abs(byLength.FromTemperatureDeg).Should().BeLessThan(1e-9);
        Math.Abs(byLength.TemperatureDeltaK).Should().BeLessThan(1e-9);
        byLength.FromLengthDeg.Should().BeGreaterThan(1.0);

        Math.Abs(byTemperature.FromLengthDeg).Should().BeLessThan(1e-9);
        Math.Abs(byTemperature.LengthDeltaM).Should().BeLessThan(1e-9);
        byTemperature.FromTemperatureDeg.Should().BeGreaterThan(1.0);

        // Cooler gas carries a SLOWER wave, so its pulse arrives LATE. Getting
        // that sign backwards would send a builder to insulate the wrong pipe.
        byTemperature.TemperatureDeltaK.Should().BeNegative();
        byTemperature.ErrorDeg.Should().BePositive("a cooler runner delivers its pulse late");

        // The two parts must account for the WHOLE error, exactly. Transit is
        // the only thing that moves an arrival off the firing grid, so a
        // residual would mean the sentence blames two causes for something a
        // third is doing.
        foreach (var a in new[] { byLength, byTemperature })
        {
            a.UnexplainedDeg.Should().BeApproximately(0.0, 1e-9);
        }
    }

    [Fact]
    public void Gate_the_attribution_accounts_for_the_whole_error_on_the_real_case()
    {
        // The M50 factory manifold has BOTH faults at once and a monotonic
        // spread, which is where an attribution measured against the design
        // mean falls apart: the anchor cylinder's own deviation goes
        // unaccounted for. Against the anchor the split is exact.
        foreach (var rpm in new[] { 2000.0, 4000.0, 6500.0 })
        {
            var a = CollectorSpectrum.Attribute(SoundCases.M50Factory(), rpm)!;
            output.WriteLine(
                $"{rpm,6:F0} rpm  cylinder {a.Cylinder} vs anchor {a.ReferenceCylinder}: "
                + $"error {a.ErrorDeg,7:F2}° = length {a.FromLengthDeg,7:F2}° + "
                + $"temperature {a.FromTemperatureDeg,6:F2}° (residual {a.UnexplainedDeg:E1}°)");

            a.UnexplainedDeg.Should().BeApproximately(0.0, 1e-9,
                "the named causes must account for the error they are offered as an explanation of");
            Math.Abs(a.ErrorDeg).Should().BeGreaterThan(1.0);
        }
    }

    private static double[] Even(double value) => Enumerable.Repeat(value, 6).ToArray();

    private static ExhaustSoundDesign Design(
        string name, IReadOnlyList<double> lengthsMm, IReadOnlyList<double> temperaturesK)
    {
        var branches = new List<CollectorBranch>();
        for (var i = 0; i < lengthsMm.Count; i++)
        {
            var cylinder = i + 1;
            branches.Add(new CollectorBranch(
                cylinder,
                SoundCases.M50FiringAngles[cylinder],
                lengthsMm[i] / 1000.0,
                SoundCases.SoundSpeedAt(temperaturesK[i]),
                90.0));
        }

        return new ExhaustSoundDesign { Name = name, Branches = branches };
    }
}
