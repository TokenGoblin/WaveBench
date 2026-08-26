using System.Numerics;
using WaveBench.Core.Components;

namespace WaveBench.Acoustics;

/// <summary>A two-port element of the transfer-matrix chain (plan §3.3).</summary>
public interface IAcousticElement
{
    FourPole Matrix(double frequency, AcousticMedium medium);
}

/// <summary>
/// Uniform duct with mean flow and visco-thermal damping (plan §3.3):
///   T = [[cos(k_c L), jZ·sin(k_c L)], [j/Z·sin(k_c L), cos(k_c L)]]·e^(−jM·k_c·L)
/// with k_c = k/(1 − M²), Z = ρc/S, and complex k = ω/c − jα.
/// α is the Kirchhoff wide-tube visco-thermal attenuation
///   α = (1/(r·c))·√(ν·ω/2)·(1 + (γ−1)/√Pr)
/// (classical result; see e.g. Pierce, "Acoustics", §10-5). The optional
/// turbulent-flow addition is a documented engineering term.
/// </summary>
public sealed record UniformDuctElement(double Length, double Area, double MachNumber = 0.0, bool Damped = true)
    : IAcousticElement
{
    public FourPole Matrix(double frequency, AcousticMedium medium)
    {
        var omega = 2.0 * Math.PI * frequency;
        var c = medium.SoundSpeed;
        var k = omega / c;

        var kComplex = new Complex(k, 0.0);
        if (Damped)
        {
            var radius = Math.Sqrt(Area / Math.PI);
            var nu = PipeFlowPhysics.SutherlandViscosity(medium.Temperature) / medium.Density;
            var alpha = 1.0 / (radius * c) * Math.Sqrt(nu * omega / 2.0)
                        * (1.0 + (medium.Gamma - 1.0) / Math.Sqrt(medium.Prandtl));
            kComplex = new Complex(k, -alpha);
        }

        var m = MachNumber;
        var kc = kComplex / (1.0 - m * m);
        var arg = kc * Length;
        var cos = Complex.Cos(arg);
        var sin = Complex.Sin(arg);
        var z = new Complex(medium.Density * c / Area, 0.0);
        var convect = Complex.Exp(new Complex(0.0, -1.0) * m * kc * Length);

        return new FourPole(cos * convect, Complex.ImaginaryOne * z * sin * convect,
            Complex.ImaginaryOne / z * sin * convect, cos * convect);
    }
}

/// <summary>
/// Abrupt area discontinuity: in the [p, U] convention the ideal junction is
/// continuity of both — the area effect lives in the neighbouring duct
/// impedances. What remains is the inertial end correction (plan §3.3), a
/// series inertance L_a = ρ·δ/S_small with δ ≈ 0.6·r_small·(1 − S_small/S_large)
/// (piston-in-baffle correction, discounted for finite area ratio).
/// </summary>
public sealed record AreaDiscontinuityElement(double SmallArea, double LargeArea, bool IncludeEndCorrection = true)
    : IAcousticElement
{
    public FourPole Matrix(double frequency, AcousticMedium medium)
    {
        if (!IncludeEndCorrection)
        {
            return FourPole.Identity;
        }

        var rSmall = Math.Sqrt(SmallArea / Math.PI);
        var delta = 0.6 * rSmall * (1.0 - SmallArea / LargeArea);
        var inertance = medium.Density * delta / SmallArea;
        var omega = 2.0 * Math.PI * frequency;
        return FourPole.SeriesImpedance(new Complex(0.0, omega * inertance));
    }
}

