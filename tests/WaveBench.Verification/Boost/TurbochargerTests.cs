using FluentAssertions;
using WaveBench.Boost;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 12 gate, first two criteria: <i>"verification tests for map round-trip,
/// shaft balance and adiabatic relations pass; a known turbo/engine pair
/// produces a plausible operating line."</i>
/// </summary>
public class TurbochargerTests(ITestOutputHelper output)
{
    private const double Cp = 1005.0;

    private const double Gamma = 1.4;

    // ---- Map round-trip ---------------------------------------------------

    [Fact]
    public void Gate_a_turbocharger_round_trips_through_its_file_format_exactly()
    {
        var original = SyntheticTurbo.Turbo();
        var reloaded = Turbocharger.Load(original.Save());

        reloaded.Name.Should().Be(original.Name);
        reloaded.ShaftInertia.Should().Be(original.ShaftInertia);
        reloaded.MechanicalEfficiency.Should().Be(original.MechanicalEfficiency);
        reloaded.MaxTurbineInletK.Should().Be(original.MaxTurbineInletK);

        // Reference conditions are the point of the exercise: a map that loses
        // them on the way through the file is a map that will be read against
        // the wrong day.
        reloaded.Compressor.Reference.Should().Be(original.Compressor.Reference);
        reloaded.Turbine.Reference.Should().Be(original.Turbine.Reference);

        // Bit-identical, not merely close. Round-tripping through
        // "R" formatting is what makes a saved design reproducible.
        for (var l = 0; l < original.Compressor.SpeedLines.Count; l++)
        {
            var a = original.Compressor.SpeedLines[l];
            var b = reloaded.Compressor.SpeedLines[l];
            b.CorrectedRpm.Should().Be(a.CorrectedRpm);

            for (var p = 0; p < a.Points.Count; p++)
            {
                b.Points[p].CorrectedFlowKgPerS.Should().Be(a.Points[p].CorrectedFlowKgPerS);
                b.Points[p].PressureRatio.Should().Be(a.Points[p].PressureRatio);
                b.Points[p].Efficiency.Should().Be(a.Points[p].Efficiency);
            }
        }

        for (var l = 0; l < original.Turbine.SpeedLines.Count; l++)
        {
            var a = original.Turbine.SpeedLines[l];
            var b = reloaded.Turbine.SpeedLines[l];
            for (var p = 0; p < a.Points.Count; p++)
            {
                b.Points[p].ExpansionRatio.Should().Be(a.Points[p].ExpansionRatio);
                b.Points[p].CorrectedFlowKgPerS.Should().Be(a.Points[p].CorrectedFlowKgPerS);
                b.Points[p].Efficiency.Should().Be(a.Points[p].Efficiency);
            }
        }

        // And the solver reads the reloaded map identically.
        var before = CompressorModel.Solve(original.Compressor, 0.20, 130_000, 298.15, 101.325);
        var after = CompressorModel.Solve(reloaded.Compressor, 0.20, 130_000, 298.15, 101.325);
        after.PressureRatio.Should().Be(before.PressureRatio);
        after.PowerW.Should().Be(before.PowerW);
    }

