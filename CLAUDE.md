# WaveBench — project instructions for Claude Code

## Build contract

`docs/WaveBench-Master-Plan.md` is the single-document build contract. Read the
relevant parts before writing code. 26 phases, strictly in order, each with a
hard acceptance gate (Part 12). Never let a session span two phases.

**Phase status:** Phases 0-10 complete; Phase 11 PARTIAL (compliance done;
ISO 532-1 loudness and DIN 45692 sharpness done and verified; ISO 532-3,
ECMA-418-2, fluctuation strength and DIN 45681 outstanding - see
PsychoacousticStatus and docs/acoustics.md §4); **Phases 16 and 17 complete**
(GUI pulled forward per the Part 12 allowance; Phase 17's gate verified by
DesignGateTests - a model built through the workspace edit API alone runs
bit-identically to the same model from the CLI). **Phase 18 complete**
(manifold node graph, all nine §2.8 configurations, canvas view model +
WPF canvas, per-node inspector, cited design warnings, pulse-interference
diagram on the solved sound speed; 60 fps gate met at p99 0.68 ms against
16.67 ms with 40 components). **Phase 19 complete** (Results workspace: wave
decomposition verified against the textbook reflection, x-t wave diagram with
scrub and animation, per-cylinder charts with EGT and knock, PNG+SVG export of
every plot, Run wired to the solver; animation gate met at p99 0.0023 ms).

**Phase 20 complete** (Sound workspace: the M50 factory-vs-6-1 comparison
reproduced from geometry and firing order alone, order spectrum/waterfall/
character/timing figures, "Explain this" with an exact cause split, TMM
silencing with live sliders, level-matched gapless A/B audition. All three gate
clauses met: slider p99 8.4 ms against 50 ms; A/B matched to -23 LUFS with no
click; M50 table reproduced.) NOT built: the Compliance tab, which needs a
radiated level from a solved run - the instant model has order structure but no
absolute level, and a verdict from it would be a number with nothing behind it.

**Phase 23 complete** (Simple mode and the wizard: nine steps with explainers
and a live preview, the derivation layer filling the full model, the bounded
search, the Design Brief with why/confidence/uncertainty and a build list, PDF
export, "Open in Advanced"). Checkable gate half met: the brief's numbers are
bit-identical to Advanced mode from the same document, first preview 0.0 ms
against a one-second budget. The usability half - a novice reaching a brief in
15 minutes - is not something a test settles.

**Simple mode's Overview IS the wizard**; Advanced mode's Overview is the
summary. Same document under both, so the toggle is navigation and never a
conversion.

**PHASE ORDER IS USER-REORDERED: 19 -> 20 -> 23, then the forced-induction
block (12-15), then 21/22/24/25.** The user chose this to get a complete
naturally-aspirated tool sooner. Phase 20's acoustics engine (8-11) is already
built and Phase 23's wizard works NA-only, so nothing in that path blocks on
turbo work. Do NOT silently revert to plan order.

**Plots are DATA.** `PlotModel` in WaveBench.ViewModels.Plotting describes a
figure; `PlotView` (WPF) and `SvgPlotWriter` both render it. Never draw a chart
directly in the app - an export that does not match the screen is the bug this
design exists to prevent. Series name COLOUR TOKENS, never colours.
`ResultsWorkspace.AllPlots()` is what export-all and the report generator walk,
so a new figure must be added there too.

**Manifold canvas:** all behaviour is in `ManifoldWorkspace` (zero UI types);
`ManifoldCanvas.cs` in the app only draws and forwards gestures. The canvas
edits the graph as a VALUE - Draft() deep-copies, Commit() writes back through
the session - because mutating in place leaves undo with two references to one
object and nothing to restore (§8.11). Never call `_refresh()` from a mouse
press handler: a refresh rebuilds the surface, which destroys the element
mid-drag along with its mouse capture.

**Design workspace:** field metadata is DATA in `DesignCatalogue`, not
branches in a renderer - add a field there and it appears, converts units and
validates. A reflection test walks the document schema and fails if any
editable property is unreachable from the UI, so the Phase 17 gate cannot rot.
`DesignWorkspace` holds all behaviour; `WorkspaceContent` only builds controls.
Unit conversion happens ONLY at that boundary.

**UI framework:** WPF, not WinUI 3 - no Windows App SDK workload here and
unpackaged WinUI needs its runtime present. The plan sanctions WPF as the
fallback. ALL UI logic lives in WaveBench.ViewModels (plain net10.0, zero
UI types), so switching heads is a new XAML layer, not a rewrite. Never put
logic in WaveBench.App beyond view construction.