/// <summary>
/// Quarter-wave side branch (plan §3.3): shunt impedance of a closed stub,
/// Z = −j·(ρc/S)·cot(kL_eff), with the open-end inertial correction folded
/// into L_eff and Kirchhoff damping in k.
/// </summary>
public sealed record QuarterWaveStubElement(double Length, double Area, double EndCorrectionFactor = 0.6133)
    : IAcousticElement
{
    public double EffectiveLength => Length + EndCorrectionFactor * Math.Sqrt(Area / Math.PI);

    public FourPole Matrix(double frequency, AcousticMedium medium)
    {
        var omega = 2.0 * Math.PI * frequency;
        var c = medium.SoundSpeed;
        var radius = Math.Sqrt(Area / Math.PI);
        var nu = PipeFlowPhysics.SutherlandViscosity(medium.Temperature) / medium.Density;
        var alpha = 1.0 / (radius * c) * Math.Sqrt(nu * omega / 2.0)
                    * (1.0 + (medium.Gamma - 1.0) / Math.Sqrt(medium.Prandtl));
        var k = new Complex(omega / c, -alpha);

        var z0 = medium.Density * c / Area;
        var tan = Complex.Tan(k * EffectiveLength);
        var z = -Complex.ImaginaryOne * z0 / tan;
        return FourPole.ShuntImpedance(z);
    }
}

/// <summary>
/// Helmholtz resonator side branch (plan §3.3):
/// Z = R + j(ω·M_a − 1/(ω·C_a)), M_a = ρ·L_eff/S_neck, C_a = V/(ρc²),
/// resonant at f = (c/2π)·√(S/(V·L_eff)). R defaults to the neck's
/// visco-thermal resistance (small).
/// </summary>
public sealed record HelmholtzResonatorElement(
    double NeckLength, double NeckArea, double CavityVolume, double EndCorrectionFactor = 0.85)
    : IAcousticElement
{
    /// <summary>Both neck ends corrected: L_eff = L + factor·2·r_neck (≈1.7·r total default).</summary>
    public double EffectiveNeckLength => NeckLength + 2.0 * EndCorrectionFactor * Math.Sqrt(NeckArea / Math.PI);

    public double ResonantFrequency(AcousticMedium medium) =>
        medium.SoundSpeed / (2.0 * Math.PI) * Math.Sqrt(NeckArea / (CavityVolume * EffectiveNeckLength));

    public FourPole Matrix(double frequency, AcousticMedium medium)
    {
        var omega = 2.0 * Math.PI * frequency;
        var inertance = medium.Density * EffectiveNeckLength / NeckArea;
        var compliance = CavityVolume / (medium.Density * medium.SoundSpeed * medium.SoundSpeed);

        var radius = Math.Sqrt(NeckArea / Math.PI);
        var nu = PipeFlowPhysics.SutherlandViscosity(medium.Temperature) / medium.Density;
        var resistance = medium.Density * EffectiveNeckLength / NeckArea
                         * Math.Sqrt(2.0 * nu * omega) / radius;

        var z = new Complex(resistance, omega * inertance - 1.0 / (omega * compliance));
        return FourPole.ShuntImpedance(z);
    }
}

/// <summary>
/// Levine–Schwinger open-end radiation impedance (plan §3.5), low-ka form:
///   unflanged: Z = (ρc/S)·[(ka)²/4 + j·0.6133·ka]
///   flanged:   Z = (ρc/S)·[(ka)²/2 + j·0.8216·ka]
/// Valid to ka ≈ 1.5; above that directivity takes over (§3.5, Phase 9).
/// </summary>
public static class RadiationImpedance
{
    public static Complex Unflanged(double frequency, double area, AcousticMedium medium) =>
        Terminate(frequency, area, medium, resistanceFactor: 0.25, endCorrection: 0.6133);

    public static Complex Flanged(double frequency, double area, AcousticMedium medium) =>
        Terminate(frequency, area, medium, resistanceFactor: 0.5, endCorrection: 0.8216);

    private static Complex Terminate(
        double frequency, double area, AcousticMedium medium, double resistanceFactor, double endCorrection)
    {
        var a = Math.Sqrt(area / Math.PI);
        var ka = 2.0 * Math.PI * frequency / medium.SoundSpeed * a;
        var z0 = medium.Density * medium.SoundSpeed / area;
        return z0 * new Complex(resistanceFactor * ka * ka, endCorrection * ka);
    }
}
