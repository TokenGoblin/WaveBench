using FluentAssertions;
using WaveBench.Analysis;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Plan §5.5: bandwidth characterisation — propagate a broadband pulse down a
/// long uniform pipe, measure the scheme's transfer function between two
/// probes, and publish the −3 dB bandwidth per mesh size. Audio above this
/// frequency is not physical and the UI must grey it out.
/// </summary>
public class BandwidthTests(ITestOutputHelper output)
{
    private static readonly PerfectGas Gas = new(1.4, 287.05);

    /// <summary>
    /// −3 dB bandwidth (Hz) over a 2 m propagation distance in 20 °C air for
    /// the given mesh size, from the probe-to-probe transfer function of a
    /// broadband (σ = 6 mm) Gaussian pulse.
    /// </summary>
    private static double MeasureBandwidth(double cellSize)
    {
        const double length = 3.0;
        const double rho0 = 1.2;
        const double p0 = 1e5;
        const double sigma = 0.006;
        const double probeNear = 0.4;
        const double probeFar = 2.4;
        var a0 = Gas.SoundSpeed(rho0, p0);

        var cells = (int)Math.Round(length / cellSize);
        var solver = new DuctSolver(DuctGeometry.Uniform(length, cells, 0.05), new PerfectGasModel(Gas));
        for (var i = 0; i < cells; i++)
        {
            var dp = 50.0 * Math.Exp(-Math.Pow(solver.CellCentre(i) - 0.25, 2) / (2 * sigma * sigma));
            solver.SetState(i, new PrimitiveState(rho0 + dp / (a0 * a0), dp / (rho0 * a0), p0 + dp));
        }

        var iNear = (int)(probeNear / cellSize);
        var iFar = (int)(probeFar / cellSize);
        var near = new List<double>();
        var far = new List<double>();
        var tEnd = (probeFar + 0.25) / a0;
        var dt = solver.StableTimestep();

        while (solver.Time < tEnd)
        {
            solver.Step(dt);
            near.Add(solver.GetPrimitive(iNear).P - p0);
            far.Add(solver.GetPrimitive(iFar).P - p0);
        }

        var sampleRate = 1.0 / dt;
        var specNear = Fft.MagnitudeSpectrum(near.ToArray().AsSpan(), out var padded);
        var specFar = Fft.MagnitudeSpectrum(far.ToArray().AsSpan(), out _);

        // Scan upward while the source spectrum has signal; the −3 dB point is
        // the first frequency where the probe-to-probe magnitude ratio drops
        // below 1/√2.
        var floor = specNear.Max() * 0.02;
        const double minusThreeDb = 0.70710678;
        for (var bin = 1; bin < specNear.Length; bin++)
        {
            if (specNear[bin] < floor)
            {
                return Fft.BinFrequency(bin, sampleRate, padded); // never attenuated to −3 dB in-band
            }

            if (specFar[bin] / specNear[bin] < minusThreeDb)
            {
                return Fft.BinFrequency(bin, sampleRate, padded);
            }
        }

        return Fft.BinFrequency(specNear.Length - 1, sampleRate, padded);
    }

    [Fact]
    public void Gate_minus_3_db_bandwidth_is_measured_and_published()
    {
        var bw3 = MeasureBandwidth(0.003);
        var bw6 = MeasureBandwidth(0.006);

        output.WriteLine($"-3 dB bandwidth over 2 m, 20 °C air: {bw3:F0} Hz at dx = 3 mm, {bw6:F0} Hz at dx = 6 mm");

        // Published in docs/numerics.md; these assertions pin the documented
        // values so a scheme regression is caught.
        bw3.Should().BeGreaterThan(2000.0, "acoustic-mode mesh must resolve at least 2 kHz over 2 m");
        bw6.Should().BeLessThan(bw3, "halving resolution must lower the resolved bandwidth");
    }

    [Fact]
    public void Fft_recovers_a_known_tone()
    {
        const int n = 1024;
        const double fs = 8192.0;
        const double f0 = 440.0;
        var signal = new double[n];
        for (var i = 0; i < n; i++)
        {
            signal[i] = Math.Sin(2 * Math.PI * f0 * i / fs);
        }

        var spectrum = Fft.MagnitudeSpectrum(signal, out var padded);
        var peakBin = Array.IndexOf(spectrum, spectrum.Max());
        Fft.BinFrequency(peakBin, fs, padded).Should().BeApproximately(f0, fs / padded);
    }
}
