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
    /// the frequency domain (plan §3.1 — never a naive finite difference).
    ///
    /// The jω factor amplifies high frequencies, including the unresolved
    /// numerical content above the scheme's measured −3 dB bandwidth
    /// (docs/numerics.md §5), so the result is rolled off there. The roll-off
    /// is a MODELLING CHOICE, not a physical correlation: a Butterworth-shape
    /// magnitude 1/√(1+(f/f_b)⁴), i.e. −12 dB/octave above f_b, chosen so the
    /// product jω·H falls at −6 dB/octave and unresolved content cannot grow.
    /// Its only job is to stop the code presenting unresolved audio as
    /// physical (plan §5.5); the §5.6 hybrid replaces this band with the TMM.
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
    /// <summary>dB per neper: 20/ln(10). The single conversion constant.</summary>
    public const double DecibelsPerNeper = 8.685889638065035;

    /// <summary>Attenuation coefficient in nepers/m — the primitive form (α in e^(−αr)).</summary>
    public static double NepersPerMetre(
        double frequency, double temperatureK = 293.15,
        double relativeHumidityPercent = 70.0, double pressurePa = 101_325.0) =>
        DbPerMetre(frequency, temperatureK, relativeHumidityPercent, pressurePa) / DecibelsPerNeper;

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
        var alpha = DecibelsPerNeper * f2 * (
            1.84e-11 * (pr / p) * Math.Sqrt(t / t0)
            + Math.Pow(t / t0, -2.5) * (
                0.01275 * Math.Exp(-2239.1 / t) / (frO + f2 / frO)
                + 0.1068 * Math.Exp(-3352.0 / t) / (frN + f2 / frN)));
        return alpha;
    }
}

/// <summary>
/// Plane-wave pressure reflection coefficients for common ground surfaces
/// (plan §3.5, "configurable surface impedance"). These are broadband
/// engineering values for the normal-incidence-ish geometry of vehicle
/// noise measurement: hard surfaces reflect nearly perfectly, porous ones
/// absorb. Real ground impedance is frequency-dependent (Delany–Bazley
/// flow-resistivity model), which is the planned refinement; until then a
/// real, frequency-independent coefficient is used and labelled as such.
/// Validity: broadband approximation, grazing angles below ~30°.
/// </summary>
public static class GroundSurface
{
    /// <summary>Sealed asphalt / concrete — the FSAE and ISO 362 test-pad case.</summary>
    public const double Asphalt = 0.99;

    public const double CompactedGravel = 0.85;

    /// <summary>Grass — strongly absorbing above a few hundred Hz in reality.</summary>
    public const double Grass = 0.6;
}

/// <summary>
/// Free-field propagation with spherical spreading, ISO 9613-1 air
/// absorption, and a single ground reflection (image source with a real
/// reflection coefficient — see <see cref="GroundSurface"/>; the
/// interference dips of outdoor recordings fall out of the two-path sum,
/// plan §3.5).
///
/// The sound speed is DERIVED from <see cref="TemperatureK"/>, never a
/// literal: the same air state must set both the absorption and the
/// interference comb, or a hot-day drive-by gets its dip in the wrong place
/// (plan §2.2 — local a, always).
/// </summary>
public sealed record PropagationPath(
    double SourceHeight,
    double ReceiverHeight,
    double HorizontalDistance,
    double GroundReflectionCoefficient = GroundSurface.Asphalt,
    double TemperatureK = 293.15,
    double RelativeHumidityPercent = 70.0)
{
    /// <summary>a = √(γRT) for dry air at the path temperature, m/s.</summary>
    public double SoundSpeed => Math.Sqrt(1.4 * 287.05 * TemperatureK);

    public double DirectDistance => Math.Sqrt(
        HorizontalDistance * HorizontalDistance
        + Math.Pow(SourceHeight - ReceiverHeight, 2));

    public double ReflectedDistance => Math.Sqrt(
        HorizontalDistance * HorizontalDistance
        + Math.Pow(SourceHeight + ReceiverHeight, 2));

    /// <summary>
    /// Complex frequency response relative to the 1 m free-field pressure:
    /// direct + ground-image path, each with 1/r spreading, air absorption
    /// and its propagation phase. <paramref name="soundSpeedOverride"/>
    /// exists for callers that already know the local a (e.g. from the solved
    /// gas state); by default the path's own temperature governs.
    /// </summary>
    public Complex Response(double frequency, double? soundSpeedOverride = null)
    {
        var c = soundSpeedOverride ?? SoundSpeed;
        var k = 2.0 * Math.PI * frequency / c;
        var alpha = AtmosphericAbsorption.NepersPerMetre(
            frequency, TemperatureK, RelativeHumidityPercent);

        Complex Path(double r, double amplitude) =>
            amplitude / r * Math.Exp(-alpha * r)
            * Complex.Exp(-Complex.ImaginaryOne * k * r);

        return Path(DirectDistance, 1.0) + Path(ReflectedDistance, GroundReflectionCoefficient);
    }
}

