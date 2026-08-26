using System.Numerics;

namespace WaveBench.Acoustics;

/// <summary>
/// 2×2 complex transfer (four-pole) matrix in the [p, U] convention
/// (acoustic pressure, volume velocity):
///   [p₁; U₁] = T · [p₂; U₂]   (1 = upstream face, 2 = downstream face)
/// The volume-velocity convention makes area changes fall out of the duct
/// matrices themselves (characteristic impedance ρc/S per element).
/// </summary>
public readonly struct FourPole(Complex a, Complex b, Complex c, Complex d)
{
    public Complex A { get; } = a;

    public Complex B { get; } = b;

    public Complex C { get; } = c;

    public Complex D { get; } = d;

    public static FourPole Identity => new(Complex.One, Complex.Zero, Complex.Zero, Complex.One);

    public static FourPole operator *(in FourPole left, in FourPole right) => new(
        left.A * right.A + left.B * right.C,
        left.A * right.B + left.B * right.D,
        left.C * right.A + left.D * right.C,
        left.C * right.B + left.D * right.D);

    /// <summary>Series impedance Z: p drops by Z·U, U continuous.</summary>
    public static FourPole SeriesImpedance(Complex z) => new(Complex.One, z, Complex.Zero, Complex.One);

    /// <summary>Shunt (side-branch) impedance Z: U splits into the branch, p continuous.</summary>
    public static FourPole ShuntImpedance(Complex z) => new(Complex.One, Complex.Zero, Complex.One / z, Complex.One);
}

/// <summary>
/// Acoustic medium state for element evaluation: from the same gas state the
/// nonlinear solver uses (plan §3.0 — sound and tuning are the same physics).
/// </summary>
public sealed record AcousticMedium
{
    /// <summary>Speed of sound, m/s (local, from T and composition — never 343 by fiat).</summary>
    public required double SoundSpeed { get; init; }

    /// <summary>Density, kg/m³.</summary>
    public required double Density { get; init; }

    /// <summary>Temperature, K (for visco-thermal damping properties).</summary>
    public double Temperature { get; init; } = 293.15;

    /// <summary>Ratio of specific heats (damping term).</summary>
    public double Gamma { get; init; } = 1.4;

    /// <summary>Prandtl number (damping term).</summary>
    public double Prandtl { get; init; } = 0.71;

    public static AcousticMedium Air20C { get; } = new()
    {
        SoundSpeed = 343.2,
        Density = 1.204,
        Temperature = 293.15,
    };
}
