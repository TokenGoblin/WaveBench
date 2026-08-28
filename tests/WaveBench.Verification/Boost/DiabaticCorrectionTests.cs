using FluentAssertions;
using WaveBench.Boost;
using WaveBench.Boost.Thermal;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 13 gate: <i>"the diabatic correction improves on raw-map outlet
/// temperature"</i>.
///
/// <b>What "improves" can honestly mean here.</b> Validation case 21 asks for a
/// measured on-engine compressor outlet temperature to compare against, and
/// there is no such dataset in this repository — so what is verified is the
/// machinery and the magnitude, not agreement with a measurement:
///
/// <list type="number">
/// <item>Against a SYNTHETIC truth where the heat flux is known exactly, the
/// corrected prediction recovers it and the raw map does not. That is an exact
/// test of the correction itself.</item>
/// <item>The magnitude lands inside the range the plan states for on-engine
/// operation — 15–30 K above the adiabatic prediction — over a representative
/// operating range.</item>
/// <item>The direction is right everywhere and for the right reason: the
/// correction only ever adds heat on an engine, because engine turbine inlet
/// temperature is above any gas stand's.</item>
/// </list>
///
/// Validation case 21 stays open, and the conductances in
/// <see cref="TurboThermalProperties"/> are exposed for exactly that.
/// </summary>
public class DiabaticCorrectionTests(ITestOutputHelper output)
{
    private static readonly TurboEnvironment Engine = new(AmbientK: 350.0, OilInletK: 380.0, CoolantK: 363.0);

    [Fact]
    public void Gate_the_correction_recovers_a_known_heat_flux_that_the_raw_map_misses()
    {
        // The synthetic truth. A compressor with a KNOWN aerodynamic efficiency
        // is measured on a gas stand, where the stand's own heat flux inflates
        // the measured temperature rise and therefore deflates the apparent
        // efficiency. The map records the apparent value. The correction has to
        // recover the aerodynamic one and then re-apply the ENGINE's heat.
        const double trueAero = 0.78;
        const double pressureRatio = 2.2;
        const double inlet = 298.15;
        const double flow = 0.12;
        const double cp = 1005.0;
        const double gamma = 1.4;

        var properties = new TurboThermalProperties();
        var stand = DiabaticCorrection.GasStandCondition.Hot;

        var idealRise = inlet * (Math.Pow(pressureRatio, (gamma - 1.0) / gamma) - 1.0);
        var aeroRise = idealRise / trueAero;
        var adiabaticOutlet = inlet + aeroRise;

        // What the stand would have measured: the aerodynamic rise plus its own
        // heat, computed from the same thermal model the correction uses.
        var standModel = new TurboThermalModel(properties);
        var standHeat = standModel.SolveSteady(
            stand.TurbineInletK, 0.5 * (inlet + adiabaticOutlet), stand.Conditions).CompressorAirHeatW;

        var measuredRise = aeroRise + (standHeat / (flow * cp));
        var apparentEfficiency = idealRise / measuredRise;

        output.WriteLine(
            $"true aerodynamic η {trueAero:P1}; the stand's {standHeat:F0} W makes the map read "
            + $"{apparentEfficiency:P1}");

        apparentEfficiency.Should().BeLessThan(trueAero,
            "a gas stand always measures a lower efficiency than the compressor has");

        var corrected = DiabaticCorrection.Correct(
            apparentEfficiency, pressureRatio, inlet, flow,
            engineTurbineInletK: 1100.0, Engine, stand, properties);

        // The correction must recover the aerodynamic efficiency it started
        // from. This is exact: the same model that generated the synthetic
        // measurement is being inverted, so anything but a match is an error in
        // the inversion.
        corrected.AerodynamicEfficiency.Should().BeApproximately(trueAero, 2e-3);
        corrected.AdiabaticOutletK.Should().BeApproximately(adiabaticOutlet, 0.5);

        output.WriteLine(
            $"recovered η {corrected.AerodynamicEfficiency:P2} against a true {trueAero:P2}");
        output.WriteLine(
            $"raw map {corrected.RawMapOutletK:F1} K · adiabatic {corrected.AdiabaticOutletK:F1} K · "
            + $"on engine {corrected.DiabaticOutletK:F1} K");

        // And the gate itself: on the engine the raw map under-predicts, and the
        // correction closes the gap.
        corrected.DiabaticOutletK.Should().BeGreaterThan(corrected.RawMapOutletK,
            "an engine's turbine end is hotter than any gas stand's, so the air arrives hotter than the map says");
    }

