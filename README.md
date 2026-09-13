# WaveBench

[![CI](https://github.com/TokenGoblin/WaveBench/actions/workflows/ci.yml/badge.svg)](https://github.com/TokenGoblin/WaveBench/actions/workflows/ci.yml)

Laboratory-grade 1D engine gas-dynamics, acoustics and forced-induction design
suite.

- **Platform:** .NET 10 (LTS) · Windows desktop (WPF) · headless CLI on any .NET target
- **Licence:** Apache-2.0
- **Audience:** Formula SAE teams, race engine developers, professional engine designers, DIY engine enthusiasts
- **Scope:** intake and exhaust wave tuning · camshaft timing · collector configuration and cylinder pairing · exhaust sound design and auralisation · turbocharging and supercharging · multi-objective optimisation

**No telemetry. No network calls at runtime.** Your designs, dyno data and
audio never leave your machine.

---

## Fifteen minutes to a torque curve

```
dotnet build
dotnet run --project src/WaveBench.Cli -- sweep examples/single-360.json \
           --from 4000 --to 9000 --step 500 --plot sweep.png
```

That is a converged torque curve. To check it is a property of the engine
rather than of the grid it was solved on:

```
dotnet run --project src/WaveBench.Cli -- mesh examples/single-360.json --rpm 7000
```

For the desktop app, open a template, describe your engine in **Design**,
press **Run**, and read **Results**. The full walkthrough is
[`docs/user-guide.md`](docs/user-guide.md).

---

## Status

**25 of 26 phases complete.** 1016 tests green (608 unit + 408 verification),
none skipped. The complete build specification lives in
[`docs/WaveBench-Master-Plan.md`](docs/WaveBench-Master-Plan.md) — a staged
build contract with 26 phases, each with a hard acceptance gate. Physics
before pixels: Phases 0–15 produced a headless, test-covered engine; no UI
existed before Phase 16.

What works today:

- **Gas dynamics** — species-resolved 1D MUSCL-Hancock + HLLC, verified
  against exact Riemann solutions; well-balanced variable area;
  friction, heat and wall-thermal sources; reservoir, orifice, plenum and
  junction components; an FSAE restrictor that chokes at theory.
- **Engines** — motored and fired single- and multi-cylinder engines with
  wave-tuned VE curves, Wiebe combustion, knock tracking, per-cylinder
  breakdowns.
- **Acoustics** — transfer-matrix engine cross-validated against the nonlinear
  solver to 0.45 dB, collector pulse-timing analysis that reproduces the
  crossplane-vs-flat-plane signature from firing order alone, and audio
  synthesis with phase-coherent crank-angle wavetables and BS.1770
  level-matched A/B.
- **Forced induction** — compressor and turbine maps with a digitiser, steady
  matching, coupled unsteady flow, transient spool with a sensitivity band,
  Greitzer surge, and a Boost workspace that draws the operating line on the
  map.
- **Optimisation** — DOE, Morris screening, CMA-ES, NSGA-II, Bayesian
  optimisation with a Gaussian-process surrogate, Nelder–Mead and Powell
  refiners, a surrogate inner loop, and a Pareto explorer with
  click-to-audition.
- **The learn layer** — why-text and a typical range on every field, "Show me"
  parametric sweeps, concept explainers, guided tours and a
  generic-defaults banner.
- **Reporting** — one-click PDF and HTML, both rendered from one document so
  they cannot disagree.

Remaining: Phase 11's four psychoacoustic metrics, and Phase 15's
transient-spool validation against a measured case. Both are deferred for
stated reasons rather than skipped — see [the gallery](#validation-gallery).

---

## Headless CLI

```
wavebench info    examples/single-360.json
wavebench run     examples/single-360.json --rpm 5000
wavebench sweep   examples/single-360.json --from 4000 --to 9000 --step 500 \
                  --db results.db --plot sweep.png
wavebench mesh    examples/single-360.json --rpm 7000
wavebench render  examples/single-360.json --from 2500 --to 7500 --seconds 9
wavebench report  examples/single-360.json --from 4000 --to 9000 --step 500 --out reports
wavebench validate --out validation
```

`report` solves the sweep, runs the mesh-sensitivity study and writes
`report.html` and `report.pdf`: the complete model dump with the origin of
every value, the geometry, every figure, the convergence evidence, the
acoustics, the turbo match, every assumption with its citation, and a
validation statement. It is built to be handed to an FSAE design-event judge,
which is why it states its own weaknesses rather than leaving them to be
found.

`render` solves an rpm grid, builds crank-angle wavetables from the solved
pressure history and synthesises phase-coherent audio — 24-bit/48 kHz WAV with
separate exhaust/intake stems and a provenance sidecar recording the model
hash, seed and resolved bandwidth. Content above that bandwidth is labelled as
not physically resolved rather than presented as prediction.

---

## Validation gallery

Every claim is backed by something outside this tool, or it is listed below as
not backed. The full citation list — every correlation, its source and its
validity range — is [`docs/citations.md`](docs/citations.md).

### Published-data case

The open-access CSU thesis runner-length study. WaveBench reproduces the
published optimum exactly at 800 mm and within the 250 rpm gate at 600 mm:

![Yin runner-length validation](validation/yin-runner-length.png)

The short-runner discrepancy is documented rather than tuned away —
[`validation/yin-runner-length.md`](validation/yin-runner-length.md) and
`docs/physics.md` §1.9.

### Analytical anchors

These have exact answers, which makes them better anchors than any
measurement. All run on every commit.

| Case | Result |
|---|---|
| Shock tube against the exact Riemann solution | density, velocity and pressure match |
| Stationary taper, well-balancedness | spurious velocity < 1e-10 m/s |
| Steady pipe friction vs Darcy–Weisbach | within 1% |
| Steady wall heat transfer vs analytical | within 1% |
| Isentropic nozzle through to choking | matches compressible-flow relations |
| Organ-pipe resonance of a closed-open duct | lands on the analytical n·c/4L series |
| Junction under a pulse of 69% of mean pressure | 0.07% error |
| TMM against the nonlinear solver | 0.45 dB |
| FSAE restrictor | chokes at theory |

### Known-answer optimisation benchmarks

| Problem | Result |
|---|---|
| Sphere 4-D, 20 seeds | 20/20 to 1e-8 |
| Rosenbrock 4-D, 20 seeds | 19/20 to 1e-6 |
| Rastrigin 2-D, 20 seeds | 15/20 find the global optimum |
| Ishigami Sobol indices | match the analytic values |
| ZDT1 / ZDT2 fronts | match the known fronts |

### Not validated

Listed because a project that only publishes what works is not publishing
evidence.

| Gap | Why | What to do about it |
|---|---|---|
| Transient spool vs a measured case | No redistributable measured dataset was found (`docs/physics.md` §6.4 records what was checked) | Self-consistency is checked instead; compare against your own logged data before quoting a spool time |
| Measured order levels | No redistributable dataset | Acoustic comparisons are relative between designs, not absolute |
| Diabatic correction vs a measured on-engine outlet temperature | No redistributable dataset | — |
| ISO 532-3, ECMA-418-2, fluctuation strength, DIN 45681 | Each needs verification against reference signals that cannot be redistributed | Character is reported through the metrics that are verified |

**Have dyno data with known geometry — especially FSAE?** Please open an
issue. A measured case with provenance is the most valuable contribution this
project can receive.

---

## Building

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. Everything except `WaveBench.App` is a
cross-buildable class library or the CLI; the desktop app is WPF and therefore
Windows-only.

## Solution layout

| Project | Purpose |
|---|---|
| `WaveBench.Core` | Physics: thermodynamics, 1D solver, components, engine model (no UI, no I/O beyond streams) |
| `WaveBench.Acoustics` | TMM, radiation, order analysis, psychoacoustics, synthesis |
| `WaveBench.Boost` | Turbo/supercharger maps, shaft dynamics, thermal states, boost control |
| `WaveBench.Model` | Serialisable model tree, strongly-typed units, validation rules, provenance |
| `WaveBench.Analysis` | Post-processing, FFT, wave decomposition |
| `WaveBench.Optimize` | DOE, optimisers, surrogates, constraints |
| `WaveBench.ViewModels` | All UI logic and the report generator — plain net10.0, zero UI types |
| `WaveBench.Cli` | Headless runner and scripting entry point |
| `WaveBench.App` | WPF desktop app |

`WaveBench.ViewModels` holding every piece of UI logic with no UI types in it
is what lets the CLI generate the same report the app does, and lets three
quarters of the interface be tested without a window.

Tests: `WaveBench.Core.Tests` (unit), `WaveBench.Verification` (§6.1, per-PR
CI), `WaveBench.Validation` (§6.2, nightly), `WaveBench.Bench`
(BenchmarkDotNet).

## Documentation

| Document | What is in it |
|---|---|
| [`docs/user-guide.md`](docs/user-guide.md) | How to use it, start to finish |
| [`docs/citations.md`](docs/citations.md) | Every correlation, its source and its validity range |
| [`docs/physics.md`](docs/physics.md) | What the models do and the measurements behind them |
| [`docs/acoustics.md`](docs/acoustics.md) | The acoustic engine |
| [`docs/numerics.md`](docs/numerics.md) | Scheme behaviour, bandwidth, mesh guidance |
| [`docs/WaveBench-Master-Plan.md`](docs/WaveBench-Master-Plan.md) | The build contract |
| [`CHANGELOG.md`](CHANGELOG.md) | Every phase, with its measurements |

## Ground rules (from the plan, Part 0)

1. Do not skip phases; every gate must pass before proceeding.
2. TDD is mandatory in the physics layers, tested against analytical or published references.
3. Every empirical correlation is cited in an XML doc comment with its validity range.
4. `WaveBench.Core` never references a UI assembly (enforced by an architecture test).
5. Determinism: same input file → bit-identical results.
6. Docs ship in the same commit as the code.
