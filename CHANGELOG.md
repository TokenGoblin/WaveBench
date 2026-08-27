# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Phase 12 — turbomachinery data and steady matching.** `WaveBench.Boost`:
  compressor and turbine map schema in SAE J1826 corrected quantities with
  per-map reference conditions that are **required and never defaulted**
  (plan §4.2 calls assuming them "a classic silent 5% error" — the same
  130 000 rpm at 320 K corrects 1.69% apart between the two common gas-stand
  references); piecewise-linear interpolation with affinity-law extrapolation
  and a `MapRegion` on every reading so extrapolated points can be shaded;
  a turbine swallowing model closed to exactly zero flow at ER = 1 and to a
  choked plateau above the measured range; steady shaft balance
  `P_turbine·η_mech = P_compressor + P_friction` with the expansion ratio set
  by what the turbine actually swallows; surge and choke margins; the turbo
  library with mandatory source and licence per entry; and auto-match ranking
  that disqualifies with reasons rather than silently deducting.
- **The map digitiser** (plan §4.7): axis calibration from two labelled
  gridlines per axis, colour tracing by intensity-weighted centroid, efficiency
  reconstructed radially from nested islands, and a PNG decoder in
  `WaveBench.Boost` so tracing runs headless. JPEG and interlaced PNG are
  refused by name rather than scrambled. **Gate: 2%. Measured worst error
  0.63% on efficiency, 0.08% on pressure ratio, 0.15% on the traced flow
  range**, against an analytic map surface rendered to a 900 × 700 image.
  Test maps are synthetic — §4.7 forbids shipping manufacturer maps without
  written permission, and that applies to the test suite too.
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
  VE 1.087720, torque 216.065062 N·m, IMEP 1967521.228 Pa, knock 5.303747 on
  both paths (re-baselined when the duct source terms were wired in; see
  "Fixed (physics)" below). Unit conversion happens only at that boundary — type 4 inches
  and the document holds 101.6 mm — and switching units or mode cannot change
  a byte of the model. Theme switching stays complete: every colour still
  resolves through `Tokens.xaml`, enforced by the existing token tests, which
  caught two undefined resource keys in this very change.
- **Phase 23 — Simple mode and the wizard.** Nine steps, each with the "why
  this matters" explainer §8.6 asks for and a live preview that updates as the
  answers change. Every answer writes into the **full model** — there is no
  parallel simple model — and derived fields go through `ApplyWizard`, so a
  re-run touches only `Auto` and `Wizard` fields and anything the user typed,
  imported or optimised survives untouched.

  The Design Brief is recommendation → number → why → confidence, with the
  plan's four-dot indicator, an uncertainty band on every prediction, and a
  build list that snaps primaries to tube sizes that exist on a shelf. It
  reports its *weakest* confidence rather than an average, because a brief is
  only as good as its shakiest input. PDF export is a self-contained writer in
  `WaveBench.ViewModels` — base-14 fonts, no embedding — so the CLI can produce
  a brief without a desktop stack, checked the way a reader checks it: header,
  object table, every cross-reference offset landing on its object, every
  declared stream length matching its real one.

  **Gate (checkable half) met.** The brief's numbers are bit-identical to what
  Advanced mode produces from the same document — 233.831233 N·m on both paths
  at every sweep point — and the first preview lands in 0.0 ms against the
  one-second budget, declining to predict anything since no solve has run. The
  usability half (a novice reaching a brief in 15 minutes) is not something a
  test settles.

  Two seeding errors caught by the numbers being obviously wrong. The organ-pipe
  relation is the *fundamental*, so taking it at face value gave a 2.29 m intake
  runner; the third return is checkable against this project's own §6.2 Yin case
  (measured 800 mm at 3000 rpm, seed 762 — 4.8% short). And runner diameter came
  out at 17 mm for an 82 mm bore because it was sized from cycle-mean volume
  flow, which is wrong by a factor of four when the valve is open a quarter of
  the cycle; mean port velocity is piston area × mean piston speed over port
  area.
