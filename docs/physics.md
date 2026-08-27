# WaveBench physics documentation

Source of truth: the master plan, Part 2. This document records what is
actually implemented, with sources and validity ranges. It grows with the
phases; sections are added in the same commit as the code they describe.

## 1. Thermodynamic properties (Phase 1)

### 1.1 Species data

NASA 7-coefficient (CHEMKIN two-range) polynomials, evaluated per species:

```
cp/R = a1 + a2·T + a3·T² + a4·T³ + a5·T⁴
h/RT = a1 + a2/2·T + a3/3·T² + a4/4·T³ + a5/5·T⁴ + a6/T   (includes Δh_f,298)
s/R  = a1·ln T + a2·T + a3/2·T² + a4/3·T³ + a5/4·T⁴ + a7
```

Curated species set (embedded resource `Thermo/Data/thermo.dat`):
`N2 O2 AR CO2 H2O CO H2 NO OH O H` plus fuel vapours
`CH4 C3H8 CH3OH C2H5OH C7H8 IC8H18 NC7H16`.

Provenance (also recorded in the data file header):

- Core species and light fuels: **GRI-Mech 3.0** `thermo30.dat`.
- Ethanol, toluene, iso-octane, n-heptane: **Burcat & Ruscic** thermochemical
  database, via the OpenFOAM `therm.dat` redistribution.
- **H2O upper range (1000–3500 K) is a WaveBench fit** to NIST-JANAF (Shomate)
  data, because both the GRI and the 1989-NASA H2O fits run ~1% high against
  NIST-JANAF at 2000 K (51.7 vs 51.20 J/mol·K). The fit deviates ≤ 0.17 % from
  NIST-JANAF over 1000–3500 K and is continuity-matched to the (JANAF-accurate)
  lower range at 1000 K, with a6/a7 chosen for h and s continuity.

Verification: species-level c_p against NIST-JANAF at 298–2000 K within 0.2 %;
formation enthalpies (CO2 −393.52, H2O −241.83, CO −110.53 kJ/mol) within
0.5 %; c_p continuity at every range split; dh/dT ≡ c_p numerically.

Atomic weights: IUPAC 2021 abridged. Universal gas constant: CODATA exact
8314.462618 J/(kmol·K).

### 1.2 Mixtures

`MixtureThermo` implements (plan §2.3):

```
cp,mix(T,Y) = Σ Yk cp,k(T)       h_mix(T,Y) = Σ Yk hk(T)
R_mix(Y)    = Ru Σ (Yk / Mk)     γ = cp/(cp − R)     a = √(γ R T)
```

Entropy uses partial pressures: `s = Σ Yk [s°k(T) − Rk ln(Xk p/p_ref)]`.

Dry air is defined once (`AirComposition`): mole fractions N2 0.78084,
O2 0.20946, Ar 0.00934, CO2 0.000412 → M = 28.965 kg/kmol, R = 287.0 J/(kg·K).

Gate results (tolerances 0.2 % on c_p, 0.1 % on a, plan §2.3): air c_p matches
ideal-gas air tables at 300/500/1000/1500 K; γ(300 K) = 1.400; a = 331.4 m/s
at 0 °C and 343.2 m/s at 20 °C.

### 1.3 Fast path

`TabulatedSpecies`: c_p, h, s° pre-tabulated on a 200–3500 K grid at 5 K steps,
cubic (Catmull-Rom) interpolation, falling back to direct evaluation outside
the grid. Verified against direct evaluation within 0.02 %. Mixture-level
caching keyed on quantised composition is deferred to the solver phases, where
the hot path exists to profile against.

### 1.4 Combustion products

`CombustionProducts.Of(fuel CxHyOz, φ)`:

- φ ≤ 1: complete combustion (CO2, H2O, excess O2, N2, Ar; air CO2 carried).
- φ > 1: CO/H2 by the water-gas-shift equilibrium `CO2 + H2 ⇌ CO + H2O` with
  K constant (default 3.5, its value near 1740 K) — the standard simplification
  after Heywood ch. 4. Dissociation species (OH, NO, O, H) are **not** included
  in bulk products; they matter for emissions, not for exhaust R and γ
  (plan §2.2 minimum species set). The species exist in the database for later
  two-zone work.

Verified: element balances close to 1e-6; shift quotient equals K; stoich
iso-octane products give M = 28.72 kg/kmol and γ(1173 K) in the documented
1.28–1.31 band; a(950 K) ≈ 600 m/s (plan §2.2).