    [Fact]
    public void Gate_the_on_engine_rise_reaches_the_published_range_where_the_literature_puts_it()
    {
        // Plan §4.2: "on-engine compressor outlet temperature routinely runs
        // 15–30 K above the adiabatic prediction."
        //
        // <b>That is not a flat offset, and this test does not pretend it is.</b>
        // The heat flux is roughly fixed by the housing temperatures; carrying a
        // fixed power in a smaller mass flow makes a bigger temperature rise. So
        // the effect is large at low flow — which is where the reported figures
        // come from, and where a matched turbo spends its transient — and small
        // at high flow. That fall-off is itself the published behaviour
        // (Serrano/Olmeda/Arnau), and a model that produced 20 K everywhere
        // would be reproducing the quoted number rather than the physics.
        //
        // The conductances were set to reproduce compressor-side heat fluxes of
        // the published order — several hundred watts to about 1.5 kW — and NOT
        // adjusted to land this band.
        output.WriteLine("  ṁ kg/s    PR    TIT K   raw map K   adiabatic K   on engine K   over adiabatic   heat W");

        var rises = new List<(double Flow, double OverAdiabatic, double OverRawMap, double HeatW)>();

        foreach (var (flow, pr, tit) in new[]
                 {
                     (0.06, 1.6, 950.0),
                     (0.09, 1.9, 1020.0),
                     (0.12, 2.2, 1080.0),
                     (0.16, 2.5, 1120.0),
                     (0.20, 2.8, 1150.0),
                 })
        {
            var corrected = DiabaticCorrection.Correct(
                mapEfficiency: 0.74, pr, 298.15, flow, tit, Engine);

            var overAdiabatic = corrected.DiabaticOutletK - corrected.AdiabaticOutletK;
            rises.Add((flow, overAdiabatic, corrected.DiabaticOutletK - corrected.RawMapOutletK,
                corrected.EngineHeatW));

            output.WriteLine(
                $"{flow,8:F2}  {pr,4:F1}  {tit,7:F0}   {corrected.RawMapOutletK,9:F1}   "
                + $"{corrected.AdiabaticOutletK,11:F1}   {corrected.DiabaticOutletK,11:F1}   "
                + $"{overAdiabatic,13:F1} K   {corrected.EngineHeatW,6:F0}");
        }

        rises[0].OverAdiabatic.Should().BeInRange(15.0, 30.0,
            "at the low-flow end — where the published figures are measured — the rise must land in the "
            + "plan's stated band");

        rises.Select(r => r.OverAdiabatic).Should().BeInDescendingOrder(
            "the same heat in less air is a bigger temperature rise");

        rises.Should().AllSatisfy(r => r.HeatW.Should().BeInRange(300.0, 1600.0,
            "compressor-side heat flux must stay in the published order of magnitude"));

        // The gate clause itself, at every point on the line: the corrected
        // answer is above the raw map, by an amount that is not negligible where
        // it matters.
        rises.Should().AllSatisfy(r => r.OverRawMap.Should().BeGreaterThan(2.0));
        rises[0].OverRawMap.Should().BeGreaterThan(6.0,
            "at low flow the raw map under-predicts by enough to matter to an intercooler sizing");
    }

    [Fact]
    public void The_correction_never_cools_the_charge_on_an_engine()
    {
        // A directional invariant worth pinning: engine turbine inlet
        // temperature is above any gas stand's, so the engine heat flux exceeds
        // the stand's and the corrected outlet is always above the adiabatic
        // one. A correction that could go either way would be a sign the two
        // heat fluxes had been swapped.
        foreach (var tit in new[] { 700.0, 900.0, 1100.0, 1250.0 })
        {
            var corrected = DiabaticCorrection.Correct(0.74, 2.0, 298.15, 0.12, tit, Engine);
            corrected.DiabaticOutletK.Should().BeGreaterThan(corrected.AdiabaticOutletK, $"at TIT {tit} K");
        }
    }

