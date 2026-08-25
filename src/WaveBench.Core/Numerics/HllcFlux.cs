namespace WaveBench.Core.Numerics;

/// <summary>
/// HLLC approximate Riemann solver for the 1D Euler equations.
/// Toro, "Riemann Solvers and Numerical Methods for Fluid Dynamics",
/// 3rd ed., §10.4–10.6, with the PVRS-based adaptive pressure estimate for
/// the wave speeds (§10.5.2). Restores the contact wave that plain HLL
/// smears — which is what makes it fit for tracking the fresh-charge /
/// residual interface later.
/// </summary>
public static class HllcFlux
{
    public static (double FRho, double FMom, double FEner) Compute(
        in PrimitiveState left, in PrimitiveState right, in PerfectGas gas)
    {
        var g = gas.Gamma;
        var aL = gas.SoundSpeed(left.Rho, left.P);
        var aR = gas.SoundSpeed(right.Rho, right.P);

        // PVRS pressure estimate, floored at zero (Toro eq. 10.61).
        var pPvrs = 0.5 * (left.P + right.P)
                    - 0.125 * (right.U - left.U) * (left.Rho + right.Rho) * (aL + aR);
        var pStar = Math.Max(0.0, pPvrs);

        // Wave speed estimates with shock correction factors (Toro eq. 10.59-10.60).
        var qL = pStar <= left.P
            ? 1.0
            : Math.Sqrt(1.0 + (g + 1.0) / (2.0 * g) * (pStar / left.P - 1.0));
        var qR = pStar <= right.P
            ? 1.0
            : Math.Sqrt(1.0 + (g + 1.0) / (2.0 * g) * (pStar / right.P - 1.0));

        var sL = left.U - aL * qL;
        var sR = right.U + aR * qR;

        // Contact wave speed (Toro eq. 10.37).
        var sStar = (right.P - left.P
                     + left.Rho * left.U * (sL - left.U)
                     - right.Rho * right.U * (sR - right.U))
                    / (left.Rho * (sL - left.U) - right.Rho * (sR - right.U));

        if (sL >= 0.0)
        {
            return EulerMath.Flux(left, gas);
        }

        if (sR <= 0.0)
        {
            return EulerMath.Flux(right, gas);
        }

        return sStar >= 0.0
            ? StarFlux(left, sL, sStar, gas)
            : StarFlux(right, sR, sStar, gas);
    }

    private static (double, double, double) StarFlux(
        in PrimitiveState w, double sK, double sStar, in PerfectGas gas)
    {
        var e = gas.TotalEnergy(w.Rho, w.U, w.P);
        var (fRho, fMom, fEner) = EulerMath.Flux(w, gas);

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