    [Fact]
    public void A_map_file_with_no_reference_conditions_is_refused()
    {
        // Plan §4.2 calls assuming reference conditions "a classic silent 5%
        // error". The loader has to refuse, not default: a map read against the
        // wrong reference produces an operating line that looks entirely
        // reasonable and is wrong everywhere.
        const string json = """
            {
              "name": "A map from somewhere",
              "speedLines": [
                { "correctedRpm": 100000, "points": [
                  { "correctedFlowKgPerS": 0.10, "pressureRatio": 1.8, "efficiency": 0.74 },
                  { "correctedFlowKgPerS": 0.22, "pressureRatio": 1.5, "efficiency": 0.70 } ] },
                { "correctedRpm": 130000, "points": [
                  { "correctedFlowKgPerS": 0.13, "pressureRatio": 2.6, "efficiency": 0.76 },
                  { "correctedFlowKgPerS": 0.27, "pressureRatio": 2.1, "efficiency": 0.71 } ] }
              ]
            }
            """;

        var act = () => CompressorMap.Load(json);
        act.Should().Throw<System.Text.Json.JsonException>()
            .WithMessage("*reference*", "the failure has to name the missing field, not just fail");

        // The same map WITH a reference loads.
        var withReference = json.Replace(
            "\"name\": \"A map from somewhere\",",
            "\"name\": \"A map from somewhere\", \"reference\": { \"temperatureK\": 298.15, \"pressureKPa\": 100 },",
            StringComparison.Ordinal);

        CompressorMap.Load(withReference).Reference.Should().Be(MapReference.SaeJ1826);
    }

    [Fact]
    public void Reading_a_map_against_the_wrong_reference_day_shifts_it_measurably()
    {
        // Not a defect test — a demonstration of why the field is required.
        // The same physical point, corrected against two common gas-stand
        // references, is not the same point on the map.
        const double rpm = 130_000;
        const double actualK = 320.0;

        var asJ1826 = Corrected.Speed(rpm, actualK, MapReference.SaeJ1826);
        var asStandardDay = Corrected.Speed(rpm, actualK, MapReference.StandardDay);

        var shift = Math.Abs(asStandardDay - asJ1826) / asJ1826;
        shift.Should().BeApproximately(1.0 - Math.Sqrt(288.15 / 298.15), 1e-12);
        shift.Should().BeGreaterThan(0.015, "the two references differ by more than 1.5% in corrected speed");

        output.WriteLine(
            $"{rpm:N0} rpm at {actualK} K corrects to {asJ1826:N0} against J1826 and "
            + $"{asStandardDay:N0} against a standard day — {shift:P2} apart.");
    }

    [Fact]
    public void Corrected_quantities_invert_exactly()
    {
        var reference = MapReference.SaeJ1826;
        const double flow = 0.2137;
        const double t = 311.4;
        const double p = 93.8;

        Corrected.ActualFlow(Corrected.Flow(flow, t, p, reference), t, p, reference)
            .Should().BeApproximately(flow, 1e-15);

        Corrected.ActualSpeed(Corrected.Speed(122_345, t, reference), t, reference)
            .Should().BeApproximately(122_345, 1e-9);
    }

    // ---- Adiabatic relations ----------------------------------------------

    [Fact]
    public void Gate_the_compressor_obeys_the_adiabatic_compression_relations()
    {
        var map = SyntheticTurbo.Compressor();
        var result = CompressorModel.Solve(map, 0.20, 130_000, 298.15, 101.325);

        // T₀₂ = T₀₁·[1 + (PR^((γ−1)/γ) − 1)/η_is], computed here from first
        // principles rather than from anything the model shares with it.
        var ideal = 298.15 * (Math.Pow(result.PressureRatio, (Gamma - 1.0) / Gamma) - 1.0);
        var expectedOutlet = 298.15 + (ideal / result.Efficiency);

        result.OutletTemperatureK.Should().BeApproximately(expectedOutlet, 1e-9);

        // And the shaft power is the enthalpy rise it implies.
        result.PowerW.Should().BeApproximately(0.20 * Cp * (result.OutletTemperatureK - 298.15), 1e-9);

        // Sanity, against the hand calculation: an ideal compressor doing PR 2
        // from 298.15 K raises the total temperature by 2^(1/3.5) = 1.2190, to
        // 363.45 K — a 65.3 K rise before any inefficiency is added.
        var ideal2 = 298.15 * Math.Pow(2.0, (Gamma - 1.0) / Gamma);
        ideal2.Should().BeApproximately(363.45, 0.01);

        output.WriteLine(
            $"PR {result.PressureRatio:F3}, η {result.Efficiency:P1}, T₀₂ {result.OutletTemperatureK:F1} K, "
            + $"{result.PowerW / 1000:F2} kW.");
    }

