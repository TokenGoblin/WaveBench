# WaveBench acoustics documentation

Source of truth: the master plan, Part 3. Records what is implemented, with
sources and measured verification results. Grows with the phases, in the
same commit as the code.

## 1. Linear acoustics engine — TMM (Phase 8)

Frequency-domain transfer-matrix method (plan §3.3) in the [p, U] (pressure,
volume velocity) convention, so area enters through each element's
characteristic impedance ρc/S and junctions are natural. Complex arithmetic
via `System.Numerics.Complex`; the medium state (a, ρ, T, γ, Pr) comes from
the same gas state the nonlinear solver uses — never a fixed 343 m/s.

### Elements (`WaveBench.Acoustics`)

- **Uniform duct** with mean flow and damping:
  T = [[cos k_cL, jZ·sin k_cL], [j/Z·sin k_cL, cos k_cL]]·e^(−jM·k_cL),
  k_c = k/(1−M²), complex k = ω/c − jα with the classical Kirchhoff
  wide-tube visco-thermal attenuation α = (1/rc)·√(νω/2)·(1+(γ−1)/√Pr)
  (Pierce, *Acoustics* §10-5).
- **Area discontinuity**: continuity of p and U plus the series inertial end
  correction L_a = ρδ/S_small, δ ≈ 0.6·r_small·(1 − S_small/S_large).
- **Quarter-wave stub** (shunt): Z = −j(ρc/S)·cot(k·L_eff), end-corrected,
  damped.
- **Helmholtz resonator** (shunt): Z = R + j(ωM_a − 1/ωC_a) with
  M_a = ρL_eff/S, C_a = V/(ρc²), neck visco-thermal R.
- **Levine–Schwinger radiation** (§3.5): unflanged Z = (ρc/S)[(ka)²/4 +
  j·0.6133·ka], flanged [(ka)²/2 + j·0.8216·ka]; low-ka forms valid to
  ka ≈ 1.5 (directivity takes over above — Phase 9).
- **Terminations**: anechoic, unflanged/flanged open, rigid,
  pressure-release.

`AcousticNetwork` chains elements and produces transmission loss (four-pole
formula with port impedances), insertion loss vs a reference chain, input
impedance (the quantity that also governs scavenging — plan §3.3), and the
pressure transfer function. Conical/tapered geometry is handled as segmented
ducts (≥ 20 segments per wavelength, plan §3.3); perforated and dissipative
elements arrive with the silencer work.

### Verification results (§6.1)

| Test | Tolerance | Result |
|---|---|---|
| Expansion chamber TL vs 10·log₁₀[1+¼(m−1/m)²sin²kL] | 0.1 dB | pass, 50–2000 Hz |
| Quarter-wave stub modes (2n−1)c/4L | 1% | pass (n = 1, 2) |
| Helmholtz resonance (c/2π)√(S/VL_eff) | 2% | pass, TL > 20 dB at f₀ |
| Convective shift f_n = n·c(1−M²)/2L at M = 0.2 | 0.5% | pass |
| Levine–Schwinger end correction (open-pipe resonance shift) | published curve | pass; R ∝ (ka)² exact |
| Reciprocity (reversed chain) | 1e-9 | pass |
| 20-element network, 512 frequencies 1–10 kHz | < 10 ms | **4.3 ms** |

### TMM vs the nonlinear solver (the §6.1 headline check)

The same smooth-taper expansion chamber (d40 → d80 × 0.2 m, 40 mm
smoothstep transitions) solved both ways: nonlinear FV pulse transmission
(straight-duct reference run cancels every shared numerical artefact) versus
the segmented-duct TMM. **Worst deviation 200–2500 Hz: 0.45 dB at the
2.5 mm production mesh (0.24 dB at 1.25 mm)** — inside the 1 dB gate with
margin. At large amplitude the divergence between the methods *is* the
nonlinearity, and the UI must present it as such.

## 2. Pulse timing, order analysis, propagation (Phase 9)

### Collector timing (§3.2) — the header-sound core

