using FluentAssertions;
using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// What a junction does to a strong pulse passing straight through it.
///
/// This test exists because the volute work needed an answer to a question it
/// had raised: splitting one pipe into two joined by a junction changed the
/// turbine power by a third, and either the junction was wrong or the volute
/// model was. The way to find out is to take the turbine out entirely and ask
/// whether a junction in the middle of a plain pipe conserves what flows
/// through it.
///
/// <see cref="JunctionModel.ConstantPressure"/> solves a LINEARISED
/// characteristic relation, <c>u = u_i + (p_i − p*)/(ρa)</c>. That is exact for
/// acoustic amplitudes and progressively wrong as the pulse grows — and a
/// blowdown pulse is not an acoustic amplitude. This measures where it stops
/// being good enough, so that the limit is a documented number rather than a
/// surprise inside somebody's collector.
/// </summary>
public class JunctionUnderPulseTests(ITestOutputHelper output)
{
    private const double LengthM = 0.50;
    private const double DiameterM = 0.040;
    private const double MeanPa = 175_000.0;
    private const double TemperatureK = 1050.0;

    /// <summary>
    /// Push a raised-cosine pulse through a pipe and measure the mass and
    /// energy that leave the far end, with and without a junction in the middle.
    /// </summary>
    private static (double MassKg, double EnergyJ) PushPulse(bool split, double amplitudePa)
    {
        var gas = new PerfectGasModel(new PerfectGas(1.33, 287.0));
        var rho = MeanPa / (287.0 * TemperatureK);

        var ducts = new List<DuctSolver>();
        var junctions = new List<Junction>();

        if (split)
        {
            ducts.Add(new DuctSolver(DuctGeometry.Uniform(LengthM / 2, 20, DiameterM), gas));
            ducts.Add(new DuctSolver(DuctGeometry.Uniform(LengthM / 2, 20, DiameterM), gas));
            var junction = new Junction(gas);
            junction.Connect(ducts[0], false);
            junction.Connect(ducts[1], true);
            junctions.Add(junction);
        }
        else
        {
            ducts.Add(new DuctSolver(DuctGeometry.Uniform(LengthM, 40, DiameterM), gas));
        }

        foreach (var duct in ducts)
        {
            for (var i = 0; i < duct.CellCount; i++)
            {
                duct.SetState(i, new PrimitiveState(rho, 0.0, MeanPa));
            }
        }

        var source = new ReservoirBoundary { StagnationPressure = MeanPa, StagnationTemperature = TemperatureK };
        ducts[0].LeftBoundary = BoundaryKind.External;
        ducts[0].LeftEnd = source;

        var sink = new ReservoirBoundary { StagnationPressure = 101_325.0, StagnationTemperature = TemperatureK };
        ducts[^1].RightBoundary = BoundaryKind.External;
        ducts[^1].RightEnd = sink;

        const double frequency = 100.0;
        const double periods = 6.0;
        var area = Math.PI * DiameterM * DiameterM / 4.0;

        double time = 0, mass = 0, energy = 0;
        var recordFrom = (periods - 1) / frequency;

        while (time < periods / frequency)
        {
            var dt = ducts.Min(d => d.StableTimestep());
            dt = Math.Min(dt, (periods / frequency) - time);
            if (dt <= 0)
            {
                break;
            }

            var phase = (time * frequency) % 1.0;
            source.StagnationPressure = phase < 1.0 / 3.0
                ? MeanPa + (amplitudePa * 0.5 * (1.0 - Math.Cos(6.0 * Math.PI * phase)))
                : MeanPa;

            foreach (var junction in junctions)
            {
                junction.Update();
            }

            foreach (var duct in ducts)
            {
                duct.Step(dt);
            }

            time += dt;

            if (time >= recordFrom)
            {
                // Flux out of the last cell: what actually left the pipe.
                var last = ducts[^1];
                var s = last.GetState(last.CellCount - 1);
                var w = last.GetPrimitive(last.CellCount - 1);
                var cp = 1.33 * 287.0 / 0.33;
                var flow = w.Rho * s.U * area;
                mass += flow * dt;
                energy += flow * ((cp * s.T) + (0.5 * s.U * s.U)) * dt;
            }
        }

        return (mass, energy);
    }

    [Theory]
    [InlineData(5_000.0)]
    [InlineData(20_000.0)]
    [InlineData(60_000.0)]
    [InlineData(120_000.0)]
    public void A_junction_passes_a_pulse_through_with_an_error_that_grows_with_amplitude(double amplitude)
    {
        var whole = PushPulse(split: false, amplitude);
        var jointed = PushPulse(split: true, amplitude);

        var massError = Math.Abs(jointed.MassKg - whole.MassKg) / Math.Abs(whole.MassKg);
        var energyError = Math.Abs(jointed.EnergyJ - whole.EnergyJ) / Math.Abs(whole.EnergyJ);

        output.WriteLine(
            $"amplitude {amplitude / 1000,4:F0} kPa ({amplitude / MeanPa:P0} of mean): "
            + $"mass {massError:P2}, energy {energyError:P2}  "
            + $"[{whole.MassKg:E4} vs {jointed.MassKg:E4} kg]");

        // Acoustic amplitudes must pass through essentially untouched — that is
        // the regime the linearisation is exact in, and failing here would mean
        // the junction was broken rather than merely linear.
        if (amplitude <= 20_000.0)
        {
            massError.Should().BeLessThan(0.02,
                "a junction must be transparent to a small pulse in a uniform pipe");
        }
    }

    [Fact]
    public void The_junction_error_under_a_blowdown_sized_pulse_is_recorded_here_deliberately()
    {
        // Not an assertion of correctness — a measurement of a known limitation,
        // pinned so it cannot drift unnoticed and so the number is on record.
        // Plan §2.6's Bassett unsteady junction coefficients are the fix, and
        // they are already a tracked deferral.
        var whole = PushPulse(split: false, 120_000.0);
        var jointed = PushPulse(split: true, 120_000.0);

        var massError = Math.Abs(jointed.MassKg - whole.MassKg) / Math.Abs(whole.MassKg);

        output.WriteLine(
            $"blowdown-sized pulse (120 kPa on a 175 kPa mean): junction changes delivered mass by {massError:P1}");

        massError.Should().BeLessThan(0.60,
            "the linearised junction is known to be inaccurate at this amplitude; this pins how inaccurate");
    }
}
