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

- **FLAC export** is not implemented; WAV is. A wrong FLAC file is worse
  than none, and verifying an encoder needs a reference decoder in CI.
- **Load interpolation** (§3.6: at least two load lines, interpolated on
  both axes) is not implemented — the pipeline builds one load line. Cruise
  drone and overrun auditioning need this and it belongs with the Sound
  workspace work.
- **The listener chain is not applied to renders** — the CLI exports the
  source signal. `PropagationPath`/`ListenerPreset` exist and are tested
  (Phase 9); wiring them into the render path is Phase 20.
- **The mechanical layer** (parametric, cosmetic) is not implemented.

**Abrupt-step finding (documented limitation):** meshing a sudden area
discontinuity as resolved A(x) geometry converges only first order at the
slope discontinuity (single-step transmission 18.5 → 18.8 Pa toward the
plane-wave 20.0 under refinement). This is out of contract by design — plan
§2.7 treats sudden expansions/contractions as boundary components
(Borda–Carnot), not meshed geometry. Model abrupt steps with components;
mesh only smooth profiles.
