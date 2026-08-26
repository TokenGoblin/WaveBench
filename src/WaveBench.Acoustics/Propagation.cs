using System.Numerics;

namespace WaveBench.Acoustics;

/// <summary>
/// Monopole source strength and free-field propagation (plan §3.1/§3.5).
/// </summary>
public static class SourceRadiation
{
    /// <summary>
    /// Far-field pressure spectrum of a monopole from the volume-velocity
    /// spectrum: P(f) = jω·ρ₀/(4πr)·Q(f), with the differentiation done in
    /// the frequency domain and a documented roll-off above the resolved
    /// bandwidth (plan §3.1 — never a naive finite difference).
    /// </summary>
    public static Complex FarFieldPressure(
        Complex volumeVelocitySpectrum, double frequency, double distance,
        double density, double resolvedBandwidthHz)
    {
        var omega = 2.0 * Math.PI * frequency;
        var rollOff = 1.0 / Math.Sqrt(1.0 + Math.Pow(frequency / resolvedBandwidthHz, 4));
        return Complex.ImaginaryOne * omega * density / (4.0 * Math.PI * distance)
               * volumeVelocitySpectrum * rollOff;
    }
}

/// <summary>
/// Atmospheric absorption per ISO 9613-1: α(f) from the oxygen and nitrogen
/// relaxation frequencies at the given temperature, humidity and pressure.
/// Implemented from the standard's equations; validity 0.05–2.5 kPa water
/// vapour partial pressure, 195–330 K.
/// </summary>
public static class AtmosphericAbsorption
{
    /// <summary>Attenuation coefficient, dB/m.</summary>
    public static double DbPerMetre(
        double frequency, double temperatureK = 293.15,
        double relativeHumidityPercent = 70.0, double pressurePa = 101_325.0)
    {
        const double t0 = 293.15;
        const double t01 = 273.16;
        const double pr = 101_325.0;
        var t = temperatureK;
        var p = pressurePa;

        // Molar concentration of water vapour, % (ISO 9613-1 Annex B).
        var psat = pr * Math.Pow(10.0, -6.8346 * Math.Pow(t01 / t, 1.261) + 4.6151);
        var h = relativeHumidityPercent * psat / p;

        var frO = p / pr * (24.0 + 4.04e4 * h * (0.02 + h) / (0.391 + h));
        var frN = p / pr * Math.Sqrt(t0 / t)
                  * (9.0 + 280.0 * h * Math.Exp(-4.170 * (Math.Pow(t / t0, -1.0 / 3.0) - 1.0)));

        var f2 = frequency * frequency;
        var alpha = 8.686 * f2 * (
            1.84e-11 * (pr / p) * Math.Sqrt(t / t0)
            + Math.Pow(t / t0, -2.5) * (
                0.01275 * Math.Exp(-2239.1 / t) / (frO + f2 / frO)
                + 0.1068 * Math.Exp(-3352.0 / t) / (frN + f2 / frN)));
        return alpha;
    }
}

/// <summary>
/// Free-field propagation with spherical spreading, ISO 9613-1 air
/// absorption, and a single ground reflection (image source with a real
/// reflection coefficient — configurable surface; the interference dips of
/// outdoor recordings fall out of the two-path sum, plan §3.5).
/// </summary>
public sealed record PropagationPath(
    double SourceHeight,
    double ReceiverHeight,
    double HorizontalDistance,
    double GroundReflectionCoefficient = 0.95,
    double TemperatureK = 293.15,
    double RelativeHumidityPercent = 70.0)
{
    public double DirectDistance => Math.Sqrt(
        HorizontalDistance * HorizontalDistance
        + Math.Pow(SourceHeight - ReceiverHeight, 2));

    public double ReflectedDistance => Math.Sqrt(
        HorizontalDistance * HorizontalDistance
        + Math.Pow(SourceHeight + ReceiverHeight, 2));

    /// <summary>
    /// Complex frequency response relative to the 1 m free-field pressure:
    /// direct + ground-image path, each with 1/r spreading, air absorption
    /// and its propagation phase.
    /// </summary>
    public Complex Response(double frequency, double soundSpeed = 343.2)
    {
        var k = 2.0 * Math.PI * frequency / soundSpeed;
        var alphaNepersPerMetre = AtmosphericAbsorption.DbPerMetre(
            frequency, TemperatureK, RelativeHumidityPercent) / 8.686;

        Complex Path(double r, double amplitude) =>
            amplitude / r * Math.Exp(-alphaNepersPerMetre * r)
            * Complex.Exp(-Complex.ImaginaryOne * k * r);

        return Path(DirectDistance, 1.0) + Path(ReflectedDistance, GroundReflectionCoefficient);
    }
}