    [Fact]
    public void An_isentropic_compressor_reaches_exactly_the_isentropic_temperature()
    {
        // The limiting case pins the relation itself, independently of the map:
        // at η = 1 the outlet temperature must be the isentropic one exactly.
        var perfect = new CompressorMap
        {
            Name = "Isentropic",
            Reference = MapReference.SaeJ1826,
            SpeedLines =
            [
                new CompressorSpeedLine(100_000, [new(0.10, 2.0, 1.0), new(0.30, 2.0, 1.0)]),
                new CompressorSpeedLine(120_000, [new(0.10, 2.0, 1.0), new(0.30, 2.0, 1.0)]),
            ],
        };

        var result = CompressorModel.Solve(perfect, 0.20, 100_000, 288.15, 101.325);

        result.PressureRatio.Should().BeApproximately(2.0, 1e-12);
        result.OutletTemperatureK.Should().BeApproximately(
            288.15 * Math.Pow(2.0, (Gamma - 1.0) / Gamma), 1e-9);
    }

    [Fact]
    public void Gate_the_turbine_obeys_the_adiabatic_expansion_relations()
    {
        var map = SyntheticTurbo.Turbine();
        const double tit = 1100.0;
        var result = TurbineModel.Solve(map, 2.0, 110_000, tit, 101.325);

        // T₄ = T₀₃·[1 − η·(1 − ER^(−(γ−1)/γ))] with γ = 1.33 for products.
        const double gammaExhaust = 1.33;
        var ideal = 1.0 - Math.Pow(2.0, -(gammaExhaust - 1.0) / gammaExhaust);
        var expectedOutlet = tit * (1.0 - (result.Efficiency * ideal));

        result.OutletTemperatureK.Should().BeApproximately(expectedOutlet, 1e-9);
        result.PowerW.Should().BeApproximately(
            result.MassFlowKgPerS * 1150.0 * (tit - result.OutletTemperatureK), 1e-9);

        // The expansion must COOL the gas, and by a sensible amount — a turbine
        // dropping 1100 K by only a few degrees, or by half, would both pass a
        // relation check and be obviously wrong.
        var drop = tit - result.OutletTemperatureK;
        drop.Should().BeInRange(80.0, 300.0);

        output.WriteLine(
            $"ER 2.0 at {tit} K: ṁ {result.MassFlowKgPerS:F4} kg/s, η {result.Efficiency:P1}, "
            + $"T₄ {result.OutletTemperatureK:F1} K ({drop:F1} K drop), {result.PowerW / 1000:F2} kW.");
    }

    [Fact]
    public void A_turbine_at_unity_expansion_ratio_passes_no_flow_and_makes_no_power()
    {
        // The closure below the measured range has to reach zero at ER = 1 and
        // not cross it. A linear extension of the first two measured points
        // would go negative — a turbine pumping backwards — somewhere above 1.
        var map = SyntheticTurbo.Turbine();

        var atUnity = TurbineModel.Solve(map, 1.0, 80_000, 1000.0, 101.325);
        atUnity.MassFlowKgPerS.Should().BeApproximately(0.0, 1e-12);
        atUnity.PowerW.Should().BeApproximately(0.0, 1e-12);

        foreach (var er in new[] { 1.02, 1.05, 1.10, 1.15, 1.19 })
        {
            var point = TurbineModel.Solve(map, er, 80_000, 1000.0, 101.325);
            point.MassFlowKgPerS.Should().BeGreaterThan(0.0, $"ER {er} must pass some flow");
            point.PowerW.Should().BeGreaterThan(0.0);
            point.IsExtrapolated.Should().BeTrue($"ER {er} is below the measured range and must say so");
        }
    }

