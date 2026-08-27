namespace WaveBench.Boost;

/// <summary>
/// Bearing friction as a function of shaft speed and oil condition
/// (plan §4.1).
///
/// It dominates at low speed and is a first-order contributor to spool time,
/// so leaving it out makes every turbo look faster than it is. A power law in
/// speed with a viscosity factor is the standard lumped treatment; the
/// coefficient is a fitted quantity and is exposed rather than buried.
/// </summary>
/// <param name="Coefficient">
/// Scales the whole loss; calibrate against a coast-down. The default puts
/// roughly 0.9 kW into the bearings at 150 000 rpm, which is the right order
/// for a 60 mm-wheel journal-bearing unit — but it is a fitted lumped number,
/// not a measurement, and the Phase 13 spool prediction is sensitive to it.
/// </param>
/// <param name="SpeedExponent">
/// Friction TORQUE exponent, so power goes as N^(exponent+1). A pure Petroff
/// journal bearing would be 1; measured turbocharger losses come out nearer 2
/// once windage and thrust-face losses are in, and ball-bearing cartridges
/// lower.
/// </param>
/// <param name="OilViscosityRatio">Relative to the calibration oil at temperature. Cold oil is several times this.</param>
public sealed record BearingFriction(
    double Coefficient = 2.7e-13,
    double SpeedExponent = 2.0,
    double OilViscosityRatio = 1.0)
{
    /// <summary>Friction power at a shaft speed, W.</summary>
    public double PowerW(double shaftRpm) =>
        Coefficient * OilViscosityRatio * Math.Pow(Math.Max(0.0, shaftRpm), SpeedExponent + 1.0);
}

/// <summary>A matched steady operating point of the whole turbocharger.</summary>
/// <param name="ShaftRpm">Where the shaft settles.</param>
/// <param name="Compressor">The compressor side at that speed.</param>
/// <param name="Turbine">The turbine side.</param>
/// <param name="ExpansionRatio">Turbine expansion ratio, and therefore exhaust back-pressure.</param>
/// <param name="FrictionPowerW">Bearing loss at that speed.</param>
/// <param name="Converged">False when no speed balanced the shaft inside the search range.</param>
public sealed record MatchPoint(
    double ShaftRpm,
    CompressorPointResult Compressor,
    TurbinePointResult Turbine,
    double ExpansionRatio,
    double FrictionPowerW,
    bool Converged)
{
    /// <summary>
    /// Exhaust manifold pressure over intake manifold pressure.
    ///
    /// Turbine inlet pressure is ER × ambient and compressor outlet pressure is
    /// PR × ambient, so the ambient cancels and this is simply ER/PR. Above 1
    /// the engine is pumping against more back-pressure than boost, which costs
    /// pumping work and closes the scavenging window (plan §4.6.3) — the number
    /// that decides whether a match is a good one, and the one a boost figure
    /// alone hides.
    /// </summary>
    public double BackPressureRatio =>
        Compressor.PressureRatio > 0 ? ExpansionRatio / Compressor.PressureRatio : double.NaN;
}

