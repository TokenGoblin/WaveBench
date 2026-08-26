using FluentAssertions;
using WaveBench.Acoustics.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 11 verification: IEC 61672 weighting against the standard's
/// published table, the FSAE test-speed calculation for known strokes, and
/// the uncertainty band on every compliance result.
/// </summary>
public class MetricsAndComplianceTests(ITestOutputHelper output)
{
    /// <summary>
    /// IEC 61672-1 Table 3: the published A- and C-weightings, dB.
    ///
    /// The table is tabulated at the EXACT base-ten third-octave frequencies
    /// f = 10^(n/10), not at their rounded nominal labels — the nominal "16
    /// Hz" band is actually 15.849 Hz, and on the steep part of the A-curve
    /// that difference is worth several tenths of a dB. Testing against the
    /// labels instead of the exact frequencies is the classic way to
    /// "discover" a weighting bug that is not there; both are given here.
    /// Tolerance 0.1 dB — the table's own precision.
    /// </summary>
    public static TheoryData<double, double, double, double> Iec61672Table => new()
    {
        //  nominal,  exact Hz,     A dB,   C dB
        { 10, 10.0, -70.4, -14.3 },
        { 12.5, 12.589, -63.4, -11.2 },
        { 16, 15.849, -56.7, -8.5 },
        { 20, 19.953, -50.5, -6.2 },
        { 25, 25.119, -44.7, -4.4 },
        { 31.5, 31.623, -39.4, -3.0 },
        { 40, 39.811, -34.6, -2.0 },
        { 50, 50.119, -30.2, -1.3 },
        { 63, 63.096, -26.2, -0.8 },
        { 80, 79.433, -22.5, -0.5 },
        { 100, 100.0, -19.1, -0.3 },
        { 125, 125.89, -16.1, -0.2 },
        { 160, 158.49, -13.4, -0.1 },
        { 200, 199.53, -10.9, 0.0 },
        { 250, 251.19, -8.6, 0.0 },
        { 315, 316.23, -6.6, 0.0 },
        { 400, 398.11, -4.8, 0.0 },
        { 500, 501.19, -3.2, 0.0 },
        { 630, 630.96, -1.9, 0.0 },
        { 800, 794.33, -0.8, 0.0 },
        { 1000, 1000.0, 0.0, 0.0 },
        { 1250, 1258.9, 0.6, 0.0 },
        { 1600, 1584.9, 1.0, -0.1 },
        { 2000, 1995.3, 1.2, -0.2 },
        { 2500, 2511.9, 1.3, -0.3 },
        { 3150, 3162.3, 1.2, -0.5 },
        { 4000, 3981.1, 1.0, -0.8 },
        { 5000, 5011.9, 0.5, -1.3 },
        { 6300, 6309.6, -0.1, -2.0 },
        { 8000, 7943.3, -1.1, -3.0 },
        { 10000, 10000.0, -2.5, -4.4 },
        { 12500, 12589.0, -4.3, -6.2 },
        { 16000, 15849.0, -6.6, -8.5 },
        { 20000, 19953.0, -9.3, -11.2 },
    };

    [Theory]
    [MemberData(nameof(Iec61672Table))]
    public void Gate_weighting_matches_the_published_iec_61672_table(
        double nominal, double exact, double aDb, double cDb)
    {
        FrequencyWeighting.Decibels(exact, Weighting.A).Should().BeApproximately(aDb, 0.1,
            $"A-weighting at the nominal {nominal} Hz band (exact {exact} Hz), IEC 61672-1 Table 3");
        FrequencyWeighting.Decibels(exact, Weighting.C).Should().BeApproximately(cDb, 0.1,
            $"C-weighting at the nominal {nominal} Hz band (exact {exact} Hz), IEC 61672-1 Table 3");
    }

    [Fact]
    public void Weighting_is_exactly_zero_at_one_kilohertz()
    {
        FrequencyWeighting.Decibels(1000.0, Weighting.A).Should().BeApproximately(0.0, 1e-12);
        FrequencyWeighting.Decibels(1000.0, Weighting.C).Should().BeApproximately(0.0, 1e-12);
        FrequencyWeighting.Decibels(1000.0, Weighting.Z).Should().Be(0.0);
    }