**Colour rule:** Tokens.xaml is the ONLY file that may contain a colour
literal - three tests enforce it, including one that resolves every
resource key because XAML lookups fail at runtime, not compile time.

**Never drive synthetic mouse/keyboard input at the desktop to capture the
app.** Use `WaveBench.App.exe --screenshot <dir>`, which renders the
visual tree offscreen.

**Never edit files with `Get-Content | ... | Set-Content` in PowerShell 5.1.**
`Get-Content` reads UTF-8 as the ANSI codepage and `Set-Content -Encoding
UTF8` writes it back double-encoded plus a BOM, so every `§ × → —` in the
file silently becomes `Â§ Ã— â†' â€"`. The source is full of them (plan
references, units). Use the Edit tool, or `[IO.File]::ReadAllText/WriteAllText`
with `New-Object Text.UTF8Encoding $false`. Same reason commit messages go
via `git commit -F` with a UTF-8-no-BOM file.

**And do not diagnose mojibake from PowerShell's own output.** The console
prints UTF-8 files through the ANSI codepage, so a perfectly good `§` shows
up as `Â§` in `Get-Content` / `Select-String` results. "Repairing" that
round-trips valid UTF-8 through CP1252 and destroys the character for real.
Check the bytes — `[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8)` — or
just use the Read tool, before concluding a file is damaged.

**Cylinders must not burn on their first step.** The Wiebe increment is
`xb - _previousBurnFraction`, and a cylinder whose first step lands PAST its
burn window would see xb ~= 0.9933 against a stored zero and dump the whole
cycle's fuel at once. On a four-cylinder engine two of the four start past the
window on every run, one of them mid exhaust-stroke with its valve open, and
they detonated into a cold pipe at 14 bar on the first degree of crank. A wide
pipe absorbed it; a narrow one went to negative density and took the solve to
NaN. `Cylinder.Step` seeds `_previousBurnFraction` from the starting angle;
`NarrowPrimaryTests.Gate_no_cylinder_burns_fuel_it_never_had` guards it.

LESSON FOR THE NEXT ONE OF THESE: the failure threshold coincided exactly with
valve throat area crossing pipe area, which made a flow-limit explanation look
obvious and cost three wrong fixes to the valve/duct coupling. What actually
identified it was varying something the theory said was irrelevant - cylinder
COUNT - and finding that 1 and 2 survived where 4 did not. When a hypothesis
fails twice, stop refining it and go looking for a variable it does not mention.

**Duct source terms (fixed, keep them wired).** `EngineBuilder.ApplyThermal`
equips EVERY duct with Haaland friction and a Colburn wall node from
`document.PipeThermal`. For a long time nothing did, and every pipe in the
product ran adiabatic and frictionless while Phase 3's component gates passed -
so `PipeThermalTests.Gate_every_duct_in_a_built_engine_has_friction_and_a_wall`
exists to stop that recurring. Wall temperature is solved BETWEEN cycles
(`WallUpdate.CyclicSteady`), never integrated within them: a steel wall's time
constant is ~10 s against a 20 ms cycle. The intake wall is held fixed by
design - see docs/physics.md §1.11 before changing that.

**Committed performance figures live in docs/physics.md, CHANGELOG.md and the
app's Overview tiles + TorqueCard arrays.** Any physics change moves them; the
sweep behind the tiles is `wavebench sweep examples/single-360.json --from 3000
--to 9000 --step 500`.

Known deferrals: ISO 532-3 / ECMA-418-2 / fluctuation strength / DIN 45681
(Phase 11 - no verification anchors available; ISO 532-1 and DIN 45692 are
done and verified); Bassett 2001 UNSTEADY junction coefficients (branch-angle
dependence is now carried via the Idelchik wye forms); Yin-case short-runner discrepancy
(docs/physics.md §1.9); SIMD flux kernels when 3000-cell collector networks
arrive (docs/numerics.md §6).

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
- Units: everything is SI internally, everywhere. The strongly-typed
  quantities in `WaveBench.Model.Units` are mandatory at model/UI/data
  boundaries (user inputs, model files, reports). Core physics kernels
  (per-cell solver math, property evaluation) use raw SI doubles for hot-path
  performance, with the unit stated in the XML doc of every parameter. Where a
  correlation's native unit differs from SI (e.g. Douaud-Eyzat in atm) or a
  quantity is commonly quoted in non-SI units, ALSO provide a typed overload
  (see KnockModel.InductionTime, FlameSpeed.Laminar) so callers outside the
  hot path get compile-time unit safety.
- CI must be green before a phase gate is declared passed.
