using FluentAssertions;
using WaveBench.Boost.Thermal;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Plan §4.7: "repeat-run heat soak, because a second dyno pull is not the
/// same as the first." <see cref="TurboThermalModel"/>'s housing state
/// (Phase 13) already carries over between calls to <c>Step</c>; what was
/// missing until Stage B is a scripted transient to carry it OVER, and a
/// coupling that lets the carried-over heat actually change what the engine
/// breathes — see <see cref="TransientDriver"/>'s diabatic-outlet remarks and
/// docs/physics.md §6.3.
/// </summary>
public class HeatSoakScenarioTests(ITestOutputHelper output)
{
    private static readonly DrivingProfile StepThrottle = new(new ThrottleStep(0.25, 1.0, StepAtSeconds: 0.0));

    [Fact]
    public void Gate_a_second_scripted_pull_runs_hotter_than_the_first_because_the_housings_carried_over_heat()
    {
        var sharedThermal = new TurboThermalModel();

        var firstRig = new TransientSpoolRig(sharedThermal: sharedThermal);
        var first = Run(firstRig, StepThrottle, durationSeconds: 0.03);
        var housingBeforeHoldK = sharedThermal.CompressorHousingK;

        // A coupled gas-dynamics transient is CFL-limited to microsecond-scale
        // steps, but the housings' own time constants are seconds to minutes
        // (TurboThermalModel's own doc comment: "stepped on the transient's
        // own clock rather than the solver's"). Simulating an entire
        // multi-second hold-hot period through the gas dynamics is not
        // computationally reasonable in a unit test, and it is not what the
        // thermal model needs either — it accepts an arbitrary dt already.
        // This directly represents the engine idling, still hot, between two
        // logged dyno pulls — held at a representative ON-ENGINE hot-idle
        // exhaust condition (900 K turbine inlet, the engine bay itself
        // warmer than a cold gas stand), not whatever this single-cylinder
        // rig's own low-load exhaust happens to read at 30 ms into a cold
        // start. That is exactly the scenario plan §4.7 names ("a second
        // dyno pull is not the same as the first").
        const double holdTurbineInletK = 900.0;
        var onEngine = new TurboEnvironment();

        // Idle flow through the compressor is small, so the air it sees is
        // close to under-hood ambient, not the cold intake-side ambient
        // (~298 K) a boosted pull draws from — using the latter here would
        // pull compressor housing toward a temperature colder than the
        // engine bay it actually sits in.
        var holdCompressorAirK = onEngine.AmbientK + 5.0;
        for (var i = 0; i < 20; i++)
        {
            sharedThermal.Step(3.0, holdTurbineInletK, holdCompressorAirK, onEngine);
        }

        var housingAfterHoldK = sharedThermal.CompressorHousingK;

        var second = Run(new TransientSpoolRig(sharedThermal: sharedThermal), StepThrottle, durationSeconds: 0.03);
        var housingAfterSecondK = sharedThermal.CompressorHousingK;

        var firstOutletK = first[^1].CompressorOutletK;
        var secondOutletK = second[^1].CompressorOutletK;

        output.WriteLine($"compressor housing: {housingBeforeHoldK:F2} K after pull 1 -> {housingAfterHoldK:F2} K after a "
                          + $"60 s hot idle -> {housingAfterSecondK:F2} K after pull 2");
        output.WriteLine($"compressor outlet at t = 30 ms: {firstOutletK:F2} K (pull 1) vs {secondOutletK:F2} K (pull 2), "
                          + $"Δ = {secondOutletK - firstOutletK:F3} K");

        housingAfterHoldK.Should().BeGreaterThan(housingBeforeHoldK,
            "held at the first pull's hot exhaust condition, the housing should keep climbing toward its steady value");
        secondOutletK.Should().BeGreaterThan(firstOutletK,
            "a hotter housing puts more heat into the same aerodynamic duty, so the same throttle step should come out of the compressor hotter the second time");
    }

    [Fact]
    public void A_fresh_thermal_model_run_twice_independently_shows_no_carry_over()
    {
        // Control: two pulls that do NOT share a TurboThermalModel should
        // land at (very nearly) the same outlet temperature, since neither
        // one has anything to carry over. This is what tells the gate test
        // above apart from "later runs are just different for some other
        // reason" — the effect it is checking for is CARRIED HEAT, not
        // sample-to-sample noise.
        var first = Run(new TransientSpoolRig(), StepThrottle, durationSeconds: 0.03);
        var second = Run(new TransientSpoolRig(), StepThrottle, durationSeconds: 0.03);

        var firstOutletK = first[^1].CompressorOutletK;
        var secondOutletK = second[^1].CompressorOutletK;

        output.WriteLine($"independent pulls: {firstOutletK:F3} K vs {secondOutletK:F3} K, "
                          + $"Δ = {Math.Abs(secondOutletK - firstOutletK):F4} K");

        secondOutletK.Should().BeApproximately(firstOutletK, 0.01,
            "two runs that never share a thermal model should reproduce each other, not merely trend the same way");
    }

    private static List<TransientSample> Run(TransientSpoolRig rig, DrivingProfile profile, double durationSeconds)
    {
        var samples = new List<TransientSample>();
        TransientSample sample;
        do
        {
            sample = rig.Driver.Advance(profile);
            samples.Add(sample);
        }
        while (sample.TimeSeconds < durationSeconds);

        return samples;
    }
}
