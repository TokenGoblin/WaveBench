using System.Diagnostics;
using FluentAssertions;
using WaveBench.Boost.Unsteady;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 13 gate: <i>"the turbine hysteresis ... validation case passes"</i>
/// and <i>"volute-resolved runtime within 2× quasi-steady"</i>.
///
/// Validation case 15: <i>"Quasi-steady vs volute-resolved turbine — hysteresis
/// loops must appear and widen with pulse frequency and amplitude, matching
/// published qualitative behaviour."</i>
///
/// What is asserted is qualitative on purpose. There is no redistributable
/// pulsating gas-stand dataset here, so what is checked is the <b>shape</b> of
/// the result reported throughout that literature (Dale &amp; Watson 1986;
/// Winterbone &amp; Pearson; Szymko, Martinez-Botas et al.): resolving the
/// volute opens a loop the map cannot contain, and the loop widens with both
/// pulse amplitude and pulse frequency — that is, with Strouhal number.
/// </summary>
public class TurbineHysteresisTests(ITestOutputHelper output)
{
    private const double ShaftRpm = 90_000.0;

    private static PulsatingTurbineRig Rig(
        TurbineModelKind kind, double amplitudePa, double frequencyHz,
        VoluteGeometry? volute = null, double manifoldLengthM = 0.35) =>
        new(kind, SyntheticTurbo.Turbine(), ShaftRpm,
            meanPressurePa: 175_000.0,
            pulseAmplitudePa: amplitudePa,
            pulseFrequencyHz: frequencyHz,
            volute: volute,
            manifoldLengthM: manifoldLengthM);

    [Fact]
    public void Gate_resolving_the_volute_opens_a_hysteresis_loop_the_map_cannot_contain()
    {
        const double amplitude = 120_000.0;
        const double frequency = 100.0;

        var quasi = Rig(TurbineModelKind.QuasiSteady, amplitude, frequency);
        quasi.Run(periods: 6);

        var resolved = Rig(TurbineModelKind.VoluteResolved, amplitude, frequency);
        resolved.Run(periods: 6);

        var quasiLoop = quasi.LoopOpenness();
        var resolvedLoop = resolved.LoopOpenness();

        output.WriteLine(
            $"quasi-steady    : loop openness {quasiLoop:F4}, mean power {quasi.MeanPowerW() / 1000:F2} kW");
        output.WriteLine(
            $"volute-resolved : loop openness {resolvedLoop:F4}, mean power {resolved.MeanPowerW() / 1000:F2} kW");
        output.WriteLine($"ratio {resolvedLoop / Math.Max(quasiLoop, 1e-9):F1}×");

        // The quasi-steady trace is nearly single-valued but NOT exactly so, and
        // the reason is worth stating rather than tuning away: during the quiet
        // part of the cycle the manifold falls to near p₄ and the rotor stops
        // being the restriction, so the flow is set by what the duct can deliver
        // rather than by the map. That is a real regime, not a memory effect —
        // but it is a fraction of the opening that resolving the volute produces.
        resolvedLoop.Should().BeGreaterThan(5.0 * quasiLoop,
            "the volute's filling and emptying must dominate any duct-limited residue");

        resolvedLoop.Should().BeGreaterThan(0.10,
            "a resolved volute under a strong pulse must open a substantial loop");
    }

    [Fact]
    public void Gate_the_loop_widens_with_pulse_amplitude()
    {
        var results = new List<(double Amplitude, double Openness, double Span)>();

        foreach (var amplitude in new[] { 60_000.0, 120_000.0, 180_000.0 })
        {
            var rig = Rig(TurbineModelKind.VoluteResolved, amplitude, 100.0);
            rig.Run(periods: 6);
            results.Add((amplitude, rig.LoopOpenness(), rig.DeliveredExpansionRatioSpan()));
        }

        output.WriteLine("  amplitude kPa   delivered ER span   loop openness");
        foreach (var (a, w, span) in results)
        {
            output.WriteLine($"{a / 1000,13:F0}   {span,17:F3}   {w,13:F4}");
        }

        results.Select(r => r.Openness).Should().BeInAscendingOrder(
            "a bigger pulse drives the volute further from equilibrium");
    }

    [Fact]
    public void Gate_the_loop_widens_with_pulse_frequency_at_a_given_delivered_amplitude()
    {
        // A raw frequency sweep at fixed SOURCE amplitude does not isolate the
        // frequency effect: the manifold is a low-pass filter, so a faster pulse
        // arrives at the turbine smaller, and the two effects fight. Measured
        // that way the loop turns over above ~100 Hz, which reads as the
        // physics failing when it is really the experiment being confounded.
        //
        // Dividing the opening by the delivered expansion-ratio span separates
        // them: this is how much loop the volute opens PER unit of pulse it was
        // actually given, and it is that quantity which rises with Strouhal
        // number.
        var results = new List<(double Frequency, double Openness, double Span)>();

        foreach (var frequency in new[] { 25.0, 50.0, 100.0, 200.0 })
        {
            var rig = Rig(TurbineModelKind.VoluteResolved, 120_000.0, frequency);
            rig.Run(periods: 6);
            results.Add((frequency, rig.LoopOpenness(), rig.DeliveredExpansionRatioSpan()));
        }

        output.WriteLine("  freq Hz   delivered ER span   openness   openness per unit span");
        foreach (var (f, w, span) in results)
        {
            output.WriteLine($"{f,9:F0}   {span,17:F3}   {w,8:F4}   {w / span,22:F4}");
        }

        results.Select(r => r.Openness / r.Span).Should().BeInAscendingOrder(
            "a faster pulse gives the volute less time to empty — rising Strouhal number");
    }