/// <summary>
/// Steady shaft matching (plan §4.1): find the speed at which the turbine
/// exactly drives the compressor.
///
/// <code>P_turbine·η_mech = P_compressor + P_friction</code>
///
/// This is the calculation that turns two maps and an engine into a boost
/// prediction. It is steady — the transient version integrates
/// J·dω/dt against the same imbalance, and is Phase 13.
/// </summary>
public static class ShaftBalance
{
    /// <summary>
    /// Match the turbo to an engine's demand at one operating point.
    /// </summary>
    /// <param name="turbo">Both maps and the mechanical properties.</param>
    /// <param name="airMassFlowKgPerS">What the engine is drawing.</param>
    /// <param name="exhaustMassFlowKgPerS">What it is expelling — air plus fuel.</param>
    /// <param name="compressorInletK">T₀₁.</param>
    /// <param name="compressorInletKPa">p₀₁, sub-atmospheric behind a restrictor.</param>
    /// <param name="turbineInletK">T₀₃, the turbine inlet temperature.</param>
    /// <param name="turbineOutletKPa">p₄, downstream of the turbine.</param>
    /// <param name="friction">Bearing model.</param>
    public static MatchPoint Match(
        Turbocharger turbo,
        double airMassFlowKgPerS,
        double exhaustMassFlowKgPerS,
        double compressorInletK,
        double compressorInletKPa,
        double turbineInletK,
        double turbineOutletKPa,
        BearingFriction? friction = null)
    {
        ArgumentNullException.ThrowIfNull(turbo);

        var bearing = friction ?? new BearingFriction();

        // Net shaft power at a trial speed. The turbine's expansion ratio is
        // not free: the engine's exhaust flow has to go through it, so the
        // ratio is whatever swallows that flow at this speed. That is the
        // coupling people miss — a turbo does not "make" boost, it back-
        // pressures the engine until the flows agree.
        double Imbalance(double rpm)
        {
            var er = TurbineModel.ExpansionRatioFor(
                turbo.Turbine, exhaustMassFlowKgPerS, rpm, turbineInletK, turbineOutletKPa);

            var turbine = TurbineModel.Solve(turbo.Turbine, er, rpm, turbineInletK, turbineOutletKPa);
            var compressor = CompressorModel.Solve(
                turbo.Compressor, airMassFlowKgPerS, rpm, compressorInletK, compressorInletKPa);

            return (turbine.PowerW * turbo.MechanicalEfficiency)
                   - compressor.PowerW
                   - bearing.PowerW(rpm);
        }

        // Turbine power rises with speed more slowly than compressor power
        // does, so the imbalance falls through zero exactly once: bisection is
        // safe and needs no derivative.
        var lo = 1_000.0;
        var hi = turbo.Compressor.HighestSpeed * 1.4;

        var fLo = Imbalance(lo);
        var fHi = Imbalance(hi);

        var converged = fLo > 0 && fHi < 0;
        if (!converged)
        {
            // No crossing: either the turbine cannot drive the compressor at
            // all (fLo < 0) or it overwhelms it beyond the map (fHi > 0). The
            // nearer end is returned so the caller can report WHICH, rather
            // than getting a number with no standing.
            var rpmEnd = fLo < 0 ? lo : hi;
            return Assemble(turbo, rpmEnd, airMassFlowKgPerS, exhaustMassFlowKgPerS,
                compressorInletK, compressorInletKPa, turbineInletK, turbineOutletKPa, bearing, false);
        }

        for (var i = 0; i < 80 && hi - lo > 10.0; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (Imbalance(mid) > 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Assemble(turbo, 0.5 * (lo + hi), airMassFlowKgPerS, exhaustMassFlowKgPerS,
            compressorInletK, compressorInletKPa, turbineInletK, turbineOutletKPa, bearing, true);
    }

    private static MatchPoint Assemble(
        Turbocharger turbo, double rpm, double airFlow, double exhaustFlow,
        double compressorInletK, double compressorInletKPa,
        double turbineInletK, double turbineOutletKPa,
        BearingFriction bearing, bool converged)
    {
        var er = TurbineModel.ExpansionRatioFor(
            turbo.Turbine, exhaustFlow, rpm, turbineInletK, turbineOutletKPa);

        return new MatchPoint(
            rpm,
            CompressorModel.Solve(turbo.Compressor, airFlow, rpm, compressorInletK, compressorInletKPa),
            TurbineModel.Solve(turbo.Turbine, er, rpm, turbineInletK, turbineOutletKPa),
            er,
            bearing.PowerW(rpm),
            converged);
    }

    /// <summary>
    /// An operating line: the matched point at every engine speed, which is
    /// what gets drawn on the compressor map (plan §4.7).
    /// </summary>
    public static IReadOnlyList<(double EngineRpm, MatchPoint Point)> OperatingLine(
        Turbocharger turbo,
        IReadOnlyList<(double EngineRpm, double AirFlowKgPerS, double ExhaustFlowKgPerS, double TurbineInletK)> demand,
        double compressorInletK = 298.15,
        double compressorInletKPa = 101.325,
        double turbineOutletKPa = 101.325,
        BearingFriction? friction = null)
    {
        ArgumentNullException.ThrowIfNull(demand);

        return demand
            .Select(d => (d.EngineRpm, Match(
                turbo, d.AirFlowKgPerS, d.ExhaustFlowKgPerS,
                compressorInletK, compressorInletKPa, d.TurbineInletK, turbineOutletKPa, friction)))
            .ToList();
    }
}
