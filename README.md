# WaveBench

[![CI](https://github.com/TokenGoblin/WaveBench/actions/workflows/ci.yml/badge.svg)](https://github.com/TokenGoblin/WaveBench/actions/workflows/ci.yml)

Laboratory-grade 1D engine gas-dynamics, acoustics and forced-induction design suite.

- **Platform:** .NET 10 (LTS), Windows-native (WinUI 3 / Windows App SDK)
- **Licence:** Apache-2.0
- **Audience:** Formula SAE teams, race engine developers, professional engine designers, DIY engine enthusiasts
- **Scope:** intake and exhaust wave tuning · camshaft timing · collector configuration and cylinder pairing · exhaust sound design and auralisation · turbocharging and supercharging · multi-objective optimisation

**No telemetry. No network calls at runtime.** Your designs, dyno data and
audio never leave your machine.

## Status

**Phase 0 complete** (foundations: solution scaffolding, CI, units layer).
The complete build specification lives in
[`docs/WaveBench-Master-Plan.md`](docs/WaveBench-Master-Plan.md) — a staged
build contract with 26 phases, each with a hard acceptance gate. Physics
before pixels: Phases 0–15 produce a headless, test-covered, validated
engine; no UI exists before Phase 16.

## Building

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. The desktop app project is a placeholder until
Phase 16; everything else is cross-buildable class libraries plus a CLI.

## Solution layout

| Project | Purpose |
|---|---|
| `WaveBench.Core` | Physics: thermodynamics, 1D solver, components, engine model (no UI, no I/O beyond streams) |
| `WaveBench.Acoustics` | TMM, radiation, order analysis, psychoacoustics, synthesis |
| `WaveBench.Boost` | Turbo/supercharger maps, shaft dynamics, thermal states, boost control |
| `WaveBench.Model` | Serialisable model tree, strongly-typed units, validation rules, provenance |
| `WaveBench.Analysis` | Post-processing, FFT, wave decomposition |
| `WaveBench.Optimize` | DOE, optimisers, surrogates, constraints |
| `WaveBench.Cli` | Headless runner and scripting entry point |
| `WaveBench.App` | WinUI 3 desktop app (Phase 16+) |

Tests: `WaveBench.Core.Tests` (unit), `WaveBench.Verification` (§6.1, per-PR
CI), `WaveBench.Validation` (§6.2, nightly), `WaveBench.Bench`
(BenchmarkDotNet).

## Ground rules (from the plan, Part 0)

1. Do not skip phases; every gate must pass before proceeding.
2. TDD is mandatory in the physics layers, tested against analytical or published references.
3. Every empirical correlation is cited in an XML doc comment with its validity range.
4. `WaveBench.Core` never references a UI assembly (enforced by an architecture test).
5. Determinism: same input file → bit-identical results.
6. Docs ship in the same commit as the code.
