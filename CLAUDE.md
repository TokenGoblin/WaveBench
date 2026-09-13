# WaveBench — project instructions for Claude Code

## Build contract

`docs/WaveBench-Master-Plan.md` is the single-document build contract. Read the
relevant parts before writing code. 26 phases, strictly in order, each with a
hard acceptance gate (Part 12). Never let a session span two phases.

## START HERE — where the project stands

**24 of 26 phases complete. 988 tests green, none skipped. CI green on main.**

| Phase | State | Notes |
|---|---|---|
| 0-10 | done | core gas dynamics, thermo, engine, acoustics |
| **11** | **PARTIAL** | **blocks v0.4** — see "What is left" below |
| 12 | done | turbo maps, steady matching, map digitiser |
| 13 | done | coupled unsteady forced induction — docs/physics.md §4 |
| 14 | done | forced-induction engine behaviour — docs/physics.md §5 |
| **15** | **PARTIAL** | transient + FI acoustics · **v0.6** — gate clause 1 open, see below |
| 16-20 | done | shell, Design, Manifold canvas, Results, Sound |
| 21 | done | Boost workspace · **v0.9** — docs/physics.md §7 |
| 22 | done | Optimisation · CMA-ES, NSGA-II, Bayesian, screening, refiners, presets |
| 23 | done | Simple mode and the wizard |
| **24** | **NEXT** | Learn layer and guardrails |
| **25** | to do | Reporting, docs, packaging · **v1.0** |

**PHASE ORDER WAS USER-REORDERED.** See the note further down for why; the
remaining phases (24, 25) are back in plan order, so nothing is pending on
the reordering except Phase 11's psychoacoustic metrics and Phase 15's gate
clause 1, both listed below.

### What is left, in the order the user chose

1. **Phase 15's gate clause 1 — transient spool within 15% of a measured
   case.** Everything else in Phase 15 is built and tested: Stage A (turbine
   four-pole, OPI drop, blade-pass, whoosh, surge flutter derived from a new
   Greitzer surge model, wastegate/BOV noise, an Intake tab in Sound — gate
   clauses 2 and 3 met, docs/acoustics.md §5) and Stage B (`TransientDriver`
   coupling the gas dynamics to shaft/thermal/compressor state under a
   scripted throttle profile, `TimeToTorqueResult`'s sensitivity band,
   repeat-run heat soak reaching the boost air — docs/physics.md §6). Clause
   1 stays open: a bounded search found no redistributable measured
   transient-spool dataset (docs/physics.md §6.4 has the full account of what
   was checked and ruled out). Self-consistency (mesh convergence, energy
   balance, sensitivity-band behaviour, the heat-soak effect against a
   no-carry-over control) is what CI actually checks in its place. Close this
   only if a suitable licensed dataset turns up — see the standing deferral
   below (validation case 20).
2. **Phase 24 — Learn layer. PICK UP HERE.** Breadth, not depth: "why" text
   on every field, "Show me" parametric sweeps, a Concepts panel, "Explain this
   result", guided tours, implausible-input detection, a generic-defaults
   banner, global search, and cross-workspace warning links.

   Much of the raw material exists: `DesignCatalogue` and `BoostCatalogue`
   already carry per-field `Help`, `OptimisationCatalogue` carries a `Why` for
   every variable, and `DesignWarning` already carries a citation and a
   cross-link. The gate asks that EVERY user-editable field have why-text and a
   typical range, that "Show me" work on every numeric parameter in the solve,
   and that every design warning link to the field or plot causing it — so the
   work is largely completing coverage and adding the sweep machinery.
3. **Phase 25 — Reporting, docs, release (v1.0).**
4. **Phase 11's four psychoacoustic metrics** — ISO 532-3, ECMA-418-2,
   fluctuation strength, DIN 45681. Deferred for a REASON, not skipped: each
   needs verification against published reference signals and none are
   redistributable. This is the only backwards gap and it blocks the v0.4 tag
   while everything after it is built. Decide with the user whether to hunt for
   anchors or re-scope the milestone.

### Standing deferrals (all deliberate, all stated in docs)

- Bassett 2001 UNSTEADY junction coefficients — branch-angle dependence
  currently carried by the Idelchik wye forms.
- Phase 20's **Compliance tab** — needs an absolute radiated level from a solved
  run; the instant model has order structure but no absolute level, and a
  verdict from it would be a number with nothing behind it.
- Yin-case short-runner discrepancy (docs/physics.md §1.9).
- SIMD flux kernels, for when 3000-cell collector networks arrive
  (docs/numerics.md §6).
- Validation cases needing measured data that is not here: 12 (measured order
  levels), 20 (transient spool), 21 (diabatic correction vs a measured
  on-engine outlet temperature).
- Twin-scroll partial-admission efficiency coefficient — follows the published
  trend, fitted to no dataset, exposed for calibration.