/// <summary>
/// Listener presets (plan §3.5 table). Geometry only in Phase 9; the cabin
/// transfer function and Doppler paths arrive with auralisation (Phase 10).
/// </summary>
public sealed record ListenerPreset(
    string Name, double Distance, double AngleDeg, double Height, bool GroundReflection, string Purpose)
{
    /// <summary>FSAE static test: 0.5 m, 45° in the horizontal plane, free field (§3.7 rules basis).</summary>
    public static ListenerPreset FsaeStatic { get; } =
        new("FSAE static test", 0.5, 45.0, 0.0, false, "Rules compliance (ISO 5130 style)");

    public static ListenerPreset SaeJ1287 { get; } =
        new("SAE J1287-style", 0.5, 45.0, 0.0, false, "Stationary reference");

    public static ListenerPreset DriveBy { get; } =
        new("Drive-by", 7.5, 90.0, 1.2, true, "ISO 362-style pass");

    public static ListenerPreset ChaseCam { get; } =
        new("Chase cam", 3.0, 180.0, 1.0, true, "The video shot");

    public static IReadOnlyList<ListenerPreset> All { get; } =
        [FsaeStatic, SaeJ1287, DriveBy, ChaseCam];
}

/// <summary>
/// Broadband flow-noise generator (plan §3.4): spectrally shaped, seeded
/// deterministic noise gated by the instantaneous local velocity. Sources
/// scale as confined-dipole U⁶ (valve/step) or jet U⁸-like at the tailpipe,
/// with a Strouhal-scaled spectral peak (St ≈ 0.2). The absolute level is
/// EMPIRICAL and must be calibrated against a measured case — the API takes
/// an explicit calibration factor and the docs say so plainly.
/// </summary>
public static class FlowNoise
{
    /// <summary>
    /// Deterministic shaped-noise sample stream: white noise (seeded xorshift)
    /// filtered by a one-pole band shape centred on the Strouhal frequency of
    /// the current velocity, amplitude ∝ U^(exponent/2) in pressure.
    /// </summary>
    public static double[] Generate(
        ReadOnlySpan<double> velocity, double sampleRate, double characteristicDiameter,
        ulong seed, double calibrationFactor = 1.0, double velocityExponent = 6.0)
    {
        var output = new double[velocity.Length];
        var state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        double lp = 0.0, bp = 0.0;

        for (var i = 0; i < velocity.Length; i++)
        {
            var u = Math.Abs(velocity[i]);
            // Strouhal-scaled centre frequency, clamped into the band.
            var fPeak = Math.Clamp(0.2 * u / characteristicDiameter, 20.0, 0.4 * sampleRate);
            var g = Math.Clamp(2.0 * Math.PI * fPeak / sampleRate, 1e-4, 1.2);

            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var white = ((state * 0x2545F4914F6CDD1DUL >> 11) * (1.0 / (1UL << 53))) * 2.0 - 1.0;

            // State-variable band-pass around fPeak.
            lp += g * bp;
            var hp = white - lp - 1.2 * bp;
            bp += g * hp;

            // Normalise by √bandwidth so the U-scaling law is carried by the
            // amplitude factor alone, not by the moving filter width.
            var amplitude = calibrationFactor * Math.Pow(u, velocityExponent / 2.0);
            output[i] = amplitude * bp / Math.Sqrt(g);
        }

        return output;
    }
}
