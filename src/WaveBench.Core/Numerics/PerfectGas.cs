namespace WaveBench.Core.Numerics;

/// <summary>
/// Calorically perfect gas (constant γ, R) — the EOS for the Phase 2 solver
/// core and its Riemann verification problems, which are defined for perfect
/// gas. The species-resolved caloric EOS (plan §2.3) couples in with species
/// transport in Phase 3.
/// </summary>
public readonly record struct PerfectGas(double Gamma, double SpecificGasConstant)
{
    /// <summary>Diatomic air, γ = 1.4 (the standard test-problem gas).</summary>
    public static PerfectGas Air => new(1.4, 287.05);

    public double Pressure(double rho, double momentum, double totalEnergy)
    {
        var kinetic = 0.5 * momentum * momentum / rho;
        return (Gamma - 1.0) * (totalEnergy - kinetic);
    }

    public double TotalEnergy(double rho, double u, double p) =>
        p / (Gamma - 1.0) + 0.5 * rho * u * u;

    public double SoundSpeed(double rho, double p) => Math.Sqrt(Gamma * p / rho);
}

/// <summary>Primitive state (ρ, u, p) of a cell or face.</summary>
public readonly record struct PrimitiveState(double Rho, double U, double P);

public static class EulerMath
{
    /// <summary>Physical flux F(W) of the 1D Euler equations.</summary>
    public static (double FRho, double FMom, double FEner) Flux(in PrimitiveState w, in PerfectGas gas)
    {
        var e = gas.TotalEnergy(w.Rho, w.U, w.P);
        return (w.Rho * w.U, w.Rho * w.U * w.U + w.P, w.U * (e + w.P));
    }
}
