# WaveBench user guide

This is the whole of what you need to use WaveBench. It starts with getting a
torque curve out of it, which takes about ten minutes, and then explains each
workspace in the order you will meet them.

If you read nothing else, read [Fifteen minutes to a torque
curve](#fifteen-minutes-to-a-torque-curve) and [What the numbers are worth](#what-the-numbers-are-worth).

---

## Contents

1. [Fifteen minutes to a torque curve](#fifteen-minutes-to-a-torque-curve)
2. [What the numbers are worth](#what-the-numbers-are-worth)
3. [The two modes](#the-two-modes)
4. [Workspaces](#workspaces)
5. [The learn layer](#the-learn-layer)
6. [The headless CLI](#the-headless-cli)
7. [File formats and exports](#file-formats-and-exports)
8. [Troubleshooting](#troubleshooting)

---

## Fifteen minutes to a torque curve

### 1. Install (2 minutes)

Download the release for your platform and run it. There is no installer step
that asks about services or telemetry, because there is neither: WaveBench
makes no network calls at runtime and your models, dyno data and audio never
leave your machine.

To build from source instead you need the .NET 10 SDK:

```
dotnet build
dotnet test
```

### 2. Open a template (1 minute)

WaveBench opens on a worked example — a 360 cc tuned single — so there is
something to look at before you have typed anything. Five templates ship:

| Template | What it is for |
|---|---|
| FSAE 600 cc four | The restricted four most Formula Student cars run |
| FSAE 450 single | The single-cylinder alternative |
| NA V8, 4-2-1 | A naturally aspirated V8 with a tri-Y header |
| Turbo inline-4 | A boosted road-car layout |
| Blank | Nothing assumed |

Pick the one closest to your engine. Everything in it is editable and every
field says where its value came from.

### 3. Describe your engine (5 minutes)

Go to **Design**. Four tabs, in the order they matter:

- **Engine** — bore, stroke, rod length, compression ratio, cylinder count.
  These fix displacement and the thermodynamic ceiling; nothing downstream can
  make up for getting them wrong.
- **Head & Cam** — valve sizes, lift, and the four cam events. Of these,
  **intake closing** moves volumetric efficiency most.
- **Manifold** — runner and primary lengths and diameters, and the collector
  if you have one.
- **Fuel & Combustion** — fuel, λ, spark advance, burn duration, ambient.

Every field has a sentence saying what moving it does and a typical range.
Hover the label. If you type something unusual — not wrong, just unusual —
the field says so and **keeps your value**: WaveBench warns, it does not
block.

If you do not know a number, leave it. The default is a stated derivation, not
a guess, and the report will tell you which numbers you never set.

### 4. Run a sweep (3 minutes)

Go to **Run**, set the speed range, press **Run**. A four-cylinder sweep of
ten points takes a minute or two on a modern laptop; the window stays usable
while it works.

Go to **Results**. The torque and power curves are the first figure. That is
the converged torque curve, and you are done with the quick start.

### 5. Check it is real (2 minutes)

A torque curve is not evidence until you know it is a property of the engine
rather than of the grid it was solved on. From the CLI:

```
wavebench mesh my-engine.json --rpm 7000
```

or tick **Include mesh-sensitivity evidence** when you generate a report. It
solves the same point at half and double the cell size. If the answer moves by
more than 1%, refine the mesh before quoting the number — the tool says so
rather than leaving you to notice.

---

## What the numbers are worth

Read this before you act on anything WaveBench tells you.

**The largest error is almost never the physics.** It is the data you did not
supply. A banner at the top of every screen names the biggest one for your
model, and the report lists all of them with what would remove each.

The usual order, worst first:

1. **Discharge coefficients are generic.** Valve flow comes from a generic
   C_d curve, not from your head. Two heads with the same valve sizes can
   differ by 10% in flow at high lift, and torque follows flow almost
   proportionally. Flow-bench data fixes this and nothing else does.
2. **Cam profiles are analytic** unless you import measured lift. The area
   under an analytic profile is close; the opening and closing ramps are not,
   and that is where timing sensitivity lives.
3. **Turbo maps are analytic.** Every turbocharger in the shipped library is
   an analytic surface — this tool ships no manufacturer maps without written
   permission. Trends are right; absolute efficiencies are representative.
   Digitise the real map from a published sheet to fix it.

**Blow-through is reported as a bracket, not a prediction.** The cylinder is
single-zone and mixes perfectly, which is the lower bound; the
perfect-displacement upper bound is reported beside it. What lies between is
port and chamber geometry a one-dimensional solver cannot resolve. The fuel
cost of the upper bound is charged to net torque, so an optimiser cannot buy
scavenging your engine may not have.

**Acoustic results are relative.** WaveBench resolves order structure and
transmission loss, which is what a choice between two exhausts turns on. It
does not produce an absolute dB(A) at a measurement point, so it states no
pass or fail against a rules limit.

**What has been validated against something outside this tool** is listed in
[`docs/citations.md`](citations.md) and restated in every generated report,
along with what has not.

---

## The two modes

**Simple** mode's Overview *is* a wizard: nine steps, each with an explainer
and a live preview, ending in a Design Brief with a why, a confidence and an
uncertainty band for every recommendation.

**Advanced** mode's Overview is a summary, and every field is on screen.

The same document underlies both, so the toggle is navigation and never a
conversion. Switching modes cannot change your model — and a banner names any
advanced setting that is active but not shown.

---

## Workspaces

**Overview** — the model at a glance, or the wizard in Simple mode.

**Design** — the engine itself, in four tabs. Field metadata is data, so every
field converts units, validates, and carries its origin.

**Boost** — appears only when the model is forced. Compressor map with your
operating line on it, surge and choke margins, the turbine A/R trade, charge
cooling, boost control and the transient. Hidden on a naturally aspirated
model, and the command palette still finds "add forced induction".

**Sound** — order structure, spectrogram, silencer transmission loss, intake
acoustics and an audition you can actually listen to. Two designs that differ
by three decibels can sound entirely different, and no number tells you that.

**Run** — operating points, solver settings, and the job tray.

**Results** — torque and power, volumetric efficiency, BMEP and BSFC, the x–t
wave diagram, wave decomposition, and per-cylinder breakdowns. A four-cylinder
mean hides the thing a header is designed to fix.

**Optimise** — pick what may change and what "better" means, then let it
search. Constraints are lexicographic: a design that violates one is worse
than any design that does not, however good its objective. Where two
objectives disagree the answer is a front, not a winner.

**Compare** — two runs side by side.

**Report** — one click, both formats. See [Exports](#file-formats-and-exports).

**Library** — fuels, turbos, cams, flow data, templates and presets.

---

## The learn layer

Present in both modes, and the difference between this and a spreadsheet.

- **"Why" on every field** — a sentence and a typical range, on hover.
- **"Show me"** — beside any numeric field that enters the solve. It sweeps
  that one field across its usual range with everything else held fixed,
  solves every point, and plots what it does. About ten seconds. "What does
  runner length actually do?" becomes an experiment instead of an argument.
- **Explain** — short explainers on wave tuning, Helmholtz resonance,
  discharge coefficients, engine orders, surge, blade speed ratio, valve
  overlap, knock, charge cooling and volumetric efficiency, each linked from
  the fields it governs and each carrying its source.
- **Tour** — a guided walk through Design, Boost, Results, Optimise or Sound.
  Skippable and re-runnable.
- **Ctrl+K** — the command palette reaches every field, workspace, action,
  concept and sweep.

---

## The headless CLI

Everything that matters runs without a window, which is what makes WaveBench
scriptable.

```
wavebench info    my-engine.json
wavebench run     my-engine.json --rpm 5000
wavebench sweep   my-engine.json --from 4000 --to 9000 --step 500 \
                  --db results.db --plot sweep.png
wavebench mesh    my-engine.json --rpm 7000
wavebench render  my-engine.json --from 2500 --to 7500 --seconds 9
wavebench report  my-engine.json --from 4000 --to 9000 --step 500 --out reports
wavebench validate --out validation
```

`report` solves the sweep, runs the mesh study and writes `report.html` and
`report.pdf`. It exits 3 when the model carries a dominant caveat, so a build
script can refuse to mail a report nobody has looked at.

`render` solves an rpm grid, builds crank-angle wavetables from the solved
pressure history and synthesises phase-coherent audio, with a sidecar
recording the model hash, seed and resolved bandwidth. Content above that
bandwidth is labelled as not physically resolved rather than presented as
prediction.

---

## File formats and exports

**Models** are JSON, diffable and versioned. A saved file round-trips
byte-for-byte, and the same file run from the CLI produces bit-identical
results to the app.

**Exports:** CSV · Parquet · PNG and SVG plots · PDF and HTML report · 48 kHz
24-bit WAV and FLAC with a metadata sidecar · manifold centreline geometry as
CSV/DXF for CAD.

**The report** is what you hand to somebody else. It carries the complete
model dump with the origin of every value, the geometry, every figure, the
convergence and mesh-sensitivity evidence, the acoustics, the turbo match,
every assumption with its citation, and a validation statement saying what has
and has not been checked. It is explicitly built to be handed to an FSAE
design-event judge, which is why it states its own weaknesses rather than
leaving them to be found.

Both formats render the same document, so they cannot disagree. The HTML is a
single self-contained file with inline vector figures — no stylesheet to lose,
nothing fetched from a network.

---

## Troubleshooting

**"The solve produced a non-finite result."** The geometry is outside what the
model can describe. Most often a pipe far shorter than it is wide, or a
diameter of zero somewhere. The Design checks panel names the component.

**Mesh sensitivity above 1%.** Refine the mesh (lower `Solver.CellSizeMm`)
until it is below, or treat the figure as indicative. A result that moves with
cell size is not yet a result.

**A warning I disagree with.** Every warning carries the source of its limit
and a link to the field or figure causing it. If your case is outside that
source's validity range, the warning is the tool being honest about a
correlation rather than about your engine.

**Volumetric efficiency above 1.0.** Not an error. A well-tuned intake
genuinely packs in more than the swept volume near its tuned speed — that is
what intake tuning is for.

**The Boost workspace is missing.** The model is naturally aspirated. Set
Design → Engine → Aspiration, or press Ctrl+K and type "add forced induction".

**An answer changed after I edited nothing.** It cannot: same input file, same
result, bit for bit. Check the provenance badges — a wizard or an optimiser
run may have written a field, and both are recorded.