`CollectorTiming.Analyze`: arrival phase per primary φ = θ_fire + 6·N·τ,
τ = L/(ā+ū) with the ACTUAL mean sound speed per primary. Verified exactly
against hand calculation: equal lengths give exactly 720/m spacing at every
rpm; a 0.1 m mismatch at a = 500 m/s gives exactly 7.2° error at 6000 rpm
and 3.6° at 3000 (the linear-in-rpm growth of §3.2); a 40 K wall-temperature
split alone produces the predicted timing error on geometrically equal
primaries. `ScrollSeparation` implements the §4.6.2 pairing index from
firing order and valve events: I4 1-3-4-2 with 1&4/2&3 scores exactly zero,
wrong pairing scores large.

### The composite-spectrum machine and the gate result

`CollectorPulseTrain` superposes per-cylinder pulses at the computed
arrivals (§3.2's one equation). Crossplane-vs-flat-plane V8 bank, identical
geometry, firing intervals the only difference: **flat-plane half-order
ratio 2.5e-30 (machine-exact cancellation), crossplane 3.39 with OPI 0.23
vs 1.0000** — the binary, unmistakable §6.2 #8 signature, and the 180°
crossover restores the flat fingerprint (#9).

### Order analysis and character metrics

`OrderAnalysis`: crank-synchronous order tracking — integer-cycle windows
make every half-order exactly periodic, so single-bin projection recovers
levels without leakage; varying speed handled by angle-domain resampling.
Gate: known orders recovered **within 0.2 dB**, constant speed and through
a 3000→3600 rpm sweep. `CharacterMetrics`: OPI (§3.2 definition),
half-order ratio, harmonic decay slope, order-to-order variance.

### Source, propagation, listener presets, flow noise

- Monopole source: P(f) = jωρ/(4πr)·Q(f), frequency-domain differentiation
  with explicit roll-off above the resolved bandwidth (§3.1).
- **ISO 9613-1 atmospheric absorption** implemented from the standard's
  equations; anchors: **4.98 dB/km at 1 kHz and 23.1 dB/km at 4 kHz**
  (20 °C, 70% RH — the published values), f² low-frequency limit, and the
  non-monotonic humidity behaviour all verified.
- Ground reflection: image-source two-path; the interference dip lands
  exactly at c/2Δ (verified to 2%).
- Listener presets (§3.5 table): FSAE static 0.5 m/45° free-field,
  SAE J1287-style, drive-by, chase cam.
- Broadband flow noise (§3.4): seeded deterministic Strouhal-shaped noise,
  U⁶-power scaling verified, bit-identical per seed, silent at zero flow.
  Absolute level is explicitly a calibration factor — honest per §3.4.
- High-resolution capture: `ProbeCapture` records duct-cell pressure with
  time and crank angle every step while enabled, feeding the results store
  (float32) and the Phase 10 auralisation chain.

## 3. Auralisation (Phase 10)

### Crank-angle wavetable synthesis (§3.6)

The plan forbids time-stretching audio between rpm points — it destroys
phase coherence and sounds like a pitch shift. Instead:

1. Solve an rpm grid (250 rpm default) — `AuralisationPipeline.BuildBanks`
   converges each point, captures k cycles and stores them as a
   **crank-angle-indexed** `CrankWavetable` per source.
2. `WavetableSynthesizer` drives a phase accumulator with instantaneous
   engine speed, reads the bank at the accumulated crank angle, and blends
   between adjacent rpm tables **in the crank-angle domain** (same angle
   read from both tables, values blended) — never in the time domain.
3. Per-cycle amplitude and crank-offset perturbations from a seeded
   deterministic stream (§3.4) change only at cycle boundaries.

**Gate results:**

| Gate | Result |
|---|---|
| 1500→7200 rpm sweep, no audible crossfade artefacts | derivative crest factor **7.51** (isolated clicks would spike this); dominant tone tracks 70 → 214 Hz |
| Same seed → bit-identical renders | exact, including the burble layer; different seed → 15.1% RMS difference |
| A/B pairs within 0.5 LU | matched to **−23.00 / −23.00 LUFS**, true 12.04 LU difference still reported |
| Crossplane vs flat-plane distinguishable | after level matching, half-order ratio **3.385 vs 5.07e-18** |

The crossplane gate is driven by the **real Phase 9 collector-timing chain**
(firing angles → arrival phases → superposed pulses → wavetables → audio),
so it tests the physics path a real render takes, not a stand-in.

### Loudness and level-matched A/B (§3.6)

`Loudness` implements ITU-R BS.1770-4 / EBU R128: the two K-weighting
biquads (published 48 kHz coefficients — other rates are rejected rather
than silently mis-weighted), 400 ms blocks at 75% overlap, absolute gate at
−70 LKFS then a relative gate 10 LU below the ungated mean. `MatchPair`
returns both renders at a common loudness **and** the true level
difference, because the plan requires the SPL delta to stay visible even
while it is prevented from biasing the ear.

### Export (§3.6)

Own 24-bit/48 kHz WAV writer (and reader, for round-trip tests) with no
audio dependency. Stems (exhaust, intake, burble) export alongside the mix
under **one shared full-scale**, so their relative balance survives. Every
render writes a JSON provenance sidecar: model name and SHA-256 hash, rpm
profile, listener preset, seed, sample rate, integrated LUFS, and the
resolved bandwidth with a plain statement that content above it is not
physical.

### Drive-by and burble

`StemMixer.DriveBy` applies a time-varying propagation delay plus 1/r
spreading; **the Doppler shift emerges from the changing delay** rather than
a pitch knob — measured 140.6 Hz approaching → 126.0 Hz receding at 20 m/s,
matching the classical ratio within 3%. `OverrunBurble` is seeded,
reproducible and gated on decel, and is labelled phenomenological in code
and docs: the rate and energy are user knobs, not predictions (§3.4).

### Hybrid nonlinear / TMM (§5.6)

`HybridSynthesis` splits the spectrum at f_hybrid = min(1.5 kHz, measured
mesh bandwidth): below it the nonlinear solution is authoritative, above it
the TMM carries the signal without numerical dissipation. The two branch
weights are **complementary by construction** (they sum to 1 at every
frequency), verified along with a unity-transfer identity test and a
band-kill test.

### Phase 10 deferrals, stated plainly

- ~~FLAC export is not implemented.~~ **Done.** `render --flac` writes a
  FLAC beside every WAV, carrying bit-identical audio because both writers
  quantise the same way (a test asserts it). Roughly 69 % of WAV size on a
  real render, 25–36 % on tonal material and 0.2 % on silence; white noise
  correctly falls back to verbatim at 100 %.

  Written from RFC 9639 to the fixed-predictor subset: CONSTANT, FIXED
  (orders 0–4) or VERBATIM subframes with partitioned Rice residuals. Fixed
  predictors need no stored coefficients and get most of FLAC's compression
  on this material; general LPC would add solving and coefficient overhead
  for a few percent more.

  **The old deferral's reasoning was right and is now satisfied.** "A wrong
  FLAC file is worse than none, and verifying an encoder needs a reference
  decoder in CI" — so there are two layers. `FlacReader` decodes the stream
  back and the samples must match bit for bit, validating both frame CRCs
  and the STREAMINFO MD5 along the way; and a CI job installs the reference
  `flac` tool and runs `flac -t` over every file a render produces, because
  an encoder and decoder written from one reading of a spec can share a
  misreading. Round-trips are pinned at 1, 15, 16, 4095, 4096, 4097, 8192
  and 12290 samples: RFC 9639 §8.2 requires the minimum block size to be at
  least 16, and 4097 samples would otherwise leave a 1-sample final frame.

  Two defects found while building it, both real. The first version emitted
  only 4-bit Rice parameters, capping the parameter at 14 — but 24-bit audio
  routinely needs 15–20, because the low bits of a rendered stem are
  rounding noise, so nearly every real block was declared incompressible and
  sent to verbatim at 89 % of WAV size. The 5-bit parameter method exists for
  exactly that. The second was in the decoder: corrupt input ran it off the
  end of the buffer instead of reporting a bad stream, which matters because
  it reads files a user supplies.
- ~~Load interpolation is not implemented.~~ **Done.** The wavetable bank is
  now a two-dimensional rpm × load grid with bilinear interpolation, and
  every blend — both axes — happens in the crank-angle domain, so the result
  stays phase-coherent. `render --loads 1.0,0.35` (the plan's minimum, and
  the default) with `--lift-at` and `--cruise-load` driving a `LoadProfile`.
  A bank built at one load behaves exactly as the old one-dimensional bank
  did, so the axis costs nothing where it is not used.

  Load is the intake manifold absolute pressure as a fraction of ambient:
  1.0 wide open, 0.35 a light cruise. Verified end to end — throttling to
  35 % manifold pressure moves 0.315× the air per cycle at 4000 rpm, and a
  lift-off profile drops the rendered amplitude to exactly the low-load
  line's.

  **The load model is a steady pressure drop, not a throttle.** It does not
  model the plate's unsteady loss, the plenum volume's own wave dynamics, or
  the reflection the plate presents to a runner pulse. A real part-throttle
  intake is acoustically closer to a closed end than an open one, so
  **predicted intake noise at low load is optimistic**. Doing it properly
  needs the orifice-plus-plenum topology arriving with the manifold canvas
  (Phase 18); `ThrottleValve` already exists for it. Fuelling needs no
  adjustment — the charge fuel fraction is a mass fraction at fixed lambda,
  so less air is already less fuel.

  Outside the captured grid the nearest line is **held, never extrapolated**,
  and the synthesiser reports what fraction of a render was held that way so
  the CLI can warn. Extrapolated wavetable audio sounds entirely plausible,
  which is exactly why it must not pass silently as a solved result.
- ~~The listener chain is not applied to renders.~~ **Done.**
  `ListenerChain` filters a stem through a `PropagationPath` by whole-signal
  FFT convolution, and `wavebench render --listener drive-by` (or `fsae`,
  `j1287`, `chase-cam`) applies it. Verified: spherical spreading exact to
  0.01 dB at 0.5/2/8 m, 5.9 dB of excess high-frequency loss at 10 kHz over
  50 m, and the ground-reflection notch where the geometry puts it
  (1553 Hz for the drive-by preset, 36 dB below the adjacent peak). A
  free-field preset adds 0.02 dB of spectral ripple where drive-by adds
  16.9 dB — which is the whole point, and the reason auditioning a header on
  the source signal answers a question nobody asked.

  Two details worth stating. The response is referenced to the direct
  arrival, because the bulk propagation delay (22 ms at 7.5 m) is inaudible
  to a stationary listener but wraps the tail of an FFT convolution onto its
  head; a test pins that the first tenth of a render stays 120 dB below a
  burst confined to its last tenth. And the chain is applied per stem rather
  than to the mix — identical result, since it is linear, but it keeps an
  exported stem the same signal as its contribution to the mix instead of
  quietly remaining the pre-propagation source.

  The default is still `--listener source`: moving the microphone by default
  would change the output of every existing render. The console says which
  was used, and the metadata records the full chain including what was *not*
  applied.

- **Source directivity is not modelled.** A preset's azimuth positions the
  microphone but does not attenuate off-axis, so a render is on-axis in
  character regardless. That needs the outlet's radiation pattern and
  belongs with the cabin work in Phase 20. `ListenerChain.Describe` states
  this in the render metadata rather than letting a preset name imply it.
- ~~The mechanical layer is not implemented.~~ **Done**, and the broadband
  stem with it — the plan's four stems (exhaust · intake · broadband ·
  mechanical) now all render. `--broadband` and `--mechanical` set their
  levels; both default sensibly and both can be switched off.

  **Broadband** comes from the same solve as the tonal stems: the probes now
  capture velocity alongside pressure, and a velocity wavetable rides the
  same rpm × load grid. Velocity tables keep their mean, unlike pressure
  tables — the source scales on |U|, so removing the DC would delete the
  flow. Tailpipe exit uses the U⁸ quadrupole law (Lighthill 1952), the intake
  mouth the U⁶ dipole law (Curle 1955; Nelson & Morfey 1981), and both are
  verified directly: doubling the velocity gives 8.10× and 16.21× the
  radiated pressure against 8 and 16 exactly. The spectral peak sits on the
  St = 0.2 Strouhal frequency. **The absolute level is not calibrated** — it
  enters as one constant scale over the whole render, which fixes the unknown
  constant without touching the physical variation within it, and the console
  says so on every render.

  **Mechanical is cosmetic and predicts nothing**, as the plan requires it be
  labelled. Nothing here solves for valve-seating velocity, chain dynamics or
  injector solenoid motion, so no level in it is a prediction. What *is* real
  is the timing: events are placed on the crank angles the engine's own
  geometry gives, verified at exactly 50 / 100 / 200 valve events per second
  for one cylinder at 3000 rpm, one at 6000, and four at 3000; and the
  timing-drive whine lands at 666.5 Hz against the 666.7 Hz its tooth count
  and half-speed camshaft predict. It stays a separate stem so it can be
  soloed or muted and can never contaminate a metric or a compliance figure.

## 4. Sound metrics and compliance (Phase 11 — PARTIAL)

> **Phase 11 is not complete and the v0.4 milestone is not claimed.** The
> compliance half is done and verified, and of the standardised
> psychoacoustics ISO 532-1 loudness and DIN 45692 sharpness are now done and
> verified. ISO 532-3, ECMA-418-2, fluctuation strength and DIN 45681
> tonality remain. See "What is still missing" below. This section exists so
> the gap is visible rather than implied.

### Level metering — IEC 61672-1 (done, verified)

A-, C- and Z-weighting from the standard's exact pole frequencies
(20.598997, 107.65265, 737.86223, 12194.217 Hz), normalised by division at
1 kHz so the published +2.00 dB (A) and +0.06 dB (C) offsets are reproduced
rather than carried as rounded literals.

**Verified against IEC 61672-1 Table 3 at all 34 nominal bands, 10 Hz–20 kHz,
within the table's own 0.1 dB precision** — tested at the EXACT base-ten
third-octave frequencies (f = 10^(n/10)), because the table is tabulated
there, not at the rounded labels. On the steep part of the A-curve the
nominal "16 Hz" band (really 15.849 Hz) differs by several tenths of a dB;
testing against labels manufactures a bug that is not there.

Time weighting: Fast 125 ms, Slow 1 s, Impulse 35 ms, as exponential
detectors on the squared signal. A 200 ms burst of a 91.0 dB tone reads
90.0 dB Fast and 83.6 dB Slow — the expected behaviour, and the settle
window is capped at a quarter of the signal so a Slow reading on a short
clip cannot silently return −∞.

### Compliance — versioned rules data (done, verified)

`NoiseRuleSet` is **data, not code** (plan §3.8: limits change annually).
Round-trips as JSON; a rules change is an edit, never a recompile. Every
set carries its year and source, and the shipped FSAE set says VERIFY
against the live rulebook, because it is a starting point rather than an
authority.

FSAE static test: 0.5 m at 45°, 103 dB(C) Fast at idle, 110 dB(C) Fast at
the derived test speed **N = 15.25 × 30000 / stroke_mm rounded to the
nearest 500 rpm** — verified for six known strokes (60 mm → 7500, 54.5 →
8500, 76.4 → 6000, 96 → 5000, 45.8 → 10000, 88.4 → 5000).

**The honesty requirement is implemented, not just described.** Every
`ComplianceResult` carries an uncertainty band (±3 dB tonal, ±5 dB
broadband) and returns a THREE-way verdict: Pass, Fail, or
**TooCloseToCall** when the margin is inside the band. A design 1 dB under
the limit is not reported as passing. Plan §3.8: "never let a student fail
scrutineering because the software sounded confident."

### Engine character metrics (done)

The §3.7 set that actually discriminates header designs, computed by
`CharacterAnalysis`: Order Purity Index, half-order ratio, harmonic decay
slope, order-to-order variance, spectral centroid, rasp index, rumble index
(low-frequency energy weighted by 20–100 Hz envelope modulation),
tonal-to-noise ratio, and drone risk. These are **WaveBench definitions,
not standards** — each is specified precisely enough to reproduce and argue
with, and none borrows a standard's authority.

Six named target profiles ship as vectors in metric space, each with its
written mechanism (straight-six howl, flat-plane scream, crossplane rumble,
NA F1 scream, refined GT, FSAE compliant + charismatic), plus ranking by
distance. Verified: adding half-order content to a clean harmonic stack
drops OPI 1.000 → 0.599, raises the half-order ratio by 27 orders of
magnitude, and moves the design toward the crossplane target.

**Reference Match** extracts a fingerprint from the user's own recording and
tracks rpm from the firing order alone (3730 rpm recovered exactly from a
tacho-less signal). The API takes samples and returns metrics; it offers no
way to persist the audio, because the plan requires the recording never to
leave the machine.

### Loudness — ISO 532-1 Zwicker (done, verified)

`ZwickerLoudness` implements method B (stationary) from one-third-octave
band levels: low-band correction and grouping into the first three critical
bands, core loudness per band, then upward-masking slopes integrated over
the 24 Bark scale to give both a total in sone and the specific loudness
pattern N'(z) on a 0.1 Bark grid.

**Verified against the definition of the quantity**, which is the one anchor
that cannot drift: the sone *is* a 1 kHz tone at 40 dB, and loudness doubles
per 10 dB above it.

| 1 kHz tone | 40 dB | 50 dB | 60 dB | 70 dB | 80 dB |
|---|---|---|---|---|---|
| calculated | 1.000 | 2.004 | 4.000 | 8.035 | 16.155 |
| definition | 1 | 2 | 4 | 8 | 16 |

Everything inside 1%, against the ±5% conformance band ISO 532-1 §5.1 sets
for an implementation measured against its reference implementation. The
loudness *level* likewise tracks the band level in phon, and the scalar
total agrees with ∫N'(z)dz to 0.05%.

**The filter bank is part of the method, not a preprocessing detail.** The
first version of this failed every tone anchor by 7–13% while reproducing
the doubling law almost exactly — a constant scale error. The cause was the
test stimulus, not the algorithm: a pure tone was fed in as one band with
silent neighbours, which no real filter bank produces. ISO 532-1 §4 requires
class-1 filters with 20 dB damping at the adjacent band centres and prints
the consequence outright — *"a 1 kHz tone with a sound pressure level of
70 dB produces the following levels at different centre frequencies: 50 dB
at 800 Hz, 70 dB at 1 kHz and 50 dB at 1,25 kHz"* — and §5.2 warns that the
resulting upper slope *"contributes especially to the total loudness of pure
tones"*. Those skirts are ~7% of a tone's loudness. `ThirdOctaveAnalysis`
therefore applies the IEC 61260-1 idealised magnitude response with its
order solved for ISO 532-1's stated 20 dB adjacent-band damping, and a test
pins the standard's own 50/70/50 example. This is the same class of mistake
as testing IEC 61672 at nominal rather than exact band frequencies: the
implementation was right and the input was wrong.

A second real bug fell out of it — band power over a zero-padded FFT was
normalised by the padded length instead of the signal length, understating
every band by 10·log₁₀(N_pad/N_sig), or 1.35 dB for one second at 48 kHz.

#### Declaration of conformance (ISO 532-1 §5.1)

ISO publishes its reference implementation and the Annex B validation
signals free of charge at `standards.iso.org/iso/532/-1/ed-1/en`. That
package is ISO-copyrighted and is **not** redistributed here, but
`Iso532ConformanceTests` runs against it when `WAVEBENCH_ISO532_DIR` points
at an extracted copy. Measured against it:

| Annex B case | WaveBench | Reference | Deviation |
|---|---|---|---|
| B.2 third-octave levels | 83.2957 sone | 83.2957 sone | **0.00%** |
| B.2 specific loudness, all 240 Bark points | — | — | **worst 0.01%** |
| B.3 signal 2, 250 Hz 80 dB | 14.6701 | 14.6545 | +0.11% |
| B.3 signal 3, 1 kHz 60 dB | 4.0106 | 4.0192 | −0.21% |
| B.3 signal 4, 4 kHz 40 dB | 1.5490 | 1.5494 | −0.02% |
| B.3 signal 5, pink noise 60 dB | 10.5363 | 10.4978 | +0.37% |

against a permitted ±5% or ±0.1 sone. The B.2 band-level path is exact to
the reference's own float precision; the B.3 signal path carries the
additional error of substituting an FFT power-response filter bank for the
standard's 6th-order Chebyshev one, which costs under 0.4% even on pink
noise.

Three real defects were found by running this, all invisible to the sone
anchors above:

1. **A missing step.** The lowest critical band takes a further correction,
   N'₀ ← N'₀·(0.4 + 0.32·N'₀^0.2), because the threshold in quiet runs very
   steeply across it (LTQ falls 30 → 18 dB between the first two bands).
   Worth 16% on that band and 1.2% on the total.
2. **The upper-slope steepness column indexes the masking band (i − 1), not
   the band being filled.** This ran band 3's decay at 2.35 sone/Bark where
   it should be 2.80 — 5.6% in that band, 0.1% in the total.
3. **The slope's level-range index is state that persists across bands**,
   re-derived only on a genuine rise. Recomputing it per segment from the
   current value looks equivalent and is not.

Every one of those is a pattern-shaped error that a total-loudness check
absorbs, which is why the conformance test compares all 240 Bark points and
not just the scalar. The tabulated coefficients were independently correct
apart from a single mistyped upper-slope value (USL[12][4], 0.24 for 0.22).

The 16-bit test signals need calibrating: full scale is 2·√2 Pa, i.e. a
full-scale 1 kHz sine is 100 dB SPL. Every file in the package agrees on
that to 0.9%. Reading them as full-scale = 1 Pa understates by ~9 dB and
loses half the loudness.

### Sharpness — DIN 45692 (done, verified)

S = 0.11·∫N'(z)·g(z)·z dz ⁄ ∫N'(z)dz, with g(z) flat below 15.8 Bark and
rising as 0.15·e^(0.42(z−15.8)) + 0.85 above it. Built on the ISO 532-1
specific-loudness pattern.

The standard's reference signal — narrowband noise one critical band wide at
1 kHz, 60 dB, defined as exactly 1 acum — measures **1.028 acum**. Sharpness
rises monotonically with centre frequency (0.383 acum at 250 Hz to 6.530 at
8 kHz) and is near-invariant with level, moving only 3% over a 30 dB change,
which is the behaviour that makes it a timbre metric rather than a second
loudness.

### What is still missing

`PsychoacousticStatus` is a machine-readable list of exactly this, so the
UI can surface it instead of implying coverage:

| Metric | Standard | Status |
|---|---|---|
| Loudness (stationary) | ISO 532-1 Zwicker | **done, verified** |
| Sharpness | DIN 45692 | **done, verified** |
| Loudness | ISO 532-3 Moore–Glasberg | **not implemented** |
| Loudness/tonality/roughness | ECMA-418-2 | **not implemented** |
| Fluctuation strength | Zwicker & Fastl | **not implemented** |
| Tonality | DIN 45681 | **not implemented** |
| Speech interference | ANSI S3.5 | **not implemented** (needs cabin TF) |

**Why the rest are deferred rather than approximated:** the gate requires
each metric to match published reference values within its standard's
tolerance. ISO 532-3 and ECMA-418-2 are large models whose only practical
anchors are their reference implementations' outputs; DIN 45681 tonality has
no anchor available here. A plausible-but-unverified psychoacoustic figure is
worse than none — it is a number users would trust and design against. Note
that ISO 532-1's own reference code and validation signals are published
free by ISO at `standards.iso.org/iso/532/-1/ed-1/en`; cross-checking against
those is the obvious next step and would close the Annex B gap above.

**Abrupt-step finding (documented limitation):** meshing a sudden area
discontinuity as resolved A(x) geometry converges only first order at the
slope discontinuity (single-step transmission 18.5 → 18.8 Pa toward the
plane-wave 20.0 under refinement). This is out of contract by design — plan
§2.7 treats sudden expansions/contractions as boundary components
(Borda–Carnot), not meshed geometry. Model abrupt steps with components;
mesh only smooth profiles.
