namespace WaveBench.Boost.Engine;

/// <summary>The state of the restrictor at one flow.</summary>
/// <param name="MassFlowKgPerS">Flow through it.</param>
/// <param name="OutletTotalPressureKPa">p₀ downstream — the compressor's inlet, and it is below ambient.</param>
/// <param name="OutletTotalTemperatureK">T₀ downstream. Unchanged: a restrictor does no work.</param>
/// <param name="PressureRatio">p_downstream/p_ambient. Always below 1.</param>
/// <param name="IsChoked">Whether the throat has reached Mach 1.</param>
/// <param name="ChokedFlowKgPerS">The ceiling. No shaft speed, no boost and no cam can beat it.</param>
public readonly record struct RestrictorState(
    double MassFlowKgPerS,
    double OutletTotalPressureKPa,
    double OutletTotalTemperatureK,
    double PressureRatio,
    bool IsChoked,
    double ChokedFlowKgPerS);

/// <summary>
/// The FSAE intake restrictor, sitting <b>upstream of the compressor</b>
/// (plan §4.6.4).
///
/// Under the current rules that is where it goes, and the consequences are the
/// ones teams discover on the dyno:
///
/// <list type="bullet">
/// <item>The compressor inlet runs sub-atmospheric, so corrected flow and
/// corrected speed both shift and the whole operating line moves across the
/// map — usually toward surge at low flow and choke at high.</item>
/// <item>Once the restrictor chokes, the compressor cannot pull more mass
/// however fast the shaft turns. The turbo then raises pressure ratio against a
/// fixed mass flow, and that is a trajectory straight up the map into
/// surge.</item>
/// </list>
///
/// The nozzle is treated as isentropic to the throat with a discharge
/// coefficient on the effective area. A well-made venturi with a shallow
/// diffuser recovers most of the total pressure; a sharp-edged orifice recovers
/// very little, and the difference is worth several kilowatts.
/// </summary>
public sealed record IntakeRestrictor
{
    /// <summary>Throat diameter, m. FSAE petrol is 20 mm; E85 is 19 mm.</summary>
    public required double ThroatDiameterM { get; init; }

    /// <summary>Discharge coefficient at the throat. 0.95–0.98 for a proper venturi.</summary>
    public double DischargeCoefficient { get; init; } = 0.96;

    /// <summary>
    /// Fraction of the dynamic head recovered in the diffuser downstream of the
    /// throat. A shallow (6–8° included) diffuser recovers 0.8 or better; a
    /// sudden expansion recovers close to nothing. This is the single number a
    /// team can most cheaply improve, which is why it is a parameter rather
    /// than a constant.
    /// </summary>
    public double DiffuserRecovery { get; init; } = 0.80;

    /// <summary>The 20 mm restrictor of the petrol class.</summary>
    public static IntakeRestrictor Petrol20mm { get; } = new() { ThroatDiameterM = 0.020 };

    /// <summary>The 19 mm restrictor of the E85 class.</summary>
    public static IntakeRestrictor Ethanol19mm { get; } = new() { ThroatDiameterM = 0.019 };

    public double ThroatAreaM2 => Math.PI * ThroatDiameterM * ThroatDiameterM / 4.0;

    public double EffectiveAreaM2 => DischargeCoefficient * ThroatAreaM2;

    /// <summary>
    /// The choked mass flow: the hard ceiling on the whole engine.
    ///
    /// <code>ṁ* = C_d·A·p₀·√(γ/(R·T₀))·(2/(γ+1))^((γ+1)/(2(γ−1)))</code>
    ///
    /// Everything else in an FSAE induction system is an argument about how
    /// close to this number the car gets, and no amount of boost moves it.
    /// </summary>
    public double ChokedFlow(
        double ambientPressurePa = 101_325.0,
        double ambientTemperatureK = 298.15,
        double gamma = 1.4,
        double gasConstant = 287.05)
    {
        var factor = Math.Pow(2.0 / (gamma + 1.0), (gamma + 1.0) / (2.0 * (gamma - 1.0)));
        return EffectiveAreaM2 * ambientPressurePa * Math.Sqrt(gamma / (gasConstant * ambientTemperatureK)) * factor;
    }

    /// <summary>
    /// Solve the restrictor for a demanded mass flow, returning the compressor
    /// inlet condition it leaves behind.
    /// </summary>
    public RestrictorState Solve(
        double massFlowKgPerS,
        double ambientPressurePa = 101_325.0,
        double ambientTemperatureK = 298.15,
        double gamma = 1.4,
        double gasConstant = 287.05)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(massFlowKgPerS);

