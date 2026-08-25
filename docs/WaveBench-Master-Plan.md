# WaveBench — Master Build Specification

**Laboratory-grade 1D engine gas-dynamics, acoustics and forced-induction design suite**
Version 1.0 · Single-document build contract for Claude Code

- **Platform:** .NET 10 (LTS), Windows-native (WinUI 3 / Windows App SDK)
- **Licence:** Apache-2.0, open source on GitHub
- **Audience:** Formula SAE teams, race engine developers, professional engine designers, DIY engine enthusiasts
- **Scope:** intake and exhaust wave tuning · camshaft timing · collector configuration and cylinder pairing · exhaust sound design and auralisation · turbocharging and supercharging · multi-objective optimisation

Suggested name: **WaveBench**. Alternates if taken: *Resonata*, *ManifoldLab*, *PulseForge*. Avoid "WAVE" (Ricardo) and "GASDYN" (Politecnico di Milano) — both are existing codes.

---

# Table of contents

| Part | Subject |
|---|---|
| 0 | How to use this document — the working agreement |
| 1 | Product definition |
| 2 | Physics I — gas dynamics core |
| 3 | Physics II — acoustics and sound design |
| 4 | Physics III — forced induction |
| 5 | Numerical methods |
| 6 | Verification and validation |
| 7 | Software architecture |
| 8 | User interface — design language, shell, workspaces, Simple/Advanced |
| 9 | Optimisation |
| 10 | Data formats and interoperability |
| 11 | Open source setup |
| 12 | Phase plan — the build sequence |
| 13 | Bibliography |
| 14 | Gotchas — read before starting |
| 15 | First prompt |

---

# Part 0 — How to use this document

This is a **staged build contract**, not a suggestion list.

1. **Do not skip phases.** Part 12 defines 26 phases in a single linear order. Each has a hard acceptance gate. If the gate fails, fix before proceeding.
2. **Physics before pixels.** Phases 0–15 produce a headless, test-covered, validated engine. No UI exists before Phase 16.
3. **TDD is mandatory in the physics layers.** Every correlation, boundary condition and solver kernel gets a test against an analytical or published reference with a stated tolerance.
4. **Cite in code.** Every empirical correlation carries an XML doc comment with its source and validity range. If a runtime value falls outside that range, log a warning — never silently extrapolate.
5. **`WaveBench.Core` never references a UI assembly.** Enforce with an architecture test.
6. **Determinism is a requirement.** Same input file → bit-identical results. No unordered parallel floating-point reductions. This is what makes CI regression testing possible.
7. **Write the docs in the same commit as the code.** `docs/physics.md`, `docs/acoustics.md`, `docs/boost.md`, `docs/numerics.md`, `docs/validation.md`. They are the paper trail that makes this laboratory-grade rather than another calculator.
8. **Verify every citation in Part 13 before using it in code comments.** They were checked at authoring time but must be confirmed against the publisher.
9. **One model, many lenses.** There is exactly one project document. Every workspace, mode and module is a view over it. Never create a parallel simplified data structure.

---

# Part 1 — Product definition

## 1.1 What it is

A **one-dimensional unsteady compressible gas-dynamics engine simulation** with a design-oriented UI, focused on the problem of *tuning induction and exhaust systems* — for power, for response, and for sound.

It is not a general-purpose engine cycle code competing with GT-SUITE on emissions, aftertreatment or drive cycles. It does one thing at research quality: **predict how manifold geometry, cam timing, cylinder pairing, forced induction, fuel and thermal state combine to shape the torque curve and the sound.**

Three capabilities that do not exist together anywhere in open source:

1. **Predict** performance, wave behaviour, spool and acoustic character from geometry.
2. **Compare** designs quantitatively *and by ear* — instant level-matched A/B auralisation.
3. **Optimise** across power, response and sound simultaneously, with rules compliance as a constraint.

## 1.2 Non-negotiable physics requirements

| Requirement | How it is satisfied |
|---|---|
| Not static, not STP | Fully unsteady 1D Euler solution; ambient P/T/RH/altitude are user inputs and propagate everywhere |
| Local speed of sound | `a = √(γ(T,Y)·R(Y)·T)` computed **per cell, per timestep**, from local temperature and local composition — never a constant 343 m/s |
| Intake temperature | Ambient + wall heat pickup + fuel evaporative cooling + residual backflow mixing + (boosted) compressor work and intercooling, resolved in time and space |
| Exhaust temperature | From cylinder blowdown thermodynamics, evolved down the pipe with wall heat transfer and a wall thermal model (insulation / coating / wrap selectable) |
| Fuel effects | Species-resolved: LHV, stoichiometric AFR, latent heat, flame speed, knock resistance, and the effect of fuel vapour on mixture `R` and `γ` |
| Cylinder pairing | Arbitrary firing order, crank phasing, bank angle; collector and twin-scroll grouping user-defined, with pulse interaction explicitly visualised |
| Cam timing optimisation | Cam events are optimisation variables with valve-to-piston clearance as a hard constraint |
| Sound | Same solved gas state drives order analysis, psychoacoustics and audio synthesis |
| Forced induction | Coupled shaft dynamics, map-based turbomachinery, transient spool, and a *different* set of optimal answers from the NA case |

## 1.3 Primary user stories

- *FSAE powertrain lead:* "20 mm restrictor, 600 cc four, 13 000 rpm limit. Find the runner length, plenum volume and header configuration that maximises area under torque from 6 000–11 000 rpm, keep me under 110 dBC, and give me a PDF I can defend at design event."
- *Race engine developer:* "Show me the x–t wave diagram in the primary at 8 400 rpm and tell me whether the returning expansion arrives before or after EVC."
- *Enthusiast builder:* "My M50 with the factory manifold sounds muddy. Show me what an equal-length 6-into-1 does — to the torque curve and to the sound — and let me hear both."
- *Turbo builder:* "Give me the Pareto front for response versus peak power across turbine A/R and primary diameter, without ever crossing the surge line."
- *Beginner:* "I have an engine and a goal. Walk me through it and tell me what to build, with the numbers and the reasons."

---

# Part 2 — Physics I: gas dynamics core

> Source of truth for `docs/physics.md`. Implement literally.

## 2.1 Governing equations — quasi-1D unsteady compressible flow

```
∂U/∂t + ∂F(U)/∂x = S(U, x)

U = [ ρA , ρuA , ρe₀A ]ᵀ
F = [ ρuA , (ρu² + p)A , (ρe₀ + p)uA ]ᵀ
S = [ 0 , p·(dA/dx) − ρ·G·A , ρ·q̇·A ]ᵀ
```

with `e₀ = e + u²/2`, `e = e(T, Y)` from the caloric EOS (§2.3), and `p = ρ R(Y) T`, `R(Y) = Rᵤ / M(Y)`.

**Friction source term** (Fanning form):

```
G = (f/2) · (u|u| / D_h)
```

Darcy friction factor from Haaland's explicit approximation to Colebrook–White:

```
1/√f_D = −1.8 · log₁₀[ (ε/(3.7 D))^1.11 + 6.9/Re ]
Re = ρ|u|D/μ,   μ(T) via Sutherland
```

Laminar branch `f_D = 64/Re` below Re 2300, blended to 4000. Roughness `ε` is a per-pipe property with a materials table (drawn tube 0.0015 mm, welded stainless 0.03 mm, cast iron 0.26 mm, composite intake 0.05 mm — all editable).

**Heat transfer source term** — Colburn / Reynolds analogy:

```
q̇ = h · (4/D_h) · (T_wall − T) / ρ
h = (f/2) · ρ |u| · c_p · Pr^(−2/3)
```

Apply an enhancement factor for unsteady/pulsating flow (default 1.3, user-adjustable, documented as empirical — this is a known weak point in all 1D codes and the UI tooltip must say so). Add a bend-loss multiplier for pipes flagged as curved.

## 2.2 Species transport — why the speed of sound is right

```
∂(ρA Y_k)/∂t + ∂(ρuA Y_k)/∂x = 0
```

Minimum species set: **fresh air, fuel vapour, burnt gas** (composition from the combustion equivalence ratio), plus **external EGR** if enabled. This gives:

- Correct `R` and `γ` in the exhaust (`γ ≈ 1.28–1.31` at 900 °C burnt gas versus 1.40 for cold air)
- Correct **local** `a = √(γRT)` — a 950 K exhaust cell propagates waves near 600 m/s while a 310 K intake cell propagates near 353 m/s
- Correct tracking of the fresh-charge/residual interface during overlap; backflow into the intake runner is a resolved event, not an assumption

**This is the single most important modelling decision in the program.** Tuning length is `a·Δt`; if `a` is wrong by 10%, every tuned length is wrong by 10% and the software is a toy.

## 2.3 Thermodynamic properties

Implement NASA polynomial thermodynamic data (Gordon & McBride; NASA RP-1311 — 7-coefficient CHEMKIN form or 9-coefficient NASA-9). Ship a curated `thermo.dat` for `N2, O2, Ar, CO2, H2O, CO, H2, NO, OH, O, H` plus fuel vapour species.

```
c_p,mix(T,Y) = Σ Y_k c_p,k(T)
h_mix(T,Y)   = Σ Y_k h_k(T)
R_mix(Y)     = Rᵤ Σ (Y_k / M_k)
γ            = c_p / (c_p − R)
a            = √(γ R T)
```

Fast path: pre-tabulate `c_p`, `h`, `s` on a 200–3500 K grid at 5 K steps per species with cubic interpolation, and cache mixture properties keyed on quantised composition. Property evaluation must not dominate runtime — profile it.

**Validation:** `γ(T)` and `a(T)` for dry air and for stoichiometric combustion products against published tables (Heywood Appendix D; NASA CEA). Tolerance 0.2% on `c_p`, 0.1% on `a`.

## 2.4 Fuel model

A fuel is a data record, not a hard-coded constant:

| Field | Units | Notes |
|---|---|---|
| Formula `CxHyOz` | — | drives stoichiometry and product composition |
| Lower heating value | MJ/kg | |
| Stoichiometric AFR | — | computed from formula, cross-checked against the tabulated value |
| Latent heat of vaporisation | kJ/kg | **charge cooling** |
| Liquid density, vapour molar mass | kg/m³, kg/kmol | |
| RON / MON / sensitivity | — | knock model input |
| Laminar flame speed coefficients | — | Metghalchi–Keck form |
| Oxygen content | mass fraction | shifts stoich AFR and cooling |

Ship a validated library: **RON95 / RON98 / RON100 gasoline surrogates, E10, E30, E85, E100, M100 methanol, iso-octane, toluene reference fuels, CNG/methane, propane, hydrogen**, plus one or two spec race fuels — all user-editable. Diesel/CI is out of scope for v1; leave the interface open.

**Laminar flame speed** (Metghalchi & Keck, 1982):

```
S_L = S_L0(φ) · (T_u/T_0)^α · (p/p_0)^β · (1 − 2.1·Y_dil)
α = 2.18 − 0.8(φ − 1)
β = −0.16 + 0.22(φ − 1)
```

**Charge cooling** — a first-class effect:

```
ΔT_charge = − x_evap · ṁ_fuel · Δh_vap / (ṁ_air c_p,air + ṁ_fuel,vap c_p,fuel)
```

`x_evap` = fraction evaporated upstream of the valve, a function of injector location (port / direct / throttle-body), port wall temperature and time. For methanol (`Δh_vap ≈ 1100 kJ/kg`) at rich mixtures this is worth **30–50 K** — a density gain over 10% and a real change in local sound speed. E85 (`≈ 840 kJ/kg`) is similar. The model must display this number explicitly on the fuel screen.

**Knock:** unburned-zone temperature by isentropic compression; ignition delay by Douaud & Eyzat (SAE 780080):

```
τ = 17.68 · (ON/100)^3.402 · p^(−1.7) · exp(3800/T_u)     [ms, p in atm, T in K]
```

integrated by the Livengood–Wu criterion `∫₀^t dt/τ = 1`. Report **knock margin in crank degrees** and the knock-limited spark advance. This matters directly to induction design: an intake that raises trapped charge temperature can be net-negative even if it raises VE.

## 2.5 Cylinder model

**Gas exchange:** 0D single zone, open-system energy and mass conservation:

```
dU/dθ = −p dV/dθ + Σ_ports ṁ_i h_i + Q̇_wall
```

Volume from the exact slider-crank with rod ratio and wrist-pin offset.

**Combustion:**
- **Level 1 — Wiebe** (single and double), `a = 5`, `m = 2` defaults, burn duration scaling with speed and residual fraction.
- **Level 2 — two-zone** (unburned/burned). Required for knock and for correct heat-release-phase gas properties.
- **Level 3 (optional, only after Level 2 is validated) — predictive turbulent entrainment** (Blizard–Keck / Tabaczynski lineage) driven by `S_L` and a 0D k-ε turbulence model fed by the intake valve jet. This closes the loop between manifold design and burn rate.

**In-cylinder heat transfer:** implement all three, default Woschni — Woschni (SAE 670931), Hohenberg (SAE 790825), Annand (Proc. IMechE 1963). Wall temperature nodes for piston crown, head and liner, either fixed inputs or from a thermal resistance network.

**Also model:** blowby (effective ring-gap orifice), crevice volumes, and trapped residual fraction (computed, never assumed).

**Friction:** Chen–Flynn style correlation with rubbing, peak-pressure-dependent and windage terms, plus explicit accessory and pumping accounting. Expose the coefficients; document the defaults as approximations.

## 2.6 Valve and port boundary condition

Quasi-steady compressible flow through a variable restriction, solved **jointly with the adjacent cell's Riemann problem**, not sequentially:

```
ṁ = C_d · A_ref · p₀/√(R T₀) · √(2γ/(γ−1)) · [ (p/p₀)^(2/γ) − (p/p₀)^((γ+1)/γ) ]^(1/2)
```

with choking at the critical pressure ratio and separate `C_d` for inflow and outflow.

**Discharge coefficients are the accuracy bottleneck.** Blair & Drouin (SAE 962527) showed the traditional reduction of flow-bench data into a `C_d` is seriously in error in some regimes, and that correctly-reduced maps change predicted performance significantly. Therefore:

- `C_d` is a **2D map**: `C_d(A_eff/A_ref, pressure ratio)`, per direction, per valve — after Blair, Callender & Mackey (SAE 2001-01-1798).
- Provide a **flow-bench import wizard** (CFM @ 28 in H₂O vs. lift — the format every team actually has) that applies the correct reduction and warns that single-pressure-ratio data cannot capture the pressure-ratio dependence.
- State the `A_ref` convention once and use it everywhere. Recommended: valve curtain area `π·D_v·L`, effective area the minimum of curtain, throat and port areas (Blair's approach).
- Ship default maps for 2-valve and 4-valve pent-roof geometries, labelled as generic with a "replace with measured data" banner.
- Model valve masking / shrouding at the bore edge as a lift-dependent multiplier.

**Cam:**
- Import measured lift tables (crank angle vs. lift, 0.5° or 1°) from CSV and common cam-doctor exports.
- Generate analytically (polydyne / poly-11) when no measurement exists.
- Derive velocity, acceleration and jerk; flag discontinuities.
- Apply lash and hydraulic-lifter ramps.
- Parametrise as **IVO / IVC / EVO / EVC (at 0.050 in and 1 mm), duration, LCA, installed centreline, advance/retard**, with independent intake/exhaust phasing (VVT) as an rpm-dependent schedule.
- Compute and display **overlap area** (∫ of the product of intake and exhaust curtain areas over overlap) — the number that predicts scavenging, unlike overlap degrees.

**Valve-to-piston clearance:** compute minimum clearance over the cycle from piston dome and valve-pocket geometry. A **hard constraint** in the cam optimiser.

## 2.7 Manifold components

| Component | Model |
|---|---|
| Straight pipe | Quasi-1D FV mesh, per-pipe roughness and wall thermal node |
| Taper / diffuser / megaphone | Native variable area via `dA/dx`; **well-balanced** so a taper at rest generates no spurious waves |
| Stepped header | Sequence of pipes with area-change junctions |
| Bend | Straight pipe with loss and heat-transfer multipliers as `f(R/D, angle)` |
| Sudden expansion / contraction | Area-change boundary with Borda–Carnot loss, validated against steady-flow data |
| Junction (2–6 branches) | **(a)** constant-pressure (Benson) or **(b)** pressure-loss with branch-angle and area-ratio-dependent coefficients (Bassett, Winterbone & Pearson 2001; Bassett et al. SAE 2003-01-0370). **Default to (b)** — constant-pressure measurably mispredicts collectors |
| Plenum / airbox | 0D volume with mass/energy balance and multiple ports; auto-promoted to 1D above an `L/D` threshold, with a warning |
| **FSAE restrictor** | Converging–diverging nozzle: convergent cone, throat (20 mm gasoline / 19 mm E85 per current rules — a **rules-data parameter, not a constant**), divergent diffuser with half-angle. Choke detection with reported choke duration per cycle and throat Mach history. Warn above ~7° half-angle, citing Claywell & Horkheimer (SAE 2006-01-3654) |
| Throttle | Butterfly with `C_d(angle)` map, or a simple area restriction |
| Injector | Mass source with location, timing, targeting fraction and `x_evap` |
| Atmosphere | Open-end boundary with inflow/outflow `C_d`; bellmouth radius affects `C_d` and the acoustic end correction |
| Silencer / resonator | Expansion chamber, Helmholtz side-branch, quarter-wave stub, perforated tube |
| Compressor / turbine / wastegate / CAC | Part 4 |

## 2.8 Collector configurations and cylinder pairing

The topology editor must express these trivially and, critically, **show the pulse timing**:

4-1 · 4-2-1 · tri-Y · 180° crossover · log manifold · individual runners · merge collector with cone angle and length · pulse converter / nozzle junctions · X-pipe · H-pipe · V-engine bank crossovers · twin-scroll divided manifolds (Part 4).

**Required artefact — the pulse-interference diagram:** a polar or linear crank-angle plot per collector showing when each attached cylinder's blowdown pulse arrives at the junction, with collisions highlighted. Compute arrival time as `L/a_exhaust` using the **actual computed local sound speed**, not a nominal. This is the most useful visual for choosing cylinder pairing and no consumer tool presents it well.

## 2.9 Wall thermal model

Every pipe carries a wall node:

```
m_w c_w dT_w/dt = h_in A_in (T_gas − T_w) − h_out A_out (T_w − T_amb) − εσA(T_w⁴ − T_amb⁴)
```

`h_out` from natural plus forced convection at vehicle speed. Surface treatments — bare stainless, ceramic-coated, wrapped, insulated, water-jacketed — each an `(ε, external resistance)` pair. Iterate wall temperatures to convergence across cycles.

**Why it matters:** exhaust gas temperature sets `a`, and `a` sets the tuned length *and* the acoustic resonance frequencies. A wrapped header runs hotter and its optimum primary length is correspondingly shorter. The software must be able to demonstrate this — it is a differentiator and a validation test.

## 2.10 Fast analytical layer ("Quick Estimate")

Instant first cut before the full solve, computed with the **current gas state**, not STP:

- **Helmholtz intake resonance:** `f_H = (a/2π)·√(A/(V·L_eff))`, with the Engelman formulation for tuned speed and resonance ratio.
- **Organ-pipe / wave-return tuning:** the time for a wave to travel out and back is `2L/a`; at `N` rpm over `Δθ` crank degrees,
  ```
  L = a·Δθ / (12·N)      [L in m, a in m/s, N in rev/min, Δθ in degrees]
  ```
  Present for both intake (reflected compression before IVC) and exhaust (reflected expansion during overlap).
- **Inertial / ram tuning** ratio and the classic tuning-peak estimate.
- **Restrictor choke rpm** for a given VE.

These seed the 1D model and drive the live UI preview. Show them beside the 1D result so the user learns where simple theory breaks down.

---

# Part 3 — Physics II: acoustics and sound design

> Source of truth for `docs/acoustics.md`.

## 3.0 Why it belongs here

Exhaust sound and exhaust tuning are the same physics: both are governed by the amplitude, timing and reflection of finite-amplitude pressure waves in the primaries and their superposition at the collector. A 6-into-1 equal-length header sounds different from a cast log manifold for exactly the reason it makes different torque — the pulses arrive at the junction at different times. **We already compute those times.**

**The worked example the module must nail.** A BMW M50 with the factory cast manifold versus an equal-length 6-into-1, reproduced *from geometry and firing order alone*:

| | Factory cast manifold | Equal-length 6-1 |
|---|---|---|
| Runner lengths | Unequal, short | Equal |
| Collector arrival spacing | Uneven, and **rpm-dependent** | Exactly 120° crank at every rpm |
| Order content | Energy leaks into 0.5, 1, 1.5, 2, 2.5 orders | Concentrated at 3rd and its integer multiples |
| Per-cylinder pulse amplitude | Unequal (unequal scavenging and wall temperature) | Matched |
| Perceived result | Muddy, warbling, character changes with rpm | The "straight-six howl" — pure, ordered, consistent |

## 3.1 The physics chain

```
cylinder blowdown ─▶ primaries ─▶ collector ─▶ secondary/silencer ─▶ tailpipe
  (nonlinear 1D)     (pulse timing & reflection)                      │
                                                                      ▼
                                                     radiation impedance & directivity
                                                                      ▼
                                          free-field propagation (distance, ground, Doppler)
                                                                      ▼
                                   listener ─▶ order analysis ─▶ psychoacoustics ─▶ audio
```

Parallel chains mixed at the listener: **exhaust tailpipe**, **intake mouth** (airbox or ITB stacks — dominant on ITB engines, and always significant once turbocharged), and an optional parametric **mechanical** layer.

**Source strength.** The acoustic source is the fluctuating volume velocity at each open termination. For a monopole in a free half-space:

```
p(r, t) = ρ₀ / (4πr) · dQ/dt (t − r/c₀),      Q(t) = u(t)·A_exit
```

The derivative amplifies high frequencies and numerical noise with them — differentiate in the frequency domain (multiply by `jω`) with an explicit roll-off above the resolved bandwidth (§5.5), never by naive finite difference on the raw series. Above `ka ≈ 1` the monopole assumption fails and directivity matters (§3.5).

## 3.2 Pulse timing at the collector — the core of header sound design

Implement as an explicit, inspectable calculation, not as an emergent by-product of the solver.

**Order structure.** For a four-stroke at `N` rev/min:

```
cycle frequency   f_cyc  = N / 120
firing frequency  f_fire = N/120 · n_cyl
engine order      o      = f / (N/60)
firing order      o_fire = n_cyl / 2
```

I4 → 2nd order, I6 → 3rd, V8 → 4th, V10 → 5th, V12 → 6th. Half-orders exist because the cycle is 720°, not 360°, and they are *the* signature of uneven pulse arrival. A 3.0 L I6 at 7000 rpm fires at 350 Hz; its 6th and 9th orders sit at 700 and 1050 Hz, squarely in the ear's most sensitive band.

**Arrival phase.** For cylinder *j* on a primary of length `L_j`:

```
τ_j = ∫₀^{L_j} dx / (ā(x) + ū(x))     using the LOCAL solved sound speed
φ_j = θ_j,fire + 6·N·τ_j              [crank degrees]
```

Two consequences the UI must state plainly:

- **Equal length ⇒ all `τ_j` equal ⇒ relative arrival spacing is exactly `720/m` degrees at every engine speed.** The character is rpm-invariant. This is the real reason equal-length headers sound consistent, and it is *not* the explanation usually given.
- **Unequal length ⇒ spacing changes with rpm,** because a fixed transit-time error `Δτ` maps to `6·N·Δτ` crank degrees, growing linearly with speed. Nearly even at 2500 rpm, badly uneven at 6500. Hence "muddy, and it changes as it revs."
- **Unequal wall temperature does the same thing.** End cylinders run cooler; `ā ∝ √T`, so a geometrically equal-length header on an engine with a 60 K spread still has a timing error. §2.9 makes this visible — show it.

**Composite spectrum.**

```
X_collector(f) = Σ_j  H_j(f) · P_j(f) · e^{−j2πf t_j}
```

`P_j(f)` is cylinder *j*'s blowdown spectrum, `H_j(f)` the primary transfer function (§3.3), `t_j` the arrival time. With `m` equal-amplitude pulses at exactly even spacing the sum vanishes at every frequency that is not a multiple of `m·f_cyc` — energy collapses onto the firing order and its harmonics. Any timing error **or** amplitude mismatch breaks the cancellation and populates the intermediate orders. That one equation explains equal-length headers, crossplane V8 rumble and unequal-length boxer rumble at once.

**Order Purity Index:**

```
OPI = Σ energy at integer multiples of o_fire  ÷  Σ energy at all orders ≤ 12·o_fire
```

Report as a curve versus rpm. It is the single number capturing "clean howl" versus "warble", and it is directly actionable.

### 3.2.1 Configurations and their mechanisms

| Configuration | Mechanism to model and display |
|---|---|
| **6-1 equal length (I6)** | Even 120° arrival at all rpm; single shared reflection point; dominant 3rd with a clean harmonic ladder |
| **6-2-1, tri-Y** | Two staged junctions, two reflection timings; paired cylinders create an intermediate 1.5-order structure at the first junction that cancels at the second |
| **Factory cast log** | Unequal length and amplitude, high wall thermal mass (cooler gas, lower `a`) → low OPI, rpm-varying character |
| **Stepped headers** | §3.2.2 |
| **Crossplane V8, 4-1 per bank** | Bank spacing 90-180-270-180 → strong half-order content → the rumble. **Hard acceptance test** |
| **Flat-plane V8** | Even 180° per bank → clean 4th order, no rumble. Same test, opposite result |
| **180° crossover V8** | Pairs across banks to restore even spacing → rumble becomes howl |
| **Unequal-length boxer** | The canonical UEL rumble — instantly recognisable, strong validation target |
| **4-1 vs 4-2-1 (I4)** | Different collector timing and secondary resonance; 4-2-1 typically lower rasp |
| **Merge collector cone** | Cone angle and length set reflection spread; a sharp step gives more HF and rasp |
| **X-pipe / H-pipe** | Cross-bank coupling; H cancels bank-to-bank half-orders in a narrow band, X broadband |

### 3.2.2 Stepped headers

Each step is a plane-wave area discontinuity. For a wave from area `S₁` into `S₂`:

```
R = (S₁ − S₂) / (S₁ + S₂)        T = 1 + R = 2S₁ / (S₁ + S₂)
```

An expansion (`S₂ > S₁`) gives `R < 0` — a returning **expansion** wave, the same mechanism that scavenges the cylinder.

- A **single large step** returns one strong expansion at one delay → one sharp resonance, narrow rpm benefit, a pronounced spectral notch.
- **Multiple progressive steps** return several weaker expansions at staggered delays → a **comb-filter response with multiple shallower resonances**, broadening the torque benefit and enriching the harmonic spectrum. This layered overtone structure is a large part of why F1-era naturally-aspirated stepped systems sound as they do — that, plus a firing frequency of 1500 Hz for a V10 at 18 000 rpm, which puts the *fundamental* where a road V8's 6th harmonic sits.
- A **continuous taper** is the limit of infinitely many infinitesimal steps: broadband, low reflection per unit length, horn-like impedance transformation.

Allow arbitrary step count with position, diameter and optional transition length. Plot **reflection timing versus crank angle** alongside the **spectral comb**. Sweeping step position must visibly move both the torque bump and the spectral notches — demonstrating that coupling is the point.

### 3.2.3 Required visual — the collector timing chart

Per collector, linear and polar crank-angle charts showing, at the selected rpm: each cylinder's blowdown event coloured by cylinder; the arrival window at the junction with the transit delay drawn explicitly; ideal even spacing marked as ghost lines; **timing error per cylinder in crank degrees**, numerically; and an rpm slider animating how the errors grow with speed. Below it, the order spectrum with firing-order harmonics highlighted and the OPI displayed.

**This one screen is the headline feature of the acoustics work.**

## 3.3 Frequency-domain acoustics — transfer matrix method

The nonlinear solver gives the source and low-order content. A linear **TMM** gives the system's frequency response instantly with no numerical dissipation, and is what makes interactive silencer and step design possible.

**Elements** — two-port (four-pole) matrices relating `[p, ρcu]`, **with mean flow** (`M = ū/c`) and visco-thermal damping:

- **Uniform duct**, length `L`, `k_c = k/(1 − M²)`:
  ```
  [ cos(k_c L)     j sin(k_c L) ]
  [ j sin(k_c L)   cos(k_c L)   ] · e^{−jM k_c L}
  ```
  with complex `k = ω/c − jα`; `α` from Kirchhoff visco-thermal losses plus a turbulent term.
- **Area discontinuity** (§3.2.2) with inertial end correction
- **Conical / tapered** — exact horn solution or ≥ 20 segments per wavelength
- **Branch junction** — pressure continuity and volume-velocity conservation across *n* branches, with the §2.7 loss and inertance
- **Expansion chamber, quarter-wave stub, Helmholtz resonator, perforated tube, dissipative packed element** (flow-resistivity model)
- **Catalyst / DPF** as distributed resistance
- **Turbine** four-pole (§4.6)
- **Radiation impedance** at the open end (§3.5)

**Outputs:** transmission loss; insertion loss; acoustic transfer function `H_j(f)` from each cylinder port to the tailpipe; **input impedance seen by each cylinder** — the same quantity that governs scavenging, so plot it against the torque curve and let the user see the trade directly; and a live resonance map versus geometry.

## 3.4 Time-domain source, capture and broadband noise

**High-resolution capture.** The results store must optionally capture, at every probe and open termination, `p, u, T, ρ`, composition at **≤ 0.25 crank degrees** (0.5° minimum) for the last *k* converged cycles (default 20). At 8000 rpm, 0.25° = 5.2 µs ≈ 192 kHz. Store float32 with a documented crank-angle basis; resample to 48 kHz with a crank-angle-domain resampler.

**Cycle-to-cycle variability.** Without this, synthesised engine audio sounds sterile and obviously fake. Inject per cylinder per cycle, from configurable distributions: combustion phasing jitter (CA50 σ ≈ 1.0–1.5 CAD, higher at idle and light load); peak-pressure variation (CoV of IMEP 1–3%, 5–8% at idle); trapped-mass variation from residual scatter; occasional partial burns at idle. Build a **library of pulse realisations** per cylinder per operating point and draw from it stochastically during synthesis, with a fixed user-visible seed so results stay reproducible.

**Broadband flow noise.** Tonal content alone sounds like a synthesiser. Add:
- **Valve** noise during high-velocity flow — confined dipole scaling, `W ∝ U⁶` subsonic, transitioning near choke
- **Area discontinuities, steps and the collector** — separated shear-layer noise scaled on local velocity and area ratio
- **Tailpipe exit** — jet noise, Strouhal-scaled, peaking near `St ≈ 0.2`

Generate as spectrally-shaped noise gated by the instantaneous local velocity, then filter through the downstream TMM so it picks up the same resonances the tonal content does. Calibrate overall level against a measured case and document the calibration honestly as empirical.

**Overrun burble and crackle.** Phenomenological, clearly labelled: on decel fuel-cut or retarded spark, unburnt or late-burning charge combusts in the exhaust. Implement as stochastic impulsive pressure events with user-set rate, energy distribution and location, propagated through the real system. Expose the knobs; do not pretend it is predictive.

## 3.5 Radiation and propagation

**Radiation impedance** — Levine–Schwinger for the unflanged pipe:

```
unflanged:  R_rad ≈ ρ₀c (ka)²/4,   end correction δ ≈ 0.6133·a
flanged:    R_rad ≈ ρ₀c (ka)²/2,   δ ≈ 0.8216·a
```

Tip geometry (plain cut, angle cut, rolled, flared, dual outlet) changes radiation impedance and directivity; mean outflow reduces the reflection coefficient — a hot, high-velocity exit reflects less than a cold static one.

**Directivity** `D(θ, ka)` for the unflanged pipe, increasingly beamed on-axis as `ka` rises; below `ka ≈ 0.5` treat as a monopole.

**Propagation:** spherical spreading; atmospheric absorption per ISO 9613-1 (temperature and humidity dependent, and significant at 10 m and beyond); ground reflection with configurable surface impedance, producing the interference dips characteristic of outdoor recordings; Doppler and time-varying delay for drive-by; multiple outlets summed with correct relative phase and path difference.

**Listener presets:**

| Preset | Geometry | Purpose |
|---|---|---|
| FSAE static test | 0.5 m, 45° in the horizontal plane, free field | Rules compliance (§3.7) |
| SAE J1287-style | 0.5 m, 45° | Stationary reference |
| Drive-by | 7.5 m from centreline, ISO 362-style pass | Road legality |
| Chase cam | 3 m behind, 1 m high, with vehicle speed | The video shot |
| Flyby | User distance and speed, with Doppler | — |
| In-cabin | Cavity/structure transfer function, parametric or user-supplied | Drone assessment |

## 3.6 Auralisation

**Steady state.** Per converged operating point: take the far-field pressure history, resample crank-angle → 48 kHz, apply the listener chain, loop seamlessly at cycle boundaries with per-cycle variation so the loop is never identical.

**Rev sweeps — crank-angle wavetable synthesis.** Do **not** time-stretch audio between rpm points; it destroys phase coherence and sounds like a pitch shift. Instead:

1. Solve at an rpm grid (default 250 rpm, finer where order content changes fast).
2. Store each result as a **crank-angle-indexed wavetable** over 720°, per rpm, per source.
3. At playback, drive a phase accumulator with instantaneous engine speed; read the wavetable at the accumulated crank angle; **crossfade between adjacent rpm wavetables in the crank-angle domain**, not the time domain.
4. Apply per-cycle stochastic variation.
5. Sum the independently-synthesised broadband layer, level-tracked to rpm and load.
6. Apply the listener chain.

Artefact-free, phase-coherent sweeps at arbitrary rate. Support user-drawn rpm profiles (idle → redline, limiter bounce, upshift cuts, decel with burble).

**Load.** Solve at least two load lines (WOT and light-load cruise) and interpolate in the crank-angle domain on both axes, so cruise drone and overrun can be auditioned.

**Stems and export.** Render **exhaust · intake · broadband · mechanical** (parametric, clearly labelled cosmetic) as separate stems. 48 kHz / 24-bit WAV plus FLAC, with a metadata sidecar recording model hash, rpm profile, listener preset, seed and resolved bandwidth so any clip traces back to the design that produced it.

**Level-matched A/B — non-negotiable.** Humans reliably judge the louder of two sounds as better. Default to loudness-normalised A/B (ITU-R BS.1770 / EBU R128) with a clearly-labelled toggle for true relative level when the SPL difference is itself the point. Show both the LUFS-matched and true dB(A)/dB(C) numbers. Getting this wrong invalidates every subjective comparison a user makes.

## 3.7 Sound quality metrics and the "sounds good" problem

**Standard psychoacoustic metrics:**

| Metric | Standard | Unit |
|---|---|---|
| Loudness (stationary and time-varying) | ISO 532-1 (Zwicker), ISO 532-3 (Moore–Glasberg) | sone |
| Sharpness | DIN 45692 | acum |
| Loudness, tonality, roughness (hearing model) | ECMA-418-2, 2nd ed. (Sottek Hearing Model) | sone_HMS, tuHMS, asper_HMS |
| Fluctuation strength | Zwicker & Fastl | vacil |
| Tonality | DIN 45681 | — |
| A- and C-weighted SPL, fast/slow | IEC 61672 | dB(A), dB(C) |
| Speech interference / articulation index | ANSI S3.5 | — (cabin only) |

Validate against the published open-source reference implementations (SQAT, `sottek-hearing-model`, MOSQITO) using their verification signals. **Check each project's licence before vendoring**; independent re-implementation with verification is safer for an Apache-2.0 repo.

**Engine-specific character metrics** — the ones that actually discriminate header designs:

| Metric | Definition | Reads as |
|---|---|---|
| **Order Purity Index** | §3.2 | howl vs. warble |
| **Half-order ratio** | half-order energy ÷ integer-order energy | rumble / lope |
| **Harmonic decay slope** | dB per order across firing-order harmonics | mellow vs. bright |
| **Spectral centroid**, **rasp index** | energy 2–6 kHz normalised | rasp / harshness |
| **Rumble index** | LF energy weighted by modulation depth at 20–100 Hz | crossplane V8 signature |
| **Tonal-to-noise ratio** | — | musical vs. roaring |
| **Order-to-order variance** | evenness of the harmonic ladder | ordered vs. ragged |
| **Character stability** | variance of the above across the rev range | consistent vs. changing |
| **Drone risk** | order content 30–120 Hz coinciding with cruise rpm | in-cabin drone |

Display as a radar chart with the design's fingerprint over the chosen target.

**There is no universal "good" — use target profiles.** Ship named targets as vectors in metric space, each with a written mechanism:

- **Straight-six howl** — very high OPI, negligible half-order, decay ≈ −6 dB/order, moderate sharpness
- **Flat-plane scream** — high OPI at 4th order, strong upper harmonics, high spectral centroid
- **Crossplane rumble** — deliberately high half-order ratio and rumble index, low sharpness
- **NA F1 scream** — very high firing frequency, rich stepped-header comb, high tonality
- **Refined GT** — high tonality, low roughness and sharpness, **zero drone in the cruise band**
- **FSAE compliant + charismatic** — maximise OPI and tonality subject to the dB(C) limits

**Reference Match.** The user supplies their own recording of an engine they like; the tool extracts its order spectrum, harmonic decay and psychoacoustic fingerprint (rpm tracked from the firing order) and uses that as the optimisation target. The audio never leaves the machine, is never committed to the repo, and is discarded after metric extraction.

## 3.8 Compliance

Implement measurement procedures faithfully. Treat every limit as **versioned rules data in a JSON file**, never a constant — they change annually.

**Formula SAE / Formula Student static noise test** (verify against the live rulebook and record the rules year in the report):
- Free-field microphone **0.5 m** from the outlet, at **45°** in the horizontal plane, at outlet height
- **103 dB(C), fast weighting, at idle**
- **110 dB(C), fast weighting** up to the test speed corresponding to a mean piston speed of **15.25 m/s** (`N_test = 15.25 × 30000 / stroke_mm`, rounded to the nearest 500 rpm)
- Repeated per outlet; highest reading governs. Measurement per ISO 5130.

Show **pass/fail with margin in dB at both test points**, computed automatically from the stroke, and surface it on the Overview workspace. Also provide ISO 362 drive-by estimation and SAE J1287 / J2825 stationary references, plus user-defined limit sets.

**Honesty requirement.** Absolute SPL prediction from a 1D code is good to roughly ±3 dB at best, worse for broadband content. Display predicted levels with an explicit uncertainty band and a plain-language note that the tool predicts *differences between designs* far better than absolute compliance. Never let a student fail scrutineering because the software sounded confident.

---

# Part 4 — Physics III: forced induction

> Source of truth for `docs/boost.md`.

## 4.0 The central claim

Forced induction does not add to the induction-tuning problem — it **changes what the right answer is**. A turbocharged engine's exhaust manifold is designed to a different objective than an NA header (deliver pulse energy to a turbine, not scavenge to open air), and its cam timing is designed to a different objective (with positive pressure across the valve during overlap, overlap becomes a scavenging *asset* rather than a reversion liability).

**If the optimiser recommends the same 700 mm equal-length primaries for a turbo build that it recommends NA, it is wrong.**

Covered: single turbo, twin-scroll, parallel twin, compound/two-stage, VGT, electrically-assisted turbo, positive-displacement supercharger, centrifugal supercharger, and the FSAE-legal layout with the restrictor **upstream** of the compressor.

## 4.1 Shaft dynamics

```
J_shaft · dω/dt = (P_turbine · η_mech − P_compressor − P_friction) / ω
```

Turbo lag is a solvable transient — never treat boost as a boundary condition. `J_shaft` from wheel geometry or datasheet; `P_friction` from an oil-viscosity-and-temperature-dependent bearing model, which dominates at low speed and is a first-order contributor to spool time.

## 4.2 Compressor

Map-based, in SAE J1826 corrected quantities:

```
ṁ_corr = ṁ · √(T₀₁/T_ref) / (p₀₁/p_ref)
N_corr  = N / √(T₀₁/T_ref)
T₀₂ = T₀₁ · [ 1 + ( PR^((γ−1)/γ) − 1 ) / η_is ]
P_comp = ṁ · c_p · (T₀₂ − T₀₁)
```

**Read reference conditions from the map file, never assume them.** Manufacturers differ, and this is a classic silent 5% error.

- Interpolate in `(N_corr, ṁ_corr)` with monotone, artefact-free schemes; never a naive bicubic that overshoots near surge.
- **Physics-based extrapolation, not spline extension** — to low speed, zero speed (windmilling), left of the surge line and to negative flow, as needed for transient and surge simulation (Serrano et al. approach). **Shade extrapolated regions in every plot.**
- **Diabatic correction.** Gas-stand maps are measured with substantial heat transfer between the hot and cold ends, so raw map efficiency is not aerodynamic efficiency. Implement a lumped-capacitance heat-transfer correction (Serrano/Olmeda/Arnau style). On-engine compressor outlet temperature routinely runs 15–30 K above the adiabatic prediction; users who size intercoolers from raw maps under-size them.

**Surge.** Compute and display **surge margin** as percent flow at every operating point. Implement Greitzer's B-parameter to classify mild versus deep surge given plenum volume, duct length and compressor characteristic, and optionally run the Moore–Greitzer dynamic model on a surge-line crossing — because the plenum volume the user designed in the Manifold workspace is what decides whether a crossing is a chirp or a destructive oscillation. **Surge is a system property, not a compressor property**, and making that coupling visible is a core reason this module lives inside this application.

**Choke.** Flag points beyond the choke line and report the flow-capacity ceiling.

## 4.3 Turbine

```
MFP = ṁ · √T₀₃ / p₀₃
BSR = U_tip / C_is,   C_is = √( 2 c_p T₀₃ [1 − PR^(−(γ−1)/γ)] )
η_ts = f(BSR, MFP or PR, [VGT vane position])
P_turb = ṁ · c_p · T₀₃ · η_ts · [1 − PR^(−(γ−1)/γ)]
```

Peak `η_ts` typically near `BSR ≈ 0.65–0.70`. Plot instantaneous BSR against crank angle — it shows immediately whether a manifold is delivering pulses where the turbine can use them.

**Quasi-steady is not good enough, and the software must say so.** Under engine pulsation the measured MFP and efficiency describe **hysteresis loops** against pressure ratio and BSR rather than single-valued curves, and the loops widen with pulse amplitude and frequency (rising Strouhal number). Provide two models:

1. **Quasi-steady map lookup** — fast, the industry default, adequate for steady matching.
2. **Volute-resolved** — volute as a 1D duct (two for twin-scroll) terminated by the rotor as a nozzle-plus-work-extraction boundary. Recovers filling-and-emptying and much of the hysteresis without a 3D solve.

Report the difference on every run. Where it is large, the manifold volume is doing something the user should know about.

**Twin-scroll / twin-entry:** model partial admission explicitly. A twin-scroll turbine is generally *not* at full admission through an in-phase pulse and is at partial admission for most of an out-of-phase pulse; a constant-pressure assumption at the limb junction is inadequate — solve the conservation equations at the mixing plane.

**VGT:** vane position as a third map axis with interpolation between vane-position maps and a rate-limited actuator.

**Wastegate:** a parallel path with `C_d(position)` and its own duct; internal or external. **Model the loss of scroll division at an internally-gated port** — it partly defeats twin-scroll pairing at high load, and omitting it overstates the twin-scroll benefit.

## 4.4 Thermal states and control

- **Turbocharger thermal model:** lumped-capacitance nodes for turbine housing, bearing housing and compressor housing with oil and coolant rejection. Drives the diabatic correction, compressor outlet temperature and heat-soak transients.
- **Turbine inlet temperature** is a hard material constraint — expose the limit, report the margin, make it an optimiser constraint. Fuelling strategy, cam overlap and manifold design all collide here.
- **Charge air cooler:** effectiveness `ε`, pressure drop as `f(ṁ)`, **thermal mass and heat soak** so a repeated-run transient shows the IAT climb a steady-state model hides. Air-to-air and air-to-water with a separate circuit model.
- **Boost control:** wastegate duty → actuator position (pneumatic with spring/dome pressure, or electric), boost target versus rpm and gear, PID plus feed-forward, resulting overshoot and settling. Blow-off and recirculation valves with opening thresholds.

## 4.5 Superchargers and electric assist

- **Positive displacement (Roots / screw):** displacement per revolution, internal compression ratio (screw only), volumetric and adiabatic efficiency maps versus speed and PR, belt ratio, parasitic power taken directly off crank torque, and outlet temperature including the isochoric-then-isobaric behaviour of a Roots blower — which is why it heats charge more than a screw at the same PR.
- **Centrifugal:** a compressor map with a fixed drive ratio, so boost tracks rpm² — a fundamentally different torque curve shape, worth placing beside a turbo in Compare.
- **Electric assist / e-turbo:** motor torque in the shaft equation with a power budget constraint (48 V limits) and a report of electrical energy per acceleration event.

## 4.6 How forced induction changes the induction-tuning problem

### 4.6.1 Exhaust manifold: pulse energy, not scavenging

The objective at the collector changes from "create a returning expansion at the valve" to "**deliver the blowdown pulse to the turbine with its amplitude intact**" — implying short, small-diameter, equal-length primaries and minimum manifold volume, nearly the opposite of a long-primary NA header.

| Metric | Definition | Reads as |
|---|---|---|
| **Pulse energy delivery** | fraction of blowdown available energy arriving at the turbine inlet above mean pressure | how much of the pulse survived |
| **Manifold volume ratio** | manifold volume ÷ displacement per exhaust event | the pulse-vs-constant-pressure axis (Watson & Janota) |

A primary-diameter sweep must show the trade explicitly: too small chokes and raises pumping loss; too large dissipates the pulse into manifold volume and pushes toward constant-pressure operation with worse transient response.

### 4.6.2 Twin-scroll cylinder pairing

Cylinders sharing a scroll must be **360° apart in the firing order** so one cylinder's blowdown never overlaps its scroll-mate's exhaust stroke:

- I4, firing 1-3-4-2 → scroll A = 1 & 4, scroll B = 2 & 3
- I6, firing 1-5-3-6-2-4 → scroll A = 1, 2, 3; scroll B = 4, 5, 6
- V8 — bank pairing and cross-bank options both expressible

Compute a **scroll separation index**: the overlap between one cylinder's blowdown pressure history and its scroll-mate's exhaust-stroke window, per scroll, per rpm. Correct pairing must show near-zero overlap; incorrect pairing must show large overlap with a measurable penalty in pumping work and turbine efficiency. **This is a validation test, not just a display.**

### 4.6.3 Intake: overlap becomes an asset

With `p_intake > p_exhaust` during overlap, overlap **scavenges residuals and cools the chamber** instead of causing reversion. The optimum LCA and overlap for a boosted engine are therefore materially different from NA — usually more overlap and a tighter LCA than an NA build tolerates. **The optimiser must find this on its own.** Requirements:

- Track and report **blow-through fraction** (fresh charge passing straight out of the exhaust valve) per cylinder per cycle.
- Model the cost: with port injection upstream of the valve, blow-through carries fuel into the exhaust — report unburnt fuel loss, its effect on measured lambda, and the TIT change from exhaust-port combustion. With direct injection the cost largely disappears, which is exactly why DI turbo engines run overlap a port-injected engine cannot. **Do not let the optimiser exploit free scavenging that the modelled injection system cannot actually have.**
- Report the **pressure ratio across the engine** versus crank angle so the scavenging window is visible opening and closing as boost builds. A turbo achieving positive scavenging pressure only above 4500 rpm implies a very different cam from one achieving it at 2800.

Runner and plenum tuning still matter — the Helmholtz and organ-pipe mechanisms of §2.10 operate unchanged between plenum and valve — but the plenum's upstream boundary is now a compressor with a different reflection characteristic, not the atmosphere. Model it as an acoustic and gas-dynamic termination, not a fixed-pressure source.

### 4.6.4 The FSAE case: restrictor upstream of the compressor

Under the current rules the restrictor sits **upstream of the compressor**, with consequences most teams discover on the dyno:

- The compressor inlet runs sub-atmospheric, so corrected flow and corrected speed shift substantially and the operating line moves across the map — often toward surge at low flow and choke at high.
- Once the restrictor chokes, the compressor cannot pull more mass regardless of shaft speed; the turbo simply raises pressure ratio against fixed mass flow, which is a surge trajectory.
- The plenum volume between restrictor and compressor becomes an acoustic and dynamic element affecting both choke behaviour and surge classification.

Plot the operating line on the compressor map **with the restrictor in place**, show the choke-limited ceiling and warn explicitly on a surge approach. For FSAE teams this is the highest-value single feature in the module.

## 4.7 Matching tools

**Compressor map overlay** — engine operating line per rpm, boost target and ambient condition, with surge and choke margins numerically at every point, turbo speed contours and the max-speed limit, efficiency islands with the operating line's efficiency-weighted average, shaded extrapolation, and an altitude / hot-day toggle because that is where matches fail.

**Turbine matching** — sweep A/R (and trim and wheel size where parameterised) to produce the fundamental trade curve: **boost onset rpm versus peak power**, as a Pareto front rather than a recommendation. Overlay BSR versus crank angle so the user sees whether an A/R puts the turbine near peak efficiency during the pulse.

**Turbo library and map import**
- A **community turbo database** with a documented JSON schema: geometry, inertia, compressor map, turbine map, limits, provenance.
- **Ship no manufacturer maps without written permission.** The database is user-populated; each entry records source and licence.
- **Map digitiser:** load a map image, calibrate the axes, auto-trace speed and efficiency lines with manual correction, export to the schema. Every builder has JPEGs of compressor maps and no way to use them — this utility alone will drive installs.
- Import from CSV and any publicly documented text formats.

**Auto-match** — given a target torque curve, fuel, ambient conditions and rules constraints, search the database and rank candidates, showing each one's operating line, surge margin, TIT margin and predicted spool. Always show the top five with their trade-offs, never a single "best".

**Transient simulation** — step throttle at fixed rpm (time to 90% boost); gear-limited vehicle acceleration (**time-to-torque**, the number that correlates with lap time); repeat-run heat soak, because a second dyno pull is not the same as the first. Includes shaft inertia, thermal states, wastegate dynamics, CAC heat soak and boost-control behaviour. Show a **sensitivity band** on time-to-torque, since inertia and friction are rarely known accurately.

## 4.8 Forced induction and sound

**The turbine is a strong acoustic attenuator** sitting between the pulse source and the tailpipe, absorbing a large fraction of the tonal energy and flattening the exhaust order structure. Add a turbine four-pole to the TMM (area restriction plus work extraction plus a partly-anechoic termination character) and report the **Order Purity Index drop** relative to the same engine NA. That is a quantitative, physical answer to "why do turbo cars sound flat" — present it exactly that way.

**New sources:**

| Source | Model |
|---|---|
| Compressor blade-pass tone | `f = (N_turbo/60) · n_blades`, plus splitter-blade content and shaft-order sidebands; radiated mainly from the compressor inlet |
| Compressor whoosh | Broadband, scaled on tip speed and incidence, shaped by the intake duct TMM |
| **Surge flutter** | Modulation at the Greitzer surge cycle frequency — **predicted, not sampled**; it falls out of the surge model |
| Turbine blade pass | Usually masked, but present |
| Wastegate flow noise | Broadband, gated on gate position and flow |
| Blow-off / recirculation valve | Transient event, phenomenological |

The intake side becomes a primary noise path once boosted (pre-compressor inlet and post-compressor charge piping both radiate), so the intake chain moves from "important on ITB engines" to "always important."

---

# Part 5 — Numerical methods

> Source of truth for `docs/numerics.md`.

## 5.1 Baseline scheme

**Finite volume, conservative, second order in space and time: MUSCL-Hancock with an HLLC approximate Riemann solver.**

- Reconstruction: MUSCL on primitive variables with a slope limiter (default van Leer; also minmod and van Albada, selectable)
- Half-timestep evolution (Hancock), then HLLC flux
- Source terms: Strang-split, or preferably incorporate the area term in a **well-balanced** manner so a stationary uniform state in a tapered pipe is preserved exactly. **Test this explicitly** — a taper that generates waves at rest is the classic silent killer in this class of code.
- CFL ≤ 0.8; global timestep `Δt = CFL · min(Δx / (|u| + a))` across the network.

Rationale: Winterbone and Pearson demonstrate that finite-volume methods are more accurate and more robust than the classical Method of Characteristics for engine manifolds.

## 5.2 Secondary schemes — for cross-validation, not production

- **Two-step Lax–Wendroff with flux-corrected transport** (Boris & Book anti-diffusion), the Winterbone/Pearson workhorse. Agreement between two independent schemes on a real engine case is a strong validation signal.
- **Method of Characteristics** — a small module purely for a teaching-mode wave diagram and to sanity-check wave decomposition. Not for production runs.
- Blair's GPB method is documented in his books; implementing it is optional and low priority, but the Blair texts remain the best source for boundary-condition physics regardless of solver choice.

## 5.3 Meshing

- Target `Δx` 5–15 mm for performance runs, auto-generated with ≥ 6 cells per pipe and smooth grading at area changes
- Report total cells, timestep and estimated wall-clock per cycle
- One-click **mesh sensitivity study** at 0.5×, 1× and 2× cell size, reporting the change in peak torque. Warn above 1%. Publishing this by default is what separates lab-grade from hobby-grade.

## 5.4 Convergence

Run until the cycle is periodic:
- Residual on trapped mass, residual fraction, IMEP and per-pipe mean pressure below tolerance (default 0.1%) between successive cycles
- Wall temperatures converged (default 0.5 K)
- Minimum 8 cycles, maximum 60, both configurable
- **Cycle convergence and wall-temperature convergence are nested loops with different time constants.** Converge them together with a relaxation factor or the run will oscillate.
- Report cycles-to-convergence in the results

## 5.5 Acoustic bandwidth — a hard requirement

Numerical dissipation rolls off high frequencies. **Never present audio above the resolved bandwidth as physical.**

- To resolve `f_max`, `λ = a/f_max`. In hot exhaust (`a ≈ 600 m/s`), 10 kHz → 60 mm; second-order MUSCL needs roughly 20 cells per wavelength → **Δx ≈ 3 mm**. Acoustic runs therefore use a finer mesh than performance runs; make this an explicit run mode with its own cost estimate.
- Implement a **bandwidth characterisation test**: propagate a broadband pulse down a long uniform pipe, measure the scheme's transfer function versus frequency and mesh size, publish the −3 dB bandwidth. Display it in the UI as "physically resolved to X kHz" and grey everything above it in plots.

## 5.6 Hybrid nonlinear / linear architecture

Use the nonlinear solver where it is authoritative and the TMM where it is not:

```
f < f_hybrid (≈1–2 kHz, or the measured −3 dB point, whichever is lower)
     → nonlinear time-domain solution (steepening, choking, finite-amplitude
       wave speed, flow effects)

f > f_hybrid
     → source spectrum from the near-valve solution, propagated through the
       linear TMM transfer function (no numerical dissipation)

Complementary crossfade pair. Document the crossover in every plot and export.
```

## 5.7 Performance

- Struct-of-arrays layout, `Span<T>`, `System.Numerics.Vector<double>` in flux kernels
- Pipes stepped in parallel with junctions coupled at each timestep barrier, using a **deterministic fixed-order reduction**
- Operating points in a sweep are embarrassingly parallel; run across cores with a bounded scheduler
- **Budget:** 4-cylinder, ~20 pipes, ~3000 cells, 30 cycles at 8000 rpm → **< 5 s per operating point** on a modern 8-core desktop; a 20-point sweep → **< 90 s**. Volute-resolved turbine within 2× the quasi-steady cost. Miss the budget → profile with BenchmarkDotNet before adding features.
- Keep Core AOT-friendly; no reflection-heavy patterns in the hot path.

---

# Part 6 — Verification and validation

Two suites. **Both run in CI.** Verification on every PR; validation nightly and on release.

## 6.1 Verification — does the code solve the equations correctly?

| Test | Reference | Tolerance |
|---|---|---|
| Sod shock tube | Exact Riemann solution | L1 error; observed order ≥ 1.8 on smooth regions under refinement |
| Lax and 123 problems | Exact solution | No negative pressure/density; positivity preserved |
| Small-amplitude acoustic pulse | Linear acoustics | < 2% amplitude loss over 20 pipe lengths |
| Stationary state in a taper | Trivially uniform | Spurious velocity < 1e-10 m/s (well-balancedness) |
| Steady isentropic nozzle | Isentropic tables, choked mass flow | 0.5% |
| Steady pipe friction | Darcy–Weisbach | 1% |
| Steady wall heat transfer | Analytical exponential approach | 1% |
| Riemann-invariant preservation | Analytical | Within scheme dissipation bounds |
| Mass / energy conservation over a cycle | Closure | < 0.1% |
| Species conservation and boundedness | `0 ≤ Y_k ≤ 1`, `ΣY = 1` | Machine precision |
| Expansion chamber TL | `TL = 10 log₁₀[1 + ¼(m − 1/m)² sin²(kL)]` | 0.1 dB, no flow |
| Quarter-wave stub | `f = (2n−1)c/4L` | 1% in frequency |
| Helmholtz resonator | `f = (c/2π)√(A/(V·L_eff))` | 2% |
| Duct with mean flow | Analytic convective shift | 0.5% |
| Open-end reflection | Levine–Schwinger | Within published curve |
| **TMM vs. nonlinear solver, small amplitude** | Each other | < 1 dB below resolved bandwidth |
| Compressor map read/write round trip | — | Exact, including reference conditions |
| Steady shaft power balance | Turbine = compressor + friction | < 0.1% |
| Adiabatic compression relations | Closed form | 0.1% |
| Order tracking on a synthetic signal | Known order levels | 0.2 dB |
| Determinism | Repeated runs | Bit-identical |

The TMM-versus-nonlinear agreement is the important one: at small amplitude two independent methods must agree. Where they diverge at large amplitude, that divergence *is* the nonlinearity, and the UI should say so.

## 6.2 Validation — does the model match reality?

A `validation/` directory. Each case is a model file, a reference dataset, a tolerance, and a generated comparison plot committed as an artefact.

**Gas dynamics**
1. **Textbook worked examples** — case studies in Blair's *Design and Simulation of Four-Stroke Engines* and Winterbone & Pearson's two volumes include measured versus predicted pressure diagrams and VE curves. Reproduce them. **Do not copy their figures or tables**; digitise your own reference points and record provenance.
2. **Junction pressure loss** — steady-flow coefficients versus Bassett/Winterbone/Pearson data across branch angles and area ratios.
3. **Discharge coefficients** — reproduce the qualitative pressure-ratio dependence reported by Blair et al. (SAE 952138, 962527, 980764, 2001-01-1798).
4. **FSAE restrictor** — reproduce the trends in Claywell & Horkheimer (SAE 2006-01-3654) on diffuser angle, and the cylinder-to-cylinder VE uniformity ranking of intake concepts from Claywell, Horkheimer & Stockburger (SAE 2006-01-3652). **Getting the ranking right matters more than matching absolute numbers.**
5. **Motored single-cylinder VE curve** first (isolates the gas dynamics), then fired.
6. **A real dyno dataset** — if obtainable from an FSAE team with geometry, this becomes the headline case. Solicit these in the README.
7. **Wrapped vs. bare header** — the thermal model must shift the optimum length in the right direction by a plausible amount.

**Acoustics**

8. **Crossplane vs. flat-plane V8, identical geometry, firing order the only difference.** Crossplane must show strong half-order content and a high rumble index; flat-plane must not. Binary, unmistakable, and it tests the entire pulse-timing chain.
9. **180° crossover** applied to the crossplane must move it toward the flat-plane fingerprint.
10. **UEL vs. EL boxer** must reproduce the rumble/no-rumble distinction.
11. **Equal vs. unequal-length I6** must show the OPI gap *and* the rpm-dependence of the timing error.
12. **Measured comparison** — against recordings with known geometry, the first five order levels within **±3 dB** and the order *ranking* exactly correct.
13. Psychoacoustic metric implementations versus published reference verification signals.
14. Level-matching: A/B pairs within 0.5 LU.

**Forced induction**

15. **Quasi-steady vs. volute-resolved turbine** — hysteresis loops must appear and widen with pulse frequency and amplitude, matching published qualitative behaviour.
16. **Twin-scroll pairing** — correct 360°-apart pairing must show near-zero scroll overlap, lower pumping work and earlier boost than deliberately incorrect pairing, **derived from firing order alone**.
17. **Turbo vs. NA cam optimum** — run on the same engine NA and turbocharged, the optimiser must return meaningfully different overlap/LCA optima, with positive scavenging pressure ratio as the explanation.
18. **FSAE restrictor upstream of compressor** — operating line shift and choke ceiling match hand calculation; surge approach flagged.
19. **Greitzer B-parameter** mild/deep surge classification matches published behaviour for known plenum volumes.
20. **Transient spool** — time to 90% boost within 15% of a measured case.
21. **Diabatic correction** — corrected compressor outlet temperature closer to a measured on-engine value than the raw map prediction.
22. **Turbine acoustics** — expected attenuation and OPI drop; surge flutter frequency matches the Greitzer prediction.

**Rule:** the README shows the validation plots. A simulation tool with no published validation is not credible to this audience.

---

# Part 7 — Software architecture

```
WaveBench.sln
├─ src/
│  ├─ WaveBench.Core/          net10.0  — physics; no UI, no I/O beyond streams
│  │   ├─ Thermo/              NASA polynomials, mixtures, fuels, EOS
│  │   ├─ Numerics/            Riemann solvers, limiters, integrators
│  │   ├─ Components/          Pipe, Junction, Volume, Valve, Orifice, Restrictor,
│  │   │                        Throttle, Injector, Ambient, WallNode
│  │   ├─ EngineModel/         Crank, cam, firing order, cylinder thermodynamics,
│  │   │                        combustion, knock, friction
│  │   └─ Solver/              Network assembly, timestepping, convergence, cycles
│  ├─ WaveBench.Acoustics/     net10.0  — TMM, radiation, order analysis,
│  │                                      psychoacoustics, synthesis
│  ├─ WaveBench.Boost/         net10.0  — maps, turbomachinery, shaft dynamics,
│  │                                      thermal states, boost control
│  ├─ WaveBench.Model/         net10.0  — serialisable model tree, units, validation
│  │                                      rules, PROVENANCE BADGES (§8.5)
│  ├─ WaveBench.Analysis/      net10.0  — post-processing, FFT, wave decomposition
│  ├─ WaveBench.Optimize/      net10.0  — DOE, optimisers, surrogates, constraints
│  ├─ WaveBench.Cli/           net10.0  — headless runner (AOT), scripting entry point
│  └─ WaveBench.App/           net10.0-windows10.0.26100.0 — WinUI 3 desktop app
├─ tests/
│  ├─ WaveBench.Core.Tests/          xUnit
│  ├─ WaveBench.Verification/        §6.1
│  ├─ WaveBench.Validation/          §6.2 (nightly + release)
│  └─ WaveBench.Bench/               BenchmarkDotNet
├─ validation/                 model files + reference data + generated plots
├─ docs/                       physics.md, acoustics.md, boost.md, numerics.md,
│                              validation.md, user-guide/
└─ .github/workflows/
```

## 7.1 UI framework decision

**WinUI 3 on Windows App SDK, targeting .NET 10.**

- .NET 10 is the LTS train, supported to November 2028; Windows App SDK 2.0 targets it.
- Microsoft reconfirmed WinUI as the native production platform for Windows apps at Build 2026, with an LTSC servicing channel and an annual cadence — meaningful for a project maintained by volunteers over years.
- WPF remains supported on .NET 10 but is in maintenance mode; it is the safe fallback if WinUI 3 tooling causes friction and can host the same MVVM layer with minimal change.

Keep all UI logic in view models with **zero WinUI types**, so a WPF or Avalonia head remains a plausible future addition. Use `CommunityToolkit.Mvvm` source generators.

## 7.2 Charting and visualisation

- **ScottPlot 5** (`ScottPlot.WinUI`, MIT, SkiaSharp-backed) for line, scatter, bar and heatmap plots.
- **Custom SkiaSharp canvas** for the manifold node-graph editor, the x–t wave diagram with animation, valve-lift/overlap diagrams, the collector timing chart and the compressor map overlay — these need interaction models ScottPlot does not provide.
- Export every plot to PNG and SVG.

## 7.3 Libraries

| Purpose | Choice | Note |
|---|---|---|
| MVVM | CommunityToolkit.Mvvm | source-generated, AOT-friendly |
| DI | Microsoft.Extensions.DependencyInjection | |
| Serialisation | System.Text.Json, source-generated context | AOT-friendly, git-diffable |
| Results store | SQLite (Microsoft.Data.Sqlite) | queryable, single file, no server |
| Plots | ScottPlot 5 + SkiaSharp | |
| Audio | NAudio or a minimal WASAPI wrapper for playback; own WAV/FLAC writers | verify licences |
| Tests | xUnit + FluentAssertions | |
| Benchmarks | BenchmarkDotNet | |
| CLI parsing | System.CommandLine | |
| Logging | Microsoft.Extensions.Logging + Serilog file sink | |
| PDF report | QuestPDF (verify community licence terms) or HTML→PDF | must be Apache-2.0-compatible for distribution |

**No telemetry. No network calls at runtime.** State it in the README; race teams care.

---

# Part 8 — User interface

## 8.1 Design language — "UniFi-like"

Replicate the *feel* of Ubiquiti's UniFi applications: calm, flat, spacious, card-based, one strong blue accent, generous whitespace, restrained motion, dense-but-legible data tables. **Do not copy Ubiquiti assets, logos or proprietary icons.** Build the token set below; Lato is SIL-OFL licensed and safe to ship.

Put these in a single `Tokens.xaml`; **nothing else in the app may hard-code a colour** (enforce with a test that greps XAML).

```
Light
  bg/canvas        #F7F8F9      bg/surface     #FFFFFF     bg/surface-alt #F2F4F5
  border/subtle    rgba(0,0,0,0.07)             border/strong  #DCE0E4
  text/primary     #1A1D21      text/secondary #7C8593     text/disabled  #B4BBC4

Dark
  bg/canvas        #16181A      bg/surface     #1E2124     bg/elevated    #24282C
  border/subtle    rgba(255,255,255,0.08)
  text/primary     #F2F4F5      text/secondary #98A1AC

Accent (both themes)
  accent #006FFF · accent/hover #0559C9 · accent/subtle rgba(0,111,255,0.10)
  success #00A657 · warning #F5A623 · danger #F03A3E · info #7B61FF

Radius   card 8 · input 6 · pill 999
Shadow   0 1px 2px rgba(0,0,0,.06), 0 4px 14px rgba(0,0,0,.04)
Spacing  4px base grid; card padding 20; section gap 24; table row 32
Motion   150–200 ms ease-out; no bounce, no springs
Type     Lato 400/600/700 — 12 / 13 / 14 / 16 / 20 / 24 / 32
         All numeric fields use tabular figures. Monospace (JetBrains Mono or
         Cascadia Mono) for raw data dumps and code-like fields only.
Icons    single-weight 1.5 px stroke line icons, 20 px grid
```

Ship light and dark themes, following the system setting by default.

## 8.2 Governing principle

**One model. Many lenses. No duplicated truth.**

There is exactly one document — the `EngineProject` — and every workspace, mode and module is a *view* over it. Modes are not data. Switching from Simple to Advanced does not convert, migrate or regenerate anything; it changes what is visible and how it is presented.

The failure this prevents: a beginner runs the wizard, gets a good answer, switches to Advanced to explore, switches back and loses work — or finds Simple mode silently overwrote something they set by hand. **That must be structurally impossible, not merely avoided.**

## 8.3 Shell and workspaces

```
┌──┬─────────────────────────────────────────────────────────────────────┐
│  │  MyEngine.wbproj ●     [Simple ⇄ Advanced]   [Fuel: E85 ▾]  [▶ Run] │
│🏠├─────────────────────────────────────────────────────────────────────┤
│⚙ │  Engine │ Head & Cam │ Manifold │ Fuel & Combustion                 │
│🌀├─────────────────────────────────────────────────────────────────────┤
│🔊│                                                                     │
│▶ │                     workspace content                               │
│📊│                                                                     │
│🎯├─────────────────────────────────────────────────────────────────────┤
│⇄ │  Jobs: sweep 14/20 · optimise queued        cells 2840 · Δt 9.1 µs  │
│📄└─────────────────────────────────────────────────────────────────────┘
```

| # | Workspace | Sub-tabs |
|---|---|---|
| 🏠 | **Overview** | — |
| ⚙ | **Design** | Engine · Head & Cam · Manifold · Fuel & Combustion |
| 🌀 | **Boost** | Compressor · Turbine · Control · Charge Cooling · Transient |
| 🔊 | **Sound** | Timing · Spectrum · Silencing · Audition · Compliance |
| ▶ | **Run** | Operating points · Solver · Jobs |
| 📊 | **Results** | Performance · Waves · Cylinders · Transient |
| 🎯 | **Optimise** | Variables · Objectives · Run · Pareto · Archive |
| ⇄ | **Compare** | — |
| 📄 | **Report** | — |
| 📚 | **Library** | Fuels · Turbos · Cams · Flow data · Templates · Presets |

**Rules:**

- **Boost is hidden entirely on a naturally-aspirated model** and appears the moment a compressor is added. Same for any module sub-tab whose subject is absent. Provide a discovery path — "add forced induction" lives in Design → Engine and in the command palette, not only in the wizard.
- **Workspaces never own data.** The Manifold canvas and the Sound timing chart read the same pipe objects; editing a primary length in one updates the other immediately.
- **Cross-workspace links are first-class.** A Manifold warning that says "this step creates a 1.8 kHz notch" links to that plot in Sound. A surge warning in Boost links to the plenum volume field in Manifold, because that is the parameter causing it.
- **Background jobs live in a tray**, not a workspace. Switching tabs never cancels a run, optimisation or render. Jobs are checkpointed against crashes.

## 8.4 Workspace contents

**Overview.** Engine summary, live torque/power curve, metric tiles (peak power @ rpm, peak torque @ rpm, BMEP, peak VE, restrictor choke rpm and choke duration, worst cylinder-to-cylinder VE spread, minimum knock margin, surge margin, sound compliance with margin, character fingerprint thumbnail), run history.

**Design → Engine.** Bore, stroke, rod, pin offset, compression ratio, cylinder count, layout, bank angle, crank phasing, firing-order editor with a visual firing wheel. Wall temperatures, coolant/oil temperature, friction coefficients. Aspiration selector.

**Design → Head & Cam.** Valve sizes and counts, port volumes and areas, `C_d` maps with the flow-bench import wizard and resulting map plots. Cam import/generate, lift/velocity/acceleration plots, timing dials with **live overlap-area readout** and a valve-to-piston clearance number that turns red on violation. VVT schedule.

**Design → Manifold.** Node-graph schematic canvas: palette, drag, snap-to-grid, auto-layout, multi-select, copy/paste a whole bank. Inspector binding with dimensioned fields and a mm/inch toggle. Live geometry summary: total intake length per cylinder, primary length, plenum volume in displacements, taper ratios. **Inline design warnings with citations** — e.g. *"Diffuser half-angle 11°: separation likely (SAE 2006-01-3654). Suggested ≤ 7°."*

**Design → Fuel & Combustion.** Fuel picker with the full editable property table, lambda versus rpm, injector location and `x_evap`, **computed charge-cooling ΔT shown prominently**, spark strategy (fixed / MBT search / knock-limited), combustion model, ambient conditions with a density-altitude readout.

**Boost.** Compressor map overlay with operating line, margins, shaded extrapolation and the altitude toggle; turbine A/R sweep with the boost-onset-vs-peak-power front and BSR versus crank angle; boost control setup; charge cooling with heat soak; transient results with time-to-torque and its sensitivity band. Auto-match ranking with the top five candidates.

**Sound.**

```
┌─────────────────────────────────────────────────────────────────────┐
│  Design A: 6-1 equal 720 mm   ⇄   Design B: factory manifold        │
│  [▶ Idle] [▶ 4000] [▶ Sweep 1500→7200] [🎚 Level-matched ▾] [⬇ WAV] │
├──────────────────────────────┬──────────────────────────────────────┤
│  Collector timing chart      │  Order spectrum (A vs B overlaid)    │
│  (polar + linear, rpm slider)│  firing-order harmonics highlighted  │
├──────────────────────────────┼──────────────────────────────────────┤
│  Order waterfall vs rpm      │  Character radar (A, B, target)      │
├──────────────────────────────┴──────────────────────────────────────┤
│  Compliance: idle 99.2 dBC (−3.8) ✓ · 7500 rpm 108.6 dBC (−1.4) ✓  │
└─────────────────────────────────────────────────────────────────────┘
```

Plus: TMM transmission loss with live geometry sliders; radiated spectrum per listener preset; step-reflection timing; drone map (order energy vs. rpm vs. frequency, cruise band shaded); spectrogram of a rendered sweep.

Interaction: changing a header length updates the timing chart and spectrum **instantly from the TMM**, with the full nonlinear re-solve queued in the background and plots refining when it lands — clearly indicating which is showing. A/B is one keystroke, gapless, level-matched. An **"Explain this"** affordance writes a plain sentence: *"Cylinder 6's pulse arrives 14° late at 6500 rpm because its primary is 63 mm longer and runs 40 K cooler. This puts 11% of the exhaust energy into non-firing orders."*

**Run.** Operating-point table (rpm, throttle, lambda, boost, ambient override), solver settings (scheme, CFL, cell size, acoustic mesh mode, tolerances, max cycles), mesh sensitivity toggle, per-point progress with cancel.

**Results.** Torque / power / VE / BMEP / BSFC / trapped residual versus rpm; per-cylinder VE bar chart with numeric spread; pressure, temperature, velocity, Mach and mass-flow traces at any probe; **x–t wave diagram** (pressure, Mach or particle velocity as a heat map over distance and crank angle, per pipe, with valve events overlaid, scrubbable and animatable); **wave decomposition** into rightward- and leftward-running components via the Riemann variables, so the UI can annotate *"reflected expansion arrives 12° before EVC"*; the pulse-interference and scroll-separation diagrams; restrictor throat Mach with choke duration; knock margin and EGT per cylinder; transient spool traces. Everything exports.

**Compare.** Overlay any number of runs; delta table of all inputs and outputs with changed fields highlighted.

**Report.** One-click PDF/HTML: model dump, geometry drawings, all plots, acoustics section, boost matching section, convergence and mesh-sensitivity evidence, assumptions with citations, validation statement, audio links. **Explicitly designed to be handed to an FSAE design-event judge.**

## 8.5 Provenance badges — the mechanism that makes modes safe

Every field carries an origin. **Implement this in `WaveBench.Model` before the wizard exists**; it cannot be retrofitted.

| Badge | Meaning | On wizard re-run |
|---|---|---|
| `Auto` | Derived by a correlation or default | Overwritten freely |
| `Wizard` | Set from a wizard answer | Overwritten, with a diff preview |
| `You` | Typed by the user | **Never** overwritten without explicit opt-in |
| `Imported` | From a file (cam, flow bench, map) | Never overwritten |
| `Optimised` | Set by an optimiser run, linked to that run | Never overwritten without opt-in |

Hovering an `Auto` badge shows the derivation and its source citation. This turns the application into a legible model rather than a black box, and it is how a professional decides whether to trust a default.

## 8.6 Simple mode

**What it is:** a guided path from "I have an engine and a goal" to "here is a buildable design, with numbers, a predicted torque curve, a sound character and an explanation of every choice." It is **not** a crippled version — it runs the same solver at the same fidelity. It differs only in how many decisions it asks for and how it presents the result.

**The wizard.** One question or small group per step. Three regions per step: **the question**, a **"why this matters"** explainer (two or three plain sentences with a link into the docs), and a **live preview** that updates as the user answers.

| Step | Asks | Derives |
|---|---|---|
| 1 | What are you building? *FSAE · track · street · dyno/drag · restoration · learning* | Objective weights, rules set, noise limits, default rpm band |
| 2 | Engine basics — bore, stroke, cylinders, layout, CR, redline; or a template engine | Displacement, firing order, crank phasing, piston speed, FSAE test rpm, friction and wall-temperature defaults |
| 3 | Head and valvetrain — valve sizes, cam character (*stock · mild · aggressive · race*) or import real profile and flow data | Cam events, lift curves, generic `C_d`, port areas, overlap area |
| 4 | Fuel and conditions — fuel, target lambda, altitude, ambient temperature and humidity | Stoich AFR, charge cooling, knock resistance, gas properties, density altitude |
| 5 | Aspiration — *NA · turbo · supercharged*; if boosted, target boost or power plus a **response-vs-peak-power slider** | Compressor and turbine candidates, boost curve, wastegate strategy, scroll pairing, CAC size |
| 6 | Constraints — packaging envelope, noise limit, fabrication complexity slider, available tube sizes | Bounds on every geometric variable, manufacturability limits |
| 7 | Goal — rpm band slider with torque-shape preference (*broad midrange ⇄ peak power*), and a sound target profile | Optimiser objective weights |
| 8 | Review — every assumption in one scrollable list, each editable in place | — |
| 9 | Compute | Runs the bounded optimisation and produces the Design Brief |

Every answer writes into the **full model**; every derived field is tagged `Auto`. Nothing lives in a parallel "simple model."

**Under the hood** — a bounded optimisation, not a lookup:

1. Fill the full model from wizard answers plus derived defaults.
2. Seed geometry with the fast analytical layer (§2.10) and the TMM (§3.3) in milliseconds, so a preview appears immediately.
3. Run a constrained optimisation over a small variable set (runner length/diameter, plenum volume, primary length/diameter, collector type, cam centrelines; plus turbine A/R and boost curve when boosted).
4. Verify the winner with a full converged sweep and a mesh-sensitivity check.
5. Generate the brief, including the sound render and compliance check.

**Budget:** first preview under 1 second; final brief under 5 minutes, with visible progress and the ability to accept the current best at any point.

**The Design Brief** — one scrollable page, PDF-exportable, structured as **recommendation → number → why → confidence**:

```
INTAKE
  Runner length     312 mm    ↳ places the Helmholtz resonance at 6 400 rpm,
                                 the centre of your chosen band.        ●●●○ good
  Runner diameter    42 mm    ↳ keeps port velocity near 90 m/s at peak
                                 torque; larger loses low-end velocity.  ●●●○ good
  Plenum volume     3.1 L     ↳ 1.9 × displacement — enough to decouple
                                 cylinders, small enough to stay responsive. ●●○○ fair
EXHAUST
  Primary length    680 mm  …   Primary diameter 38.1 mm  …
  Collector         4-2-1, 220 mm secondary  …
CAM
  Intake centreline 108° ATDC  …   Overlap 34°  …
PREDICTED
  [torque and power curves with an uncertainty band]
  Peak power  187 hp @ 7 800 rpm (±6%)   Peak torque 142 lb-ft @ 5 400 (±5%)
SOUND
  Straight-six howl · Order Purity 0.84 · [▶ audition]
  FSAE: idle 99 dBC ✓ (−4) · 7 500 rpm 108 dBC ✓ (−2)
BUILD LIST
  6 × 38.1 mm × 680 mm primaries, mandrel bent, 1.5 mm wall …
```

Non-negotiable: **every number carries a one-sentence "why"** linked to the physics; **every prediction carries an uncertainty band** — Simple mode never presents a bare number as if measured; **every recommendation carries a confidence indicator** distinguishing well-founded (validated correlation, converged solve) from rough (generic defaults, extrapolated map, unmeasured `C_d`); **the build list has real dimensions and tube sizes**; and one button reads **"Open in Advanced"**.

## 8.7 Advanced mode

Everything, no gates, same document, same solver. Provenance badges visible throughout. **Three disclosure tiers per card — Essential / Detailed / Research.** Research tier exposes solver internals, correlation coefficients, the pulsating-flow heat-transfer enhancement factor and other empirical knobs — visible, editable, and clearly labelled as empirical.

## 8.8 Mode switching — rules that must not be broken

1. **Simple → Advanced is lossless and instant.** No dialog, no conversion. `Auto` values become visible and editable, keeping their badge until touched.
2. **Advanced → Simple never destroys data.** Values with no Simple representation stay **active** and are surfaced in a banner: *"14 advanced settings are active and not shown in Simple mode."* Clicking lists them. Removing them is a separate, explicit, undoable action.
3. **Re-running the wizard shows a diff preview** and touches only `Auto` and `Wizard` fields unless the user opts in per field.
4. **Mode is a per-user view preference**, stored outside the project file. A file authored in Advanced opens correctly in Simple and vice versa; two people can share a file and each use their own mode.
5. **Undo spans mode switches.** The undo stack is on the model; mode changes are not model changes.
6. **Results survive mode switches.**
7. **Simple mode never lies by omission.** If a hidden advanced setting materially affects a Simple-mode number, the brief says so.

## 8.9 The Learn layer

Present in both modes; the difference between this and a spreadsheet.

- **"Why" on every field** — one sentence, a typical range, and the citation where a correlation is involved.
- **"Show me" on any numeric parameter** — runs a small parametric sweep of *that one parameter*, holding all else fixed, and plots it inline. "What does runner length actually do?" becomes a ten-second experiment instead of a forum argument. **The single best teaching feature in the application**, and nearly free once sweep machinery exists.
- **Concepts panel** — short explainers with diagrams (wave tuning, Helmholtz resonance, discharge coefficients, engine orders, surge, blade speed ratio), linked from the fields they govern.
- **"Explain this result"** — plain-language narration of a chart: *"Torque dips at 4 200 rpm because the reflected compression wave arrives 40° after IVC at that speed."*
- **Guided tours** per workspace, skippable and re-runnable.

## 8.10 Guardrails

- **Implausible input detection** with a warning, never a hard block — a 12:1 rod ratio, a 2 mm runner, a 400 K wall temperature. State what is unusual and why it matters.
- **Rules compliance always visible** when a rules set is selected.
- **Uncertainty mandatory in Simple mode**, available everywhere.
- **Generic-defaults banner** — if running on generic `C_d` maps, extrapolated compressor maps or unmeasured cam data, say so prominently. A beginner should know the biggest error source is the data they did not supply.
- **No silent extrapolation** — any correlation used outside its validity range logs a warning and marks the affected output.

## 8.11 Cross-cutting UX requirements

- **Command palette** (Ctrl+K) reaching every field, workspace, action and library item; **global search** across model, results and library.
- **Job tray** with cancel, priority, completion notification, and checkpointing; jobs survive workspace changes and crashes.
- **Autosave and crash recovery** every 60 s. **Undo/redo across the whole model tree**, including canvas edits and imports.
- **Keyboard-first**; every action reachable without a mouse.
- **Accessibility:** full keyboard navigation, screen-reader labels, respects system contrast and reduced-motion settings, and **no information conveyed by colour alone** — every chart series distinguishable by line style or marker.
- **Units toggle** (metric/imperial) per user, applied everywhere, canonical SI untouched in the file.
- **Templates on first open:** FSAE 600 cc four · FSAE 450 single · NA V8 4-2-1 · turbo inline-4 · blank.
- **Performance budget:** workspace switch < 100 ms; field edit propagates to dependent analytical/TMM displays < 50 ms; canvas 60 fps with 40 components; fully responsive while a sweep or optimisation runs.

---

# Part 9 — Optimisation

## 9.1 Variables

Any numeric model field, exposed via the model tree with bounds and an optional discrete step (e.g. tube sizes actually available). Includes: runner and primary lengths and diameters, taper ratios, step positions and diameters, plenum and airbox volumes, collector type and cone geometry, secondary lengths, silencer geometry, cam events (IVC, EVO, LCA, phasing), turbine A/R and trim or a discrete turbo selection, boost target curve, wastegate strategy, scroll pairing (discrete), CAC size, restrictor–compressor duct geometry, injection timing and location.

## 9.2 Objectives

- **Weighted area under the torque curve** over a user-defined rpm band — the correct objective for a race car, and the default
- Peak power; peak torque; torque at a specific rpm
- Minimise cylinder-to-cylinder VE spread
- **Time-to-torque** (turbo response)
- Maximise knock margin; maximise efficiency-weighted compressor operating point
- **Maximise Order Purity Index**; minimise distance to a sound target profile or a Reference Match fingerprint; minimise drone risk in a cruise band; maximise tonality; minimise sharpness
- Minimise packaging length or volume

## 9.3 Constraints

Valve-to-piston clearance · maximum dimensions · rules compliance (restrictor throat, dB(C) at both FSAE test points, drive-by dB(A)) · minimum knock margin · manufacturability (minimum bend radius, available tube sizes) · compressor surge margin ≥ threshold · below choke · turbo speed limit · turbine inlet temperature limit · peak cylinder pressure limit · blow-through fuel loss ceiling · minimum torque or area-under-curve relative to a baseline.

## 9.4 Algorithms

- **Sobol / Latin-hypercube DOE** with a response surface, for orientation
- **Morris screening and Sobol sensitivity indices** — tell the user *which three variables actually matter* before spending compute
- **Nelder–Mead and Powell** for local refinement
- **CMA-ES** for single-objective global search
- **NSGA-II** for Pareto fronts
- **Bayesian optimisation** with a Gaussian-process surrogate and expected-improvement acquisition — the right default when each evaluation costs a 20-point rpm sweep

## 9.5 Engineering

Parallel evaluation across cores; a content-addressed cache keyed on a hash of the resolved model so repeated designs are free; checkpoint/resume; a full design archive re-explorable after the run.

**Cost management for expensive objectives.** Acoustic and boost evaluations are expensive (finer mesh, more cycles, more stored data). Use the analytical layer and TMM as cheap surrogates in the inner loop, and the full nonlinear solve only for candidates the surrogate ranks highly — a natural fit for the Bayesian optimiser.

## 9.6 The headline trade-off views

- **Power vs. sound** — area under torque against distance to the sound target, as a Pareto front with click-to-audition.
- **Response vs. peak power** — the fundamental turbo trade, as a Pareto front with click-to-inspect.
- **Power vs. response vs. sound** — three objectives as a **parallel-coordinates plot**, which is more readable than a 3D front.

Nothing else available does this, and it makes trade-offs concrete instead of folkloric.

## 9.7 Presets

- **Cam timing preset:** IVC, EVO, LCA and phasing as variables with clearance as a hard constraint, and a result view plotting optimum LCA against rpm so the user can see whether VVT is worth the complexity.
- **NA-vs-boosted comparison preset:** run the same engine both ways and show the divergence in optimal geometry and cam timing, with the mechanism explained.

**The optimiser will find unphysical designs** that exploit model weaknesses — a 3 m runner, a 2 mm pipe, free scavenging. Constrain aggressively and always show the geometry, not just the number.

---

# Part 10 — Data formats and interoperability

- **`.wbproj`** — JSON, schema-versioned, indented, stable key order, **diffing cleanly in git** so teams can version-control designs. Includes a `provenance` block (app version, solver scheme, date, model hash) and per-field origin badges (§8.5).
- **`.wbres`** — SQLite: run metadata, operating points, scalar results, and time-series arrays as compressed blobs. Queryable from Python/pandas, which this audience will want.
- **Imports:** cam lift CSV and cam-doctor exports · flow-bench CSV · dyno CSV (for overlay against prediction) · fuel property CSV · compressor and turbine maps (CSV, documented text formats, and the **map digitiser** from an image) · user audio for Reference Match (processed locally, discarded after metric extraction).
- **Exports:** CSV · Parquet · PNG/SVG plots · PDF/HTML report · 48 kHz 24-bit WAV and FLAC audio with a metadata sidecar (model hash, rpm profile, listener preset, seed, resolved bandwidth) · manifold centreline geometry (points and diameters) as CSV/DXF for CAD handoff.
- **CLI** — `wavebench run model.wbproj --rpm 5000:12000:250 --out results.wbres`, plus `sweep`, `optimize`, `render`, `validate`. The CLI is the scripting and CI interface and must reach feature parity with the GUI's solver capabilities.

---

# Part 11 — Open source setup

- **Licence: Apache-2.0.** Permissive with an explicit patent grant — right for adoption by student teams and companies alike. (MPL-2.0 only if you want modifications returned.)
- **Intellectual property discipline.** Implement equations and correlations from the literature and cite them. **Do not** paste copyrighted tables, figures or datasets from books or SAE papers into the repository. Coefficients that are facts may be implemented; surrounding text and figures may not be reproduced. **Ship no manufacturer turbo maps and no audio recordings** without an explicit permissive licence from their owner. Ship your own generated default datasets with documented provenance.
- **Repo furniture:** README with screenshots, validation plots and audio samples of your own making · `docs/` (physics, acoustics, boost, numerics, validation, user guide) · CONTRIBUTING · CODE_OF_CONDUCT · issue and PR templates · `.editorconfig` · `Directory.Build.props` with warnings-as-errors · CHANGELOG (Keep a Changelog) · semantic versioning.
- **CI (GitHub Actions, windows-latest):** build → unit tests → verification suite on every PR; validation suite nightly and on release; BenchmarkDotNet regression check on the solver hot path with a failure threshold.
- **Releases:** MSIX package and a portable zip; note the code-signing situation honestly.
- **Community data:** `validation/community/` for contributed dyno data, geometry, recordings and turbo maps under a permissive data licence, each case named after its contributor. This is how the project earns credibility.

---

# Part 12 — Phase plan

26 phases in a single linear order. **Gate = all criteria pass, tests green, docs updated, committed.**

Milestones: **v0.1 headless gas dynamics** at Phase 7 · **v0.4 headless acoustics** at Phase 11 · **v0.6 headless forced induction** at Phase 15 · **v0.9 full GUI** at Phase 21 · **v1.0** at Phase 25.

> *Optional reordering:* Phases 16–19 (shell, data screens, canvas, results) may be pulled forward to directly after Phase 7 if an earlier usable GUI is wanted. If you do, the Sound and Boost workspaces are added later as their physics lands. Do not reorder anything else.

---

### Phase 0 — Foundations
Solution scaffolding, all projects, `Directory.Build.props`, nullable and warnings-as-errors, xUnit harness, GitHub Actions CI, Apache-2.0 licence, README skeleton, logging, and a **units and quantities layer** — strongly-typed quantities with a single canonical SI internal representation and conversions for length (m/mm/in), pressure (Pa/kPa/bar/psi/inHg), temperature (K/°C/°F), volume, mass flow, area, angle, rotational speed and sound level, with parsing and tabular-figure-friendly formatting.
**Gate:** CI green; unit conversion tests pass including round-trip precision.

### Phase 1 — Thermodynamics and fuels
NASA polynomial evaluation, species database, mixture properties, `R`, `γ`, `a`, `h`, `u`, `s`; the fuel record type and shipped library; stoichiometry and product composition from formula; charge cooling; Metghalchi–Keck flame speed; Douaud–Eyzat plus Livengood–Wu knock.
**Gate:** `c_p`, `γ`, `a` for air and stoichiometric products match published tables within 0.2% / 0.1%; stoich AFR from formula matches the tabulated value for every shipped fuel within 0.5%; M100 charge cooling at λ = 0.8 lands in the documented 30–50 K range.

### Phase 2 — 1D solver core (constant area, no sources)
Mesh, state arrays, primitive/conservative conversion, HLLC, MUSCL reconstruction with limiters, Hancock half-step, CFL control, transmissive and reflective boundaries.
**Gate:** Sod, Lax and 123 problems match the exact Riemann solution within tolerance; observed order ≥ 1.8 under refinement; positivity preserved; acoustic pulse propagates 20 pipe lengths with < 2% amplitude loss.

### Phase 3 — Source terms and thermal model
Variable area (well-balanced), friction, wall heat transfer, species transport, pipe wall thermal nodes with surface treatments. **Also: the acoustic fine-mesh run mode and the bandwidth characterisation test (§5.5).**
**Gate:** stationary taper spurious velocity < 1e-10 m/s; steady friction within 1% of Darcy–Weisbach; steady heat transfer within 1% of analytical; species bounded and summing to one; a hot-exhaust-composition cell reports a sound speed matching hand calculation; the scheme's −3 dB bandwidth is measured and published.

### Phase 4 — Boundaries and components
Open/closed ends with `C_d` and bellmouth options, orifice, throttle, area change, 0D plenum, **junctions with both constant-pressure and pressure-loss models**, restrictor with choking, injector mass source.
**Gate:** steady nozzle flow within 0.5% of isentropic tables; choked mass flow correct; junction loss coefficients match published steady-flow data across branch angles and area ratios; restrictor chokes at the theoretically correct mass flow.

### Phase 5 — Engine assembly (motored)
Crank kinematics, cam handling and import, valve boundary with `C_d` maps, cylinder 0D gas exchange, firing order and phasing, network assembly, cycle-convergence manager. **No combustion.**
**Gate:** a motored single-cylinder produces a sensible VE curve with a visible tuning peak; mass and energy conserve to < 0.1% per cycle; the tuning peak rpm agrees with the analytical organ-pipe estimate within ~5%; repeated runs are bit-identical.

### Phase 6 — Combustion, heat transfer, knock, friction
Wiebe (single and double), two-zone model, Woschni/Hohenberg/Annand, blowby and crevices, knock integral, Chen–Flynn friction, BMEP/torque/power/BSFC. **Cycle-to-cycle variability (§3.4).** Optionally, only once the rest is green, predictive turbulent-entrainment combustion.
**Gate:** at least one published engine case reproduced — VE within 3%, peak-torque rpm within 250 rpm; the knock model gives the correct qualitative ranking across RON95 / E85 / M100 at fixed geometry.

### Phase 7 — Headless product · **v0.1**
Model schema and serialisation, validation rules, SQLite results store with high-resolution acoustic capture, CLI with run/sweep/validate, parallel operating-point execution, mesh-sensitivity utility, full verification suite in CI.
**Gate:** the §5.7 performance budget is met and recorded by BenchmarkDotNet; the CLI runs every validation case from a script; the validation report generator produces the plots committed to `validation/`.

### Phase 8 — Linear acoustics engine (TMM)
Complex arithmetic layer, four-pole element library, mean flow and damping, network assembly sharing the model tree, TL/IL/transfer function/impedance outputs, Levine–Schwinger radiation impedance.
**Gate:** all §6.1 acoustic verification tests pass; TMM and the nonlinear solver agree within 1 dB at small amplitude below the resolved bandwidth; a 20-element network solves in under 10 ms across 1–10 kHz so the UI can be interactive.

### Phase 9 — Acoustic source, capture, propagation, order analysis
Frequency-domain source-strength extraction, broadband flow-noise sources, radiation and free-field propagation with atmospheric absorption and ground reflection, listener presets, crank-synchronous order tracking, OPI and the character metrics, the collector timing calculation and the scroll separation index.
**Gate:** the crossplane vs. flat-plane half-order test passes; collector timing errors match hand calculation exactly; order tracking recovers known order levels on a synthetic signal within 0.2 dB.

### Phase 10 — Auralisation
Hybrid nonlinear/TMM combination, crank-angle wavetable synthesis, rev-sweep and load interpolation, stochastic variation, stems, Doppler and drive-by, WAV/FLAC export with metadata, loudness normalisation, the A/B player, overrun burble.
**Gate:** a 1500→7200 rpm sweep renders with no audible crossfade artefacts; two renders with the same seed are bit-identical; A/B pairs match within 0.5 LU; a listener can distinguish the crossplane and flat-plane renders.

### Phase 11 — Sound metrics and compliance · **v0.4**
ISO 532-1/-3, DIN 45692, ECMA-418-2 loudness/tonality/roughness, fluctuation strength, weighted SPL with time weighting; the engine-specific metric set; target profiles; Reference Match extraction; FSAE / ISO 5130 / ISO 362 procedures as versioned rules data with pass/fail and margin.
**Gate:** metric implementations match published reference verification signals within each standard's tolerance; the FSAE test-speed calculation is correct for a set of known stroke lengths; compliance results carry an explicit uncertainty band.

### Phase 12 — Turbomachinery data and steady matching
Map schema, import/export, map digitiser, physics-based extrapolation with shaded regions, corrected quantities with per-map references, compressor and turbine map models, shaft power balance, surge/choke detection and margins, the turbo database and auto-match ranking.
**Gate:** verification tests for map round-trip, shaft balance and adiabatic relations pass; a known turbo/engine pair produces a plausible operating line; the digitiser reproduces a synthetic map from an image within 2%.

### Phase 13 — Coupled unsteady forced induction
Shaft dynamics, bearing friction, quasi-steady and volute-resolved turbine boundaries, twin-scroll partial admission, VGT, wastegate with division loss, blow-off/recirculation, charge air cooler with thermal mass, turbocharger thermal model and diabatic correction, boost control.
**Gate:** the turbine hysteresis and twin-scroll pairing validation cases pass; the diabatic correction improves on raw-map outlet temperature; pulse-energy-delivery and manifold-volume-ratio metrics respond correctly to a primary-diameter sweep; volute-resolved runtime within 2× quasi-steady.

### Phase 14 — Forced-induction engine behaviour
Positive scavenging pressure ratio tracking, blow-through fraction, unburnt fuel loss and TIT effects, superchargers (positive displacement and centrifugal), electric assist, the FSAE restrictor-upstream layout, altitude and hot-day sensitivity.
**Gate:** the turbo-vs-NA cam optimum and FSAE restrictor validation cases pass; the NA and turbo cam optima diverge and the reason is derivable from the scavenging-pressure output.

### Phase 15 — Transient and forced-induction acoustics · **v0.6**
Step-response and vehicle-acceleration transients, time-to-torque with sensitivity band, heat-soak scenarios; turbine four-pole, compressor blade-pass, whoosh, surge flutter, wastegate and BOV noise, intake-path elevation.
**Gate:** transient spool within 15% of a measured case; turbine acoustic attenuation and OPI drop as expected; surge flutter frequency physically derived, not tuned by ear.

### Phase 16 — Shell and mode infrastructure
Workspace architecture with conditional visibility, job tray with checkpointing, **the provenance-badge system in `WaveBench.Model`**, the mode toggle with the seven §8.8 rules, undo across modes, command palette, units toggle, accessibility pass, token resource dictionary with light and dark themes.
**Gate:** an automated test round-trips a model Simple → Advanced → Simple → Advanced and asserts byte-identical output at every step; a simulated wizard re-run provably never modifies a `You`, `Imported` or `Optimised` field; killing the process mid-sweep and restarting recovers model and job state; a test asserts no hard-coded colours in XAML.

### Phase 17 — Design workspace
Engine, Head & Cam, and Fuel & Combustion screens with all inputs, imports and derived readouts. Templates. Autosave.
**Gate:** a complete model can be built and saved entirely through the UI and produces byte-identical results to the same model run from the CLI; theme switching is instant and complete.

### Phase 18 — Manifold canvas
Node-graph editor with palette, drag/snap/auto-layout, multi-select, copy/paste, inspector binding, live geometry summary, inline design warnings with citations and cross-workspace links.
**Gate:** every collector configuration in §2.8 and §4.6.2 can be built in under two minutes each by a new user; canvas stays at 60 fps with 40 components.

### Phase 19 — Results workspace
All performance plots, the x–t wave diagram with animation, wave decomposition, pulse-interference and scroll-separation diagrams, probe management, transient traces, export.
**Gate:** the wave diagram renders and animates a 30-cycle result without stutter; wave decomposition correctly identifies the reflected expansion arrival in a known textbook case; every plot exports to PNG and SVG.

### Phase 20 — Sound workspace
The §8.4 Sound layout, all acoustic plots, the interactive-TMM-then-refine pattern, the audition player with level matching, "Explain this".
**Gate:** dragging a primary-length slider updates the timing chart and spectrum in under 50 ms; A/B audition is gapless and level-matched; the M50 factory-vs-6-1 comparison is reproducible end to end and the explanation text is correct.

### Phase 21 — Boost workspace · **v0.9**
Compressor map overlay, turbine A/R sweep, boost control setup, charge cooling, transient view, auto-match, all with conditional visibility and cross-links.
**Gate:** the Boost workspace appears and disappears correctly with aspiration changes; the restrictor-upstream operating line renders correctly with surge and choke warnings.

### Phase 22 — Optimisation
Variable/objective/constraint definition UI, DOE, sensitivity screening, CMA-ES, NSGA-II, Bayesian optimisation, parallel evaluation with caching, surrogate inner loop, Pareto explorer, parallel-coordinates view, cam-timing and NA-vs-boosted presets.
**Gate:** on a synthetic problem with a known optimum the optimiser converges reliably; on a real FSAE case it improves area-under-torque measurably over the hand-designed baseline; the clearance constraint is never violated in a returned design; the power-vs-sound and response-vs-power fronts are explorable with click-to-audition and click-to-inspect.

### Phase 23 — Simple mode and the wizard
All nine wizard steps with explainers and live preview, the derivation layer filling the full model from wizard answers, the bounded optimisation, the Design Brief with why/confidence/uncertainty and the build list, PDF export, "Open in Advanced".
**Gate:** a user with no prior knowledge reaches a complete, fabricable Design Brief in under 15 minutes; the brief's numbers match what Advanced mode produces from the identical model; first preview under 1 s and final brief under 5 minutes; every recommendation carries a why, a confidence and an uncertainty band.

### Phase 24 — Learn layer and guardrails
"Why" text on every field, "Show me" parametric sweeps, Concepts panel, "Explain this result", guided tours, implausible-input detection, generic-defaults banner, global search, all cross-workspace warning links.
**Gate:** every user-editable field has "why" text and a typical range; "Show me" works on every numeric parameter participating in the solve; every design warning links to the field or plot causing it.

### Phase 25 — Reporting, docs, release · **v1.0**
PDF/HTML report generator covering performance, acoustics and boost; complete user guide; `docs/` finalised with the full citation list; validation gallery in the README; MSIX packaging; signed release.
**Gate:** a generated report is complete enough to defend a design decision — including a sound and compliance decision and a turbo match — without any other document; a new user goes from download to a converged torque curve in under 15 minutes using only the docs.

---

# Part 13 — Bibliography

**Verify every citation against the publisher before quoting it in code or docs.**

## Core texts — gas dynamics and engines
- Blair, G. P. *Design and Simulation of Four-Stroke Engines.* SAE R-186, 1999. — boundary conditions and discharge coefficients
- Blair, G. P. *Design and Simulation of Two-Stroke Engines.* SAE R-161, 1996. — unsteady gas dynamics fundamentals
- Winterbone, D. E. & Pearson, R. J. *Theory of Engine Manifold Design: Wave Action Methods for IC Engines.* SAE/PEP, 2000. — governing equations, numerics, boundary conditions
- Winterbone, D. E. & Pearson, R. J. *Design Techniques for Engine Manifolds: Wave Action Methods for IC Engines.* PEP, 1999. — application, case studies, silencing
- Benson, R. S. *The Thermodynamics and Gas Dynamics of Internal Combustion Engines, Vol. 1.* Clarendon Press, 1982.
- Heywood, J. B. *Internal Combustion Engine Fundamentals,* 2nd ed. McGraw-Hill, 2018.
- Toro, E. F. *Riemann Solvers and Numerical Methods for Fluid Dynamics,* 3rd ed. Springer, 2009. — HLLC, MUSCL-Hancock
- Ferguson, C. R. & Kirkpatrick, A. T. *Internal Combustion Engines: Applied Thermosciences,* 4th ed. Wiley, 2020.
- Caton, J. A. *An Introduction to Thermodynamic Cycle Simulations for Internal Combustion Engines.* Wiley, 2016.

## Discharge coefficients and valve flow
- Blair, G. P. & Drouin, F. M. M. "Relationship Between Discharge Coefficients and Accuracy of Engine Simulation." SAE 962527, 1996.
- Blair, G. P. et al. "Coefficients of Discharge at the Apertures of Engines." SAE 952138, 1995.
- Blair, G. P. et al. "Some Fundamental Aspects of the Discharge Coefficients of Cylinder Porting and Ducting Restrictions." SAE 980764, 1998.
- Blair, G. P., Callender, E. & Mackey, D. O. "Maps of Discharge Coefficients for Valves, Ports and Throttles." SAE 2001-01-1798, 2001.

## Junctions and manifolds
- Bassett, M. D., Winterbone, D. E. & Pearson, R. J. "Calculation of steady flow pressure loss coefficients for pipe junctions." *Proc. IMechE Part C,* 215(8), 2001.
- Bassett, M. D., Pearson, R. J., Fleming, N. P. et al. "A Multi-Pipe Junction Model for One-Dimensional Gas-Dynamic Simulations." SAE 2003-01-0370, 2003.
- Bassett, M. D., Fleming, N. P. & Pearson, R. J. "Modelling engines with pulse-converted exhaust manifolds using one-dimensional techniques." SAE 2000-01-0290, 2000.
- Bingham, J. F. & Blair, G. P. "An Improved Branched Pipe Model for Multi-Cylinder Automotive Engine Calculations." *Proc. IMechE,* 1985.
- Corberán, J. M. "A New Constant Pressure Model for N-Branch Junctions." *Proc. IMechE,* 1992.
- Benson, R. S., Woollatt, D. & Woods, W. A. "Unsteady flow in simple branch systems." *Proc. IMechE,* 1963–64.
- Pearson, R. J. & Winterbone, D. E. "Calculating the effects of variations in composition on wave propagation in gases." *Int. J. Mechanical Sciences,* 1993.

## Intake tuning theory
- Engelman, H. W. "Design of a Tuned Intake Manifold." ASME 73-WA/DGP-2, 1973.
- Thompson, M. & Engelman, H. W. "The Two Types of Resonance in Intake Tuning." ASME 69-DGP-11, 1969.
- Morse, P., Boden, R. & Schecter, H. "Acoustic vibrations and internal combustion engine performance." *J. Applied Physics,* 1938.
- Ohata, A. & Ishida, Y. "Dynamic Inlet Pressure and Volumetric Efficiency of Four-Cycle Four-Cylinder Engine." SAE 820407, 1982.
- Tabaczynski, R. J. "Effects of Inlet and Exhaust System Design on Engine Performance." SAE 821577, 1982.

## Formula SAE
- Claywell, M. & Horkheimer, D. "Improvement of Intake Restrictor Performance for a Formula SAE Race Car through 1D & Coupled 1D/3D Analysis Methods." SAE 2006-01-3654, 2006.
- Claywell, M., Horkheimer, D. & Stockburger, G. "Investigation of Intake Concepts for a Formula SAE Four-Cylinder Engine Using 1D/3D (Ricardo WAVE-VECTIS) Coupled Modeling Techniques." SAE 2006-01-3652, 2006.
- Current FSAE / Formula Student rulebook, Article IC3 — **versioned data file, not a constant**.

## Combustion, heat transfer, knock
- Woschni, G. "A Universally Applicable Equation for the Instantaneous Heat Transfer Coefficient in the Internal Combustion Engine." SAE 670931, 1967.
- Hohenberg, G. "Advanced Approaches for Heat Transfer Calculations." SAE 790825, 1979.
- Annand, W. J. D. "Heat Transfer in the Cylinders of Reciprocating Internal Combustion Engines." *Proc. IMechE,* 1963.
- Douaud, A. M. & Eyzat, P. "Four-Octane-Number Method for Predicting the Anti-Knock Behavior of Fuels and Engines." SAE 780080, 1978.
- Livengood, J. C. & Wu, P. C. "Correlation of Autoignition Phenomena in Internal Combustion Engines and Rapid Compression Machines." *5th Symposium on Combustion,* 1955.
- Metghalchi, M. & Keck, J. C. "Burning Velocities of Mixtures of Air with Methanol, Isooctane, and Indolene at High Pressure and Temperature." *Combustion and Flame,* 1982.
- Ghojel, J. I. "Review of the development and applications of the Wiebe function." *Int. J. Engine Research,* 2010.
- Gordon, S. & McBride, B. J. NASA SP-273; McBride, Zehe & Gordon, NASA TP-2002-211556 (NASA-9 coefficients).
- Boris, J. P. & Book, D. L. "Flux-Corrected Transport." *J. Computational Physics,* 1973.

## Duct acoustics and silencers
- Munjal, M. L. *Acoustics of Ducts and Mufflers,* 2nd ed. Wiley, 2014. — the transfer-matrix reference
- Davies, P. O. A. L. "Practical flow duct acoustics." *J. Sound and Vibration,* 1988.
- Davies, P. O. A. L. "Piston engine intake and exhaust system design." *J. Sound and Vibration,* 1996.
- Levine, H. & Schwinger, J. "On the radiation of sound from an unflanged circular pipe." *Physical Review,* 1948.
- Ingard, U. "On the theory and design of acoustic resonators." *JASA,* 1953.
- Morse, P. M. & Ingard, K. U. *Theoretical Acoustics.* Princeton, 1968.
- Pierce, A. D. *Acoustics: An Introduction to Its Physical Principles and Applications.* Springer, 2019.

## Psychoacoustics and sound quality
- Fastl, H. & Zwicker, E. *Psychoacoustics: Facts and Models,* 3rd ed. Springer, 2007.
- ECMA-418-2 (2nd ed., 2022) — Sottek Hearing Model: loudness, tonality, roughness
- ISO 532-1:2017 (Zwicker); ISO 532-3 (Moore–Glasberg)
- DIN 45692 (sharpness); DIN 45681 (tonality)
- Genuit, K.; Bodden, M. — automotive sound quality and sound design literature
- Lotinga, M. J. B., Torjussen, M. & Felix Greco, G. "Verified implementations of the Sottek psychoacoustic Hearing Model standardised sound quality metrics." *Forum Acusticum / Euronoise,* 2025 — and the associated open-source packages, for verification signals
- ITU-R BS.1770 / EBU R128 — loudness normalisation

## Acoustic measurement and regulation
- ISO 5130 (stationary vehicle noise) · ISO 362 (drive-by) · ISO 9613-1 (atmospheric absorption) · IEC 61672 (sound level meters, weighting, time constants) · SAE J1287, SAE J2825 (stationary exhaust sound)

## Turbocharging
- Watson, N. & Janota, M. S. *Turbocharging the Internal Combustion Engine.* Macmillan, 1982. — pulse vs. constant-pressure turbocharging
- Baines, N. C. *Fundamentals of Turbocharging.* Concepts NREC, 2005.
- Hiereth, H. & Prenninger, P. *Charging the Internal Combustion Engine.* Springer, 2007.
- Japikse, D. & Baines, N. C. *Introduction to Turbomachinery.* Concepts ETI / Oxford, 1994.
- Dixon, S. L. & Hall, C. *Fluid Mechanics and Thermodynamics of Turbomachinery.*
- SAE J1826 — Turbocharger Gas Stand Test Code (current revision 2022; defines surge, soft surge and choke line, speed-line and data-point requirements, measurement-section geometry and reporting)
- SAE J922 — Turbocharger Nomenclature and Terminology

## Unsteady turbine behaviour and 1D turbo modelling
- Costall, A. & Martinez-Botas, R. F. "Fundamental Characterization of Turbocharger Turbine Unsteady Flow Behavior." ASME GT2007-28317.
- Szymko, S., Martinez-Botas, R. F. & Pullen, K. "Experimental evaluation of turbocharger turbine performance under pulsating flow conditions." ASME GT2005-68878.
- Winterbone, D. E., Nikpour, B. & Alexander, G. "Measurement of the performance of a radial inflow turbine in conditional steady and unsteady flow." *IMechE Turbocharging Conference,* 1990.
- Karamanis, N. & Martinez-Botas, R. F. "Mixed-flow turbines for automotive turbochargers: steady and unsteady performance." *Int. J. Engine Research,* 2002.
- Chiong, M. S., Rajoo, S., Martinez-Botas, R. F. & Costall, A. W. "Engine turbocharger performance prediction: one-dimensional modeling of a twin entry turbine." *Energy Conversion and Management,* 57, 2012.
- De Bellis, V., Marelli, S., Bozza, F. & Capobianco, M. "1D simulation and experimental analysis of a turbocharger turbine for automotive engines under steady and unsteady flow conditions." *Energy Procedia,* 45, 2014.
- Ding, Z., Zhuge, W., Zhang, Y., Chen, H., Martinez-Botas, R. & Yang, M. "A one-dimensional unsteady performance model for turbocharger turbines." *Energy,* 132, 2017.
- Yang, B., Xue, Y., Martinez-Botas, R. & Yang, M. "Unsteady Gas Dynamics of Radial Turbine Volutes Under Pressure Pulsations." *ASME J. Turbomachinery,* 145(3), 2023.
- Serrano, J. R. et al. — turbine models for 0D/1D engine codes and map extrapolation methods (CMT–Universitat Politècnica de València)
- Serrano, J. R., Olmeda, P., Arnau, F. J. et al. — turbocharger heat transfer and diabatic map correction

## Surge
- Greitzer, E. M. "Surge and Rotating Stall in Axial Flow Compressors, Parts I & II." *ASME J. Engineering for Power,* 1976.
- Moore, F. K. & Greitzer, E. M. "A Theory of Post-Stall Transients in Axial Compression Systems." *ASME J. Engineering for Gas Turbines and Power,* 1986.

---

# Part 14 — Gotchas

## Gas dynamics
1. **Well-balancedness in tapers.** The most common silent bug in this class of code. Test it in Phase 3 and never let the test be deleted.
2. **Junction coupling stability.** Solving junctions after the pipe update rather than jointly produces small conservation errors that accumulate over 30 cycles. Solve the junction as a boundary Riemann problem shared by all branches within the same timestep.
3. **Discharge coefficients dominate accuracy.** No solver sophistication compensates for a wrong `C_d` map. Make the import wizard excellent and the generic-defaults warning impossible to miss.
4. **Cycle and wall-temperature convergence** are nested loops with different time constants. Converge them together with relaxation or the run oscillates.
5. **Heat transfer in pulsating flow is genuinely uncertain.** Expose the enhancement factor, document it as empirical, do not pretend otherwise.
6. **Species-transport diffusion.** A low-order scheme smears the fresh-charge/residual interface and corrupts the local sound speed. Verify interface sharpness explicitly.
7. **Determinism under parallelism.** Fix the reduction order from day one; retrofitting it is painful.
8. **Resist scope creep toward 3D.** A validated, fast, beautiful 1D tool is far more valuable here than a mediocre 1D/3D coupling.

## Acoustics
9. **Never present audio above the resolved bandwidth as physical.** Grey it out in plots, state it in export metadata. This is the line between a research tool and a toy with a nice sound.
10. **Always level-match A/B by default.** Louder wins otherwise, and every subjective conclusion the user draws will be wrong.
11. **Differentiating the volume velocity amplifies numerical noise.** Do it in the frequency domain with an explicit roll-off.
12. **Without cycle-to-cycle variability the synthesis sounds like a synthesiser.** Add it early — cheap, and it transforms perceived realism.
13. **Absolute SPL prediction is ±3 dB at best.** Say so wherever a number appears, especially on the compliance screen.
14. **The intake is not optional** — on ITB and all boosted engines it can dominate perceived character.
15. **Exhaust temperature sets everything acoustic**, because it sets `a`. Reuse the wall thermal model; never assume a fixed gas temperature.
16. **The TMM assumes linearity.** Exhaust pulses near the valve are emphatically nonlinear. Use the hybrid split and be explicit about the crossover.
17. **Do not "sweeten" the output.** No reverb, EQ or saturation on the physical render. Offer a clearly-separated presentation bus for video work, with every process listed.

## Forced induction
18. **Read reference conditions from the map file.** Assuming 25 °C / 101.3 kPa when the map says otherwise is a silent 5% error propagating into every conclusion.
19. **Never spline-extrapolate a compressor map.** Physics-based extrapolation, shaded in every plot.
20. **Gas-stand maps are not adiabatic.** Apply the diabatic correction or state plainly that outlet temperature will be optimistic.
21. **Quasi-steady turbine models hide the hysteresis.** Ship both and report the difference.
22. **A turbo engine's cam optimum is not an NA engine's cam optimum.** If the optimiser converges to the same answer, check the scavenging-pressure and blow-through modelling before believing it.
23. **Blow-through with port injection costs fuel and raises TIT.** Model it, or the optimiser will exploit scavenging the engine cannot have.
24. **An internally-gated wastegate defeats scroll division when it opens.** Model it or the twin-scroll benefit is overstated at high load.
25. **Transient results depend on inertia, friction and thermal states users rarely know accurately.** Show a sensitivity band, not a single number.
26. **Surge is a system property, not a compressor property.** It depends on the plenum the user designed in the Manifold workspace. Make that coupling visible.

## Application and modes
27. **Do not build a second, simplified model object.** The moment Simple mode has its own data structure the two will drift and every switch becomes a lossy conversion.
28. **Provenance badges must exist before the wizard**, not after. They are what makes the wizard safe to re-run.
29. **Simple mode must run the real solver.** A lookup-table simple mode is a different, worse product, and users discover the discrepancy the first time they switch.
30. **Never hide a setting that materially changes a Simple-mode number** without saying so. Hidden-but-active is fine; hidden-and-unmentioned is a trap.
31. **Confidence indicators are not decoration.** A beginner acting on a low-confidence recommendation from generic `C_d` maps is the most likely way this software wastes someone's money.
32. **Conditional workspaces need a discovery path.** Boost hidden on an NA model still needs an obvious way to add forced induction.
33. **Background jobs must not be tied to a view's lifetime.** Checkpoint them; workspace switching is a common source of lost work.
34. **Resist a third mode.** "Intermediate" always gets proposed and always makes the switching rules ambiguous. Two modes, three disclosure tiers inside Advanced.

## Licensing
35. **Do not ship copyrighted material.** No textbook tables or figures, no manufacturer turbo maps, no audio recordings — unless explicitly licensed. Implement equations, cite sources, generate your own defaults, and let the community contribute data under a permissive licence.

---

# Part 15 — First prompt

> Read `WaveBench-Master-Plan.md` in full before writing any code. Execute **Phase 0** only.
>
> Create the solution structure in Part 7, targeting .NET 10, with nullable reference types and warnings-as-errors enabled solution-wide via `Directory.Build.props`. Add the xUnit test projects, a GitHub Actions workflow that builds and tests on `windows-latest`, the Apache-2.0 licence, and a README skeleton.
>
> Then implement the units and quantities layer described in Phase 0: strongly-typed quantities with a single canonical SI internal representation, with conversions for length (m/mm/in), pressure (Pa/kPa/bar/psi/inHg), temperature (K/°C/°F), volume, mass flow, area, angle, rotational speed and sound level. Include parsing and formatting with configurable precision and tabular-figure-friendly output.
>
> Write tests covering round-trip precision, boundary values and formatting. Do not proceed to Phase 1. Report the Phase 0 gate status when done.

**Subsequent sessions:** open a new session per phase. Begin each with *"Read `WaveBench-Master-Plan.md`. Phases 0–N are complete and green. Execute Phase N+1 only, then report the gate status."* Never let a session span two phases.