- **Phase 20 — the Sound workspace.** The §8.4 layout: two named designs with a
  one-click swap, collector timing, the A-vs-B order spectrum with the firing
  harmonics marked, the order waterfall against rpm, the character radar, TMM
  silencing with live geometry sliders, and the level-matched A/B audition.
  "Explain this" writes the plan's sentence.

  **The M50 worked example is reproduced from geometry and firing order
  alone** (§3.0's "worked example the module must nail"). The equal-length 6-1
  spaces its pulses at exactly 120.000° at every speed tested; the factory cast
  manifold runs 3.4° to 16.2° and the error grows *proportionally* with rpm,
  which is the signature of a fixed transit mismatch. In the order domain the
  factory leaks −25 to −51 dB into orders 0.5 through 2.5 while the 6-1 sits at
  the numerical floor, and the firing harmonics match within 0.6 dB:
  3.7% of the factory's energy is off-harmonic against 0.0%.

  **All three gate clauses met.** A primary-length change rebuilding both the
  timing chart and the A/B spectrum runs at 6.26 ms median, 8.41 ms p99 against
  the 50 ms target — inside a 60 fps frame, which is the harder standard a drag
  is really held to. The A/B audition matches both designs to −23.00 LUFS while
  reporting the true difference, keeps the playback position across a switch,
  and crossfades: the largest sample step is 1.010× the material's own against
  33.4× for a one-sample switch, and the level moves 0.49 dB across the fade.

  Three findings worth recording. My first M50 geometry was mirror-symmetric,
  which under the 1-5-3-6-2-4 firing order repeats every three firings and
  makes the signal periodic at 360° — so it had *no half-order content at all*
  and the cast manifold looked clean on the metric that should condemn it. Real
  logs taper toward their single outlet and the coolant gradient is monotonic;
  both now are. Second, both designs were reading ≈ −310 dB at order 0.5 and
  the comparison passed on FFT round-off, so the test now requires the
  factory's content to be real first. Third, the explanation sentence attributed
  the timing error against the design mean while the error is defined against
  the anchor cylinder — it read *"15° early because ... (−6.3°) and (−0.3°)"*,
  six degrees of explanation for a fifteen-degree error. Since firing is evenly
  spaced, `error = 6·N·(τᵢ − τ_anchor)` exactly, so the anchor is the only
  reference where the parts sum to the whole; `UnexplainedDeg` is now exposed
  and asserted zero.

  Series are clipped to the plot rectangle in both renderers. An order
  spectrum's floor is −300 dB, which is no reason to rescale the axis but also
  no licence to draw over the legend.
- **Phase 19 — the Results workspace.** Performance curves, the x–t wave
  diagram with scrub and animation, per-cylinder charts, probe traces, and the
  wave-decomposition plot. Run now solves: the button sweeps 3000–9000 rpm off
  the UI thread against a deep copy of the document, reports into the job tray,
  can be cancelled mid-sweep, and lands on the Results workspace.

  **Wave decomposition** (Blair superposition, SAE 1999 §2.2–2.5) splits a
  probe's pressure history into its rightward- and leftward-running components
  from a single (p, u) pair. **Gate met** against the textbook reflection: an
  open end returns an expansion at 8.767 ms against a predicted 2(L−x)/a of
  8.726 (+0.5%), a closed end a compression at 8.448 ms (−3.2%), and the
  outgoing pulse and its reflection are separated by 8.700 ms against an 8.726
  ms round trip. The annotation reads as §8.4 writes it — *"reflected expansion
  arrives 12° before EVC"* — phrased against the valve event and taking the
  short way round the cycle.

  **The x–t field** is sampled on crank angle rather than on solver steps: CFL
  steps bunch where the gas is hot, and frames recorded per step would animate
  at a rate that varies for no physical reason. A gate test measures the
  diagonal's gradient out of the recorded frames and gets 387.3 m/s against
  Blair's finite-amplitude a+u of 384.8 (+0.6%) — not the 343 m/s small-signal
  speed, which is the distinction a nonlinear solver exists to make.

  **Animation gate met.** A 30-cycle capture is 21 601 frames × 100 cells
  (16.5 MiB). The heat map is windowed to the last cycle and downsampled to the
  rows a display can show — 0.6 ms to build — and the per-frame slice is
  0.0011 ms median, 0.0023 ms p99 against the 16.67 ms budget. Finding that
  cost 1.2 ms per frame first: `Range()` was rescanning all 2.2 million samples
  to draw a hundred points, and is now maintained as frames arrive.

  **Every plot exports to PNG and SVG.** One `PlotModel` feeds the screen, the
  WPF renderer and the SVG writer, so an exported figure is the figure that was
  on screen; series name colour *tokens*, so a dark-theme export comes out
  dark. SVG is true vector except a heat map, which embeds a PNG data URI
  because 576 000 rectangles is not a file any reader will open — which needed
  a PNG encoder, written in `WaveBench.ViewModels` rather than pulled from a UI
  stack so the CLI can export report figures without WPF.

  Per-cylinder VE, IMEP, peak pressure, knock integral and EGT are now on
  `OperatingPointResult`, with the VE spread as the single number a mean hides.
  EGT is mass-weighted at the port — a valve spends most of the cycle shut, so
  a time mean would average the blowdown that carries the energy against a long
  tail of almost no flow.
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

### Fixed (physics)

- **Cylinders released a whole cycle's fuel on their first step.** The Wiebe
  increment is `xb − _previousBurnFraction`, and `_previousBurnFraction` began
  at zero — so a cylinder whose first step landed past its burn window saw
  xb ≈ 0.9933 against a stored zero and burned everything at once. On a
  four-cylinder engine the cylinders start 180° apart, so **two of the four did
  this on every run**, one of them mid exhaust-stroke with its valve open,
  detonating into a cold pipe at 14 bar on the first degree of crank.

  A wide pipe absorbed it and the transient washed out over the convergence
  cycles, which is why it went unseen. A primary narrower than its valve
  throats did not: the duct's end cell went to negative density and the whole
  solve became NaN — silently, all the way to a reported torque figure, until
  `UpdateConserved` was given a positivity guard.

  `Cylinder.Step` now seeds the burn state from wherever the cylinder actually
  starts, which is also the physically right answer: a cylinder initialised
  half way through its exhaust stroke has already burned and holds no fresh
  charge. It fires normally on the next cycle.

  The fix moves a converged answer by **eight parts per million** (Ø38 mm
  primary: 219.969680 → 219.967828 N·m) and moved no other committed figure in
  the suite at all — a converged periodic solution does not remember its
  startup.

  Worth recording how it was found. The failure threshold coincided exactly
  with valve throat area crossing pipe area, which made a flow-limit
  explanation look obvious and cost three wrong fixes to the valve/duct
  coupling — one of which broke geometry that had been solving correctly. What
  actually identified it was varying something the theory said was irrelevant,
  the cylinder *count*, and finding that one and two cylinders survived where
  four did not.

- **Duct friction and wall heat transfer never reached a built engine.**
  `DuctSolver` implements Haaland/Darcy friction (§2.1) and Colburn wall heat
  transfer against a `WallThermalModel` node (§2.3, §2.9), and all of it
  passed Phase 3's component gates — but `FrictionEnabled` defaults to false,
  `HeatTransferEnabled` is `Wall is not null`, and nothing outside a unit test
  ever set the flag or called `AttachWall`. **Every pipe in every engine the
  product built ran adiabatic and frictionless.** Phase 3 gated a duct, Phase 5
  gated an engine; neither checked that the engine's ducts were the ducts
  Phase 3 had gated. Found by measuring the solved sound speed for the pulse
  diagram and noticing it rose monotonically to the tailpipe, which is
  backwards for an exhaust.

  `EngineBuilder.ApplyThermal` now equips every duct — intake runners, plain
  exhaust runners, and every pipe built from a manifold graph — from a new
  `PipeThermal` block on the document, with both terms on by default and flags
  to switch them off as a diagnostic. Surface treatment is selectable per §2.9:
  bare stainless, ceramic coated, water jacketed, header wrap, insulated.

  **Wall temperature is solved between cycles, not integrated within them.** A
  steel wall's time constant is ~10 s against a 20 ms cycle, so explicit
  integration is still climbing when a run ends and the answer ends up set by
  an assumed wall thickness. `WallUpdate.CyclicSteady` holds the wall fixed
  through a cycle, accumulates ∫h dt and ∫h·T_gas dt, and solves the
  cycle-average balance for T_w by Newton at each cycle boundary — plan §2.9's
  "iterate wall temperatures to convergence across cycles". `RunToConvergence`
  will not declare convergence until the wall is periodic too. Starting the
  exhaust wall at 400 K or 1100 K, with a heat capacity of 100 or 40 000
  J/(m²·K), all converge to the same 909.2 K in 7 cycles.

  The intake wall is **held** by default, and that is the physics: left free it
  balances against the charge alone and settles at or below ambient, modelling
  an intake that chills the charge where §2.2 asks for ambient plus wall heat
  pickup. Its real temperature comes from the coolant and the head, which the
  model does not represent, so it is an input rather than a fabricated
  prediction.

  The §2.9 differentiator now demonstrates: bare 909 K / 671.6 m/s, header wrap
  933 K / 672.8 m/s, ceramic 948 K / 673.8 m/s, insulated 963 K / 674.5 m/s.
  One correction to the plan's wording — it says a wrapped header's optimum
  primary is "correspondingly shorter", but `L = a·Δθ/(12·N)` from the plan's
  own §2.10 rises with `a`: a faster wave needs a *longer* primary to return at
  the same crank angle. At fixed length it is the tuned *rpm* that goes up. See
  docs/physics.md §1.11.

  **Re-baseline.** Against the previous adiabatic frictionless pipes, on the
  reference four-cylinder at 6000 rpm: VE 1.1064 → 1.0663, torque 171.93 →
  165.39 N·m. Friction costs 0.57%, the wall a further 3.25%. Every committed
  figure in the docs, in this file and in the app's Overview was re-measured.

  Enabling the source terms put the §5.7 performance budget over its 5 s
  target (5.77 s), so the innermost loop was profiled rather than the gate
  waived: `(ε/3.7D)^1.11` is geometry only and is now precomputed per cell,
  `Pr^(−2/3)` is cached on the duct, and Sutherland's `x^1.5` is evaluated as
  `x·√x`. Three fewer transcendentals per cell per step took the budget case
  from 132 to **85 ns/cell-step** and 5.77 s to **4.36 s — budget met**, with
  the full physics on.

  The Phase 17 gate figures are now VE 1.087720, torque 216.065062 N·m,
  IMEP 1967521.228 Pa, knock 5.303747 — still bit-identical between the UI and
  the CLI paths, which is what that gate is about.