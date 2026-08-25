using FluentAssertions;
using WaveBench.Analysis.ValidationCases;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Validation;

/// <summary>
/// Validation case: Yin, S., "Volumetric Efficiency Modeling of a Four-Stroke
/// IC Engine", M.S. thesis, Colorado State University (Mountain Scholar, open
/// access). Model, provenance and the documented short-runner discrepancy
/// live in <see cref="YinRunnerLengthCase"/> and docs/physics.md §1.9; the
/// same case powers the CLI `validate` command and its report plot.
/// </summary>
public class YinThesisValidation(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Validation")]
    public void Runner_length_sweep_reproduces_the_thesis_optimal_speed_trend()
    {
        var results = YinRunnerLengthCase.RunAll(output.WriteLine)
            .ToDictionary(r => r.RunnerLengthM);

        // Gate (plan Phase 6): peak rpm within 250 of the published GT-Power
        // optimum in the runner-resonance-dominated regime (600/800 mm).
        foreach (var runner in YinRunnerLengthCase.GatedRunners)
        {
            var r = results[runner];
            Math.Abs(r.PeakRpm - r.PublishedRpm).Should().BeLessThanOrEqualTo(250.0,
                $"runner {runner * 1000:F0} mm: published {r.PublishedRpm:F0}, got {r.PeakRpm:F0}");
        }

        // Short runners: bounded, documented discrepancy (thesis Cd curve is
        // figure-only; its own two models disagree by up to 1.8× here).
        foreach (var runner in new[] { 0.200, 0.400 })
        {
            var r = results[runner];
            Math.Abs(r.PeakRpm - r.PublishedRpm).Should().BeLessThanOrEqualTo(1100.0,
                $"runner {runner * 1000:F0} mm documented bound");
        }

        results[0.800].PeakRpm.Should().BeLessThan(results[0.200].PeakRpm,
            "longer runners tune lower (thesis conclusion)");
    }
}
