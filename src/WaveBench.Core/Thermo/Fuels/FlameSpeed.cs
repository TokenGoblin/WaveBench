using WaveBench.Model.Units;

namespace WaveBench.Core.Thermo.Fuels;

/// <summary>
/// Laminar flame speed, Metghalchi &amp; Keck (1982) form (plan §2.4):
///   S_L = S_L0(φ) · (T_u/T_0)^α · (p/p_0)^β · (1 − 2.1·Y_dil)
///   S_L0 = Bm + Bφ·(φ − φm)²,  α = 2.18 − 0.8(φ−1),  β = −0.16 + 0.22(φ−1)
/// with T_0 = 298 K, p_0 = 1 atm. Validity: roughly 0.8 ≤ φ ≤ 1.4,
/// T_u 298–700 K, p 0.4–50 atm, Y_dil ≤ 0.2.
/// </summary>
public static class FlameSpeed
{
    private const double T0 = 298.0;
    private const double P0 = 101_325.0;

    /// <summary>m/s. Throws when the fuel has no flame-speed coefficients (e.g. H2).</summary>
    public static double Laminar(
        Fuel fuel,
        double equivalenceRatio,
        double unburnedTemperature,
        double pressure,
        double diluentMassFraction = 0.0)
    {
        var c = fuel.FlameSpeed
            ?? throw new InvalidOperationException(
                $"Fuel '{fuel.Name}' has no Metghalchi-Keck flame-speed coefficients.");

        var sL0 = c.Bm + c.BPhi * Math.Pow(equivalenceRatio - c.PhiM, 2);
        if (sL0 <= 0)
        {
            return 0.0; // outside the flammable fit range
        }

        var alpha = 2.18 - 0.8 * (equivalenceRatio - 1.0);
        var beta = -0.16 + 0.22 * (equivalenceRatio - 1.0);
        var dilution = Math.Max(0.0, 1.0 - 2.1 * diluentMassFraction);

        return sL0
               * Math.Pow(unburnedTemperature / T0, alpha)
               * Math.Pow(pressure / P0, beta)
               * dilution;
    }

    /// <summary>Unit-safe overload (temperature in any supported unit, pressure likewise).</summary>
    public static double Laminar(
        Fuel fuel,
        double equivalenceRatio,
        Temperature unburnedTemperature,
        Pressure pressure,
        double diluentMassFraction = 0.0) =>
        Laminar(fuel, equivalenceRatio, unburnedTemperature.Kelvin, pressure.Pascals, diluentMassFraction);

    /// <summary>True when all inputs lie inside the correlation's stated validity range.</summary>
    public static bool IsWithinValidity(
        double equivalenceRatio, double unburnedTemperature, double pressure, double diluentMassFraction = 0.0) =>
        equivalenceRatio is >= 0.8 and <= 1.4 &&
        unburnedTemperature is >= 298.0 and <= 700.0 &&
        pressure >= 0.4 * P0 && pressure <= 50.0 * P0 &&
        diluentMassFraction is >= 0.0 and <= 0.2;
}
