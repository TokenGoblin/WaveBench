# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
