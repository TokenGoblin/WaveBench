namespace WaveBench.Core.EngineModel;

public enum HeatTransferCorrelation
{
    /// <summary>Woschni (SAE 670931) — the default (plan §2.5).</summary>
    Woschni,

    /// <summary>Hohenberg (SAE 790825).</summary>
    Hohenberg,

    /// <summary>Annand (Proc. IMechE 1963), convective part.</summary>
    Annand,
}

/// <summary>
/// In-cylinder gas-to-wall heat-transfer coefficient correlations
/// (plan §2.5). SI inputs; h in W/(m²·K).
///
/// - Woschni (SAE 670931): h = 3.26·B^−0.2·p^0.8·T^−0.55·w^0.8 with p in kPa,
///   and w = C1·c_m (+ the combustion pressure-rise term C2·(Vd·T_r/(p_r·V_r))
///   ·(p − p_mot), supplied by the caller as an extra gas velocity).
///   Validity: SI/CI engines near the conditions of Woschni's diesel data.
/// - Hohenberg (SAE 790825): h = 130·V^−0.06·p^0.8·T^−0.4·(c_m + 1.4)^0.8,
///   p in bar — his re-fit against instantaneous measurements.
/// - Annand (1963): Nu = a·Re^0.7, h = a·(k/B)·Re^0.7, a ≈ 0.35–0.8
///   (default 0.49); radiation term omitted for motored/SI use here and
///   noted as such.
/// </summary>
public static class InCylinderHeatTransfer
{
    public static double Coefficient(
        HeatTransferCorrelation correlation,
        double bore,
        double pressure,          // Pa
        double temperature,       // K
        double meanPistonSpeed,   // m/s
        double instantaneousVolume = 0.0,
        double extraGasVelocity = 0.0,
        double annandA = 0.49)
    {
        switch (correlation)
        {
            case HeatTransferCorrelation.Woschni:
            {
                var w = 2.28 * meanPistonSpeed + extraGasVelocity;
                return 3.26 * Math.Pow(bore, -0.2)
                            * Math.Pow(pressure / 1000.0, 0.8)
                            * Math.Pow(temperature, -0.55)
                            * Math.Pow(w, 0.8);
            }

            case HeatTransferCorrelation.Hohenberg:
            {
                var v = Math.Max(instantaneousVolume, 1e-6);
                return 130.0 * Math.Pow(v, -0.06)
                             * Math.Pow(pressure / 1e5, 0.8)
                             * Math.Pow(temperature, -0.4)
                             * Math.Pow(meanPistonSpeed + 1.4, 0.8);
            }

            case HeatTransferCorrelation.Annand:
            {
                // Gas properties for air-like charge: k and μ by simple
                // temperature power laws (documented approximations).
                var mu = Components.PipeFlowPhysics.SutherlandViscosity(temperature);
                var k = 0.0262 * Math.Pow(temperature / 300.0, 0.8);
                var rho = pressure / (287.0 * temperature);
                var re = rho * meanPistonSpeed * bore / mu;
                return annandA * k / bore * Math.Pow(re, 0.7);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(correlation));
        }
    }
}

/// <summary>
/// Chen–Flynn style friction correlation (plan §2.5):
///   FMEP = A + B·p_max + C·c_m + D·c_m²
/// Shipped defaults are documented approximations for a modern SI engine
/// (A 0.25 bar, B 0.006, C 0.09 bar·s/m, D 9e-4 bar·s²/m²) — expose and
/// calibrate against motored dyno data when available.
/// </summary>
public sealed record ChenFlynnFriction(
    double ConstantPa = 25_000.0,
    double PeakPressureFactor = 0.006,
    double SpeedFactorPaPerMps = 9_000.0,
    double SpeedSquaredFactor = 90.0)
{
    /// <summary>Friction mean effective pressure, Pa.</summary>
    public double Fmep(double peakCylinderPressure, double meanPistonSpeed) =>
        ConstantPa
        + PeakPressureFactor * peakCylinderPressure
        + SpeedFactorPaPerMps * meanPistonSpeed
        + SpeedSquaredFactor * meanPistonSpeed * meanPistonSpeed;
}
