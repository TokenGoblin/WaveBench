using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Analysis;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// §6.1's most important cross-check: at small amplitude two independent
/// methods — the nonlinear finite-volume solver and the linear TMM — must
/// agree on the transmission loss of the same geometry within 1 dB below the
/// resolved bandwidth. Where they diverge at large amplitude, that divergence
/// IS the nonlinearity (plan §6.1).
///
/// Method: the same linear pulse is sent through the muffler geometry and
/// through a straight reference duct; the transmitted-spectrum ratio is the
/// muffler TL with every shared numerical artefact (dispersion, dissipation,
/// boundary softness, windowing) cancelled — no time-gating needed.
/// </summary>
public class TmmVsNonlinearTests(ITestOutputHelper output)
{
    private const double Rho0 = 1.204;
    private const double P0 = 1.0e5;
    private const double Cell = 0.0025;
    private static readonly PerfectGas Gas = new(1.4, 287.05);

    private static (double[] Trace, double Dt) TransmittedTrace(Func<double, double> diameterAt)
    {
        var a0 = Gas.SoundSpeed(Rho0, P0);
        var geometry = DuctGeometry.FromDiameterProfile(2.2, (int)(2.2 / Cell), diameterAt);
        var solver = new DuctSolver(geometry, new PerfectGasModel(Gas));
        for (var i = 0; i < solver.CellCount; i++)
        {
            var x = solver.CellCentre(i);
            var dp = 50.0 * Math.Exp(-Math.Pow(x - 0.35, 2) / (2 * 0.02 * 0.02));
            solver.SetState(i, new PrimitiveState(Rho0 + dp / (a0 * a0), dp / (Rho0 * a0), P0 + dp));
        }

        var probe = (int)(1.7 / Cell);
        var trace = new List<double>();
        var dt = solver.StableTimestep();
        while (solver.Time < 0.060)
        {
            solver.Step(dt);
            trace.Add(solver.GetPrimitive(probe).P - P0);
        }

        return (trace.ToArray(), dt);
    }

    private static double SmoothDiameter(double x)
    {
        // d40 → d80 chamber with 40 mm smooth (smoothstep) transitions:
        // both methods represent this A(x) exactly — the FV at second order,
        // the TMM as segmented ducts (plan §3.3 conical rule). Meshing an
        // ABRUPT step is out of contract: §2.7 makes sudden area changes a
        // boundary component, and the FV converges only first-order at a
        // geometric slope discontinuity (measured; see docs/numerics.md).
        static double Ramp(double s) => s <= 0 ? 0 : s >= 1 ? 1 : s * s * (3 - 2 * s);
        var up = Ramp((x - 0.98) / 0.04);
        var down = Ramp((x - 1.18) / 0.04);
        return 0.040 + 0.040 * (up - down);
    }

    [Fact]
    public void Gate_tmm_and_nonlinear_solver_agree_within_1_db_at_small_amplitude()
    {
        var (chamber, dt) = TransmittedTrace(SmoothDiameter);
        var (straight, _) = TransmittedTrace(_ => 0.040);

        var sampleRate = 1.0 / dt;
        var specChamber = Fft.MagnitudeSpectrum(chamber, out var padded);
        var specStraight = Fft.MagnitudeSpectrum(straight, out _);

        var pipeArea = Math.PI / 4.0 * 0.040 * 0.040;
        var a0 = Gas.SoundSpeed(Rho0, P0);
        var medium = new AcousticMedium { SoundSpeed = a0, Density = Rho0 };
        var network = new AcousticNetwork(medium, pipeArea, pipeArea);
        // Segmented-duct TMM of the same smooth profile, 5 mm segments
        // (≫ 20 segments per wavelength across the band). Undamped, matching
        // the frictionless FV run; no lumped junction corrections needed.
        const double segment = 0.005;
        for (var x = 0.95; x < 1.25; x += segment)
        {
            var d = SmoothDiameter(x + segment / 2.0);
            network.Elements.Add(new UniformDuctElement(segment, Math.PI / 4.0 * d * d, Damped: false));
        }

        var worst = 0.0;
        for (var f = 200.0; f <= 2500.0; f += 50.0)
        {
            var bin = (int)Math.Round(f * padded / sampleRate);
            var measured = 20.0 * Math.Log10(specStraight[bin] / Math.Max(specChamber[bin], 1e-12));
            var tmm = network.TransmissionLoss(f);
            var deviation = Math.Abs(measured - tmm);
            worst = Math.Max(worst, deviation);
            deviation.Should().BeLessThan(1.0,
                $"gate: TMM vs nonlinear within 1 dB at {f:F0} Hz (TMM {tmm:F2} dB, measured {measured:F2} dB)");
        }

        output.WriteLine($"worst TMM-vs-nonlinear deviation 200–2500 Hz: {worst:F3} dB");
    }
}