    [Fact]
    public void A_hotter_turbine_end_makes_a_hotter_compressor_outlet()
    {
        var cool = DiabaticCorrection.Correct(0.74, 2.2, 298.15, 0.12, 850.0, Engine);
        var hot = DiabaticCorrection.Correct(0.74, 2.2, 298.15, 0.12, 1200.0, Engine);

        hot.DiabaticOutletK.Should().BeGreaterThan(cool.DiabaticOutletK);
        hot.EngineHeatW.Should().BeGreaterThan(cool.EngineHeatW);

        output.WriteLine(
            $"TIT 850 K -> {cool.EngineHeatW:F0} W, outlet {cool.DiabaticOutletK:F1} K.  "
            + $"TIT 1200 K -> {hot.EngineHeatW:F0} W, outlet {hot.DiabaticOutletK:F1} K.");
    }

    [Fact]
    public void A_water_cooled_centre_housing_puts_less_heat_into_the_charge()
    {
        // The reason water-cooled centre housings exist, quantified: the coolant
        // path intercepts heat at the bearing housing before it reaches the
        // compressor end.
        var oilOnly = DiabaticCorrection.Correct(
            0.74, 2.2, 298.15, 0.12, 1100.0, Engine, properties: new TurboThermalProperties());

        var waterCooled = DiabaticCorrection.Correct(
            0.74, 2.2, 298.15, 0.12, 1100.0, Engine,
            properties: new TurboThermalProperties().WaterCooled());

        waterCooled.EngineHeatW.Should().BeLessThan(oilOnly.EngineHeatW);
        waterCooled.DiabaticOutletK.Should().BeLessThan(oilOnly.DiabaticOutletK);

        output.WriteLine(
            $"oil-cooled {oilOnly.EngineHeatW:F0} W / {oilOnly.DiabaticOutletK:F1} K, "
            + $"water-cooled {waterCooled.EngineHeatW:F0} W / {waterCooled.DiabaticOutletK:F1} K "
            + $"({oilOnly.DiabaticOutletK - waterCooled.DiabaticOutletK:F1} K cooler charge)");
    }

    [Fact]
    public void The_housings_reach_a_steady_state_and_the_transient_gets_there()
    {
        // The steady solve and the transient integration have to agree, or one
        // of them is wrong — and it is the transient that will drive heat soak.
        var model = new TurboThermalModel();
        var steady = model.SolveSteady(1100.0, 360.0, Engine);

        var transient = new TurboThermalModel();
        transient.SetHousings(350.0, 350.0, 350.0);
        for (var t = 0.0; t < 900.0; t += 0.5)
        {
            transient.Step(0.5, 1100.0, 360.0, Engine);
        }

        transient.TurbineHousingK.Should().BeApproximately(steady.TurbineHousingK, 1.0);
        transient.BearingHousingK.Should().BeApproximately(steady.BearingHousingK, 1.0);
        transient.CompressorHousingK.Should().BeApproximately(steady.CompressorHousingK, 1.0);

        output.WriteLine(
            $"steady: turbine {steady.TurbineHousingK:F0} K, bearing {steady.BearingHousingK:F0} K, "
            + $"compressor {steady.CompressorHousingK:F0} K; "
            + $"{steady.TurbineGasHeatW:F0} W in from the gas, {steady.CompressorAirHeatW:F0} W into the charge, "
            + $"{steady.OilHeatW:F0} W to the oil");

        // Housing temperatures have to be ordered and physical: hottest at the
        // turbine, coolest at the compressor, none of them above the gas.
        steady.TurbineHousingK.Should().BeGreaterThan(steady.BearingHousingK);
        steady.BearingHousingK.Should().BeGreaterThan(steady.CompressorHousingK);
        steady.TurbineHousingK.Should().BeLessThan(1100.0);
    }
}
