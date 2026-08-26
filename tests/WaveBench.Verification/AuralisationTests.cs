using FluentAssertions;
using WaveBench.Acoustics;
using WaveBench.Acoustics.Auralisation;
using WaveBench.Analysis;
using WaveBench.Core.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 10 gate: a 1500→7200 rpm sweep renders with no audible crossfade
/// artefacts; two renders with the same seed are bit-identical; A/B pairs
/// match within 0.5 LU; crossplane and flat-plane renders are distinguishable.
/// </summary>
public class AuralisationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Synthetic source bank standing in for a solved engine: a harmonic
    /// pulse train whose content varies with speed. The synthesiser cannot
    /// tell where a wavetable came from, so the artefact and determinism
    /// gates are exercised exactly as they would be on solver output — and
    /// the test stays fast enough to run per-PR.
    /// </summary>
    private static WavetableBank SyntheticBank(
        string name, double fromRpm, double toRpm, double stepRpm, double firingOrder,
        int samplesPerCycle = 1440)
    {
        var bank = new WavetableBank(name);
        for (var rpm = fromRpm; rpm <= toRpm + 1e-9; rpm += stepRpm)
        {
            var table = new float[samplesPerCycle];
            // Brightness rises with speed, as real blowdown does.
            var harmonics = 3 + (int)(6 * (rpm - fromRpm) / Math.Max(toRpm - fromRpm, 1.0));
            for (var i = 0; i < samplesPerCycle; i++)
            {
                // One 720° cycle spans 2π here, so order o = sin(2o·φ).
                var cycleAngle = 2.0 * Math.PI * i / samplesPerCycle;
                double v = 0;
                for (var h = 1; h <= harmonics; h++)
                {
                    v += Math.Sin(2.0 * h * firingOrder * cycleAngle) / h;
                }

                table[i] = (float)v;
            }

            bank.Add(new CrankWavetable(rpm, table));
        }

        return bank;
    }

    /// <summary>
    /// Wavetable bank built from the REAL Phase 9 collector-timing chain:
    /// firing angles → arrival phases → superposed blowdown pulses. This is
    /// the actual physics path an engine render takes, so the crossplane gate
    /// tests the chain rather than a stand-in.
    /// </summary>
    private static WavetableBank BankFromFiringOrder(
        string name, double[] firingAngles, double fromRpm, double toRpm, double stepRpm,
        int samplesPerCycle = 1440)
    {
        var bank = new WavetableBank(name);
        for (var rpm = fromRpm; rpm <= toRpm + 1e-9; rpm += stepRpm)
        {
            var timing = CollectorTiming.Analyze(
                firingAngles.Select((a, i) => new CollectorBranch(i + 1, a, 0.8, 550.0)).ToArray(), rpm);
            var cycle = CollectorPulseTrain.SynthesizeCycle(timing, samplesPerCycle: samplesPerCycle);
            var mean = cycle.Average();
            bank.Add(new CrankWavetable(rpm, cycle.Select(v => (float)(v - mean)).ToArray()));
        }

        return bank;
    }

    [Fact]
    public void Gate_sweep_renders_without_crossfade_artefacts()
    {
        // 1500 → 7200 rpm over 6 s, tables every 250 rpm (plan §3.6 default).
        var bank = SyntheticBank("exhaust", 1500, 7250, 250, firingOrder: 2.0);
        var profile = RpmProfile.Sweep(1500, 7200, 6.0);
        var stem = new WavetableSynthesizer(seed: 1).Render(bank, profile, 48_000.0, SynthesisVariation.None);

        stem.Samples.Length.Should().Be(288_000);
        stem.Samples.Should().OnlyContain(s => float.IsFinite(s));

        // A crossfade click is an isolated discontinuity: a first difference
        // far larger than the signal's own slope statistics. Compare the
        // largest jump to the RMS jump — a clean render keeps this ratio low,
        // while a time-domain crossfade between rpm points spikes it.
        var diffs = new double[stem.Samples.Length - 1];
        for (var i = 0; i < diffs.Length; i++)
        {
            diffs[i] = stem.Samples[i + 1] - stem.Samples[i];
        }

        var rmsDiff = Math.Sqrt(diffs.Sum(d => d * d) / diffs.Length);
        var maxDiff = diffs.Max(Math.Abs);
        var crestFactor = maxDiff / rmsDiff;
        output.WriteLine($"sweep derivative crest factor: {crestFactor:F2} (max {maxDiff:E3}, rms {rmsDiff:E3})");

        crestFactor.Should().BeLessThan(8.0,
            "gate: no isolated discontinuities — crank-angle-domain blending is phase-coherent");

        // And the render must actually sweep: the instantaneous frequency at
        // the end is far above the start.
        double DominantHz(int from, int length)
        {
            var window = stem.Samples.Skip(from).Take(length).Select(s => (double)s).ToArray();
            var spectrum = Fft.MagnitudeSpectrum(window, out var padded);
            var peak = Array.IndexOf(spectrum, spectrum.Skip(3).Max());
            return Fft.BinFrequency(peak, 48_000.0, padded);
        }

        var early = DominantHz(24_000, 16_384);
        var late = DominantHz(240_000, 16_384);
        output.WriteLine($"dominant tone: {early:F0} Hz early → {late:F0} Hz late");
        (late / early).Should().BeGreaterThan(2.5, "the pitch must track the rpm sweep");
    }

    [Fact]
    public void Gate_same_seed_renders_are_bit_identical()
    {
        var bank = SyntheticBank("exhaust", 2000, 6000, 500, firingOrder: 2.0);
        var profile = RpmProfile.PullAndDecel(2000, 6000, 1.5);
        var variation = new SynthesisVariation(AmplitudeCoV: 0.03, PhaseJitterDeg: 1.5);

        var a = new WavetableSynthesizer(seed: 4242).Render(bank, profile, 48_000.0, variation);
        var b = new WavetableSynthesizer(seed: 4242).Render(bank, profile, 48_000.0, variation);
        b.Samples.Should().Equal(a.Samples, "gate: same seed → bit-identical render (plan §3.6)");

        var c = new WavetableSynthesizer(seed: 4243).Render(bank, profile, 48_000.0, variation);
        c.Samples.Should().NotEqual(a.Samples, "a different seed must draw different cycles");

        // The variation must be audible-scale but not wild. RMS, not peak:
        // a small crank offset on a sharp-edged waveform gives a large
        // pointwise difference at the edge while barely changing the energy,
        // which is exactly how cycle-to-cycle scatter should behave.
        var diffRms = Math.Sqrt(a.Samples.Zip(c.Samples, (x, y) => (x - y) * (double)(x - y)).Sum() / a.Samples.Length);
        var signalRms = Math.Sqrt(a.Samples.Sum(s => (double)s * s) / a.Samples.Length);
        var relative = diffRms / signalRms;
        output.WriteLine($"seed-to-seed RMS difference: {relative:P1} of signal RMS");
        relative.Should().BeInRange(0.002, 0.5);

        // Burble is seeded too.
        var burbleA = StemMixer.OverrunBurble(profile, 48_000.0, seed: 9);
        var burbleB = StemMixer.OverrunBurble(profile, 48_000.0, seed: 9);
        burbleB.Samples.Should().Equal(burbleA.Samples);
        burbleA.Samples.Any(s => s != 0.0f).Should().BeTrue("the profile has a decel section");
    }

    [Fact]
    public void Gate_ab_pairs_match_within_half_a_loudness_unit()
    {
        var quiet = SyntheticBank("quiet", 3000, 5000, 500, firingOrder: 2.0);
        var loudBank = new WavetableBank("loud");
        foreach (var table in quiet.Tables)
        {
            var scaled = table.Samples.ToArray().Select(s => s * 4.0f).ToArray();
            loudBank.Add(new CrankWavetable(table.Rpm, scaled));
        }

        var profile = RpmProfile.Steady(4000, 3.0);
        var synth = new WavetableSynthesizer(seed: 7);
        var a = synth.Render(quiet, profile, 48_000.0, SynthesisVariation.None);
        var b = synth.Render(loudBank, profile, 48_000.0, SynthesisVariation.None);

        var (matchedA, matchedB, gainA, gainB, trueDifference) = Loudness.MatchPair(a, b, targetLufs: -23.0);
        var lufsA = Loudness.IntegratedLufs(matchedA.Samples, 48_000.0);
        var lufsB = Loudness.IntegratedLufs(matchedB.Samples, 48_000.0);

        output.WriteLine($"true difference {trueDifference:F2} LU; matched to {lufsA:F2} / {lufsB:F2} LUFS " +
                         $"(gains {gainA:F2} / {gainB:F2} dB)");

        Math.Abs(lufsA - lufsB).Should().BeLessThan(0.5, "gate: A/B pairs within 0.5 LU (plan §3.6)");
        Math.Abs(lufsA - (-23.0)).Should().BeLessThan(0.5, "and both hit the requested target");

        // The true level difference must remain reported, not hidden: 4× amplitude ≈ 12 dB.
        Math.Abs(trueDifference).Should().BeApproximately(12.0, 0.5);
    }

    [Fact]
    public void Gate_crossplane_and_flat_plane_renders_are_distinguishable()
    {
        // Identical geometry; the firing intervals of one V8 bank are the ONLY
        // difference, and the wavetables come from the real Phase 9 collector
        // timing chain (§6.2 #8, now carried all the way to audio).
        var flat = BankFromFiringOrder("flat-plane", [0.0, 180.0, 360.0, 540.0], 3000, 6000, 500);
        var cross = BankFromFiringOrder("crossplane", [0.0, 90.0, 270.0, 540.0], 3000, 6000, 500);

        var profile = RpmProfile.Steady(4500, 3.0);
        var synth = new WavetableSynthesizer(seed: 11);
        var (renderFlat, renderCross, _, _, _) = Loudness.MatchPair(
            synth.Render(flat, profile, 48_000.0, SynthesisVariation.None),
            synth.Render(cross, profile, 48_000.0, SynthesisVariation.None));

        // Judged the way a listener would be asked to judge: after level
        // matching, the rumble signature must still separate them.
        OrderSpectrum Spectrum(AudioStem stem) =>
            OrderAnalysis.AtConstantSpeed(
                stem.Samples.Select(s => (double)s).ToArray(), 48_000.0, 4500.0, maxOrder: 16.0);

        var flatHalf = CharacterMetrics.HalfOrderRatio(Spectrum(renderFlat));
        var crossHalf = CharacterMetrics.HalfOrderRatio(Spectrum(renderCross));
        output.WriteLine($"level-matched half-order ratio: flat {flatHalf:E2}, crossplane {crossHalf:F3}");

        crossHalf.Should().BeGreaterThan(0.05, "gate: the crossplane render carries audible half-order rumble");
        crossHalf.Should().BeGreaterThan(100.0 * Math.Max(flatHalf, 1e-9),
            "gate: and the flat-plane render does not — they are distinguishable");
    }

    [Fact]
    public void Wav_export_round_trips_and_writes_a_provenance_sidecar()
    {
        var bank = SyntheticBank("exhaust", 3000, 5000, 500, firingOrder: 2.0);
        var profile = RpmProfile.Sweep(3000, 5000, 1.0);
        var synth = new WavetableSynthesizer(seed: 3);
        var exhaust = synth.Render(bank, profile, 48_000.0, SynthesisVariation.None);
        var intake = synth.Render(SyntheticBank("intake", 3000, 5000, 500, 2.0), profile, 48_000.0, SynthesisVariation.None);
        var mix = StemMixer.Mix("mix", (exhaust, 1.0), (intake, 0.4));

        var dir = Path.Combine(Path.GetTempPath(), $"wavebench-audio-{Guid.NewGuid():N}");
        try
        {
            var metadata = new RenderMetadata
            {
                ModelName = "test",
                ModelHash = RenderMetadata.HashOf("{\"name\":\"test\"}"),
                RpmProfile = "3000→5000 rpm, 1 s",
                ListenerPreset = ListenerPreset.FsaeStatic.Name,
                Seed = 3,
                ResolvedBandwidthHz = 4800.0,
                IntegratedLufs = Loudness.IntegratedLufs(mix.Samples, 48_000.0),
            };

            var result = RenderExport.Write(dir, "render", mix, [exhaust, intake], metadata);

            File.Exists(result.MixPath).Should().BeTrue();
            result.StemPaths.Should().HaveCount(2).And.OnlyContain(p => File.Exists(p));
            result.PeakBeforeClip.Should().BeLessThanOrEqualTo(1.0, "export must not clip");

            // 24-bit round trip: quantisation is the only loss.
            var reread = WavWriter.Read(result.MixPath);
            reread.Samples.Length.Should().Be(mix.Samples.Length);
            var scale = mix.Samples.Max(s => Math.Abs(s)) / 0.9;
            var worst = mix.Samples.Zip(reread.Samples, (a, b) => Math.Abs(a / scale - b)).Max();
            worst.Should().BeLessThan(1e-5, "24-bit quantisation only");

            var sidecar = File.ReadAllText(result.MetadataPath);
            sidecar.Should().Contain("\"seed\": 3")
                .And.Contain("resolvedBandwidthHz")
                .And.Contain("modelHash")
                .And.Contain("not physically resolved", Exactly.Once());
            output.WriteLine(sidecar);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Hybrid_crossover_is_complementary_and_hands_the_high_band_to_the_tmm()
    {
        // The two branch weights must sum to one at every frequency, or the
        // crossover itself colours the render (plan §5.6).
        const double crossover = 1500.0;
        foreach (var f in new[] { 0.0, 100.0, 750.0, 1500.0, 3000.0, 12_000.0 })
        {
            var sum = HybridSynthesis.NonlinearWeight(f, crossover) + HybridSynthesis.TmmWeight(f, crossover);
            sum.Should().BeApproximately(1.0, 1e-12, $"complementary pair at {f} Hz");
        }

        HybridSynthesis.NonlinearWeight(0.0, crossover).Should().BeApproximately(1.0, 1e-12,
            "DC is entirely the nonlinear solution");
        HybridSynthesis.NonlinearWeight(crossover, crossover).Should().BeApproximately(0.5, 1e-12,
            "equal split at the crossover");
        HybridSynthesis.NonlinearWeight(20_000.0, crossover).Should().BeLessThan(0.01,
            "well above the mesh bandwidth the TMM carries the signal");

        // The crossover is capped by the measured mesh bandwidth: a coarse
        // performance mesh must not claim nonlinear authority to 1.5 kHz.
        HybridSynthesis.CrossoverFrequency(resolvedBandwidthHz: 800.0).Should().Be(800.0);
        HybridSynthesis.CrossoverFrequency(resolvedBandwidthHz: 5600.0).Should().Be(1500.0);

        // A unity TMM must leave the signal untouched (the identity case
        // proves the transform/weighting plumbing is lossless).
        var signal = new double[4096];
        for (var i = 0; i < signal.Length; i++)
        {
            signal[i] = Math.Sin(2 * Math.PI * 300.0 * i / 48_000.0)
                        + 0.5 * Math.Sin(2 * Math.PI * 4000.0 * i / 48_000.0);
        }

        var identity = HybridSynthesis.Combine(signal, _ => System.Numerics.Complex.One, 48_000.0, crossover);
        var worst = signal.Zip(identity, (a, b) => Math.Abs(a - b)).Max();
        worst.Should().BeLessThan(1e-9, "unity transfer ⇒ unchanged signal");

        // A TMM that kills the high band must remove the 4 kHz tone while
        // leaving the 300 Hz tone (below the crossover) essentially intact.
        var lowPassed = HybridSynthesis.Combine(
            signal, f => f > 2000.0 ? System.Numerics.Complex.Zero : System.Numerics.Complex.One,
            48_000.0, crossover);

        double Tone(double[] x, double hz)
        {
            var spectrum = Fft.MagnitudeSpectrum(x, out var padded);
            return spectrum[(int)Math.Round(hz * padded / 48_000.0)];
        }

        (Tone(lowPassed, 4000.0) / Tone(signal, 4000.0)).Should().BeLessThan(0.2,
            "the TMM branch dominates at 4 kHz and it attenuates there");
        (Tone(lowPassed, 300.0) / Tone(signal, 300.0)).Should().BeGreaterThan(0.9,
            "below the crossover the nonlinear branch is authoritative and unaffected");
    }

    [Fact]
    public void Drive_by_produces_a_doppler_shift_from_the_changing_delay()
    {
        // A steady tone passing at 20 m/s must fall in pitch through the pass
        // — the shift emerges from the propagation delay, never a pitch knob.
        var bank = SyntheticBank("tone", 4000, 4000, 500, firingOrder: 2.0);
        var profile = RpmProfile.Steady(4000, 6.0);
        var source = new WavetableSynthesizer(seed: 5).Render(bank, profile, 48_000.0, SynthesisVariation.None);

        var pass = StemMixer.DriveBy(source, speedMetresPerSecond: 20.0,
            closestApproachMetres: 7.5, startDistanceMetres: 60.0);

        double DominantHz(int from, int length)
        {
            var window = pass.Samples.Skip(from).Take(length).Select(s => (double)s).ToArray();
            var spectrum = Fft.MagnitudeSpectrum(window, out var padded);
            var peak = Array.IndexOf(spectrum, spectrum.Skip(3).Max());
            return Fft.BinFrequency(peak, 48_000.0, padded);
        }

        var approaching = DominantHz(24_000, 32_768);
        var receding = DominantHz(200_000, 32_768);
        output.WriteLine($"drive-by: {approaching:F1} Hz approaching → {receding:F1} Hz receding");

        approaching.Should().BeGreaterThan(receding, "approaching is sharp, receding is flat");
        var ratio = approaching / receding;
        var expected = (1.0 + 20.0 / 343.2) / (1.0 - 20.0 / 343.2);
        ratio.Should().BeApproximately(expected, expected * 0.03, "classical Doppler ratio");

        // Level peaks at closest approach, not at the ends.
        var mid = pass.Samples.Skip(140_000).Take(20_000).Max(Math.Abs);
        var start = pass.Samples.Skip(20_000).Take(20_000).Max(Math.Abs);
        mid.Should().BeGreaterThan(start, "1/r spreading peaks at the pass");
    }
}