    [Fact]
    public void Time_weighting_constants_are_the_standard_values()
    {
        FrequencyWeighting.TimeConstant(TimeWeighting.Fast).Should().Be(0.125);
        FrequencyWeighting.TimeConstant(TimeWeighting.Slow).Should().Be(1.0);
        FrequencyWeighting.TimeConstant(TimeWeighting.Impulse).Should().Be(0.035);
    }

    [Fact]
    public void Level_metering_recovers_a_calibrated_tone()
    {
        // A 1 kHz tone at 1 Pa amplitude is 90.97 dB SPL, and 1 kHz is the
        // unity point of every weighting, so A, C and Z must agree there.
        const double rate = 48_000.0;
        var signal = new double[48_000];
        for (var i = 0; i < signal.Length; i++)
        {
            signal[i] = Math.Sin(2 * Math.PI * 1000.0 * i / rate);
        }

        var expected = SoundLevelMeter.ToneLevel(1.0);
        expected.Should().BeApproximately(90.97, 0.01);

        foreach (var weighting in new[] { Weighting.Z, Weighting.A, Weighting.C })
        {
            SoundLevelMeter.EquivalentLevel(signal, rate, weighting).Should().BeApproximately(expected, 0.1,
                $"{weighting} is unity at 1 kHz");
        }

        // At 100 Hz the weightings must separate by the published amounts.
        var low = new double[48_000];
        for (var i = 0; i < low.Length; i++)
        {
            low[i] = Math.Sin(2 * Math.PI * 100.0 * i / rate);
        }

        var z = SoundLevelMeter.EquivalentLevel(low, rate, Weighting.Z);
        (SoundLevelMeter.EquivalentLevel(low, rate, Weighting.A) - z).Should().BeApproximately(-19.1, 0.3);
        (SoundLevelMeter.EquivalentLevel(low, rate, Weighting.C) - z).Should().BeApproximately(-0.3, 0.3);
    }

    [Fact]
    public void Fast_time_weighting_tracks_a_burst_more_closely_than_slow()
    {
        // A 200 ms burst: Fast (125 ms) reaches much closer to the steady
        // level than Slow (1 s) — the reason rules specify one or the other.
        const double rate = 48_000.0;
        var signal = new double[(int)(2.0 * rate)];
        for (var i = 0; i < signal.Length; i++)
        {
            var t = i / rate;
            var envelope = t is >= 0.9 and <= 1.1 ? 1.0 : 0.0;
            signal[i] = envelope * Math.Sin(2 * Math.PI * 1000.0 * i / rate);
        }

        var fast = SoundLevelMeter.MaximumTimeWeightedLevel(signal, rate, Weighting.C, TimeWeighting.Fast);
        var slow = SoundLevelMeter.MaximumTimeWeightedLevel(signal, rate, Weighting.C, TimeWeighting.Slow);
        var steady = SoundLevelMeter.ToneLevel(1.0);
        output.WriteLine($"200 ms burst: Fast {fast:F1} dB, Slow {slow:F1} dB (steady level {steady:F1} dB)");

        // Both detectors must report a real level — a Slow measurement on a
        // clip shorter than 3τ must not silently return −∞.
        double.IsFinite(fast).Should().BeTrue();
        double.IsFinite(slow).Should().BeTrue();

        fast.Should().BeGreaterThan(slow + 2.0, "the faster detector gets nearer the true peak level");
        fast.Should().BeLessThan(steady + 0.1, "and neither can exceed the steady level");
        slow.Should().BeLessThan(fast);
    }

    [Theory]
    // N_test = 15.25 × 30000 / stroke_mm, rounded to the nearest 500 rpm.
    [InlineData(60.0, 7500.0)]   // 7625 → 7500
    [InlineData(54.5, 8500.0)]   // 8394 → 8500
    [InlineData(76.4, 6000.0)]   // 5988 → 6000
    [InlineData(96.0, 5000.0)]   // 4766 → 5000
    [InlineData(45.8, 10000.0)]  // 9989 → 10000
    [InlineData(88.4, 5000.0)]   // 5175 → 5000
    public void Gate_fsae_test_speed_is_correct_for_known_strokes(double strokeMm, double expectedRpm)
    {
        var rules = NoiseRuleSet.FormulaSae2024;
        var raw = 15.25 * 30_000.0 / strokeMm;
        rules.TestSpeedRpm(strokeMm).Should().Be(expectedRpm,
            $"stroke {strokeMm} mm → raw {raw:F0} rpm, rounded to the nearest 500");
    }

