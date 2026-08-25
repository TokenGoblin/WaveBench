namespace WaveBench.Core.Components;

/// <summary>
/// Wall friction and heat-transfer correlations for quasi-1D pipe flow
/// (plan §2.1).
/// </summary>
public static class PipeFlowPhysics
{
    /// <summary>
    /// Dynamic viscosity of air by Sutherland's law,
    /// μ = μ_ref·(T/T_ref)^{3/2}·(T_ref + S)/(T + S) with
    /// μ_ref = 1.716e-5 Pa·s at 273.15 K, S = 110.4 K.
    /// Validity ≈ 170–1900 K for air; combustion products differ by a few
    /// percent, acceptable inside the friction correlation's own accuracy.
    /// </summary>
    public static double SutherlandViscosity(double t)
    {
        const double muRef = 1.716e-5;
        const double tRef = 273.15;
        const double s = 110.4;
        return muRef * Math.Pow(t / tRef, 1.5) * (tRef + s) / (t + s);
    }

    /// <summary>
    /// Darcy friction factor. Laminar 64/Re below Re 2300; Haaland's explicit
    /// approximation to Colebrook–White above Re 4000 (within ~2% of
    /// Colebrook):
    ///   1/√f = −1.8·log10[(ε/3.7D)^1.11 + 6.9/Re]
    /// (Haaland, J. Fluids Eng. 105, 1983). Linear blend across 2300–4000.
    /// </summary>
    public static double DarcyFrictionFactor(double reynolds, double relativeRoughness)
    {
        const double reLaminar = 2300.0;
        const double reTurbulent = 4000.0;

        if (reynolds <= 0)
        {
            return 0.0;
        }

        if (reynolds < reLaminar)
        {
            return 64.0 / reynolds;
        }

        if (reynolds > reTurbulent)
        {
            return Haaland(reynolds, relativeRoughness);
        }

        var w = (reynolds - reLaminar) / (reTurbulent - reLaminar);
        return (1.0 - w) * (64.0 / reynolds) + w * Haaland(reynolds, relativeRoughness);
    }

    private static double Haaland(double re, double relRough)
    {
        var inv = -1.8 * Math.Log10(Math.Pow(relRough / 3.7, 1.11) + 6.9 / re);
        return 1.0 / (inv * inv);
    }

    /// <summary>
    /// Convective wall heat-transfer coefficient by the Colburn / Reynolds
    /// analogy, h = (f/2)·ρ|u|·c_p·Pr^(−2/3), with f the FANNING factor
    /// (= Darcy/4). An enhancement factor accounts for unsteady/pulsating
    /// flow — default 1.3, empirical, user-adjustable, and the UI must say so
    /// (plan §2.1: a known weak point of all 1D codes).
    /// </summary>
    public static double ColburnHeatTransferCoefficient(
        double darcyFrictionFactor, double rho, double speed, double cp,
        double prandtl = 0.71, double pulsatingEnhancement = 1.3)
    {
        var fanning = darcyFrictionFactor / 4.0;
        return pulsatingEnhancement * fanning / 2.0 * rho * Math.Abs(speed) * cp
               * Math.Pow(prandtl, -2.0 / 3.0);
    }
}
