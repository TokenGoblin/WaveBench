using System.Text.RegularExpressions;
using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 20 Sound workspace (plan §8.4), including the gate's third clause —
/// <i>"the explanation text is correct"</i>.
///
/// "Correct" here has to mean more than "well formed". Every number in the
/// sentence is checked against the model it describes, because a fluent
/// sentence with the wrong cylinder in it is worse than no sentence: it sends
/// a builder to cut the wrong pipe.
/// </summary>
public class SoundWorkspaceTests(ITestOutputHelper output)
{
    private static SoundWorkspace Workspace(double rpm = 4000.0) =>
        new(SoundCases.M50Factory(), SoundCases.M50EqualLength()) { Rpm = rpm };

    [Fact]
    public void Gate_the_explanation_text_is_correct()
    {
        // Plan §8.4 gives the shape it wants:
        //   "Cylinder 6's pulse arrives 14° late at 6500 rpm because its
        //    primary is 63 mm longer and runs 40 K cooler. This puts 11% of
        //    the exhaust energy into non-firing orders."
        var w = Workspace(6500.0);

        var factory = w.Explain(w.A);
        var equal = w.Explain(w.B);

        output.WriteLine("factory: " + factory);
        output.WriteLine("6-1:     " + equal);
        output.WriteLine("compare: " + w.CompareSummary());

        factory.Should().MatchRegex(@"^Cylinder \d's pulse arrives \d+° (late|early) at 6500 rpm because ");
        factory.Should().MatchRegex(@"puts \d+% of the exhaust energy into non-firing orders\.$");

        // Every claim in the sentence must be true of the model it describes.
        var timing = w.Timing(w.A);
        var worst = timing.TimingErrorDeg
            .Select((e, i) => (Error: e, Index: i))
            .MaxBy(x => Math.Abs(x.Error));

        var named = int.Parse(Regex.Match(factory, @"Cylinder (\d)").Groups[1].Value);
        named.Should().Be(w.A.Branches[worst.Index].Cylinder,
            "the sentence must name the cylinder that is actually worst");

        var statedError = double.Parse(Regex.Match(factory, @"arrives (\d+)°").Groups[1].Value);
        statedError.Should().BeApproximately(Math.Abs(worst.Error), 0.5);
        factory.Should().Contain(worst.Error > 0 ? "late" : "early");

        var statedLeak = double.Parse(Regex.Match(factory, @"puts (\d+)%").Groups[1].Value);
        statedLeak.Should().BeApproximately(w.NonFiringEnergyFraction(w.A) * 100, 1.0);

        // The reason must be attributed, and the attribution must match the
        // geometry: this cylinder's primary really is the longest and its gas
        // really is the coolest.
        var attribution = CollectorSpectrum.Attribute(w.A, 6500.0)!;
        if (Math.Abs(attribution.LengthDeltaM) >= 0.001)
        {
            factory.Should().Contain(attribution.LengthDeltaM > 0 ? "mm longer" : "mm shorter");
            var statedMm = double.Parse(Regex.Match(factory, @"(\d+) mm").Groups[1].Value);
            statedMm.Should().BeApproximately(Math.Abs(attribution.LengthDeltaM) * 1000, 1.0);
        }

        if (Math.Abs(attribution.TemperatureDeltaK) >= 5.0)
        {
            factory.Should().Contain(attribution.TemperatureDeltaK < 0 ? "K cooler" : "K hotter");
        }

        // And the clean design must not be described as though it had a fault.
        equal.Should().NotContain("late").And.NotContain("early");
        equal.Should().Contain("equal-length header is for");
    }

