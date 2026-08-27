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

        // x^1.5 as x·√x. Called once per cell per timestep in the duct source
        // terms, where Math.Pow is one of the three most expensive things in
        // the loop; Math.Sqrt is a single instruction. The two can differ in
        // the last ulp, which is far inside the correlation's own few-percent
        // accuracy, and both are deterministic.
        var r = t / tRef;
        return muRef * r * Math.Sqrt(r) * (tRef + s) / (t + s);
    }

    /// <summary>
    /// The geometry-only part of Haaland's bracket, (ε/3.7D)^1.11.
    ///
    /// Depends on nothing that changes with time, so a solver marching a mesh
    /// should evaluate it once per cell and keep it — see
    /// <see cref="DarcyFrictionFactorPrecomputed"/>. Hoisting it out removes a
    /// <c>Math.Pow</c> from the innermost loop.
    /// </summary>
    public static double HaalandRoughnessTerm(double relativeRoughness) =>
        Math.Pow(relativeRoughness / 3.7, 1.11);

    /// <summary>
    /// Pr^(−2/3), the constant factor in the Colburn analogy. Hoisted for the
    /// same reason.
    /// </summary>
    public static double PrandtlFactor(double prandtl) => Math.Pow(prandtl, -2.0 / 3.0);

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

        var roughnessTerm = HaalandRoughnessTerm(relativeRoughness);
        if (reynolds > reTurbulent)
        {
            return Haaland(reynolds, roughnessTerm);
        }

        var w = (reynolds - reLaminar) / (reTurbulent - reLaminar);
        return (1.0 - w) * (64.0 / reynolds) + w * Haaland(reynolds, roughnessTerm);
    }

    /// <summary>
    /// <see cref="DarcyFrictionFactor"/> with the roughness term already
    /// evaluated by <see cref="HaalandRoughnessTerm"/>. Identical arithmetic,
    /// one <c>Math.Pow</c> fewer per call.
    /// </summary>
    public static double DarcyFrictionFactorPrecomputed(double reynolds, double roughnessTerm)
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
            return Haaland(reynolds, roughnessTerm);
        }

        var w = (reynolds - reLaminar) / (reTurbulent - reLaminar);
        return (1.0 - w) * (64.0 / reynolds) + w * Haaland(reynolds, roughnessTerm);
    }

    private static double Haaland(double re, double roughnessTerm)
    {
        var inv = -1.8 * Math.Log10(roughnessTerm + 6.9 / re);
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
               * PrandtlFactor(prandtl);
    }

    /// <summary>
    /// <see cref="ColburnHeatTransferCoefficient"/> with Pr^(−2/3) already
    /// evaluated by <see cref="PrandtlFactor"/>.
    /// </summary>
    public static double ColburnPrecomputed(
        double darcyFrictionFactor, double rho, double speed, double cp,
        double prandtlFactor, double pulsatingEnhancement) =>
        pulsatingEnhancement * (darcyFrictionFactor / 4.0) / 2.0 * rho * Math.Abs(speed) * cp * prandtlFactor;
}