    [Fact]
    public void Turbine_flow_rises_monotonically_with_expansion_ratio()
    {
        var map = SyntheticTurbo.Turbine();
        var previous = -1.0;

        for (var er = 1.0; er <= 4.0; er += 0.05)
        {
            var flow = TurbineModel.Solve(map, er, 70_000, 1050.0, 101.325).MassFlowKgPerS;
            flow.Should().BeGreaterThanOrEqualTo(previous, $"flow must not fall as ER passes {er:F2}");
            previous = flow;
        }
    }

    // ---- Shaft balance ----------------------------------------------------

    [Fact]
    public void Gate_the_matched_point_balances_the_shaft()
    {
        var turbo = SyntheticTurbo.Turbo();
        var match = ShaftBalance.Match(
            turbo,
            airMassFlowKgPerS: 0.18,
            exhaustMassFlowKgPerS: 0.190,
            compressorInletK: 298.15,
            compressorInletKPa: 101.325,
            turbineInletK: 1100.0,
            turbineOutletKPa: 101.325);

        match.Converged.Should().BeTrue();

        var supplied = match.Turbine.PowerW * turbo.MechanicalEfficiency;
        var absorbed = match.Compressor.PowerW + match.FrictionPowerW;

        // The residual is bounded by the bisection's own speed tolerance
        // (10 rpm out of ~130 000), which is what the assertion is really
        // measuring — not a physical imbalance.
        var residual = Math.Abs(supplied - absorbed) / absorbed;
        residual.Should().BeLessThan(1e-3);

        output.WriteLine(
            $"Balanced at {match.ShaftRpm:N0} rpm: turbine {match.Turbine.PowerW / 1000:F2} kW × "
            + $"{turbo.MechanicalEfficiency:P0} = {supplied / 1000:F2} kW against compressor "
            + $"{match.Compressor.PowerW / 1000:F2} kW + bearings {match.FrictionPowerW / 1000:F2} kW. "
            + $"PR {match.Compressor.PressureRatio:F2}, ER {match.ExpansionRatio:F2}.");
    }

    [Fact]
    public void The_turbine_swallows_exactly_the_flow_the_engine_gives_it()
    {
        // The other half of the coupling: the expansion ratio is not chosen,
        // it is whatever passes the engine's exhaust flow.
        var turbo = SyntheticTurbo.Turbo();
        const double exhaust = 0.190;

        var match = ShaftBalance.Match(
            turbo, 0.18, exhaust, 298.15, 101.325, 1100.0, 101.325);

        match.Turbine.MassFlowKgPerS.Should().BeApproximately(exhaust, exhaust * 1e-4);
    }

    [Fact]
    public void More_exhaust_energy_spins_the_shaft_faster_and_makes_more_boost()
    {
        var turbo = SyntheticTurbo.Turbo();

        var cool = ShaftBalance.Match(turbo, 0.18, 0.190, 298.15, 101.325, 950.0, 101.325);
        var hot = ShaftBalance.Match(turbo, 0.18, 0.190, 298.15, 101.325, 1150.0, 101.325);

        hot.ShaftRpm.Should().BeGreaterThan(cool.ShaftRpm);
        hot.Compressor.PressureRatio.Should().BeGreaterThan(cool.Compressor.PressureRatio);

        output.WriteLine(
            $"950 K: {cool.ShaftRpm:N0} rpm, PR {cool.Compressor.PressureRatio:F2}.  "
            + $"1150 K: {hot.ShaftRpm:N0} rpm, PR {hot.Compressor.PressureRatio:F2}.");
    }

    // ---- A plausible operating line ---------------------------------------

