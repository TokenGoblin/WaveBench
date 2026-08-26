using System.Numerics;

namespace WaveBench.Acoustics;

/// <summary>How the downstream end of the chain terminates.</summary>
public enum TerminationKind
{
    /// <summary>Non-reflecting: Z = ρc/S of the exit element.</summary>
    Anechoic,

    /// <summary>Levine–Schwinger unflanged open end.</summary>
    UnflangedOpen,

    /// <summary>Levine–Schwinger flanged open end.</summary>
    FlangedOpen,

    /// <summary>Closed rigid end (U = 0).</summary>
    Rigid,

    /// <summary>Ideal pressure-release end (p = 0) — the textbook open end.</summary>
    PressureRelease,
}

/// <summary>
/// A series chain of two-port elements (side branches enter as shunt
/// elements) with the standard TMM outputs (plan §3.3): transmission loss,
/// insertion loss, input impedance and the pressure transfer function.
/// Interactive-fast by design: the Phase 8 gate demands a 20-element network
/// across 1–10 kHz in under 10 ms.
/// </summary>
public sealed class AcousticNetwork(AcousticMedium medium, double inletArea, double outletArea)
{
    public List<IAcousticElement> Elements { get; } = [];

    public AcousticMedium Medium { get; } = medium;

    public double InletArea { get; } = inletArea;

    public double OutletArea { get; } = outletArea;

    public FourPole SystemMatrix(double frequency)
    {
        var t = FourPole.Identity;
        foreach (var element in Elements)
        {
            t *= element.Matrix(frequency, Medium);
        }

        return t;
    }

    /// <summary>
    /// Transmission loss, dB — anechoic source and termination (the classic
    /// four-pole formula):
    ///   TL = 20·log₁₀( |A + B/Z₂ + C·Z₁ + D·Z₁/Z₂| / 2 ) + 10·log₁₀(S₂/S₁)·? —
    /// with Z₁ = ρc/S_in, Z₂ = ρc/S_out the port characteristic impedances.
    /// For equal port areas the familiar |A + B/Z + C·Z + D|/2 results.
    /// </summary>
    public double TransmissionLoss(double frequency)
    {
        var t = SystemMatrix(frequency);
        var z1 = Medium.Density * Medium.SoundSpeed / InletArea;
        var z2 = Medium.Density * Medium.SoundSpeed / OutletArea;
        var sum = t.A + t.B / z2 + t.C * z1 + t.D * z1 / z2;
        var factor = 0.5 * Math.Sqrt(z2 / z1);
        return 20.0 * Math.Log10(factor * sum.Magnitude);
    }

    public Complex TerminationImpedance(double frequency, TerminationKind kind) => kind switch
    {
        TerminationKind.Anechoic => new Complex(Medium.Density * Medium.SoundSpeed / OutletArea, 0.0),
        TerminationKind.UnflangedOpen => RadiationImpedance.Unflanged(frequency, OutletArea, Medium),
        TerminationKind.FlangedOpen => RadiationImpedance.Flanged(frequency, OutletArea, Medium),
        TerminationKind.Rigid => new Complex(1e30, 0.0),
        TerminationKind.PressureRelease => new Complex(1e-30, 0.0),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Input impedance seen from the upstream port: Z_in = (A·Z_t + B)/(C·Z_t + D).</summary>
    public Complex InputImpedance(double frequency, TerminationKind termination)
    {
        var t = SystemMatrix(frequency);
        var zt = TerminationImpedance(frequency, termination);
        return (t.A * zt + t.B) / (t.C * zt + t.D);
    }

    /// <summary>Pressure transfer p_out/p_in for the given termination.</summary>
    public Complex PressureTransfer(double frequency, TerminationKind termination)
    {
        var t = SystemMatrix(frequency);
        var zt = TerminationImpedance(frequency, termination);
        // p_in = (A + B/Zt)·p_out.
        return Complex.One / (t.A + t.B / zt);
    }

    /// <summary>
    /// Insertion loss vs a reference network (typically a straight pipe of
    /// the same overall length): IL = 20·log₁₀ |p_ref/p| at the termination.
    /// </summary>
    public double InsertionLoss(double frequency, AcousticNetwork reference, TerminationKind termination)
    {
        var own = PressureTransfer(frequency, termination).Magnitude;
        var reference_ = reference.PressureTransfer(frequency, termination).Magnitude;
        return 20.0 * Math.Log10(reference_ / Math.Max(own, 1e-300));
    }

    /// <summary>Convenience sweep of TL over a frequency grid.</summary>
    public double[] TransmissionLossSweep(ReadOnlySpan<double> frequencies)
    {
        var result = new double[frequencies.Length];
        for (var i = 0; i < frequencies.Length; i++)
        {
            result[i] = TransmissionLoss(frequencies[i]);
        }

        return result;
    }
}
