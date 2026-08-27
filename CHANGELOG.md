# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Phase 11 (PARTIAL — v0.4 milestone NOT claimed): sound metrics and
  compliance. IEC 61672-1 A/C/Z weighting verified against all 34 bands of
  the standard's published table, Fast/Slow/Impulse time weighting, FSAE
  noise rules as versioned JSON data with the derived test-speed formula
  verified for six strokes, and compliance results carrying an explicit
  uncertainty band with a three-way Pass/TooCloseToCall/Fail verdict. The
  full §3.7 engine character metric set, six named target profiles with
  mechanisms, and Reference Match with rpm tracking from firing order.
  **Not implemented:** ISO 532-3 loudness, ECMA-418-2, fluctuation strength,
  DIN 45681 — deferred rather than approximated because the gate demands
  verification against published reference signals. Tracked in code via
  `PsychoacousticStatus`.
- ISO 532-1:2017 method B (Zwicker) stationary loudness, in sone and as the
  specific loudness pattern N'(z) on a 0.1 Bark grid. **Verified against the
  standard's own Annex B validation data** (published free by ISO; not
  redistributable, so `Iso532ConformanceTests` runs against a local copy via
  `WAVEBENCH_ISO532_DIR`): exact on the B.2 third-octave case — 0.00% on the
  total and worst 0.01% across all 240 Bark points — and within 0.4% on all
  four B.3 signals including pink noise, against a permitted ±5%. Also
  reproduces the sone definition and its doubling law to within 1% at
  40/50/60/70/80 dB. One-third-octave analysis now
  applies the IEC 61260-1 filter magnitude response with its order solved for
  the 20 dB adjacent-band damping ISO 532-1 §4 requires, reproducing the
  standard's own worked 50/70/50 dB example — the filter skirts carry ~7% of
  a tone's loudness and an ideal rectangular bank loses them.
- DIN 45692:2009 sharpness in acum, on the ISO 532-1 specific-loudness
  pattern. The standard's reference signal measures 1.028 acum against the
  defined 1.0.
- **Phase 17 — the Design workspace.** Engine, Head & Cam, Manifold and
  Fuel & Combustion screens with every model field editable, derived readouts
  and inline model checks, four starter templates, and 60-second autosave
  with crash recovery. `DesignCatalogue` describes each field as data — path,
  tab, kind, unit family, Simple-mode visibility, bounds, choices, help — and
  a test walks the document schema by reflection so a field the UI cannot
  reach fails the build. All behaviour is in `WaveBench.ViewModels` with zero
  UI types; the WPF layer only builds controls.

  **Gate met.** A model built using nothing but the workspace's edit API,
  saved, and reloaded exactly as the CLI does, runs bit-identically:
  VE 1.118037, torque 223.878696 N·m, IMEP 2032462.799 Pa, knock 5.329482 on
  both paths. Unit conversion happens only at that boundary — type 4 inches
  and the document holds 101.6 mm — and switching units or mode cannot change
  a byte of the model. Theme switching stays complete: every colour still
  resolves through `Tokens.xaml`, enforced by the existing token tests, which
  caught two undefined resource keys in this very change.