        var choked = ChokedFlow(ambientPressurePa, ambientTemperatureK, gamma, gasConstant);

        if (massFlowKgPerS >= choked)
        {
            // At and beyond choke the throat is at Mach 1 and the flow cannot
            // rise. Reporting the ceiling rather than throwing lets the caller
            // say "this is the limit" instead of "the model failed".
            var (pChoked, _) = Recover(choked, 1.0, ambientPressurePa, ambientTemperatureK, gamma, gasConstant);
            return new RestrictorState(
                choked, pChoked / 1000.0, ambientTemperatureK, pChoked / ambientPressurePa, true, choked);
        }

        // Subsonic: find the throat Mach number that passes this flow. Mass flux
        // rises monotonically with Mach up to 1, so bisection is safe.
        var lo = 1e-6;
        var hi = 1.0;
        for (var i = 0; i < 80 && hi - lo > 1e-12; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (Flux(mid, ambientPressurePa, ambientTemperatureK, gamma, gasConstant) < massFlowKgPerS)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var mach = 0.5 * (lo + hi);
        var (pOut, tOut) = Recover(
            massFlowKgPerS, mach, ambientPressurePa, ambientTemperatureK, gamma, gasConstant);

        return new RestrictorState(
            massFlowKgPerS, pOut / 1000.0, tOut, pOut / ambientPressurePa, false, choked);
    }

    /// <summary>Mass flow at a throat Mach number, from the isentropic relations.</summary>
    private double Flux(double mach, double p0, double t0, double gamma, double r)
    {
        var g1 = gamma - 1.0;
        var factor = 1.0 + (0.5 * g1 * mach * mach);
        var p = p0 / Math.Pow(factor, gamma / g1);
        var t = t0 / factor;
        var rho = p / (r * t);
        var a = Math.Sqrt(gamma * r * t);
        return rho * mach * a * EffectiveAreaM2;
    }

    /// <summary>
    /// Total pressure and temperature downstream, after whatever the diffuser
    /// recovers. A restrictor does no work, so total temperature is unchanged;
    /// what it destroys is total PRESSURE, and that is what moves the operating
    /// line.
    /// </summary>
    private (double PressurePa, double TemperatureK) Recover(
        double massFlow, double mach, double p0, double t0, double gamma, double r)
    {
        var g1 = gamma - 1.0;
        var factor = 1.0 + (0.5 * g1 * mach * mach);
        var pThroatStatic = p0 / Math.Pow(factor, gamma / g1);

        // Total pressure downstream: the throat's static pressure plus the share
        // of its dynamic head the diffuser gets back.
        var dynamic = p0 - pThroatStatic;
        var recovered = pThroatStatic + (DiffuserRecovery * dynamic);

        _ = massFlow;
        return (Math.Min(recovered, p0), t0);
    }
}

/// <summary>
/// Ambient conditions, and the sensitivity of a boosted engine to them
/// (plan §4.7: "an altitude / hot-day toggle, because that is where matches
/// fail").
/// </summary>
/// <param name="PressurePa">Ambient static pressure.</param>
/// <param name="TemperatureK">Ambient temperature.</param>
/// <param name="Label">What to call it in a report.</param>
public sealed record AmbientCondition(double PressurePa, double TemperatureK, string Label)
{
    /// <summary>ISO standard day at sea level.</summary>
    public static AmbientCondition StandardDay { get; } = new(101_325.0, 288.15, "Standard day, sea level");

    /// <summary>A hot competition day.</summary>
    public static AmbientCondition HotDay { get; } = new(101_325.0, 308.15, "35 °C, sea level");

    /// <summary>Denver, roughly — the altitude that catches out a sea-level match.</summary>
    public static AmbientCondition Altitude1600m { get; } = new(83_500.0, 283.15, "1600 m, 10 °C");

    /// <summary>Both at once, which is where matches actually fail.</summary>
    public static AmbientCondition HotAndHigh { get; } = new(83_500.0, 308.15, "1600 m, 35 °C");

    /// <summary>Air density, kg/m³.</summary>
    public double Density(double gasConstant = 287.05) => PressurePa / (gasConstant * TemperatureK);

    /// <summary>
    /// Density relative to a standard day — the first-order factor on a
    /// naturally-aspirated engine's torque, and the thing a turbo partly (but
    /// only partly) recovers by spinning faster.
    /// </summary>
    public double DensityRatio() => Density() / StandardDay.Density();

    public static IReadOnlyList<AmbientCondition> Standard { get; } =
        [StandardDay, HotDay, Altitude1600m, HotAndHigh];
}