    [Fact]
    public void Gate_compliance_results_carry_an_explicit_uncertainty_band()
    {
        var rules = NoiseRuleSet.FormulaSae2024;

        // A design comfortably inside, one comfortably outside, one on the line.
        var results = ComplianceCheck.Evaluate(
            rules, strokeMm: 54.5, idleRpm: 3000.0,
            levelAt: (rpm, point) => point.Name == "Idle" ? 96.0 : 104.0);

        results.Should().HaveCount(2);
        foreach (var r in results)
        {
            r.UncertaintyDb.Should().Be(ComplianceCheck.DefaultUncertaintyDb,
                "gate: every compliance result carries the ±3 dB band (plan §3.8)");
            output.WriteLine(r.Describe());
        }

        results[1].TestSpeedRpm.Should().Be(8500.0, "the high-speed point uses the derived test speed");
        results.Should().OnlyContain(r => r.Verdict == ComplianceVerdict.Pass);

        // On the line: nominally a pass, but honestly too close to call.
        var marginal = ComplianceCheck.Evaluate(
            rules, 54.5, 3000.0, (_, point) => point.LimitDb - 1.0);
        marginal.Should().OnlyContain(r => r.PassesNominally);
        marginal.Should().OnlyContain(r => r.Verdict == ComplianceVerdict.TooCloseToCall,
            "1 dB of margin is inside the ±3 dB band — the plan forbids sounding confident here");

        // Clearly over.
        var over = ComplianceCheck.Evaluate(rules, 54.5, 3000.0, (_, point) => point.LimitDb + 6.0);
        over.Should().OnlyContain(r => r.Verdict == ComplianceVerdict.Fail);
        ComplianceCheck.Governing(over).MarginDb.Should().Be(-6.0);

        // Broadband content is explicitly worse (plan §3.8).
        ComplianceCheck.BroadbandUncertaintyDb.Should().BeGreaterThan(ComplianceCheck.DefaultUncertaintyDb);
    }

    [Fact]
    public void Rules_are_versioned_data_and_round_trip_as_json()
    {
        var rules = NoiseRuleSet.FormulaSae2024;
        rules.Year.Should().Be(2024, "every report records the rules year (plan §3.8)");
        rules.Source.Should().Contain("VERIFY", "the shipped numbers are a starting point, not an authority");
        rules.MicrophoneDistanceM.Should().Be(0.5);
        rules.MicrophoneAngleDeg.Should().Be(45.0);
        rules.Points.Single(p => p.Name == "Idle").LimitDb.Should().Be(103.0);
        rules.Points.Single(p => p.Name == "Test speed").LimitDb.Should().Be(110.0);
        rules.Points.Should().OnlyContain(p => p.Weighting == "C" && p.TimeWeighting == "Fast");

        // Editable as data, not code.
        var json = rules.Save();
        var reloaded = NoiseRuleSet.Load(json);
        reloaded.TestSpeedRpm(54.5).Should().Be(8500.0);
        reloaded.Save().Should().Be(json, "rule sets round-trip stably");

        var amended = NoiseRuleSet.Load(json.Replace("110", "108"));
        amended.Points.Single(p => p.Name == "Test speed").LimitDb.Should().Be(108.0,
            "a rules change is a data edit, never a recompile");
    }