    [Fact]
    public void Volume_alone_opens_the_loop_and_the_contraction_to_the_rotor_costs_the_power()
    {
        // The controlled experiment behind the headline result. Total pipe
        // length is held constant — bolting a volute onto a fixed manifold
        // lengthens the pipe, which retunes it, and measuring it that way makes
        // a change of GEOMETRY look like a change of MODEL.
        //
        // The two effects separate cleanly. A constant-area volute is the pure
        // volume case: it accounts for the SAME mean power as the quasi-steady
        // model to within rounding, and yet it still opens a substantial
        // hysteresis loop — the filling and emptying is a phase effect, not an
        // energy one, which is exactly what plan §4.3 says a pulsating gas stand
        // measures. Adding the contraction to the rotor face widens the loop
        // further AND takes real power, because now there is a restriction and
        // a reflection as well as a volume.
        const double amplitude = 120_000.0;
        const double frequency = 100.0;
        const double totalLength = 0.50;
        const double voluteLength = 0.150;
        const double manifoldArea = Math.PI * 0.040 * 0.040 / 4.0;

        var quasi = Rig(TurbineModelKind.QuasiSteady, amplitude, frequency, manifoldLengthM: totalLength);
        quasi.Run(periods: 6);

        var straight = Rig(
            TurbineModelKind.VoluteResolved, amplitude, frequency,
            new VoluteGeometry(voluteLength, manifoldArea, manifoldArea, 12),
            totalLength - voluteLength);
        straight.Run(periods: 6);

        var contracting = Rig(
            TurbineModelKind.VoluteResolved, amplitude, frequency,
            new VoluteGeometry(voluteLength, manifoldArea, 8.0e-4, 12),
            totalLength - voluteLength);
        contracting.Run(periods: 6);

        double Error(PulsatingTurbineRig r) =>
            Math.Abs(r.MeanPowerW() - quasi.MeanPowerW()) / quasi.MeanPowerW();

        output.WriteLine($"quasi-steady, 500 mm            : {quasi.MeanPowerW() / 1000:F2} kW");
        output.WriteLine(
            $"resolved, constant-area volute  : {straight.MeanPowerW() / 1000:F2} kW "
            + $"({Error(straight):P1}), openness {straight.LoopOpenness():F4}");
        output.WriteLine(
            $"resolved, contracting volute    : {contracting.MeanPowerW() / 1000:F2} kW "
            + $"({Error(contracting):P1}), openness {contracting.LoopOpenness():F4}");

        Error(straight).Should().BeLessThan(0.01,
            "a constant-area volute is just more pipe: the same energy, accounted the same way");

        straight.LoopOpenness().Should().BeGreaterThan(10.0 * quasi.LoopOpenness(),
            "and yet the volume alone opens a loop, because filling and emptying is a phase effect "
            + "and not an energy one");

        Error(contracting).Should().BeGreaterThan(0.05,
            "the contraction to the rotor face is a restriction and a reflection as well as a volume, "
            + "and it costs real power the quasi-steady model does not see");

        contracting.LoopOpenness().Should().BeGreaterThan(straight.LoopOpenness(),
            "and it widens the loop further still");
    }

    [Fact]
    public void A_volute_too_short_to_resolve_is_refused_rather_than_answered()
    {
        // Found by trying to take the convergence experiment to its limit: with
        // the handover junction a few millimetres from the rotor, the answer
        // moved 30% and moved again with cell count. No real volute is that
        // short, and returning a number from that configuration would be worse
        // than refusing it.
        var act = () => new VoluteGeometry(0.010, 1.2e-3, 8.0e-4, 8).Validate();

        act.Should().Throw<ArgumentException>()
            .WithMessage("*at least 30 mm*")
            .WithMessage("*quasi-steady*");
    }

    [Fact]
    public void Gate_volute_resolved_runtime_stays_within_twice_quasi_steady()
    {
        const double amplitude = 120_000.0;
        const double frequency = 100.0;

        // Warm both paths so the measurement is of the solver, not of the JIT.
        Rig(TurbineModelKind.QuasiSteady, amplitude, frequency).Run(2);
        Rig(TurbineModelKind.VoluteResolved, amplitude, frequency).Run(2);

        var quasiMs = Measure(TurbineModelKind.QuasiSteady, amplitude, frequency);
        var resolvedMs = Measure(TurbineModelKind.VoluteResolved, amplitude, frequency);

        var ratio = resolvedMs / quasiMs;

        output.WriteLine(
            $"quasi-steady {quasiMs:F1} ms, volute-resolved {resolvedMs:F1} ms, ratio {ratio:F2}× (gate: 2×)");

        ratio.Should().BeLessThan(2.0,
            "resolving the volute must stay affordable enough to leave on by default");
    }

    private static double Measure(TurbineModelKind kind, double amplitude, double frequency)
    {
        // Best of three: this runs on a shared machine, and a single timing
        // would report the worst interference rather than the cost of the model.
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var rig = Rig(kind, amplitude, frequency);
            var sw = Stopwatch.StartNew();
            rig.Run(periods: 8);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }
}
