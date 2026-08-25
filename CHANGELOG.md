# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