/// <summary>
/// Listener presets (plan §3.5 table). Geometry only in Phase 9; the cabin
/// transfer function and Doppler paths arrive with auralisation (Phase 10).
///
/// Geometry convention, stated once so callers cannot disagree:
/// <c>SlantDistanceM</c> is the straight-line source→receiver distance (the
/// quantity the rules specify), <c>AzimuthDeg</c> the horizontal angle from
/// the outlet axis, <c>ReceiverHeightM</c> the microphone height above
/// ground. <see cref="ToPath"/> is the ONE place that converts a preset into
/// a <see cref="PropagationPath"/>.
/// </summary>
public sealed record ListenerPreset(
    string Name,
    double SlantDistanceM,
    double AzimuthDeg,
    double ReceiverHeightM,
    bool GroundReflection,
    string Purpose)
{
    /// <summary>Typed accessors for the model/UI boundary (CLAUDE.md units convention).</summary>
    public Model.Units.Length SlantDistance => Model.Units.Length.FromMetres(SlantDistanceM);

    public Model.Units.Angle Azimuth => Model.Units.Angle.FromDegrees(AzimuthDeg);

    public Model.Units.Length ReceiverHeight => Model.Units.Length.FromMetres(ReceiverHeightM);

    /// <summary>
    /// The propagation path for this preset given the outlet height. Horizontal
    /// separation follows from the slant distance and the height difference;
    /// presets flagged free-field get a zero ground reflection coefficient.
    /// </summary>
    public PropagationPath ToPath(
        double sourceHeightM,
        double groundReflectionCoefficient = GroundSurface.Asphalt,
        double temperatureK = 293.15,
        double relativeHumidityPercent = 70.0)
    {
        var dh = ReceiverHeightM - sourceHeightM;
        var horizontal = Math.Sqrt(Math.Max(0.0, SlantDistanceM * SlantDistanceM - dh * dh));
        return new PropagationPath(
            sourceHeightM, ReceiverHeightM, horizontal,
            GroundReflection ? groundReflectionCoefficient : 0.0,
            temperatureK, relativeHumidityPercent);
    }

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
/// deterministic noise gated by the instantaneous local velocity.
///
/// Scaling laws and their sources:
/// - <b>U⁶ acoustic power (default)</b> — compact dipole radiation from
///   flow over a surface or through a constriction: Curle, Proc. R. Soc. A
///   231 (1955) 505–514. Nelson &amp; Morfey, J. Sound Vib. 79 (1981) 263–289
///   confirm the U⁶ law for duct flow noise at area discontinuities below
///   the cut-on frequency, which is precisely the regime here.
///   Pressure amplitude therefore scales as U³ (power ∝ p²).
/// - <b>U⁸ (quadrupole)</b> — free-jet mixing noise at the tailpipe exit:
///   Lighthill, Proc. R. Soc. A 211 (1952) 564–587. Pass
///   <c>velocityExponent = 8</c> for the exit-jet source.
/// - <b>Spectral peak at St ≈ 0.2</b> (f = 0.2·U/D) — the standard jet and
///   bluff-body Strouhal number; see Lighthill (1952) for jets and the
///   duct-noise spectra of Nelson &amp; Morfey (1981).
///
/// Validity: subsonic, compact sources below the duct cut-on frequency;
/// the transition near choke and any supersonic screech are NOT modelled.
/// <b>The absolute level is empirical</b> — the scaling laws fix the shape
/// and the exponents, not the constant. <c>calibrationFactor</c> must be set
/// from a measured case, and the UI must present predicted broadband level
/// as calibrated, not predicted (plan §3.4).
/// </summary>
public static class FlowNoise
{
    /// <summary>
    /// Deterministic shaped-noise sample stream: white noise (seeded xorshift)
    /// through a state-variable band-pass centred on the Strouhal frequency of
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