    /// <summary>
    /// A 2.0-litre four-cylinder at 90% volumetric efficiency on a wide-open
    /// throttle sweep — the demand curve a solved engine would hand the matcher.
    /// Air flow is quoted at the boost the engine actually reaches, so this is
    /// one iteration of the engine/turbo loop rather than the converged answer;
    /// Phase 13 closes that loop against the cycle simulation.
    /// </summary>
    private static IReadOnlyList<BoostDemandPoint> TwoLitreDemand() =>
    [
        new(2000, 0.062, 0.0662, 900.0, 1.4),
        new(2500, 0.083, 0.0886, 950.0, 1.6),
        new(3000, 0.105, 0.1121, 1000.0, 1.8),
        new(3500, 0.124, 0.1324, 1040.0, 1.9),
        new(4000, 0.142, 0.1516, 1070.0, 2.0),
        new(4500, 0.158, 0.1687, 1090.0, 2.0),
        new(5000, 0.172, 0.1836, 1100.0, 2.0),
        new(5500, 0.184, 0.1964, 1110.0, 2.0),
        new(6000, 0.193, 0.2060, 1120.0, 2.0),
        new(6500, 0.198, 0.2114, 1130.0, 2.0),
    ];

    [Fact]
    public void Gate_a_known_turbo_and_engine_pair_produce_a_plausible_operating_line()
    {
        var turbo = SyntheticTurbo.Turbo();
        var demand = TwoLitreDemand();

        var line = ShaftBalance.OperatingLine(
            turbo,
            demand.Select(d => (d.EngineRpm, d.AirFlowKgPerS, d.ExhaustFlowKgPerS, d.TurbineInletK)).ToList());

        output.WriteLine("  rpm   air kg/s   shaft rpm     PR      ER   back    η_c   surge%  choke%");
        foreach (var (rpm, m) in line)
        {
            output.WriteLine(
                $"{rpm,5:N0}  {demand.First(d => d.EngineRpm == rpm).AirFlowKgPerS,8:F4}  "
                + $"{m.ShaftRpm,9:N0}  {m.Compressor.PressureRatio,5:F2}  {m.ExpansionRatio,6:F2}  "
                + $"{m.BackPressureRatio,5:F2}  {m.Compressor.Efficiency,5:P0}  "
                + $"{m.Compressor.SurgeMarginPercent,6:F1}  {m.Compressor.ChokeMarginPercent,6:F1}");
        }

        line.Should().AllSatisfy(p => p.Point.Converged.Should().BeTrue());

        // Plausibility, clause by clause. Each of these is a way a matching
        // calculation goes wrong while still returning numbers.
        foreach (var (rpm, m) in line)
        {
            m.Compressor.PressureRatio.Should().BeInRange(1.05, 3.4, $"at {rpm:N0} rpm");
            m.ShaftRpm.Should().BeInRange(20_000, 175_000, $"at {rpm:N0} rpm");
            m.Compressor.Efficiency.Should().BeInRange(0.55, 0.82, $"at {rpm:N0} rpm");
            m.Compressor.OutletTemperatureK.Should().BeInRange(300, 520, $"at {rpm:N0} rpm");
            m.ExpansionRatio.Should().BeGreaterThan(1.0, $"at {rpm:N0} rpm");

            var tit = demand.First(d => d.EngineRpm == rpm).TurbineInletK;
            m.Turbine.OutletTemperatureK.Should().BeLessThan(tit, $"at {rpm:N0} rpm the turbine must cool the gas");
        }

        // Boost rises with engine speed and then plateaus — the shape of a
        // wastegate-free match. A line that fell with speed would mean the
        // turbine was running out of capacity, which this one should not.
        line[0].Point.Compressor.PressureRatio.Should()
            .BeLessThan(line[^1].Point.Compressor.PressureRatio);

        for (var i = 1; i < line.Count; i++)
        {
            line[i].Point.ShaftRpm.Should().BeGreaterThan(
                line[i - 1].Point.ShaftRpm,
                $"the shaft must keep accelerating from {line[i - 1].EngineRpm:N0} to {line[i].EngineRpm:N0} rpm");
        }

        // Back-pressure below boost across the range. Above 1 the engine is
        // pumping uphill; a turbo that only makes boost by strangling the
        // exhaust is a bad match however good the boost figure looks.
        line.Should().AllSatisfy(p =>
            p.Point.BackPressureRatio.Should().BeInRange(0.4, 1.6));
    }