## 1.5 Species transport and the local sound speed (Phase 3)

The solver transports the §2.2 minimum species set (fresh air, fuel vapour,
burnt gas constituents) as mass fractions with the flow, and evaluates
R(Y), γ(T,Y) and a = √(γRT) per cell per timestep through the species
database — never a constant 343 m/s. Verified end-to-end: a 950 K burnt-gas
cell reports ≈ 600 m/s, a 310 K intake cell ≈ 353 m/s, both matching the
thermo layer's hand calculation. See docs/numerics.md §4 for the transport
scheme and boundedness guarantees.

## 1.6 Wall friction, heat transfer and the wall node (Phase 3)

Implemented per plan §2.1/§2.9 in `PipeFlowPhysics`, `DuctSolver` and
`WallThermalModel`: Haaland friction (laminar 64/Re below 2300, blended to
4000; per-pipe roughness), Sutherland μ(T), Colburn heat transfer with the
empirical pulsating-flow enhancement factor (default 1.3 — a documented weak
point of all 1D codes; the UI must present it as such), and a per-cell wall
thermal node with radiation and surface treatments as (ε, R_ext) pairs.
The wrapped-vs-bare wall temperature difference — which shifts tuned length
via the gas temperature — is demonstrated in the verification suite.

## 1.7 Boundaries and components (Phase 4)

- **Reservoir / open end** (`ReservoirBoundary`): characteristic-compatible
  subsonic boundary — inflow solves reservoir stagnation (h0, s0) against the
  interior's outgoing Riemann invariant; outflow imposes ambient pressure on
  the interior entropy and invariant. Verified: steady nozzle flow within
  0.5% of the isentropic tables; equal pressures produce no flow. End C_d
  (bellmouth ≈ 1.0, plain end ≈ 0.85) is an engineering default pending the
  full Blair-style end treatment.
- **Orifice / valve flow** (`CompressibleOrifice`): the §2.6 compressible
  restriction with choking; Φ* = 0.6847 (γ = 1.4) and the critical ratio
  0.5283 pinned against standard tables.
- **FSAE restrictor**: modelled as geometry (cone–throat–diffuser via
  `DuctGeometry.FromDiameterProfile`), so choking arises from the solved gas
  dynamics, not a formula. Verified: chokes at the theoretical mass flow
  within 1%, throat sonic, mass flow independent of further back-pressure
  reduction.
- **0D plenum** (`PlenumVolume`): open-system mass/energy/composition balance
  with queued port flows and deterministic commit. Verified against the exact
  adiabatic blowdown ODE within 0.5% (and it cools as it empties).
- **Orifice connector** (`OrificeConnector` + endpoints): quasi-steady
  coupling duct↔plenum↔ambient with per-direction C_d; duct side applied as
  an end-face flux override carrying upstream enthalpy and composition.
- **Throttle** (`ThrottleValve`): butterfly effective-area map by the
  standard geometric approximation (1 − cosθ/cosθ₀) with leakage floor,
  replaceable by a measured C_d(angle) map.
