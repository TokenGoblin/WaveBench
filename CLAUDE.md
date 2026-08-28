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

**Phase 12 complete** (turbomachinery data and steady matching: map schema in
SAE J1826 corrected quantities, compressor and turbine models with physical
extrapolation, shaft balance, surge/choke margins, turbo library with
auto-match ranking, and the map digitiser). All three gate clauses met: map
round-trip is bit-identical and refuses a map with no reference conditions;
shaft balance closes to 1e-3 (the bisection's own tolerance) and the adiabatic
relations are checked against hand calculations, not against the model; a
2.0-litre four on the synthetic 60 mm unit gives a plausible operating line
(PR 1.11 -> 3.00, shaft 35k -> 141k of 165k rated, back-pressure ratio 0.99 ->
0.65); the digitiser reads back an analytic map from a 900x700 PNG to within
0.63% on efficiency and 0.08% on pressure ratio, against a 2% gate.

**Phase 13 complete** (coupled unsteady forced induction: the rotor as a duct
end boundary, quasi-steady and volute-resolved turbine models, twin-scroll
partial admission and the firing-order pairing rule, shaft dynamics, the
three-node turbocharger thermal model and its diabatic correction, VGT,
wastegate with scroll-division loss, BOV/recirculation, charge cooler with
thermal mass, boost control). All four gate clauses met: hysteresis loop 27x
wider resolved than quasi-steady and widening with amplitude and frequency;
twin-scroll separation index 0.000 correct against 1.000 wrong from firing
order alone; diabatic correction recovers a known aerodynamic efficiency to
0.2% and gives 20.6 K over adiabatic at low flow; volute-resolved runtime
0.76x against a 2x gate. See docs/physics.md §4.

**Phase 14 complete** (forced-induction engine behaviour: fresh-charge tracking,
scavenging pressure ratio, blow-through and its fuel/TIT cost, superchargers,
electric assist, the FSAE restrictor, ambient sensitivity). Both gate clauses
met: the NA and boosted cam optima diverge by 15 degrees of lobe centre (20 vs
50 degrees of overlap) and the scavenging output explains it - NA below a
pressure ratio of 1 everywhere, boosted above it everywhere; the FSAE choke
ceiling matches hand calculation at 0.0715 kg/s and a choked restrictor is shown
turning shaft speed into a surge trajectory. See docs/physics.md §5.

**Blow-through is a BRACKET, not a prediction.** The cylinder is single-zone, so
it mixes perfectly - the lower bound, under 1% here where a measured DI turbo
shows several. `ScavengingAnalyser.ShortCircuitFraction` gives the
perfect-displacement upper bound. Do not "improve" the default away from 0: what
lies between the bounds is port and chamber geometry a 1D solver cannot resolve,
and picking a number would be inventing one. The bracket IS charged to net
torque through the fuel it costs, which is what stops the optimiser buying
scavenging it cannot have.

LESSON: the flame first consumed fresh charge as `mass * (1 - dxb)` per step,
which compounds to exp(-0.9933) = 0.37 and reported 35% blow-through on an
engine with ZERO overlap. Consumption is proportional to the charge present at
ignition, not a repeated fraction of what remains. A metric that is non-zero
where the mechanism cannot operate is the cheapest defect signal there is.

**A duct end boundary must admit BACKFLOW.** An exhaust outlet does not only
blow - between pulses the manifold falls below what is downstream and gas comes
back. `RotorNozzleBoundary` delegates that case to `ReservoirBoundary` rather
than extending the outflow isentrope backwards. A sign error that suppressed it
made the boundary a check valve: the engine drew a quarter of the air it should
have and primaries went to NaN at the junction.

LESSON: the failure got WORSE under mesh refinement. That is the signature of an
ill-posed boundary, not an under-resolved one - and it is the fastest way to
tell those two apart. (The junction was suspected first and measured innocent:
0.07% error on a pulse of 69% of mean pressure. `JunctionUnderPulseTests` keeps
that number on record.)

**Turbo maps are SYNTHETIC in this repo and must stay that way.** Plan §4.7:
ship no manufacturer maps without written permission, and that applies to the
test suite. `SyntheticTurbo` is an analytic surface, which is also the better
verification anchor - a test can ask what the answer SHOULD be instead of
comparing two readings of the same picture.

**A map's reference conditions are required and never defaulted.**
`MapReference` has no default and `CompressorMap.Load` refuses a file without
one. Do not "helpfully" fall back to a standard day: the two common gas-stand
references are 1.69% apart in corrected speed before that propagates into
pressure ratio, and the error is invisible in the answer.

**Simple mode's Overview IS the wizard**; Advanced mode's Overview is the
summary. Same document under both, so the toggle is navigation and never a
conversion.

**PHASE ORDER IS USER-REORDERED: 19 -> 20 -> 23 (all done), then the
forced-induction block (12, 13 and 14 done, 15 next), then 21/22/24/25.** The
user chose this to get a complete naturally-aspirated tool sooner. Phase 20's acoustics engine (8-11) is already
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

**A workspace renderer must CLEAR its host before adding to it.** Every
`Render(Panel host, ...)` hands its children a `Refresh` closure that calls
straight back into itself, so a sub-tab, a slider or a Next button re-enters the
same method. Three of the four renderers appended without clearing, so each
click stacked a whole second copy of the workspace below the first, inside a
StackPanel, off the bottom of the viewport - the stale copy stayed put and the
app looked frozen. `ContentHostTests` scans the App source and fails if any
renderer omits the clear or does it after the first Add.

LESSON: the user reported "buttons don't do anything and don't change colour".
The colour half was real (there was no Button style at all) and fixing it
changed nothing, because it was not the cause. What settled it was counting
buttons in the LIVE window through UI Automation - 81 of them, with the Sound
workspace present five times over. When a UI symptom is vague, enumerate the
actual visual/automation tree before theorising about styles or handlers.

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