    [Fact]
    public void Surge_and_choke_margins_are_signed_and_meaningful()
    {
        var map = SyntheticTurbo.Compressor();

        // On the surge line itself the margin is zero; left of it, negative.
        var atSurge = SyntheticTurbo.SurgeFlow(1.0);
        var onLine = CompressorModel.Solve(map, atSurge, 150_000, 298.15, 100.0);
        onLine.SurgeMarginPercent.Should().BeApproximately(0.0, 0.5);

        var leftOfSurge = CompressorModel.Solve(map, atSurge * 0.85, 150_000, 298.15, 100.0);
        leftOfSurge.SurgeMarginPercent.Should().BeNegative();
        leftOfSurge.InSurge.Should().BeTrue();
        leftOfSurge.Region.Should().Be(MapRegion.BeyondSurge);

        var atChoke = SyntheticTurbo.ChokeFlow(1.0);
        var onChoke = CompressorModel.Solve(map, atChoke, 150_000, 298.15, 100.0);
        onChoke.ChokeMarginPercent.Should().BeApproximately(0.0, 0.5);

        var pastChoke = CompressorModel.Solve(map, atChoke * 1.10, 150_000, 298.15, 100.0);
        pastChoke.ChokeMarginPercent.Should().BeNegative();
        pastChoke.Region.Should().Be(MapRegion.BeyondChoke);

        output.WriteLine(
            $"Surge line at {atSurge:F3} kg/s, margin {onLine.SurgeMarginPercent:F2}%; "
            + $"15% left of it, {leftOfSurge.SurgeMarginPercent:F1}%.");
    }

    [Fact]
    public void An_extrapolated_reading_says_so()
    {
        // Plan §4.2: extrapolated regions are shaded, so the model has to know
        // which points came from outside the measured data. Anything that
        // reports an extrapolation as measured makes the shading a lie.
        var map = SyntheticTurbo.Compressor();

        CompressorModel.Solve(map, 0.20, 130_000, 298.15, 101.325)
            .IsExtrapolated.Should().BeFalse();

        CompressorModel.Solve(map, 0.20, 40_000, 298.15, 101.325)
            .IsExtrapolated.Should().BeTrue("40 000 rpm is below the lowest measured speed line");

        CompressorModel.Solve(map, 0.20, 200_000, 298.15, 101.325)
            .IsExtrapolated.Should().BeTrue("200 000 rpm is above the highest");
    }

    // ---- Database and auto-match ------------------------------------------

    [Fact]
    public void The_library_refuses_an_entry_with_no_provenance()
    {
        var db = new TurboDatabase();

        var act = () => db.Add(new TurboEntry
        {
            Turbo = SyntheticTurbo.Turbo(),
            Source = "",
            Licence = "CC0",
        });

        act.Should().Throw<InvalidDataException>().WithMessage("*where its data came from*");
    }

