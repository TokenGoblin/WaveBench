using WaveBench.Core.Thermo.Fuels;

namespace WaveBench.Core.Thermo;

/// <summary>
/// Burnt-gas composition for a CxHyOz fuel in standard dry air at a given
/// equivalence ratio φ.
///
/// Lean/stoichiometric (φ ≤ 1): complete combustion — CO2, H2O, excess O2,
/// N2, Ar (air CO2 carried through).
///
/// Rich (φ > 1): no O2 survives; CO and H2 from the water-gas-shift
/// equilibrium CO2 + H2 ⇌ CO + H2O with K treated as constant
/// (default 3.5, the value near 1740 K — the standard simplification after
/// Heywood, "Internal Combustion Engine Fundamentals", ch. 4). Dissociation
/// (OH, NO, O, H) is neglected here; those species matter for emissions, not
/// for the R and γ of bulk exhaust (plan §2.2 minimum species set).
/// </summary>
public static class CombustionProducts
{
    public static GasComposition Of(
        FuelFormula fuel,
        double equivalenceRatio,
        SpeciesDatabase database,
        double waterGasShiftK = 3.5)
    {
        if (equivalenceRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(equivalenceRatio), "φ must be positive.");
        }

        double x = fuel.Carbon, y = fuel.Hydrogen, z = fuel.Oxygen;
        var a = x + y / 4.0 - z / 2.0;             // kmol O2 per kmol fuel, stoichiometric
        var o2Supplied = a / equivalenceRatio;

        var n2 = o2Supplied * AirComposition.N2PerO2;
        var ar = o2Supplied * AirComposition.ArPerO2;
        var airCo2 = o2Supplied * AirComposition.Co2PerO2;

        var moles = new List<KeyValuePair<string, double>>
        {
            new("N2", n2),
            new("AR", ar),
        };

        if (equivalenceRatio <= 1.0)
        {
            moles.Add(new("CO2", x + airCo2));
            moles.Add(new("H2O", y / 2.0));
            var excessO2 = a * (1.0 / equivalenceRatio - 1.0);
            if (excessO2 > 1e-12)
            {
                moles.Add(new("O2", excessO2));
            }
        }
        else
        {
            // Element balances with b = kmol CO:
            //   C: x = nCO2 + b        H: y/2 = nH2O + nH2
            //   O: z + 2·o2Supplied = 2·nCO2 + b + nH2O  →  nH2O = d + b,
            //      d = z + 2·o2Supplied − 2x
            // K = b·nH2O / (nCO2·nH2) gives a quadratic in b.
            var k = waterGasShiftK;
            var d = z + 2.0 * o2Supplied - 2.0 * x;
            if (y / 2.0 + d <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(equivalenceRatio), "Mixture too rich: not enough oxygen for CO/H2 products.");
            }

            var qa = k - 1.0;
            var qb = k * (d - x - y / 2.0) - d;
            var qc = k * x * (y / 2.0 - d);
            double b;
            if (Math.Abs(qa) < 1e-12)
            {
                b = -qc / qb;
            }
            else
            {
                var disc = Math.Sqrt(qb * qb - 4.0 * qa * qc);
                b = (-qb - disc) / (2.0 * qa);
                if (b < 0 || b > x)
                {
                    b = (-qb + disc) / (2.0 * qa);
                }
            }

            // Physical bounds: nCO2 ≥ 0 → b ≤ x; nH2O ≥ 0 → b ≥ −d; nH2 ≥ 0 → b ≤ y/2 − d.
            b = Math.Clamp(b, Math.Max(0.0, -d), Math.Min(x, y / 2.0 - d));
            var nCo2 = x - b;
            var nH2O = d + b;
            var nH2 = y / 2.0 - nH2O;

            moles.Add(new("CO2", nCo2 + airCo2));
            moles.Add(new("CO", b));
            moles.Add(new("H2O", nH2O));
            if (nH2 > 1e-12)
            {
                moles.Add(new("H2", nH2));
            }
        }

        return GasComposition.FromMoleFractions(moles, database);
    }
}
