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

**Abrupt-step finding (documented limitation):** meshing a sudden area
discontinuity as resolved A(x) geometry converges only first order at the
slope discontinuity (single-step transmission 18.5 → 18.8 Pa toward the
plane-wave 20.0 under refinement). This is out of contract by design — plan
§2.7 treats sudden expansions/contractions as boundary components
(Borda–Carnot), not meshed geometry. Model abrupt steps with components;
mesh only smooth profiles.