    [Fact]
    public void The_library_round_trips_and_ranks_candidates_with_their_trade_offs()
    {
        var db = new TurboDatabase();
        db.Add(SyntheticTurbo.Entry());

        // A deliberately oversized unit: every flow scaled up 60%, so it makes
        // the same boost far later and sits closer to surge low down.
        db.Add(Scaled("Synthetic oversized", 1.60));

        // And an undersized one, which will run out of compressor.
        db.Add(Scaled("Synthetic undersized", 0.70));

        var reloaded = TurboDatabase.Load(db.Save());
        reloaded.Entries.Should().HaveCount(3);

        var ranked = reloaded.Rank(TwoLitreDemand());

        output.WriteLine("candidate                 score   η̄     worst surge  back-p  onset rpm  extrap");
        foreach (var c in ranked)
        {
            output.WriteLine(
                $"{c.Entry.Turbo.Name,-24} {c.Score,6:F1}  {c.MeanEfficiency,5:P0}  "
                + $"{c.WorstSurgeMargin,10:F1}%  {c.WorstBackPressureRatio,6:F2}  "
                + $"{(double.IsNaN(c.BoostOnsetRpm) ? "never" : c.BoostOnsetRpm.ToString("N0")),9}  "
                + $"{c.ExtrapolatedPoints,6}");
            foreach (var d in c.Disqualifications)
            {
                output.WriteLine($"    ✗ {d}");
            }
        }

        // The ranking must be an ordering with reasons attached, not a verdict:
        // every candidate carries its own margins so the user can disagree.
        ranked.Should().HaveCount(3);
        ranked[0].Entry.Turbo.Name.Should().Be("Synthetic 60 mm unit",
            "the unit sized for this engine should rank above one 60% too big or 30% too small");

        // Viable candidates first, then scores descending inside each group.
        // (Comparing name/score projections rather than the candidates
        // themselves: a MatchCandidate carries its whole operating line, and a
        // failure message that dumps thirty MatchPoints is unreadable.)
        ranked.Select(c => c.Viable).Should().BeInDescendingOrder();

        foreach (var group in ranked.GroupBy(c => c.Viable))
        {
            group.Select(c => c.Score).Should().BeInDescendingOrder();
        }

        // Every candidate is evaluated at every demand point, viable or not —
        // a disqualified turbo still has to show its operating line, because
        // "it surges below 3000 rpm" is a trade-off the user may accept.
        ranked.Select(c => c.OperatingLine.Count).Should().AllBeEquivalentTo(10);
        ranked.Where(c => !c.Viable).Should().AllSatisfy(
            c => c.Disqualifications.Should().NotBeEmpty());
    }

    /// <summary>A geometrically scaled copy of the synthetic unit: same shape, different size.</summary>
    private static TurboEntry Scaled(string name, double flowScale)
    {
        var baseline = SyntheticTurbo.Turbo();

        var compressor = new CompressorMap
        {
            Name = name,
            Reference = baseline.Compressor.Reference,
            MaxSpeedRpm = baseline.Compressor.MaxSpeedRpm / Math.Sqrt(flowScale),
            Provenance = "Analytic test map — scaled from SyntheticTurbo.",
            SpeedLines = baseline.Compressor.SpeedLines.Select(l => new CompressorSpeedLine(
                l.CorrectedRpm / Math.Sqrt(flowScale),
                l.Points.Select(p => p with { CorrectedFlowKgPerS = p.CorrectedFlowKgPerS * flowScale })
                    .ToList())).ToList(),
        };

        var turbine = new TurbineMap
        {
            Name = name,
            Reference = baseline.Turbine.Reference,
            AreaRatio = baseline.Turbine.AreaRatio * flowScale,
            Provenance = "Analytic test map — scaled from SyntheticTurbo.",
            SpeedLines = baseline.Turbine.SpeedLines.Select(l => new TurbineSpeedLine(
                l.CorrectedRpm / Math.Sqrt(flowScale),
                l.Points.Select(p => p with { CorrectedFlowKgPerS = p.CorrectedFlowKgPerS * flowScale })
                    .ToList())).ToList(),
        };

        return new TurboEntry
        {
            Turbo = new Turbocharger
            {
                Name = name,
                Compressor = compressor,
                Turbine = turbine,
                ShaftInertia = baseline.ShaftInertia * Math.Pow(flowScale, 2.5),
                MechanicalEfficiency = baseline.MechanicalEfficiency,
                MaxTurbineInletK = baseline.MaxTurbineInletK,
                Provenance = "Analytic test unit — not a product.",
            },
            Source = "Generated analytically by SyntheticTurbo",
            Licence = "Part of the WaveBench test suite",
        };
    }
}