Per-phase detail and every committed figure live in `docs/physics.md`,
`docs/acoustics.md`, `docs/numerics.md` and `CHANGELOG.md`. Do not duplicate
them here; this file is for what a new session needs to avoid repeating a
mistake.

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

The SHIPPED library (`TurboLibrary`, five sizes, what the Boost workspace
draws) is analytic for the same reason, and every entry says so in its own
`Source` and `Licence`. It is deliberately NOT shared with `SyntheticTurbo`:
a verification anchor built from product code would agree with whatever the
product code happens to do.

**A map's reference conditions are required and never defaulted.**
`MapReference` has no default and `CompressorMap.Load` refuses a file without
one. Do not "helpfully" fall back to a standard day: the two common gas-stand
references are 1.69% apart in corrected speed before that propagates into
pressure ratio, and the error is invisible in the answer.

**Simple mode's Overview IS the wizard**; Advanced mode's Overview is the
summary. Same document under both, so the toggle is navigation and never a
conversion.

**A CONDITIONAL WORKSPACE'S CONDITION IS A DOCUMENT FIELD, never a shell
flag.** `ShellViewModel.HasForcedInduction` is derived from
`ForcedInduction.Aspiration`, which is an ordinary field edited in Design →
Engine. That single choice makes undo/redo, provenance, save/load and the
command palette work with no extra wiring — and it removes the failure a flag
would guarantee: a turbocharged project loaded from disk showing no Boost
workspace until something remembered to set the flag.

**`ShaftBalance.Match` is the GATE-SHUT answer. The engine does not run
there.** It finds where the shaft settles with nothing bled off, which for any
turbo below its own limit is well above the boost target. Drawing that on the
compressor map puts the operating line, both margins, the charge temperature
and the shaft-speed check at the exact condition the wastegate exists to
prevent. `BoostWorkspace.HoldToTarget` applies the gate — solve for the speed
that makes the target ratio, then for the expansion ratio giving exactly that
speed's power — and keeps both points: controlled for every figure, wide-open
only for the Control tab, where the difference IS the setup decision.
docs/physics.md §7.2.

LESSON: this was found by LOOKING at the rendered screen, not by a test. Every
test passed; the numbers were all finite, in range and self-consistent. What a
screenshot showed was a 2-litre running 60 kPa over its own target with the
shaft past its rated speed — obviously wrong to anyone who has matched a turbo,
and invisible to an assertion that only asks whether the arithmetic closed.
Render the screen before declaring a UI phase done.

**A CONSTRAINT IS NOT AN OBJECTIVE WITH A BIG WEIGHT.** Phase 22's gate says
the clearance constraint is *never* violated in a returned design — never, not
rarely. So `ScoredDesign.ScalarScore` orders lexicographically: every
infeasible design sits above every feasible one, ordered among themselves by
how badly they break. A penalty large enough to dominate also flattens the
objective landscape inside the feasible region; one small enough not to is one
the optimiser will happily pay. Both failures are avoided by not using a
penalty at all. Measured: 30 runs against a constraint placed through the
unconstrained optimum, 0 violations, 30 landing on the bound.

**A SURROGATE MAY COARSEN THE MESH. IT MAY NOT DROP OPERATING POINTS.**
Area-under-torque integrated over every second rpm is not a cheaper version of
the objective — it is a DIFFERENT objective, so the surrogate optimises
something the solve is not measuring. Measured on the FSAE intake case:
halving the points dropped the surrogate's Spearman rank correlation against
the solve to 0.68 and swapped two of seven designs, while coarsening the mesh
from 1x to 2x moved the correlation by nothing at all. With the points held
fixed, 2x coarsening ranks designs IDENTICALLY (Spearman 1.000) and is still
3.3x faster. Cheapness belongs in how well each point is resolved, never in
which points exist.

