using FluentAssertions;
using WaveBench.Boost;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 15 §4.7's transient deliverables — <see cref="TransientDriver"/>,
/// time-to-90%-boost/torque with a sensitivity band — verified by
/// self-consistency rather than against a measured case.
///
/// Gate clause 1 ("transient spool within 15% of a measured case") is a
/// documented, deliberate deferral: a real search (web search plus a research
/// pass covering Argonne's Downloadable Dynamometer Database and an MDPI
/// <i>Energies</i> paper) found no redistributable measured transient-spool
/// dataset — see docs/physics.md §6.4 and CLAUDE.md's standing deferrals
/// (validation case 20). What this class actually checks, and what CI
/// actually gates on: the coupling converges under mesh refinement, conserves
/// energy through the shaft exactly as <see cref="TurboShaft"/> already
/// verifies on its own, produces a boost rise that behaves sensibly under a
/// step throttle, and gives a sensitivity band that widens when the supplied
/// inertia/friction uncertainty widens — per Part 14 Gotcha #25.
/// </summary>
public class TransientSpoolTests(ITestOutputHelper output)
{
    private static readonly DrivingProfile StepThrottle = new(new ThrottleStep(0.25, 1.0, StepAtSeconds: 0.0));

    [Fact]
    public void Gate_transient_spool_converges_under_mesh_refinement()
    {
        var coarse = Run(new TransientSpoolRig(cellSizeMm: 24.0), StepThrottle, durationSeconds: 0.03);
        var fine = Run(new TransientSpoolRig(cellSizeMm: 12.0), StepThrottle, durationSeconds: 0.03);

        var coarseFinal = coarse[^1];
        var fineFinal = fine[^1];

        var rpmError = Math.Abs(fineFinal.ShaftRpm - coarseFinal.ShaftRpm) / fineFinal.ShaftRpm;
        var boostError = Math.Abs(fineFinal.BoostPressurePa - coarseFinal.BoostPressurePa) / fineFinal.BoostPressurePa;

        output.WriteLine($"coarse (24 mm, {coarse.Count} steps): {coarseFinal.ShaftRpm:N0} rpm, "
                          + $"{coarseFinal.BoostPressurePa / 1000.0:F2} kPa at t = {coarseFinal.TimeSeconds * 1000:F2} ms");
        output.WriteLine($"fine   (12 mm, {fine.Count} steps): {fineFinal.ShaftRpm:N0} rpm, "
                          + $"{fineFinal.BoostPressurePa / 1000.0:F2} kPa at t = {fineFinal.TimeSeconds * 1000:F2} ms");
        output.WriteLine($"rpm error {rpmError:P2}, boost error {boostError:P2}");

        rpmError.Should().BeLessThan(0.10, "halving the cell size should not materially change where the shaft ends up");
        boostError.Should().BeLessThan(0.10, "nor the boost pressure it produced");
    }

    [Fact]
    public void Gate_shaft_energy_balances_through_a_transient()
    {
        var rig = new TransientSpoolRig();
        var shaft = rig.Stage.Shaft;

        var omega0 = shaft.Omega;
        var ke0 = 0.5 * shaft.Inertia * omega0 * omega0;

        var netWorkJ = 0.0;
        var previousTime = 0.0;
        while (previousTime < 0.03)
        {
            rig.Driver.Advance(StepThrottle);
            var dt = rig.Engine.Time - previousTime;
            previousTime = rig.Engine.Time;

            // NetPowerW·dt is exactly what TurboShaft.Step just integrated
            // into kinetic energy — summing it independently and comparing
            // against the shaft's own before/after ΔKE is a check on the
            // COUPLING (did TransientDriver hand it the right dt and the
            // right powers), not a re-test of TurboShaft's own integration,
            // which ShaftAndControlTests already covers in isolation.
            netWorkJ += shaft.NetPowerW * dt;
        }

        var omega1 = shaft.Omega;
        var ke1 = 0.5 * shaft.Inertia * omega1 * omega1;
        var deltaKe = ke1 - ke0;

        var errorFraction = Math.Abs(deltaKe - netWorkJ) / Math.Max(Math.Abs(deltaKe), 1e-9);

        output.WriteLine($"ΔKE = {deltaKe * 1000:F4} mJ, Σ(NetPowerW·dt) = {netWorkJ * 1000:F4} mJ, "
                          + $"error {errorFraction:P4}");

        errorFraction.Should().BeLessThan(1e-6, "the shaft's own energy formulation should close to numerical precision");
    }