    [Fact]
    public void The_explanation_blames_only_causes_the_design_actually_has()
    {
        var lengthOnly = Design("length only", [400, 400, 400, 400, 400, 520], Even(920));
        var temperatureOnly = Design("temperature only", Even(400), [920, 920, 920, 920, 920, 700]);

        var w = new SoundWorkspace(lengthOnly, temperatureOnly) { Rpm = 6000.0 };

        var byLength = w.Explain(lengthOnly);
        var byTemperature = w.Explain(temperatureOnly);
        output.WriteLine("length only:      " + byLength);
        output.WriteLine("temperature only: " + byTemperature);

        byLength.Should().Contain("mm longer");
        byLength.Should().NotContain("cooler").And.NotContain("hotter",
            "there is no temperature spread in this design, so none may be blamed");

        byTemperature.Should().Contain("cooler");
        byTemperature.Should().NotContain("mm longer").And.NotContain("mm shorter");

        // A cooler cylinder carries a slower wave, so its pulse arrives LATE.
        byTemperature.Should().Contain("late");
    }

    [Fact]
    public void The_comparison_summary_names_the_better_design()
    {
        var w = Workspace();
        var summary = w.CompareSummary();
        output.WriteLine(summary);

        summary.Should().StartWith(w.B.Name, "the equal-length header is the cleaner of the two");
        summary.Should().Contain(w.A.Name);
        summary.Should().Contain("warble");
    }

    // ---- Figures ----------------------------------------------------------

    [Fact]
    public void Every_sound_figure_renders_and_exports()
    {
        var w = Workspace();
        var plots = new[]
        {
            w.TimingChart(),
            w.OrderSpectrumChart(),
            w.Waterfall(steps: 12),
            w.CharacterRadar(),
        };

        foreach (var plot in plots)
        {
            var svg = SvgPlotWriter.Write(plot);
            System.Xml.Linq.XDocument.Parse(svg);
            output.WriteLine($"{plot.FileStem(),-42} {svg.Length,7} bytes SVG");
        }

        plots.Select(p => p.FileStem()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_spectrum_chart_marks_the_firing_harmonics_and_overlays_both_designs()
    {
        var w = Workspace();
        var plot = w.OrderSpectrumChart();

        plot.Series.Should().HaveCount(2);
        plot.Series[0].Name.Should().Be(w.A.Name);
        plot.Series[1].Name.Should().Be(w.B.Name);

        // Distinguishable without colour (§8.11).
        plot.Series[0].Kind.Should().NotBe(plot.Series[1].Kind);

        // A four-stroke six: 3, 6, 9, 12.
        plot.Markers.Select(m => m.X).Should().Equal(3.0, 6.0, 9.0, 12.0);
    }

    [Fact]
    public void Every_figure_says_whether_it_is_an_estimate_or_a_solve()
    {
        // Plan §8.4 requires the interactive estimate and the refined solve to
        // be told apart. An estimate presented as a solve is worse than no
        // estimate.
        var w = Workspace();

        w.TimingChart().Subtitle.Should().Contain("instant estimate");

        w.Fidelity = SoundFidelity.Solved;
        w.TimingChart().Subtitle.Should().Contain("solved gas state");
        w.Waterfall(steps: 6).Subtitle.Should().Contain("solved gas state");
    }

    [Fact]
    public void Changing_a_primary_length_changes_the_timing_and_the_spectrum()
    {
        // The interaction the gate is about: a length change has to move both
        // figures, not just the one the user is looking at.
        var w = Workspace(6000.0);
        var before = w.Timing(w.B).MaxAbsTimingErrorDeg;
        var beforeLeak = w.NonFiringEnergyFraction(w.B);

        before.Should().BeLessThan(1e-9, "the 6-1 starts perfectly even");
        beforeLeak.Should().BeLessThan(1e-6);

        w.B = w.B.WithPrimaryLength(3, 0.900);

        var after = w.Timing(w.B).MaxAbsTimingErrorDeg;
        var afterLeak = w.NonFiringEnergyFraction(w.B);

        output.WriteLine($"lengthening one primary by 180 mm: error {before:F2}° -> {after:F2}°, "
                         + $"off-harmonic {beforeLeak * 100:F2}% -> {afterLeak * 100:F2}%");

        after.Should().BeGreaterThan(5.0);
        afterLeak.Should().BeGreaterThan(0.01);
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