- **Phase 18 — the Manifold canvas.** A manifold is now a node graph
  (`ManifoldSpec`: ports, pipes, junctions, plenums, open ends) that the
  solver builds from, with all nine §2.8 collector configurations in
  `CollectorLibrary` — 4-1, 4-2-1, Tri-Y, individual runners, log, 180°
  crossover, X-pipe, H-pipe and twin-scroll divided. `ManifoldWorkspace`
  carries the canvas behaviour with zero UI types: palette, selection,
  drag with grid snap, auto-layout, copy/paste of a whole bank, a per-node
  inspector in the user's units, the live geometry summary and inline design
  warnings. The WPF layer draws the graph and forwards gestures.

  Every design warning names its source — the plan's own §8.4 example comes
  out verbatim (*"Diffuser half-angle 11.3°: separation likely. Suggested
  ≤ 7° — lengthen the cone or reduce the exit diameter. (Claywell &
  Horkheimer, SAE 2006-01-3654)"*) — alongside branch angle on a merge
  (Idelchik), collector-to-primary area ratio in both directions (Blair;
  Watson & Janota), L/D below 1, and the multi-leg junction fallback. A test
  asserts every warning carries a citation or an actionable suggestion, and a
  matching one asserts a library 4-2-1 produces none.

  The canvas edits the graph as a VALUE — every operation deep-copies,
  mutates and commits — so undo and redo work across canvas edits as §8.11
  requires, rather than leaving undo holding two references to one object.

  **60 fps gate met.** `WaveBench.Bench -- canvas` times 300 frames of
  geometry summary + design warnings + a full hit-test over 40 components:
  median 0.370 ms, p99 0.684 ms against the 16.67 ms budget, 24× headroom.
- The pulse-interference diagram now uses the **solved** sound speed, as plan
  §2.8 requires, instead of a nominal handed in by the caller.
  `ManifoldPulseState.MeanSoundSpeed` samples each pipe across a full cycle —
  on crank angle rather than step count, mass-weighted rather than
  length-weighted — and transit becomes `Σ Lᵢ / aᵢ` over the pipes the pulse
  actually crosses. On the reference 4-2-1 at 6000 rpm the pipes report
  668–722 m/s and the port-to-merge transit is 34.3° of crank against 68.2°
  at an ambient 343 m/s.

- Two-zone burned/unburned combustion split (plan §2.4 Level 2), closing that
  deferral. Zones share the cylinder pressure and their volumes sum to it;
  the unburned zone compresses isentropically from start of combustion and
  the burned zone takes the remaining volume. Wall heat transfer is now
  resolved by zone rather than from the bulk mean — at x_b = 0.63 the zones
  sit at 632 K and 3666 K either side of a 2543 K mean, and heat loss is
  linear in (T − T_wall), so a single mean under-predicts it. **On by
  default**; costs 0.7–0.9% torque and 1–2 g/kWh BSFC with VE unchanged, and
  `combustion.twoZoneHeatTransfer: false` recovers the old behaviour. New
  outputs: `BurnedTemperature`, `BurnedVolume`, `BurnedFraction`,
  `CumulativeHeatLoss`.
- Polydyne cam generator (`CamProfile.Polydyne`), closing that deferral.
  y = 1 + C₂x² + C_px^p + C_qx^q + C_rx^r with the coefficients solved from
  zero lift, velocity, acceleration and jerk at the seat (Dudley 1948;
  Thoren, Engemann & Stoddart, SAE 1952); default exponents 2-8-10-12. The
  raised cosine it replaces reaches the seat with acceleration π²/2 in
  normalised units, so its jerk is unbounded — which is what bounces a
  follower. Also `CamProfile.PolydyneDerivative` for exact follower velocity,
  acceleration and jerk. Verified against a closed form: the 2-4-6-8 family
  is exactly (1 − x²)⁴, matched to 1e-12 across the flank.
- Branch-angle dependence in the junction pressure-loss model, via the cos α
  terms of Idelchik's converging- and diverging-wye formulas.
  `Junction.Connect` takes a branch angle; 90° reduces bit-identically to the
  previously verified right-angle model. A shallow collector now shows
  pressure recovery rather than loss (ξ = −0.079 at 15° where the same
  geometry as a tee gives +0.084), which is the scavenging effect the
  geometry exists to produce. Bassett's unsteady coefficients remain
  unimplemented; see docs/physics.md.
- FLAC export (`render --flac`), closing the last Phase 10 deferral. Written
  from RFC 9639 to the fixed-predictor subset (CONSTANT / FIXED 0–4 /
  VERBATIM with partitioned Rice residuals); ~69% of WAV size on a real
  render, 25–36% on tonal material, with bit-identical audio to the WAV.
  Verified two ways, as the deferral demanded: `FlacReader` round-trips every
  sample exactly while validating both frame CRCs and the STREAMINFO MD5, and
  a CI job runs the reference `flac -t` over every file a render produces.
  Confirmed against reference libFLAC (via libsndfile 1.2.2): all six stems
  of a render and fourteen block-boundary lengths from 1 to 65537 samples
  decode to exactly the WAV samples, maximum difference 0 in all 20 cases.
- The broadband and mechanical stems, completing the plan's four-stem render
  (exhaust · intake · broadband · mechanical). Probes now capture velocity
  alongside pressure, so broadband flow noise comes from the same solve and
  the same rpm × load grid as the tonal stems; the U⁸ tailpipe and U⁶ intake
  scaling laws are verified directly (8.10× and 16.21× pressure for doubled
  velocity, against 8 and 16). Its absolute level stays uncalibrated and is
  labelled so on every render. The mechanical layer is cosmetic and says so:
  event timing follows real crank geometry (50/100/200 valve events per
  second for 1-cyl at 3000, 1-cyl at 6000, 4-cyl at 3000; timing-drive whine
  at 666.5 Hz against a geometric 666.7) but every level is a knob.
  `--broadband` and `--mechanical` on `render`.
- Load as the second wavetable axis, closing the Phase 10 deferral that left
  the pipeline building a single load line. `WavetableBank` is now an
  rpm × load grid with bilinear interpolation, both axes blended in the
  crank-angle domain; `EngineBuilder.Build` takes an `intakeLoadFraction`;
  `LoadProfile` carries the throttle track; `render --loads 1.0,0.35`
  (default) with `--lift-at` / `--cruise-load`. A single-load bank behaves
  exactly as before. Outside the grid the nearest line is held rather than
  extrapolated, and the synthesiser reports the held fraction so the CLI
  warns instead of passing edge-held audio off as solved.
- `ListenerChain` applies a `PropagationPath` to a rendered stem, closing the
  Phase 10 deferral that left renders exporting the raw source signal.
  `wavebench render --listener drive-by|fsae|j1287|chase-cam` (default
  `source`, so existing renders are unchanged) with `--outlet-height` setting
  the ground-reflection geometry. Verified against the Phase 9 propagation
  physics: 1/r exact to 0.01 dB, 5.9 dB excess absorption at 10 kHz over
  50 m, and the ground-reflection notch at the geometric frequency. Render
  metadata records the full chain, including that source directivity is not
  modelled.

### Fixed

- **Combustion released no fuel before TDC and then dumped it in one step.**
  The per-cycle burn reset fired at the local-angle wrap, which sits at
  firing TDC and therefore inside the burn window for any spark advance: the
  previous cycle's burned fraction suppressed the whole pre-TDC portion, then
  the accumulated fraction was released in the single step after the wrap.
  9.7% of the cycle's fuel in one timestep at −15° spark, 56.0% at −30°, peak
  pressure 152.6 bar against 99.4 bar. Spark-timing sensitivity was not being
  modelled at all, and the SOC reference for the knock integral was frozen at
  TDC rather than at spark. The reset now cycles on the burn-window
  coordinate, at gas-exchange TDC; largest single-step release is 0.37% at
  every advance from −5° to −45°. Found by code review.
- Two-zone follow-ups from the same review: the zones stayed open through
  expansion and exhaust because the Wiebe asymptote (0.9933) never reaches
  the `>= 1.0` completion test, carrying a fictitious unburned pocket that
  cooled below wall temperature and fed heat back in; the zone split was
  reported before any fuel had burned; and the temperature backstop clamped
  rather than rejecting, which breaks p·V = m·R·T while still feeding the
  heat-transfer model. Completion is now decided by the burn window, the
  split is gated on the SOC reference, and an implausible split sets
  `ZonesResolved = false` instead of being clamped into range.
- One-third-octave band power over a zero-padded FFT was normalised by the
  padded length rather than the signal length, understating every band by
  10·log₁₀(N_pad/N_sig) — 1.35 dB for one second at 48 kHz.
- Three defects in the Zwicker loudness pattern, all found by the Annex B
  conformance run and all invisible to a total-loudness check: the lowest
  critical band was missing its threshold-in-quiet correction (16% on that
  band); the upper-slope steepness column indexed the band being filled
  rather than the masking band below it (5.6% on band 3); and the slope's
  level-range index was recomputed per segment instead of persisting across
  bands. One mistyped upper-slope coefficient (USL[12][4]) as well.

### Changed

- `OrderAnalysis`/`CharacterMetrics` moved from `WaveBench.Analysis` to
  `WaveBench.Acoustics.Metrics`, where the plan's Part 7 layout puts order
  analysis and psychoacoustics.

- Phase 10: auralisation — WaveBench makes sound. Crank-angle wavetable
  synthesis (phase-coherent, never time-stretched), seeded per-cycle
  variation, BS.1770 gated loudness with level-matched A/B, own 24-bit WAV
  writer with stems and a provenance sidecar, drive-by Doppler from the
  changing propagation delay, overrun burble, and the §5.6 hybrid
  nonlinear/TMM crossover. New `wavebench render` command. Gates: sweep
  crest factor 7.51, bit-identical seeded renders, A/B within 0.5 LU,
  crossplane vs flat-plane half-order 3.385 vs 5.07e-18 after matching.
  Deferred and documented: FLAC, load interpolation, listener chain in the
  render path, mechanical layer.

### Changed

- `Fft` moved from `WaveBench.Analysis` to `WaveBench.Core.Numerics`: it is
  a numerical primitive, and `Acoustics` depending on the post-processing
  assembly read backwards.

- Phase 9: acoustic source, capture, propagation, order analysis. Collector
  timing calculator (hand-exact, rpm-linear errors, wall-temperature
  effect), scroll-separation index, pulse-train synthesis, crank-synchronous
  order tracking (0.2 dB gate, sweeps included), OPI + character metrics —
  crossplane/flat-plane gate: half-order 3.39 vs 2.5e-30. Monopole source
  with bandwidth roll-off, ISO 9613-1 absorption (published anchors hit),
  ground-reflection comb, listener presets, seeded Strouhal flow noise,
  high-resolution probe capture.

- Phase 8: linear acoustics engine (TMM). Four-pole element library in the
  [p, U] convention — damped duct with mean flow, area discontinuity with
  end correction, quarter-wave stub, Helmholtz resonator, Levine–Schwinger
  radiation, five termination kinds — with TL/IL/impedance/transfer outputs.
  §6.1 gates: chamber TL to 0.1 dB, stub 1%, Helmholtz 2%, convective shift
  0.5%, reciprocity, 20-element sweep in 4.3 ms, and TMM-vs-nonlinear
  agreement worst 0.45 dB (gate < 1 dB) on a shared smooth-taper chamber.
  Documented: abrupt steps belong to components, not meshed geometry (FV is
  first-order at slope discontinuities). docs/acoustics.md started.

- Phase 7: headless product v0.1. Serialisable model schema with validation
  rules and stable JSON, EngineBuilder, OperatingPointRunner (parallel
  sweeps, mesh-sensitivity study), SQLite results store with float32 probe
  captures, the `wavebench` CLI (run/sweep/mesh/validate/info) with ScottPlot
  output, validation report artefacts committed under `validation/`, and the
  §5.7 performance budget: profiled 15.6 s → 4.3 s (devirtualised EOS hot
  path, cached wave speeds, warm-started valve solve, cell-count-gated
  parallel pipes) — budget met and recorded.

- Phase 6: combustion, heat transfer, knock, friction. Wiebe (single/double),
  quasi-two-zone knock tracking (isentropic unburned zone + Livengood–Wu;
  RON95/E85/M100 ranking gate passes), Woschni/Hohenberg/Annand with exact
  scaling tests, Chen–Flynn friction, blowby and isothermal crevices,
  IMEP/BMEP/torque/BSFC metrics, seeded deterministic cycle-to-cycle
  variability. First §6.2 validation case (Yin CSU thesis runner-length
  study): 800 mm optimum exact, 600 mm within the 250 rpm gate; short-runner
  discrepancy documented (unpublished thesis Cd curve).

- Phase 5: engine assembly (motored). Exact slider-crank kinematics, cam
  profiles (harmonic generic + CSV import), Blair-convention valve areas
  with a 2D generic Cd map, the valve boundary solved jointly with the duct
  characteristics, 0D composition-resolved cylinders, deterministic engine
  network stepping with a cycle-convergence manager, and the Quick Estimate
  layer. Gate: VE peak 1.25 at 5000 rpm vs organ-pipe estimate 5015 rpm
  (0.3%); sealed-engine conservation to 1e-6/0.1%; bit-identical reruns.

- Phase 4: boundaries and components. Characteristic-compatible reservoir
  boundaries (nozzle within 0.5% of isentropic), compressible orifice with
  choking, FSAE restrictor as solved geometry (chokes at theory within 1%,
  sonic throat), 0D plenum verified against the exact blowdown ODE, orifice
  connectors (duct/plenum/ambient), butterfly throttle, Benson
  constant-pressure junctions plus an Idelchik 90° tee pressure-loss model
  (published anchors: Crane 1.3, Idelchik combining formulas; branch-angle
  generalisation deferred to collector work), and injector mass sources.

- Phase 3: source terms and thermal model. Quasi-1D `DuctSolver` (supersedes
  the constant-area solver): well-balanced variable area (taper at rest
  < 1e-10 m/s), species transport with machine-precision ΣY = 1, the
  species-resolved caloric EOS in the solver (composition-correct local
  sound speed), Haaland friction and Colburn heat transfer verified within
  1% of analytical, per-cell wall thermal nodes with surface treatments,
  radix-2 FFT in WaveBench.Analysis, and the §5.5 bandwidth characterisation
  test (−3 dB ≈ 4.8 kHz at Δx = 3 mm over 2 m in 20 °C air).

- Phase 2: 1D solver core. MUSCL-Hancock finite-volume scheme with HLLC
  fluxes (Toro), slope limiters (van Leer/minmod/van Albada), positivity
  guards, CFL control, transmissive/reflective/periodic boundaries, and the
  exact Riemann solver as verification reference (anchored to Toro Table
  4.3). Verification suite: Sod/Lax/123 vs exact, observed order > 1.8,
  machine-precision conservation, acoustic pulse > 98% amplitude retention
  over 20 lengths. `docs/numerics.md` started.

- Phase 1: thermodynamics and fuels. NASA-7 species database (GRI-Mech 3.0 +
  Burcat, with a WaveBench NIST-JANAF-fitted H2O upper range), CHEMKIN parser,
  mixture properties (R, cp, γ, a, h, u, s) with a tabulated fast path,
  combustion-product composition (lean complete / rich water-gas-shift), the
  fuel record and 14-fuel shipped library, formula-derived stoichiometry,
  evaporative charge cooling, Metghalchi–Keck laminar flame speed, and
  Douaud–Eyzat + Livengood–Wu knock. `docs/physics.md` started.

- Phase 0: solution scaffolding for all Part 7 projects, `Directory.Build.props`
  with nullable reference types and warnings-as-errors solution-wide, xUnit test
  harness, GitHub Actions CI (build + test on `windows-latest`, nightly
  validation workflow), Apache-2.0 licence, structured logging in the CLI.
- Units and quantities layer (`WaveBench.Model.Units`): strongly-typed
  quantities with a canonical SI internal representation — length (m/mm/in),
  pressure (Pa/kPa/bar/psi/inHg), temperature (K/°C/°F), volume, mass flow,
  area, angle, rotational speed and sound level — with parsing and
  tabular-figure-friendly formatting.

### Known gaps

- **No wall heat transfer on any duct in a built engine.** `WallThermalModel`
  and the Colburn coefficient in `DuctSolver` exist and pass their Phase 3
  component gates, but neither `EngineBuilder` nor `ManifoldAssembler`
  attaches a wall to the ducts it builds, so `HeatTransferEnabled` is false
  throughout and friction dissipation is the only source term acting on the
  gas — exhaust can only get hotter down the pipe, which is backwards. Plan
  §2.3 requires wall heat transfer with a selectable surface treatment
  (bare / coated / wrapped / insulated). Recorded rather than fixed alongside
  Phase 18 because attaching it moves every committed VE, torque and BSFC
  figure: it is a re-baseline, not an edit. See docs/physics.md §1.10.