    [Fact]
    public void Gate_boost_rises_toward_its_new_steady_value_under_a_step_throttle()
    {
        var rig = new TransientSpoolRig(initialShaftRpm: 30_000.0);
        var samples = Run(rig, StepThrottle, durationSeconds: 0.03);

        var start = samples[0].BoostPressurePa;
        var end = samples[^1].BoostPressurePa;

        output.WriteLine($"boost: {start / 1000.0:F2} kPa -> {end / 1000.0:F2} kPa over {samples.Count} steps "
                          + $"({samples[^1].TimeSeconds * 1000:F2} ms)");

        end.Should().BeGreaterThan(start, "a wide-open step at the same rpm should raise, not lower, boost");

        // Coarsely resampled (every ~5% of the run) rather than step-to-step:
        // a poppet valve's pulsing flow, even smoothed, can wobble a fraction
        // of a percent between consecutive CFL steps without that being a
        // real reversal of the transient's trend.
        var bucketCount = 20;
        var buckets = new double[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            var index = (int)((i + 1) / (double)bucketCount * (samples.Count - 1));
            buckets[i] = samples[index].BoostPressurePa;
        }

        var worstReversal = 0.0;
        for (var i = 1; i < buckets.Length; i++)
        {
            worstReversal = Math.Max(worstReversal, buckets[i - 1] - buckets[i]);
        }

        var span = end - start;
        output.WriteLine($"worst bucket-to-bucket reversal: {worstReversal / 1000.0:F3} kPa "
                          + $"({(span > 0 ? worstReversal / span : 0):P1} of the total rise)");

        worstReversal.Should().BeLessThan(0.15 * span,
            "no single coarse step of the transient should give back more than 15% of the total rise");
    }

    [Fact]
    public void Gate_the_sensitivity_band_widens_as_the_supplied_uncertainty_widens()
    {
        var narrow = EvaluateBand(inertiaSpread: 0.05, frictionSpread: 0.05);
        var wide = EvaluateBand(inertiaSpread: 0.40, frictionSpread: 0.40);

        output.WriteLine($"narrow (±5%): boost band {narrow.BoostBandWidthS * 1000:F3} ms, "
                          + $"torque band {narrow.TorqueBandWidthS * 1000:F3} ms");
        output.WriteLine($"wide   (±40%): boost band {wide.BoostBandWidthS * 1000:F3} ms, "
                          + $"torque band {wide.TorqueBandWidthS * 1000:F3} ms");

        wide.BoostBandWidthS.Should().BeGreaterThan(narrow.BoostBandWidthS,
            "more uncertainty in inertia and friction should produce a wider band, never a narrower one");

        narrow.TimeTo90PercentBoostS.Should().BeInRange(
            narrow.TimeTo90PercentBoostLowS, narrow.TimeTo90PercentBoostHighS,
            "the nominal run should fall inside its own bound runs, since it is parametrically between them");
    }

    private static TimeToTorqueResult EvaluateBand(double inertiaSpread, double frictionSpread)
    {
        const double nominalInertia = 3.1e-6;
        var nominalFriction = new BearingFriction();

        TransientDriver BuildAt(double inertiaScale, double frictionScale)
        {
            var rig = new TransientSpoolRig(
                shaftInertia: nominalInertia * inertiaScale,
                friction: nominalFriction with { Coefficient = nominalFriction.Coefficient * frictionScale });
            return rig.Driver;
        }

        return TimeToTorqueResult.Evaluate(
            buildNominal: () => BuildAt(1.0, 1.0),
            buildBoundA: () => BuildAt(1.0 - inertiaSpread, 1.0 - frictionSpread),
            buildBoundB: () => BuildAt(1.0 + inertiaSpread, 1.0 + frictionSpread),
            profile: StepThrottle,
            durationSeconds: 0.03);
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
