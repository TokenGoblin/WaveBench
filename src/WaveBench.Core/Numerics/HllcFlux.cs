namespace WaveBench.Core.Numerics;

/// <summary>
/// HLLC approximate Riemann solver for the 1D Euler equations.
/// Toro, "Riemann Solvers and Numerical Methods for Fluid Dynamics",
/// 3rd ed., §10.4–10.6, with the PVRS-based adaptive pressure estimate for
/// the wave speeds (§10.5.2). Restores the contact wave that plain HLL
/// smears — which is what makes it fit for tracking the fresh-charge /
/// residual interface later.
/// </summary>
/// <summary>One side of an interface Riemann problem, EOS-agnostic.</summary>
public readonly record struct HllcSide(
    double Rho, double U, double P, double TotalEnergy, double SoundSpeed, double Gamma);

public static class HllcFlux
{
    /// <summary>Perfect-gas convenience overload.</summary>
    public static (double FRho, double FMom, double FEner) Compute(
        in PrimitiveState left, in PrimitiveState right, in PerfectGas gas)
    {
        return Compute(Side(left, gas), Side(right, gas));

        static HllcSide Side(in PrimitiveState w, in PerfectGas gas) => new(
            w.Rho, w.U, w.P, gas.TotalEnergy(w.Rho, w.U, w.P), gas.SoundSpeed(w.Rho, w.P), gas.Gamma);
    }

    public static (double FRho, double FMom, double FEner) Compute(in HllcSide left, in HllcSide right)
    {
        // PVRS pressure estimate, floored at zero (Toro eq. 10.61).
        var pPvrs = 0.5 * (left.P + right.P)
                    - 0.125 * (right.U - left.U) * (left.Rho + right.Rho)
                            * (left.SoundSpeed + right.SoundSpeed);
        var pStar = Math.Max(0.0, pPvrs);

        // Wave speed estimates with shock correction factors (Toro eq. 10.59-10.60),
        // each side using its own frozen γ (real-gas practice).
        var qL = pStar <= left.P
            ? 1.0
            : Math.Sqrt(1.0 + (left.Gamma + 1.0) / (2.0 * left.Gamma) * (pStar / left.P - 1.0));
        var qR = pStar <= right.P
            ? 1.0
            : Math.Sqrt(1.0 + (right.Gamma + 1.0) / (2.0 * right.Gamma) * (pStar / right.P - 1.0));

        var sL = left.U - left.SoundSpeed * qL;
        var sR = right.U + right.SoundSpeed * qR;

        // Contact wave speed (Toro eq. 10.37).
        var sStar = (right.P - left.P
                     + left.Rho * left.U * (sL - left.U)
                     - right.Rho * right.U * (sR - right.U))
                    / (left.Rho * (sL - left.U) - right.Rho * (sR - right.U));

        if (sL >= 0.0)
        {
            return PhysicalFlux(left);
        }

        if (sR <= 0.0)
        {
            return PhysicalFlux(right);
        }

        return sStar >= 0.0
            ? StarFlux(left, sL, sStar)
            : StarFlux(right, sR, sStar);
    }

    private static (double, double, double) PhysicalFlux(in HllcSide w) =>
        (w.Rho * w.U, w.Rho * w.U * w.U + w.P, w.U * (w.TotalEnergy + w.P));

    private static (double, double, double) StarFlux(in HllcSide w, double sK, double sStar)
    {
        var e = w.TotalEnergy;
        var (fRho, fMom, fEner) = PhysicalFlux(w);

        // Star-region conserved state (Toro eq. 10.39).
        var factor = w.Rho * (sK - w.U) / (sK - sStar);
        var uStarRho = factor;
        var uStarMom = factor * sStar;
        var uStarEner = factor * (e / w.Rho + (sStar - w.U) * (sStar + w.P / (w.Rho * (sK - w.U))));

        var uRho = w.Rho;
        var uMom = w.Rho * w.U;

        return (
            fRho + sK * (uStarRho - uRho),
            fMom + sK * (uStarMom - uMom),
            fEner + sK * (uStarEner - e));
    }
}
