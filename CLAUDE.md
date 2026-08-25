# WaveBench — project instructions for Claude Code

## Build contract

`docs/WaveBench-Master-Plan.md` is the single-document build contract. Read the
relevant parts before writing code. 26 phases, strictly in order, each with a
hard acceptance gate (Part 12). Never let a session span two phases.

**Phase status:** Phases 0–1 complete. Next: Phase 2 (1D solver core:
HLLC, MUSCL, Sod/Lax/123 verification).

Non-negotiables (plan Part 0): TDD in physics layers · cite every empirical
correlation in an XML doc comment with source and validity range ·
`WaveBench.Core` never references a UI assembly · determinism (same input →
bit-identical results) · docs in the same commit as the code · one model, many
lenses.

## Public-repo sanitization — CRITICAL

This repository is public at `https://github.com/TokenGoblin/WaveBench`. The
owner's identity, machine and network must never appear in it:

- Git author/committer for every commit MUST be
  `TokenGoblin <42847007+TokenGoblin@users.noreply.github.com>`. This is set in
  the repo-local git config — never override it with a personal name or email,
  and never commit with `--author` pointing at a real identity.
- No absolute local paths (`C:\Users\...`, drive letters, home directories) in
  code, tests, docs, configs, scripts, commit messages or CI workflows. Use
  relative paths from the repo root.
- No hostnames, LAN addresses, internal service URLs or other details of the
  owner's local network.
- No real names, personal email addresses or non-GitHub account identifiers.
- Machine-generated files that may embed local paths (test logs, `.user` files,
  crash dumps, BenchmarkDotNet artifacts) stay untracked — extend `.gitignore`
  rather than committing them.
- Before committing: scan the staged diff for the above. Before pushing:
  `git log --format='%an <%ae>' origin/main..` must show only the TokenGoblin
  identity.
- User-supplied audio recordings and personal measurement data are never
  committed (see plan §3.7; already gitignored).

## Conventions

- .NET 10, `Directory.Build.props` enforces nullable + warnings-as-errors
  solution-wide. Do not suppress warnings per-project without a comment.
- Tests: xUnit in `tests/`. Unit tests in `WaveBench.Core.Tests`, verification
  cases (§6.1) in `WaveBench.Verification`, validation cases (§6.2) in
  `WaveBench.Validation` (nightly CI), benchmarks in `WaveBench.Bench`.
- Units: all physics code uses the strongly-typed quantities in
  `WaveBench.Model.Units` (canonical SI internally). Never pass raw doubles
  with implicit units across public APIs.
- CI must be green before a phase gate is declared passed.
