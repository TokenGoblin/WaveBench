# WaveBench numerical methods documentation

Source of truth: the master plan, Part 5. Records what is implemented, with
sources and measured behaviour. Grows with the phases, in the same commit as
the code.

## 1. Baseline scheme (Phase 2)

Finite volume, conservative, second order in space and time:
**MUSCL-Hancock with an HLLC approximate Riemann solver** (plan §5.1), after
Toro, *Riemann Solvers and Numerical Methods for Fluid Dynamics*, 3rd ed.

Per timestep (`EulerSolver1D`):

1. Ghost cells (2 per side) filled by boundary kind — transmissive
   (zero-gradient), reflective (mirror, velocity negated), periodic.
2. MUSCL reconstruction on primitive variables (ρ, u, p) with a slope
   limiter — van Leer (default), minmod, van Albada — in slope form,
   symmetric, vanishing at extrema.
3. Hancock half-step: both face states of each cell evolved ½Δt by the
   flux difference of the reconstructed faces (conservative variables).
4. HLLC interface flux (Toro §10.4–10.6) with the PVRS-based adaptive
   pressure estimate for wave speeds. HLLC restores the contact wave that
   HLL smears — needed later for the fresh-charge/residual interface.
5. Conservative update; global timestep `Δt = CFL·Δx/max(|u|+a)`, CFL 0.8.

**Positivity guards:** a cell whose reconstruction or Hancock half-step
produces non-positive density or pressure falls back to first order (zero
slope / un-evolved face) for that step. This is what carries the scheme
through the near-vacuum 123 problem at CFL 0.8 with every limiter.

**EOS:** the Phase 2 core uses a calorically perfect gas (γ, R) — the Riemann
verification problems are defined for perfect gas. The species-resolved
caloric EOS of `docs/physics.md` couples in with species transport (Phase 3).

**Layout:** struct-of-arrays state (plan §5.7), allocation-free stepping.
SIMD vectorisation is deferred until the Phase 7 performance budget is
profiled with real networks.

## 2. Exact Riemann solver (verification reference)

`ExactRiemannSolver` implements Toro ch. 4: Newton iteration on the pressure
function with the §9.3 adaptive initial guess (PVRS / two-rarefaction /
two-shock), then self-similar sampling. Anchored in unit tests to Toro
Table 4.3 star values:

| Problem | p* | u* |
|---|---|---|
| Sod | 0.30313 | 0.92745 |
| 123 (double rarefaction) | 0.00189 | 0.0 |
| Left blast (p = 1000/0.01) | 460.894 | 19.5975 |

It is a reference tool only — never part of a production solve.

## 3. Phase 2 verification results (§6.1)

| Test | Configuration | Result |
|---|---|---|
| Sod vs exact | 100/200/400 cells, t = 0.25 | L1(ρ) < 8e-3 at 200 cells, monotone refinement |
| Lax vs exact | 200/400 cells, t = 0.12 | L1(ρ) < 2.5e-2 at 200 cells, monotone refinement |
| 123 double rarefaction | 200 cells, t = 0.15, all limiters | ρ > 0, p > 0 everywhere; L1(ρ) < 2e-2 |
| Order of accuracy | advected density sine, periodic, N = 100→400 | observed L1 order > 1.8 on both refinements |
| Conservation | periodic domain | mass/momentum/energy drift < 1e-12 (relative) |
| Acoustic pulse | 10 Pa Gaussian on 1e5 Pa, 20 domain lengths | amplitude retention > 98% |
| Wall reflection | Sod shock onto reflective end | wall velocity ≈ 0, pressure above incident star value |

**Acoustic dissipation is resolution-limited by extremum clipping.** TVD
limiters are locally first-order at smooth extrema, so peak amplitude loss
over 20 lengths measured: 6.5% at σ = 20 cells, 2.4% at 40, 1.4% at 60
(effective extremum order ≈ 1.4). The < 2% gate is met in the acoustic-mode
meshing regime (plan §5.5 — acoustic runs use a finer mesh than performance
runs). The Phase 3 bandwidth characterisation test will publish the scheme's
−3 dB bandwidth per mesh so the UI can grey out unresolved frequencies; the
§5.6 hybrid hands frequencies above the crossover to the dissipation-free TMM.

Note on HLLC vs the exact Godunov flux: on a strong single-interface jump
(raw Sod data) HLLC's momentum flux differs from the exact Godunov flux by
~20% — intrinsic to the PVRS wave-speed estimate, and irrelevant to
converged accuracy (see the Sod L1 result). The unit test asserts the
weak-wave limit instead, where HLLC approaches the exact flux within 1%.
