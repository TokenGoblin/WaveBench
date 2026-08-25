# WaveBench

Laboratory-grade 1D engine gas-dynamics, acoustics and forced-induction design suite.

- **Platform:** .NET 10 (LTS), Windows-native (WinUI 3 / Windows App SDK)
- **Licence:** Apache-2.0 (to be added in Phase 0)
- **Audience:** Formula SAE teams, race engine developers, professional engine designers
- **Scope:** intake and exhaust wave tuning · camshaft timing · collector configuration and cylinder pairing · exhaust sound design and auralisation · turbocharging and supercharging · multi-objective optimisation

## Status

Pre-Phase-0. The complete build specification lives in
[`docs/WaveBench-Master-Plan.md`](docs/WaveBench-Master-Plan.md) — a staged
build contract with 26 phases, each with a hard acceptance gate. Physics
before pixels: Phases 0–15 produce a headless, test-covered, validated
engine; no UI exists before Phase 16.

## Ground rules (from the plan, Part 0)

1. Do not skip phases; every gate must pass before proceeding.
2. TDD is mandatory in the physics layers, tested against analytical or published references.
3. Every empirical correlation is cited in an XML doc comment with its validity range.
4. `WaveBench.Core` never references a UI assembly (enforced by an architecture test).
5. Determinism: same input file → bit-identical results.
6. Docs ship in the same commit as the code.