LESSON: my own doc comment named this risk ("a narrower band is a different
objective, not a cheaper one") and the implementation then guarded only the
band's ENDS while thinning its interior. Writing the hazard down is not the
same as defending against it — the test that measured the correlation is what
caught it.

**VALVE-TO-PISTON CLEARANCE IS NOT A TDC CHECK.** The piston is highest at
TDC, but both valves are near their seats there — the pinch point is 10-20
degrees either side, where the piston has barely dropped and the valve is
substantially open. Measured on the test engine: 2.64 mm at TDC against a true
minimum of 2.39 mm at +6 degrees. `ValveClearance.Minimum` sweeps 270-450
degrees for that reason. A check evaluated at TDC alone reads as a safety
limit while permitting exactly the collision it appears to prevent.

The clearance available at TDC with the valve shut is a STATED parameter, not
a derived one: the schema carries no piston dome or valve-pocket geometry.
Do not invent one — a clearance constraint computed from a guessed pocket
depth is worse than no constraint.

**NEVER USE `Progress<T>` IN A TEST THAT ASSERTS ON WHAT IT COLLECTED.** It
posts callbacks to the captured SynchronizationContext, and a test has none —
so they go to the thread pool, arrive OUT OF ORDER, and race on whatever they
are appended to. A progress test written that way passed alone and failed only
under the full suite's parallelism. `SyntheticProblems.Immediate<T>` reports on
the calling thread; use it. (The app is unaffected: its `Progress<T>` is
created on the UI thread, so callbacks are posted in order to the dispatcher.)

**AN ARCHIVE MUST FILL AS IT GOES, NOT IN BULK AT THE END.** Every search
used to hand its history to `DesignArchive` once it finished, which works
until a run is CANCELLED and the hand-over never happens — six evaluations in,
one out. The hook is now `OptimisationProblem.Observed`, called for every
design the problem scores, so one mechanism covers all five algorithms and any
added later, and a cancelled run keeps its work by construction. The same
argument applies to anything else accumulated during a long run.

**BAYESIAN OPTIMISATION LOSES ON HUGE DYNAMIC RANGE, AND THAT IS MEASURED.**
It beats CMA-ES at 40 evaluations on sphere 3-D (12/12 seeds), sphere 6-D
(12/12), Rastrigin 3-D (10/12) and Rosenbrock on a tight box (10/12). On
Rosenbrock over [-5,5]^3 — five orders of magnitude of range — it wins only
5/12 and CMA-ES's median is better. Cause: one shared length scale with
standardised outputs, where the standardisation is dominated by the extremes
and the near-optimal region compresses into numerical noise. Narrowing the box
to a range of ~3600 restores 10/12, which is what IDENTIFIES the cause rather
than guessing it. Real objectives here are the narrow case (area under torque
varies a few percent), but if a future objective has enormous range, reach for
CMA-ES or fix the surrogate. `BayesianTests` keeps both measurements.

LESSON: my first version of that test asserted "BO wins >= 8 of 12" on the
wide box because that is what I expected. It won 6. The fix was to MEASURE
across six problems and three budgets, then write the assertion to match
reality and keep the losing case as its own test. Do not tune a threshold
until a test passes; find out what is true and assert that.

**CMA-ES is rank-based, and that is load-bearing here.** It uses only the
ORDER of the candidates, never the objective values, which is what makes the
enormous numeric gap between the feasible and infeasible score bands harmless.
Do not "simplify" the score into something continuous to help a future
optimiser — check that optimiser is rank-based first.

**Surge on a steady wide-open line needs BOTH halves of the mistake.** An
oversized compressor alone never surges here, because the steady shaft balance
is self-limiting: the shaft only turns as fast as the exhaust drives it, so an
oversized wheel simply fails to spool. It takes an oversized compressor AND a
small turbine housing — the shaft driven hard against a mass flow the
restrictor has already capped. Do not "fix" a surge test that will not surge
by loosening the threshold; find the configuration that genuinely does it.

**Why the phase order was changed** (the order itself is in START HERE above).
The user asked for 19 -> 20 -> 23 first, then the forced-induction block, to get
a complete naturally-aspirated tool sooner. Phase 20's acoustics engine (8-11)
was already built and Phase 23's wizard works NA-only, so nothing on that path
blocked on turbo work. Do NOT silently revert to plan order.

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

**Boost owns the forced-induction fields the same way** (`BoostCatalogue`), and
the schema walk accepts EITHER catalogue - the invariant is "reachable from
some workspace", not "on the Design screen". Both screens share one
`FieldEditor` (parse, convert, validate, write through the session) and one
row renderer (`WorkspaceContent.AddFieldRow`, taking `IFieldEditingSurface`),
so there is still exactly one unit boundary. A second copy would be a second
boundary that rounds differently.

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

**...and NOTHING THAT FIRES UNDER A HELD POINTER MAY TRIGGER THAT REBUILD.**
Two names, one convention: `refresh` is the full rebuild, anything else
(`redraw`, `editField`, `ShowFrame`) is targeted. A Slider's ValueChanged fires
while the mouse is down; rebuild the tree from it and WPF disconnects the
capturing Thumb, drops capture, cancels the drag and hands back a new slider
holding nothing - one step per click, no drag. Same for a TextBox committing on
LostFocus: that fires AFTER focus has moved, so a rebuild destroys the control
the user just clicked into and swallows the click. Sliders and text boxes
therefore live in the CHROME and only a `body` panel is rebuilt.
`ContentHostTests.Gate_no_slider_rebuilds_the_tree_it_is_being_dragged_in`
enforces it.

**An async continuation must check it is still on screen before re-rendering.**
`Navigate` never cancels a job, so the wizard's compute can finish while the
user is looking at Results - and an unguarded `refresh()` then replaces the
Results body while the rail and title still say Results.

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

Deferrals are listed once, in START HERE at the top of this file.

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
