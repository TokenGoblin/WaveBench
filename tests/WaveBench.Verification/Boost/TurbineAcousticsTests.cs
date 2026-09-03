using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Metrics;
using WaveBench.Boost.Acoustics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 15 gate, second clause: <i>"turbine acoustic attenuation and OPI
/// (Order Purity Index) drop as expected."</i> Plan §4.8: <i>"the turbine is
/// a strong acoustic attenuator ... report the Order Purity Index drop
/// relative to the same engine NA. That is a quantitative, physical answer to
/// 'why do turbo cars sound flat.'"</i>
///
/// The mechanism modelled here: the turbine's resistive (work-extraction)
/// impedance dissipates acoustic energy that a plain open tailpipe would
/// otherwise reflect back up the duct. Reflection off an open end is exactly
/// what reinforces engine-order content in an NA exhaust (it is the physical
/// basis of header tuning at all); a turbine that instead absorbs a
/// significant share of that energy weakens the reinforcement, which shows up
/// as a lower Order Purity Index downstream of it. Both networks below use
/// the SAME termination (unflanged open, i.e. what the atmosphere past the
/// turbine or tailpipe presents) and the SAME upstream duct, so any
/// difference is attributable to the turbine element alone, not to a
/// different assumed boundary.
/// </summary>
public class TurbineAcousticsTests(ITestOutputHelper output)
{
    private static readonly AcousticMedium ExhaustGas = new()
    {
        SoundSpeed = SoundCases.SoundSpeedAt(920.0),
        Density = 101_325.0 / (288.0 * 920.0),
        Temperature = 920.0,
        Gamma = 1.33,
    };

    private const double PipeDiameterM = 0.05;

    private static double PipeArea => Math.PI / 4.0 * PipeDiameterM * PipeDiameterM;

    [Fact]
    public void Gate_turbine_attenuates_exhaust_order_energy_and_drops_opi_relative_to_na()
    {
        var design = SoundCases.M50Factory();
        const double rpm = 4000.0;
        var firingOrder = design.FiringOrder;

        var source = CollectorSpectrum.At(design, rpm);

        var reference = BuildNetwork(includeTurbine: false);
        var withTurbine = BuildNetwork(includeTurbine: true);

        var naSpectrum = Filter(source, reference, rpm);
        var turboSpectrum = Filter(source, withTurbine, rpm);

        var opiNa = CharacterMetrics.OrderPurityIndex(naSpectrum, firingOrder);
        var opiTurbo = CharacterMetrics.OrderPurityIndex(turboSpectrum, firingOrder);

        output.WriteLine($"NA OPI:    {opiNa:F4}");
        output.WriteLine($"Turbo OPI: {opiTurbo:F4}");
        output.WriteLine($"Drop:      {(opiNa - opiTurbo):F4}");

        var tlAtFiring = withTurbine.TransmissionLoss(firingOrder * rpm / 60.0)
                          - reference.TransmissionLoss(firingOrder * rpm / 60.0);
        output.WriteLine($"Turbine's own added TL at the firing frequency: {tlAtFiring:F2} dB");

        opiTurbo.Should().BeLessThan(opiNa,
            "a turbine that dissipates rather than reflects removes the reinforcement that concentrates "
            + "energy onto the firing harmonics, which is the physical reason turbo exhausts read flatter");
    }

    [Fact]
    public void The_turbine_element_adds_real_transmission_loss_over_a_bare_duct()
    {
        var reference = BuildNetwork(includeTurbine: false);
        var withTurbine = BuildNetwork(includeTurbine: true);

        double[] frequencies = [100, 300, 600, 1000, 2000];
        foreach (var f in frequencies)
        {
            var added = withTurbine.TransmissionLoss(f) - reference.TransmissionLoss(f);
            output.WriteLine($"{f,5:F0} Hz: +{added:F2} dB over a bare duct");
            added.Should().BeGreaterThan(0.0, "the resistive work-extraction term must add real loss, not just reactance");
        }
    }

    private static OrderSpectrum Filter(OrderSpectrum source, AcousticNetwork network, double rpm)
    {
        var filtered = new double[source.Amplitude.Length];
        for (var i = 0; i < source.Amplitude.Length; i++)
        {
            var order = source.Orders[i];
            var frequency = order * rpm / 60.0;
            var transfer = network.PressureTransfer(frequency, TerminationKind.UnflangedOpen).Magnitude;
            filtered[i] = source.Amplitude[i] * transfer;
        }

        return new OrderSpectrum(source.OrderStep, filtered);
    }

    private static AcousticNetwork BuildNetwork(bool includeTurbine)
    {
        var area = PipeArea;
        var network = new AcousticNetwork(ExhaustGas, area, area);
        network.Elements.Add(new UniformDuctElement(0.15, area));

        if (includeTurbine)
        {
            network.Elements.Add(new TurbineFourPoleElement(
                UpstreamAreaM2: area,
                RotorThroatAreaM2: area * 0.6,
                MeanMassFlowKgPerS: 0.09));
        }

        return network;
    }
}