- **Junctions** (`Junction`): Benson constant-pressure solve (linearised
  characteristics, mixed enthalpy/composition to outflowing branches) and a
  pressure-loss variant applying Idelchik pair-coefficients
  (`TeeJunctionLoss`, referenced to combined-leg head, under-relaxed).
  Verified: mass conservation through a splitting junction within 1%, exact
  symmetric split, and the published-anchor coefficient checks (Crane 1.3
  dividing-branch value, Idelchik combining formulas).

  **Branch angle** is now carried, via the cos α terms of Idelchik's
  converging- and diverging-wye formulas: `Junction.Connect` takes a branch
  angle, 90° being a plain tee and a collector merging primaries typically
  10–30°. It matters — charging a shallow collector the right-angle
  coefficient overstates its loss substantially.

  Three things anchor it, none of them a fit. At 90° every cos term is zero,
  so all coefficients reduce **bit-identically** to the previously verified
  right-angle model (asserted to 1e-12, not to a tolerance). With all the
  flow through an aligned equal-area branch the junction is a straight pipe
  and ξ comes out exactly 0. And loss falls monotonically as the angle
  closes, 0.413 → 0.138 at q = 0.5.

  The combining branch coefficient **can be negative, and is allowed to be.**
  A primary merging into a larger collector decelerates and is dragged along
  by the faster combined stream, gaining total pressure at the other streams'
  expense — an ejector. Idelchik's converging-wye tables carry negative
  branch coefficients for this, the previous right-angle model already
  returned them at small q, and it is the scavenging a collector exists to
  produce: a 4-1 at 15° with A_s/A_c = 0.5 measures ξ = −0.079 where the same
  geometry as a tee measures +0.084. Only the junction as a whole must
  dissipate, not each leg pair. No clamp is applied — the bound falls out of
  the algebra, since the bracket's minimum is −1 at q = 0 and A ≤ 1, and a
  sweep asserts it. The welded-tee correction k < 1 in the dividing
  coefficient is a right-angle artefact and is faded out as the branch aligns
  (k_eff = k + (1 − k)·cos α), which leaves 90° untouched and is what keeps
  a dividing branch from going negative at shallow angles.

  **Limitation, unchanged:** these are *steady-flow* coefficients applied
  quasi-steadily. The unsteady junction coefficients of Bassett, Winterbone &
  Pearson (2001) and Bassett et al. (SAE 2003-01-0370), which the plan names,
  are still not implemented, and the angle dependence above is verified at
  its limits rather than against those papers' measured data. The Phase 4
  gate ("junction loss coefficients match published steady-flow data across
  branch angles and area ratios") is therefore closer but not fully met.
- **Injector** (`DuctMassSource`): metered vapour mass of one species into a
  cell at a given temperature, zero axial momentum, enthalpy-consistent.
  Verified: injected mass exactly matches the metered rate.

## 1.8 Engine assembly, motored (Phase 5)

- **Crank kinematics** (`CrankGeometry`): exact slider-crank with rod ratio
  and wrist-pin offset; V(θ), dV/dθ, mean piston speed, max-piston-speed
  angle. Verified: TDC/BDC volumes exact, ∮|dV| = 2·Vd, pin offset shifts
  true TDC to sinθ = e/(l+a).
- **Cam** (`CamProfile`): measured-table import (CSV, mm/m inferred), a
  generic harmonic analytic profile, a half-sine, and a **polydyne
  generator** — all analytic profiles flagged generic, since measured lift
  always wins. Plus event detection and effective-closing angle at a lift
  fraction.

  The polydyne (Dudley, *Machine Design* 1948; Thoren, Engemann & Stoddart,
  SAE 1952) is the family real cams are designed from:
  y = 1 + C₂x² + C_px^p + C_qx^q + C_rx^r on x = (θ − θ_nose)/(Δθ/2), with
  the four coefficients fixed by requiring lift, velocity, acceleration **and
  jerk** all to vanish at the seat. Default exponents 2-8-10-12.

  This matters because the raised cosine — the previous generic profile —
  reaches the seat with a finite acceleration of ½L(2π/Δθ)². Measured: 4.935
  in normalised units, which is π²/2 exactly. Acceleration therefore steps
  discontinuously to zero at seating and jerk is unbounded, which is what
  makes a follower bounce, and why no real cam is a cosine. The polydyne
  arrives with all three at zero.

  Verified against exact analytic derivatives rather than finite differences
  — near the seat every quantity vanishes together, so a difference stencil
  measures its own truncation error (it reported 3e-5 for something
  identically zero). The strongest check turned up by accident: with
  exponents 2-4-6-8 the solver returns C = [−4, 6, −4, 1], the binomial
  coefficients of **(1 − x²)⁴**, which satisfies the seating conditions by
  inspection. The linear solve is therefore checked against an independent
  closed form, matched to 1e-12 across the whole flank.

  One thing it is *not*: a breathing gain. The polydyne encloses 99.5% of the
  cosine's lift-area at equal peak and duration. The advantage is entirely
  kinematic.
- **Valve flow** (§2.6): reference area = valve curtain, effective area =
  min(curtain, throat) — Blair's convention; C_d(L/D, pressure ratio) 2D map
  with a generic pent-roof default (flagged; replace with flow-bench data).
- **Valve boundary** (`ValveConnection`): solved JOINTLY with the duct
  characteristics per §2.6 — the duct-face state rides the interior's
  outgoing Riemann invariant and isentrope while the face pressure is
  bisected until face mass flux equals the orifice flow; handles both
  directions (reversion included) and choking.
- **Cylinder** (`Cylinder`): 0D open-system dU = −p·dV + Σṁh, composition-
  resolved, exact-volume, work-integrating. Sealed motored cycle: mass exact,
  ΔU + ∮p·dV = 0 to 1e-6, reversible return to initial pressure at 0.1%.
- **Network** (`MotoredEngine`): deterministic fixed-order stepping
  (junctions → valves → connectors → ducts → plenums → cylinders) on the
  global CFL timestep; cycle runner and convergence manager (§5.4 metric
  change < 0.1% between cycles, min/max cycle counts).

**Phase 5 gate results** (360 cc single, 86×62, 4-valve, 0.60 m runner):
VE curve sweeps 1.16 → **1.25 peak at 5000 rpm** → 0.54 at 8500 — a clear
wave-tuning peak with VE above unity. Organ-pipe estimate with the
geometry-derived window (launch at max piston speed after overlap TDC,
return by the 25%-lift effective closing): **5015 rpm — 0.3% from the
solved peak**. Sealed-engine mass conserved to 1e-6; energy budget closes
against ∮p·dV to 0.1%; repeated runs bit-identical.

## 1.9 Combustion, heat transfer, knock, friction (Phase 6)

- **Wiebe** (single and double, a = 5, m = 2 defaults): heat release into the
  single zone; premixed charge energy Q = m·f_fuel·LHV·η_c frozen at start of
  combustion. Anchors verified (99.33% at θ0+Δθ, exact midpoint value).
  **Known optimism:** with the perfect-gas model (γ = 1.4 in burned gas vs
  ~1.27 real) efficiency comes out high — the fired fixture below returns
  173 g/kWh, which on a 44 MJ/kg fuel is 47% *brake* thermal efficiency
  against a real naturally-aspirated peak nearer 35–38%. This is precisely
  the §2.2 argument for species-resolved burned-gas properties, and the
  multi-species model closes it.

> **All fired figures in this section were re-measured after the burn-phasing
> fix below.** Anything quoted for Phase 6 in `CHANGELOG.md` predates it and
> is kept as a historical record, not as a current claim. Motored results
> (§1.8, Phase 5) are unaffected — there is no combustion in them.

**Phase 6 fixture** — 86×62 single, rod 107, CR 11, 4-valve (2×31 mm intake,
2×26 mm exhaust, 10 mm lift), 0.60 m × 38 mm intake runner and 0.20 m ×
35 mm exhaust, ambient 1.0 bar / 300 K, 5000 rpm, Woschni with T_wall 420 K,
−15° spark over 55°, stoichiometric premixed charge at AFR 14.6, perfect gas
(γ = 1.4), knock tracking off:

IMEP 20.99 bar, peak 98.8 bar, BMEP 19.12 bar, torque 54.8 N·m, power
28.7 kW, BSFC 173 g/kWh, converged in 5 cycles.

The knock integrals below use the same geometry and speed with an octane
number supplied; nothing else differs, since the octane number feeds only
the Livengood–Wu integral and not the thermodynamics.
- **Burn bookkeeping cycles at gas-exchange TDC, not at the local-angle
  wrap.** This was a real and serious defect, found by review of the two-zone
  work and pre-dating it. Local angle runs 0–720 with 0 at firing TDC, so a
  spark at −15° puts the burn window at 705°→720°→40° — straddling the wrap.
  Resetting the per-cycle burn state there meant the previous cycle's burned
  fraction (0.9933) was still in force for the whole pre-TDC portion, so the
  incremental burn was clamped to zero and **no fuel burned before TDC**; the
  entire accumulated fraction was then released in the single step after the
  wrap. Measured before the fix: 9.7% of the cycle's fuel in one timestep at
  −15° spark, 56.0% at −30°, with peak pressure 152.6 bar against 99.4 bar.

  Spark-timing sensitivity was therefore not being modelled at all, the
  pressure trace and `PeakPressure` were corrupted, and the start-of-
  combustion reference that both the knock integral and the zone split key
  off was frozen at TDC rather than at spark. The reset now cycles on the
  burn-window coordinate, putting it at gas-exchange TDC — the point furthest
  from combustion. After the fix the largest single-step release is 0.37% at
  every spark advance tested (−5° to −45°), which is simply the Wiebe's peak
  rate over a 0.1° step, and the burn tracks the spark as it should.

- **Two-zone burned/unburned split** (plan §2.4 Level 2). Both zones share
  the cylinder pressure and their volumes sum to the cylinder volume — that
  constraint is what makes it a two-zone model rather than two unrelated
  gases, and it is asserted directly. The unburned zone is compressed
  isentropically from the start of combustion; the burned zone takes the
  volume left over, and its temperature follows from the ideal-gas law at the
  shared pressure. The pressure solve itself is untouched: the total energy
  balance is unchanged, so the zones are diagnostic plus an input to wall
  heat transfer.

  **Why it matters.** Heat loss is linear in (T − T_wall), and during the
  burn the mean gas temperature is not what touches the wall. Measured at
  x_b = 0.63: unburned 632 K, mean 2543 K, burned 3666 K — a 3034 K spread
  either side of the mean a single-zone model would use. Wall area is
  apportioned by volume fraction, which is Heywood's simple treatment
  (*Internal Combustion Engine Fundamentals* §12.4); the real split depends
  on flame geometry and plug position, which is why this is presented as a
  zone-resolved heat-transfer model rather than anything more.

  **On by default**, with measured cost against the single-zone model on a
  600 cc single: 0.7–0.9% torque and 1–2 g/kWh BSFC across 3000–7000 rpm,
  with volumetric efficiency unchanged — heat lost during the burn does not
  change how the engine breathes. It moves in the direction of the known
  efficiency optimism noted above. `combustion.twoZoneHeatTransfer: false`
  recovers the old behaviour. The whole suite, including the Yin validation
  case, passes either way.

  **The split closes when the burn window does.** The Wiebe asymptote is
  1 − e^(−a) = 0.9933 at a = 5, never 1, so a "burned fraction reached 1"
  condition never fires — the first version kept the zones open through
  expansion, blowdown and the entire exhaust stroke, carrying a fictitious
  0.67% unburned pocket that cooled isentropically to 324 K, below the 420 K
  wall, and therefore fed heat *back into* the charge. Over half the cycle's
  wall heat was being accumulated on that path. Completion is now decided by
  the burn window, not by the fraction.

  **The zone temperature is validated, not clamped.** A clamp leaves
  p·V_b = m_b·R·T_b silently violated: the two "zones" stop being a partition
  of the charge while still feeding the heat-transfer model, and a ceiling
  value against a realistic mean would drive an order-of-magnitude heat flux
  — wrecking the energy balance the ceiling exists to protect. If the split
  is not physical, `ZonesResolved` goes false and the step uses the bulk
  state.

  **The flame kernel is the hard part, and it bit.** At initiation the burned
  mass goes to zero and T_b = p·V_b/(m_b·R) is a 0/0 whose two limits do not
  approach at the same rate. Observed: 73% of the chamber volume assigned to
  a zone of essentially zero mass, returning 5.6×10¹⁰ K, which then poisoned
  the energy balance for the rest of the run. Zones are therefore not
  resolved below 1% burned mass — where the single-zone answer is also the
  physically honest one, since a kernel that small cannot dominate wall heat
  transfer — with a far-off backstop ceiling behind it. A first attempt at a
  4000 K ceiling was itself a bug: it rejected the Yin case, whose *mean*
  temperature legitimately reaches 4013 K on the perfect-gas model. A ceiling
  tight enough to police physics is tight enough to break honest results.

- **Knock tracking** rides on the same unburned zone (plan §2.4), feeding
  Douaud–Eyzat + Livengood–Wu during the burn. Gate: at fixed geometry the
  knock integrals rank RON95 (6.828) &gt; E85 (4.703) &gt; M100 (4.277) —
  correct qualitative ordering, which is what the gate asks for.

  These rose about 8% with the burn-phasing fix, and that is the expected
  direction: the start-of-combustion reference was previously frozen at TDC,
  so the unburned zone's isentropic compression was measured from the wrong
  state and over a shorter span. The integral now accumulates across the real
  pre-TDC portion of the burn. The **ordering** is the verified claim; the
  absolute values are model output, not a validated prediction.
- **Wall heat transfer**: Woschni (SAE 670931, default), Hohenberg
  (SAE 790825), Annand (1963) with exact scaling-exponent tests and
  published-range magnitude checks (500–5000 W/m²K firing); exposed area =
  head + crown + instantaneous liner band; fixed wall temperatures
  (thermal-network nodes later). Woschni losses take a visible bite out of
  IMEP versus adiabatic.
- **Chen–Flynn friction**: FMEP = A + B·p_max + C·c_m + D·c_m² with exposed,
  documented default coefficients (≈2.3 bar at c_m 15, 80 bar peak).
- **Blowby**: effective ring-gap orifice to crankcase (choked-capable), mass
  and enthalpy leave the cycle. **Crevices**: isothermal wall-temperature
  crevice exchanging mass with pressure; clips the compression peak; charge
  conserved including standing crevice content.
- **Metrics**: net IMEP from ∮p·dV per cycle, BMEP, torque, power, BSFC.
- **Cycle-to-cycle variability** (§3.4): seeded deterministic per-cylinder
  per-cycle perturbations of phasing/duration/energy (CA50 σ 1.2°, energy CoV
  2% defaults). Same seed → bit-identical stochastic cycles; measured IMEP
  CoV **1.96%**, in the 1–3% band typical of a well-behaved engine.

  This fell from 2.4% with the burn-phasing fix, and again the direction is
  the expected one. The per-cycle perturbation is drawn at the cycle reset,
  which used to sit at firing TDC — in the middle of the burn — so a phase
  shift moved the single-step heat dump that the old bookkeeping produced,
  which is a far more violent perturbation than shifting a smooth burn. The
  draw now happens at gas-exchange TDC, before the burn starts, and the same
  1.2° CA50 scatter produces the smaller and more physical IMEP spread.

### Validation case: Yin (CSU thesis) runner-length study

First §6.2 validation case (nightly suite): the open-access Colorado State
thesis engine (100×100 mm, rod 250, CR 10, sine-lift 10 mm valves 50/40 mm,
IVO 10 BTDC/IVC 45 ABDC/EVO 45 BBDC/EVC 10 ATDC, heat release 35 BTDC/60°),
intake runner swept 200–800 mm against its published GT-Power optimal-speed
table. Result: **800 mm exact (3000 vs 3000 rpm), 600 mm within the 250 rpm
gate (4000 vs 3750)** — the runner-resonance regime our solver computes.
Short runners (200/400 mm) differ by 750–1000 rpm: the thesis's optima sit
flat at its base-engine ram peak, set by its unpublished (figure-only)
measured Cd curve; the thesis's own two models disagree by up to 1.8× there.
Bounded in the test as a documented discrepancy; closing it requires
digitising the thesis's Cd figure. Runner diameter (not stated) inferred at
50 mm from the thesis's own Helmholtz column via its Eq. 24.

## 1.10 Manifold pulse state and the solved sound speed (Phase 18)

Plan §2.8 requires the pulse-interference diagram to place arrivals using
`L / a` with **the actual computed local sound speed**, never a nominal. Until
Phase 18 every caller passed a constant (600 m/s in the tests), which is the
right order of magnitude and the wrong method.

`ManifoldPulseState.MeanSoundSpeed` now reads it out of the solve.
`ManifoldAssembler` keeps its graph-node-id → `DuctSolver` map, `EngineBuilder`
hangs that map on the engine, and the analysis samples each pipe across one
full cycle. Two choices worth stating:

- **Sampled on crank angle, not on step count.** Stepping is CFL-limited, so a
  mean over steps is weighted by wherever the timestep happened to be small.
  Sampling at fixed angular spacing keeps it a time mean of the cycle.
- **Mass-weighted across cells, not length-weighted.** A length mean gives a
  cool, nearly empty stretch of pipe the same say as a dense slug of hot gas,
  and it is the hot gas the pulse travels through.

Transit is then `Σ Lᵢ / aᵢ` over the pipes the pulse actually crosses, not
`L / a` with one number for the path. On the reference 4-2-1 at 6000 rpm the
pipes report 668–722 m/s, a 7.8% spread, and the port-to-final-merge transit
is **34.3° of crank against 68.2° at an ambient 343 m/s** — a factor of two,
on the same axis the diagram uses to decide whether two pulses collide.

### Known gap: no wall heat transfer on a built engine's ducts

The measured speeds *rise* monotonically down the header — 668 m/s in a
primary, 722 in the tailpipe. That is backwards for a real exhaust, and the
cause is not the sampling.

`WallThermalModel` and the Colburn coefficient in `DuctSolver` exist, are
tested, and pass their Phase 3 component gates. But **no code path attaches a
wall to a duct built for an engine** — neither `EngineBuilder` for the plain
runners nor `ManifoldAssembler` for the graph. `DuctSolver.HeatTransferEnabled`
is `Wall is not null`, so it is false for every duct in every engine the
product builds. Friction dissipation is then the only source term acting on the
gas, and dissipation only ever heats it.

Plan §2.3 requires the opposite — *"evolved down the pipe with wall heat
transfer and a wall thermal model (insulation / coating / wrap selectable)"* —
and §2.3 goes on to say the effect is a differentiator and a validation test:
a wrapped header runs hotter and wants a shorter primary.

This is a Phase 5 assembly hole that Phase 3's component-level gate could not
see. It is left recorded rather than fixed here because attaching wall heat
transfer moves exhaust density and back-pressure, and therefore every committed
VE, torque and BSFC figure in this document and in the app — a re-baseline, not
a Phase 18 edit. `Gate_the_pulse_diagram_uses_the_solved_sound_speed_not_a_nominal_one`
asserts the current, wrong-way-round direction on purpose: when the wall model
is attached that assertion fails, which is the reminder that should fire.

## 2. Fuel model (Phase 1)

A fuel is a data record (`Fuel`), never a constant. Shipped library
(`FuelLibrary`): iso-octane, n-heptane, toluene, gasoline RON95/98/100
surrogates (C8H15 pseudo-molecule, H/C 1.875, M 111.2), E10/E30/E85 volume
blends, ethanol, methanol, CNG/methane, propane, hydrogen.

- **LHV, stoich AFR, latent heats:** Heywood, *Internal Combustion Engine
  Fundamentals*, App. D (typical values; all user-editable).
- **Stoichiometry** is computed from the formula and the `AirComposition`
  constants: `AFR = (x + y/4 − z/2) · m_air,per-kmol-O2 / M_fuel`.
  Gate: computed AFR within 0.5 % of the tabulated value for every shipped
  fuel (test iterates the whole library).
- **Blends** combine element moles exactly (stoichiometry stays exact); LHV
  and latent heat are mass-weighted; density volume-additive; RON/MON are
  supplied per blend because octane blending is non-linear.
- **Octane numbers** are typical published values; H2 carries none (knock
  model not applicable).

### 2.1 Charge cooling

```
ΔT = x_evap · ṁf · Δh_vap / (ṁa·cp,air + ṁf,vap·cp,fuel-vapour)
```

Default pre-valve evaporated fractions: throttle-body 0.40, port 0.22,
direct 0.05. These are **WaveBench engineering defaults, not a literature
correlation** — they represent the commonly reported 20–30 % pre-valve
evaporation for port injection of gasoline and alcohols. Treat them as
calibration parameters; the UI must present them as adjustable and label
them empirical. Gate: M100 at
λ = 0.8 with port defaults → 44 K, inside the documented 30–50 K band
(plan §2.4). Gaseous fuels (CH4, H2) have zero latent heat → zero cooling.

### 2.2 Laminar flame speed

Metghalchi & Keck (Combust. Flame 48, 1982):

```
S_L = S_L0(φ)·(Tu/298)^α·(p/1 atm)^β·(1 − 2.1·Y_dil)
S_L0 = Bm + Bφ(φ − φm)²,  α = 2.18 − 0.8(φ−1),  β = −0.16 + 0.22(φ−1)
```

Measured coefficients: methanol (0.3692, −1.4051, 1.11), propane
(0.3422, −1.3865, 1.08), iso-octane (0.2632, −0.8472, 1.13), RMFD-303
indolene for gasoline (0.2758, −0.7834, 1.13) — all m/s. Ethanol (Gülder
1982), methane (Gu, Haq, Lawes & Woolley 2000), toluene and n-heptane
(Davis & Law 1998) coefficients are M-K-form fits to those published data
sets, flagged `IsApproximate` in code. Validity: φ 0.8–1.4, Tu 298–700 K,
p 0.4–50 atm, Y_dil ≤ 0.2 (`FlameSpeed.IsWithinValidity`). Hydrogen has no
coefficients; calling throws.

### 2.3 Knock

Douaud & Eyzat (SAE 780080) induction time, integrated by Livengood–Wu:

```
τ [ms] = 17.68·(ON/100)^3.402·p^−1.7·exp(3800/T)   (p in atm, T in K)
knock when ∫ dt/τ reaches 1
```

Validity: gasoline-family fuels, roughly ON 80–110. Not applicable to
hydrogen. The unburned-zone T/p trace comes from the cylinder model
(Phase 6); Phase 1 ships the correlation, the integrator and fuel-ranking
tests (RON95 < E85 < M100 in knock resistance on identical traces).