    [Fact]
    public void Character_metrics_separate_a_pure_tone_stack_from_a_rumbling_one()
    {
        const double rate = 48_000.0;
        const double rpm = 4000.0;
        var n = (int)(16 * 120.0 / rpm * rate);

        double[] Build(double halfOrderAmplitude)
        {
            var signal = new double[n];
            for (var i = 0; i < n; i++)
            {
                var rev = 2.0 * Math.PI * rpm / 60.0 * i / rate;
                double v = 0;
                for (var h = 1; h <= 5; h++)
                {
                    v += Math.Sin(h * 4.0 * rev) / h;
                }

                v += halfOrderAmplitude * Math.Sin(1.5 * rev) + halfOrderAmplitude * Math.Sin(2.5 * rev);
                signal[i] = v;
            }

            return signal;
        }

        var clean = CharacterAnalysis.Analyse(Build(0.0), rate, rpm, firingOrder: 4.0);
        var rumbly = CharacterAnalysis.Analyse(Build(0.7), rate, rpm, firingOrder: 4.0);

        output.WriteLine($"clean : OPI {clean.OrderPurityIndex:F3} half {clean.HalfOrderRatio:E2} " +
                         $"centroid {clean.SpectralCentroidHz:F0} Hz rumble {clean.RumbleIndex:F4}");
        output.WriteLine($"rumbly: OPI {rumbly.OrderPurityIndex:F3} half {rumbly.HalfOrderRatio:F3} " +
                         $"centroid {rumbly.SpectralCentroidHz:F0} Hz rumble {rumbly.RumbleIndex:F4}");

        clean.OrderPurityIndex.Should().BeGreaterThan(0.99);
        rumbly.OrderPurityIndex.Should().BeLessThan(clean.OrderPurityIndex);
        rumbly.HalfOrderRatio.Should().BeGreaterThan(100.0 * Math.Max(clean.HalfOrderRatio, 1e-12));
        rumbly.RumbleIndex.Should().BeGreaterThan(clean.RumbleIndex);
        clean.HarmonicDecaySlopeDbPerOrder.Should().BeLessThan(0.0, "a 1/h stack decays with order");

        // The target ranking must put the rumbly design nearer crossplane.
        var cleanTop = SoundTarget.Rank(clean)[0].Target.Name;
        var rumblyRank = SoundTarget.Rank(rumbly);
        var crossplaneRankForRumbly = rumblyRank.ToList().FindIndex(x => x.Target == SoundTarget.CrossplaneRumble);
        var crossplaneRankForClean = SoundTarget.Rank(clean).ToList()
            .FindIndex(x => x.Target == SoundTarget.CrossplaneRumble);
        output.WriteLine($"closest target — clean: {cleanTop}, rumbly: {rumblyRank[0].Target.Name}");
        crossplaneRankForRumbly.Should().BeLessThan(crossplaneRankForClean,
            "adding half-order rumble must move a design toward the crossplane target");
    }

    [Fact]
    public void Reference_match_tracks_rpm_from_the_firing_order_alone()
    {
        // A "recording" of a 4th-order engine at 3730 rpm, no tacho channel.
        const double rate = 48_000.0;
        const double trueRpm = 3730.0;
        var signal = new double[(int)(0.5 * rate)];
        for (var i = 0; i < signal.Length; i++)
        {
            var rev = 2.0 * Math.PI * trueRpm / 60.0 * i / rate;
            for (var h = 1; h <= 4; h++)
            {
                signal[i] += Math.Sin(h * 4.0 * rev + 0.3 * h) / h;
            }
        }

        var tracked = ReferenceMatch.TrackRpm(signal, rate, firingOrder: 4.0, minRpm: 2000, maxRpm: 6000);
        output.WriteLine($"tracked {tracked:F0} rpm (true {trueRpm:F0})");
        tracked.Should().BeApproximately(trueRpm, 20.0);

        var fingerprint = ReferenceMatch.Extract(signal, rate, tracked, firingOrder: 4.0);
        fingerprint.OrderPurityIndex.Should().BeGreaterThan(0.9, "the reference is a clean harmonic stack");
    }

    [Fact]
    public void Psychoacoustic_status_is_explicit_about_what_is_not_implemented()
    {
        // The plan requires honesty about gaps; this is the machine-readable
        // form of it, so the UI can surface it rather than implying coverage.
        PsychoacousticStatus.Implemented.Should().Contain(m => m.Standard.Contains("61672"));
        PsychoacousticStatus.Outstanding.Should().Contain(m => m.Standard.Contains("532-1"));
        PsychoacousticStatus.Outstanding.Should().Contain(m => m.Standard.Contains("45692"));
        PsychoacousticStatus.Outstanding.Should().Contain(m => m.Standard.Contains("ECMA-418-2"));
        PsychoacousticStatus.Outstanding.Should().OnlyContain(m => m.Note.Length > 20,
            "each gap states why, not just that");

        foreach (var m in PsychoacousticStatus.Outstanding)
        {
            output.WriteLine($"NOT IMPLEMENTED — {m.Metric} ({m.Standard}): {m.Note}");
        }
    }
}
