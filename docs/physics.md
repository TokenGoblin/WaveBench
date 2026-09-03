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

Those pipe speeds are from before §1.11 wired the duct source terms in. With
wall heat transfer attached the same header reads 658–684 m/s, and the tailpipe
now runs *cooler* than the collector feeding it — which is what finding that
gap was worth.

## 1.11 Duct friction and wall heat transfer, on a built engine (Phases 3, 5)

Measuring the solved sound speed for §1.10 turned up something worse than a
sampling question. The speeds rose *monotonically* down the header — 668 m/s in
a primary, 722 in the tailpipe — which is backwards for a real exhaust.

The cause was that **no duct in any engine the product built had either source
term switched on.** `DuctSolver` implements Haaland/Darcy friction (§2.1) and
Colburn wall heat transfer against a `WallThermalModel` node (§2.3, §2.9); all
of it passed Phase 3's component gates. But `FrictionEnabled` defaults to false
and `HeatTransferEnabled` is `Wall is not null`, and nothing outside a unit test
ever set the flag or called `AttachWall`. Every pipe in every engine ran
adiabatic and frictionless, and the only thing acting on the gas was numerical
dissipation — which can only heat it.

Phase 3's gate tested a duct. Phase 5's gate tested an engine. Neither tested
that the engine's ducts were the ducts Phase 3 had gated.

### What was wired

`EngineBuilder.ApplyThermal` now equips every duct — the intake runners, the
plain exhaust runners, and every pipe `ManifoldAssembler` builds from a manifold
graph — from a new `PipeThermal` block on the document. Both source terms are
**on by default**, with flags to switch them off as a diagnostic.

### Wall temperature is solved between cycles, not integrated within them

A steel wall's areal heat capacity is ≈ 7900 J/(m²·K) and its inner coefficient
during flow is a few hundred W/(m²·K), so its time constant is on the order of
ten seconds — around 800 cycles at 6000 rpm. Integrating it explicitly, the wall
is still climbing when the run ends, and the reported answer is then set by an
assumed wall thickness rather than by the physics. Plan §2.9 says what to do
instead: *"iterate wall temperatures to convergence across cycles."*

`WallUpdate.CyclicSteady` holds the wall fixed within a cycle — which is what it
physically is, on a 20 ms cycle — and accumulates `∫h dt` and `∫h·T_gas dt`.
At each cycle boundary `SolveCyclicSteady` solves each cell's balance

```
h̄·(T̄_gas − T_w) = U_out·(T_w − T_amb) + εσ·(T_w⁴ − T_amb⁴)
```

for `T_w` by Newton. Because `T_w` is constant over the cycle, substituting
`h̄·T̄_gas = (1/Δt)∫h·T_gas dt` is exact rather than a linearisation. The left
side falls and the right side rises with `T_w`, so the residual is strictly
monotone and the root is bracketed between ambient and the flow-weighted gas
mean. `EngineSimulator.RunToConvergence` calls it every cycle and will not
declare convergence until the wall is also periodic.

Two things follow, and both are asserted in `PipeThermalTests`:

- **The answer no longer depends on the wall.** Starting the exhaust wall at
  400 K or at 1100 K, and giving it a heat capacity of 100 or 40 000 J/(m²·K),
  all converge to the same 909.2 K in 7 cycles.
- **The adopted temperature satisfies the balance.** `LastResidual` is the
  residual at the temperature actually in effect; for a free wall Newton drives
  it to ≈ 3e-11 W/m² against the 9.2 kW/m² the wall is shedding. The same
  number is the useful diagnostic for a *held* wall, where it reports the net
  heat the imposed temperature is pushing through: 500 K on this header reads
  98 kW/m².

### The intake wall is held, and that is the physics

`FixIntakeWall` defaults to **true**. Left free, the intake wall balances against
the intake charge alone and settles at or below ambient, because gas expanding
down a runner genuinely runs cooler than the air outside. That models an intake
tract which *chills* the charge, where plan §2.2 asks for ambient **plus wall
heat pickup**. An intake port's wall temperature is set by the coolant and the
head it is cast into; predicting it needs a coolant circuit and a head
conduction path the model does not have, so it is an input (330 K by default)
rather than a fabricated prediction. The exhaust wall is free, because an
exhaust pipe hanging in air really is in balance with the gas inside and the
air outside.

### Surface treatments, and the plan's own worked claim

Plan §2.9: *"exhaust gas temperature sets `a`, and `a` sets the tuned length and
the acoustic resonance frequencies. A wrapped header runs hotter and its optimum
primary length is correspondingly shorter. The software must be able to
demonstrate this — it is a differentiator and a validation test."*

On the reference four-cylinder at 6000 rpm:

| Exhaust surface | Wall (K) | a (m/s) | Tuned primary at 6000 rpm |
|---|---|---|---|
| Bare stainless  | 909 | 671.6 | 2332 mm |
| Header wrap     | 933 | 672.8 | 2336 mm |
| Ceramic coated  | 948 | 673.8 | 2340 mm |
| Insulated       | 963 | 674.5 | 2342 mm |

Wrapping does raise the wall, and it does raise the wave speed. Ceramic lands
between wrap and insulation because at ~930 K radiation is a real term and
ceramic's low emissivity (0.55) holds more heat than the wrap's resistance
alone — the presets are an (ε, R) pair, not a single "insulation" number.

**One correction to the plan's wording.** The second half of that sentence does
not follow from the tuning relation the plan itself gives in §2.10,
`L = a·Δθ/(12·N)`. `L` rises with `a` at fixed `N`: a faster wave needs a
*longer* primary to bring the reflection back at the same crank angle. The
reading that is "shorter" is the other rearrangement — at a *fixed* length, a
wrapped header tunes at a *higher* rpm. The test asserts the direction the
physics gives, not the sentence.

### What it cost

Against the previous adiabatic, frictionless pipes, on the reference
four-cylinder at 6000 rpm:

| | VE | Torque (N·m) |
|---|---|---|
| Neither (what the product silently did) | 1.1064 | 171.93 |
| Friction only | 1.0993 | 170.94 |
| Friction and wall | 1.0663 | 165.39 |

Friction costs 0.57% of torque; the wall a further 3.25%, almost all of it the
330 K intake wall heating the charge and costing density. Every committed
performance figure in this document, in the CHANGELOG and in the app's Overview
was re-measured against the new baseline.

### And it cost runtime, which was paid back

Switching the source terms on put the §5.7 budget case over its 5 s target, at
5.77 s. The gate's own instruction is to profile before adding features, so the
innermost loop was profiled rather than the target waived. Three transcendentals
per cell per timestep were removable without changing the physics:

- `(ε/3.7D)^1.11` in Haaland's bracket depends only on geometry, and is now
  evaluated once per cell at construction (`HaalandRoughnessTerm`).
- `Pr^(−2/3)` in the Colburn analogy is a constant, cached on the duct and
  recomputed only if `Prandtl` is assigned.
- Sutherland's `(T/T_ref)^1.5` is `x·√x`. `Math.Sqrt` is one instruction where
  `Math.Pow` is a call; the two can differ in the last ulp, which is far inside
  the correlation's own few-percent accuracy and is equally deterministic.

Result: **132 → 85 ns/cell-step**, and the budget case from 5.77 s to 4.36 s
with the full physics running. Budget met.

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

## 3. Turbomachinery: maps, matching and the digitiser (Phase 12)

### 3.1 Corrected quantities, and the reference conditions they need

Everything in `WaveBench.Boost` is expressed in SAE J1826 corrected form:

```
ṁ_corr = ṁ·√(T₀₁/T_ref)/(p₀₁/p_ref)
N_corr = N/√(T₀₁/T_ref)
```

`MapReference` carries `T_ref` and `p_ref` and is a **required** property of
every map — there is no default and the loader refuses a map file without one.
Plan §4.2 calls assuming reference conditions "a classic silent 5% error", and
it is right: manufacturers publish against 25 °C/100 kPa (J1826's own),
15 °C/101.325 kPa, and others. `Reading_a_map_against_the_wrong_reference_day_shifts_it_measurably`
puts a number on it — the same 130 000 rpm at 320 K corrects to 125 483 rpm
against J1826 and 123 361 against a standard day, 1.69% apart before it
propagates into pressure ratio.

### 3.2 Compressor: interpolation piecewise linear, extrapolation by affinity

Speed lines are read by piecewise-linear interpolation in flow and then between
adjacent speed lines. **Deliberately not bicubic:** a spline through the last
few points before surge overshoots, and the overshoot lands exactly where the
surge margin is being read.

Outside the measured speeds the affinity laws take over — `PR − 1 ∝ N²`,
`ṁ ∝ N` — which is the physics of a centrifugal stage and degrades gracefully
to zero speed. Left of surge the characteristic is closed parabolically to a
finite shut-off head; right of choke it falls steeply. Every reading carries a
`MapRegion`, and `CompressorPointResult.IsExtrapolated` is what lets §4.2's
shaded regions be honest.

Outlet state and power, with γ = 1.4 and c_p = 1005 J/kg·K for air:

```
T₀₂ = T₀₁·[1 + (PR^((γ−1)/γ) − 1)/η_is]
P   = ṁ·c_p·(T₀₂ − T₀₁)
```

### 3.3 Turbine: a swallowing characteristic, not a pressure source

A turbine map states the corrected flow it will pass at a given expansion ratio.
That makes the turbine the component that **sets exhaust manifold pressure** for
a given engine flow — the reverse of the usual mental picture, in which an
engine pushes a chosen boost through it.

Turbine maps are published sparsely, so the closures matter more than the
interpolation:

- **Below the measured range**, flow follows an orifice shape `ṁ ∝ √(1 − ER⁻²)`
  anchored on the first measured point, reaching exactly zero at ER = 1. A
  linear extension of the first two points crosses zero at an arbitrary ER > 1
  and then goes negative — a turbine pumping backwards.
- **Above it**, corrected flow holds at the last measured value: the nozzle is
  choked. A linear extension would keep adding flow a choked throat cannot
  pass, overstating turbine power exactly where a wastegate decision is made.

With γ = 1.33 and c_p = 1150 J/kg·K for products:

```
T₄ = T₀₃·[1 − η·(1 − ER^(−(γ−1)/γ))]
P  = ṁ·c_p·(T₀₃ − T₄)
```

Turbine **inlet** pressure is `ER × p₄`, derived rather than supplied. Treating
it as an independent input produces corrected flows that correspond to no real
operating point.

### 3.4 Steady shaft matching

`ShaftBalance.Match` solves

```
P_turbine·η_mech = P_compressor + P_friction
```

for the shaft speed at which it holds, by bisection on the imbalance. The
coupling that makes it a real calculation is that the expansion ratio is not
free: the engine's exhaust flow has to pass the turbine, so ER is whatever
swallows it at the trial speed. `The_turbine_swallows_exactly_the_flow_the_engine_gives_it`
pins that to one part in ten thousand.

Bearing friction is a lumped power law, `P = C·μ_ratio·N^(n+1)` with n = 2, the
default C putting ~0.9 kW into the bearings at 150 000 rpm. It is a **fitted**
number, not a measurement, and Phase 13's spool prediction will be sensitive to
it — hence the oil-viscosity ratio, so a cold-oil case can be run against the
same calibration.

The reported quantity to watch is not boost but `MatchPoint.BackPressureRatio`
= ER/PR (the ambient cancels): exhaust manifold pressure over intake manifold
pressure. Above 1 the engine is pumping uphill and the scavenging window of
§4.6.3 is shut.

### 3.5 The map digitiser

`MapDigitiser` turns a map image into map data. Axes are calibrated from **two
labelled gridlines each**, not from the plot rectangle — map images are cropped
and skewed, and a user can identify a gridline with certainty where they cannot
identify the true axis limit.

Curves are traced by colour. For each image column the tracer takes the
**intensity-weighted centroid** of the matching pixels rather than the first
match: taking the topmost matching pixel biases every reading by half the line
width, which on a typical map is a whole percent of pressure ratio.

Efficiency is reconstructed from the traced islands radially about the peak —
each contour is reduced to a radius-versus-angle profile and a point's
efficiency comes from where its radius falls between the two contours
bracketing it on the same ray. Radial rather than nearest-contour because a
nearest-boundary rule creases along the medial axis between two contours, and
that crease lands on the operating line often enough to matter. Points outside
every contour are reported as such rather than extrapolated.

`WaveBench.Boost` reads PNG itself (`PngReader`: non-interlaced, bit depths
1–16, colour types 0/2/3/4/6, all five row filters) so that tracing runs
identically headless and behind the desktop UI. JPEG and interlaced PNG are
refused **by name** rather than producing a scrambled raster — a scrambled
raster digitises into a plausible-looking wrong map.

### 3.6 What the gate measured

Against `SyntheticTurbo`, an analytic map surface rendered to a 900 × 700 PNG
with antialiased curves and grey gridlines (`SyntheticMapImage`):

| Quantity | Worst error over 4 speed lines × 12 points |
|---|---|
| Traced flow range (surge and choke ends) | 0.15% |
| Pressure ratio | 0.08% |
| Efficiency | 0.63% |

against a 2% gate. Solving the digitised map at 0.18 kg/s and 130 000 rpm gives
PR 2.690 and 22.32 kW against the true 2.689 and 22.17 kW.

The maps are synthetic on purpose. Plan §4.7 forbids shipping manufacturer maps
without written permission and that applies to the test suite too — and an
analytic surface is the better anchor anyway, because the test can ask what the
answer *should* be instead of comparing two readings of the same picture.

The operating line for a 2.0-litre four on the synthetic 60 mm unit:

```
  rpm   air kg/s   shaft rpm     PR      ER   back    η_c   surge%  choke%
 2,000    0.0620     34,623   1.11    1.10   0.99    77%    85.0    30.8
 3,000    0.1050     71,744   1.52    1.31   0.86    77%    51.2    43.5
 4,000    0.1420    100,184   2.03    1.53   0.75    78%    47.4    42.5
 5,000    0.1720    124,638   2.57    1.74   0.68    79%    42.2    38.5
 6,500    0.1980    141,400   3.00    1.96   0.65    77%    40.3    34.5
```

The air-flow column is an input here, not a solved quantity: this is one
iteration of the engine/turbo loop, which is what §4.1 steady matching is.
Phase 13 closes it against the cycle simulation.

### 3.7 Auto-match ranking

`TurboDatabase.Rank` scores candidates on flow-weighted mean compressor
efficiency, penalises back-pressure above unity and map extrapolation, and caps
the reward for surge margin — otherwise the ranking prefers an oversized turbo
that never spools. Surge margin below the requirement, choke, overspeed, TIT
over the rating and a shaft that will not balance are **disqualifications with
reasons**, not score deductions, and a disqualified candidate still carries its
full operating line. Plan §4.7: "Always show the top five with their
trade-offs, never a single best."

Every entry must record its source and licence or the library refuses it. The
database is user-populated, and one anonymous contribution would make the whole
library un-shippable.

## 4. Coupled unsteady forced induction (Phase 13)

### 4.1 The rotor is a boundary condition, not a pressure source

`RotorNozzleBoundary` is the keystone of the phase. The rotor does not impose a
pressure and it does not impose a flow — it imposes a **relationship** between
them, and the duct's outgoing characteristic imposes another. Where the two meet
is the boundary state, and solving for it every step is what lets a blowdown
pulse arrive, do work, and reflect.

Given a trial face pressure `p`, the duct delivers

```
speed = −u_in + 2(a_i − a_b)/(γ−1),   a_b = a_i·(p/p_i)^((γ−1)/2γ)
ṁ_duct = speed · ρ_b · A
```

and the rotor swallows `ṁ_map(ER, N_corr)` read at the face's **total**
conditions. The duct term falls with pressure and the rotor term rises, so the
imbalance crosses zero exactly once and bisection is safe without a derivative.

Three bounds matter, and two of them were learned by getting them wrong:

- **Blocked.** Outflow reaches zero at `a_b = a_i − u_in·(γ−1)/2`. **Minus** —
  `u_in` is positive *into* the duct, so an outflowing end has it negative.
- **Choked.** The face cannot exceed sonic:
  `a_crit = [(γ−1)·(−u_in) + 2a_i]/(γ+1)`.
- **Backflow.** When the blocking pressure has already fallen below what is
  downstream, gas is coming *in*, and the state it brings is the downstream
  reservoir's rather than the interior's. That case delegates to
  `ReservoirBoundary` rather than extending the outflow isentrope backwards
  through a discontinuity that is not there.

Placing this boundary directly on the manifold outlet gives the **quasi-steady**
model. Placing it on the far end of a volute duct gives the **volute-resolved**
model — the same boundary, a different topology.

### 4.2 What the two turbine models actually differ by

Measured on a pulsating rig at fixed shaft speed (the way a gas stand runs the
test), 120 kPa of pulse on a 175 kPa mean at 100 Hz:

| Configuration | Mean power | Loop openness |
|---|---|---|
| Quasi-steady | 28.51 kW | 0.014 |
| Resolved, constant-area volute | 28.51 kW | 0.207 |
| Resolved, contracting volute | 24.66 kW | 0.370 |

Read that table carefully, because the obvious reading of it is wrong. A
constant-area volute accounts for **the same mean power to within rounding** and
still opens a loop fifteen times the quasi-steady residue: filling and emptying
is a **phase** effect, not an energy one, which is exactly what a pulsating gas
stand measures and a single-valued map cannot contain. The contraction to the
rotor face is a restriction and a reflection as well as a volume, and it is that
which takes the 13.5% of power.

The loop widens with pulse amplitude (0.199 → 0.522 over 60–180 kPa) and with
frequency (0.072 → 0.246 per unit delivered pulse over 25–200 Hz), matching the
qualitative behaviour reported by Dale & Watson, Winterbone & Pearson, and
Szymko/Martinez-Botas.

**Loop openness is measured as the vertical spread of the (ER, MFP) trace at a
given expansion ratio, not as the enclosed area.** The obvious metric — shoelace
area over the bounding box — measures the wrong thing: a bigger pulse grows the
box faster than the area, so a box-normalised loop appears to *shrink* with
amplitude. The literature's "wider loop" means the vertical opening.

Runtime: volute-resolved measured at **0.76×** quasi-steady against a 2× gate.
It is not cheaper per step — it runs at a milder state and takes a larger stable
timestep.

**A resolved volute shorter than 30 mm is refused rather than answered.** Below
that the handover junction sits on top of the rotor boundary; measured on the
rig, a 10 mm volute moved mean power by up to 30% and moved it again with cell
count, while a 150 mm one was mesh-independent to 0.2%. No real volute is that
short, and returning a number from that configuration would be worse than
refusing it.

### 4.3 Twin-scroll pairing and partial admission

`ScrollPairing` is arithmetic on the firing order and the cam, with no gas
dynamics in it at all. That is the point — plan §4.6.2 requires the pairing to
be derived from firing order alone, so a wrong one is caught before anybody runs
anything.

The **separation index** is the fraction of one cylinder's blowdown window that
falls inside a scroll-mate's exhaust stroke, worst pair reported:

| Engine | Pairing | Index | Min spacing |
|---|---|---|---|
| I4 1-3-4-2 | {1,4} / {3,2} | 0.000 | 360° |
| I4 1-3-4-2 | {1,3} / {4,2} | 1.000 | 180° |
| I6 1-5-3-6-2-4 | {1,2,3} / {4,5,6} | 0.000 | 240° |
| I6 1-5-3-6-2-4 | {1,5,3} / {6,2,4} | 1.000 | 120° |
| V8 crossplane | alternating | 1.000 | 180° |
| V8 crossplane | by bank | 1.000 | 90° |

The V8 rows are a finding, not a failure: eight cylinders in two scrolls means
four events per scroll, so the widest spacing available is 180°, and a blowdown
starting at 140° always lands inside a mate's exhaust stroke 180° later. **No
two-scroll V8 escapes it.** The index saturates at 1 for every arrangement
there, which is why `MinimumSpacingDeg` exists — it is what still ranks the
options when the overlap metric has run out of room.

**Partial admission** (`TwinScrollTurbine.Redistribute`) allocates the rotor
annulus in proportion to what each scroll is currently able to deliver. That
reduces to an even split when the scrolls are in phase — full admission, no
penalty — and to one scroll taking 98% of the rotor when its mate has gone
quiet, which is the out-of-phase case the pairing rule is designed to produce.
The efficiency penalty is linear in admission imbalance and defaults to 15% at
full single-entry admission; the **shape** follows the published trend for
twin-entry turbines under unequal admission, the **coefficient is not fitted to
any dataset** and is exposed for calibration.

### 4.4 Turbocharger thermal model and the diabatic correction

Three lumped-capacitance nodes — turbine housing, bearing housing, compressor
housing — with oil and coolant rejection, external convection, and radiation
from the turbine housing. At 1100 K turbine inlet in a 350 K bay:

```
turbine 777 K, bearing 438 K, compressor 385 K
3878 W in from the gas, 1461 W to the oil, 999 W into the charge
```

The claim it supports: **a gas-stand map's efficiency is an apparent
efficiency.** The stand measures a rise that already contains heat conducted
from the turbine end, so `η_map = ΔT_ideal/(ΔT_aero + ΔT_heat,stand)`. Two
consequences follow, pointing opposite ways — the compressor's real aerodynamic
efficiency is *higher* than the map says, and the on-engine outlet temperature
is *higher* than the map predicts, because an engine's turbine end is 400–500 K
above a stand's.

Verified two ways, because there is no measured dataset here:

1. Against a **synthetic truth** where the heat flux is known exactly, the
   correction recovers a 78.0% aerodynamic efficiency from the 74.3% apparent
   value the stand would have recorded — to 0.2%.
2. The **magnitude** lands where the literature puts it:

| ṁ kg/s | raw map | adiabatic | on engine | over adiabatic | heat |
|---|---|---|---|---|---|
| 0.06 | 356.1 K | 343.2 K | 363.7 K | **20.6 K** | 1041 W |
| 0.12 | 400.0 K | 395.4 K | 404.7 K | 9.3 K | 1139 W |
| 0.20 | 436.0 K | 434.2 K | 439.1 K | 4.9 K | 1195 W |

Plan §4.2's "15–30 K above the adiabatic prediction" is **not a flat offset**,
and the model does not pretend it is. The heat flux is roughly fixed by the
housing temperatures; carrying a fixed power in a smaller mass flow makes a
bigger temperature rise. The effect is large at low flow — where the published
figures are measured, and where a matched turbo spends its transient — and small
at high flow. A model producing 20 K everywhere would be reproducing the quoted
number rather than the physics.

**Validation case 21 is still open**: a *measured* on-engine outlet temperature
to compare against. The conductances in `TurboThermalProperties` are calibration
parameters and are exposed for exactly that.

### 4.5 Pulse energy delivery and manifold volume ratio

`TurbineDeliveryRecorder` reduces a recorded cycle at the turbine inlet to the
two metrics §4.6.1 names.

**Pulse energy delivery** is mean turbine power divided by the power the same
mean mass flow would have produced arriving steadily at the cycle-mean pressure.
Above 1 the pulse paid for itself; at 1 the manifold has flattened it into
constant-pressure operation. This is the Watson & Janota axis as a single
number.

The first definition tried — the fraction of delivered power arriving while
pressure was above its own cycle mean — **did not discriminate**: it sat between
60% and 67% for every manifold in a primary sweep and was not even monotone,
because a smooth trace spends about as long above its mean as a peaky one does.

A four-cylinder at 6800 rpm through a 4-1 header into the synthetic turbine:

```
  Ø mm   volume ratio   pulse delivery   peak/mean p   turbine kW      VE    IMEP bar   BSR
    20           1.59           101.5%         1.158        16.71   0.873      13.95   0.53
    24           1.92           105.9%         1.247        14.68   0.922      15.11   0.55
    28           2.31           106.6%         1.268        14.24   0.935      15.52   0.55
    32           2.76           105.0%         1.263        13.30   0.934      15.60   0.57
    38           3.56           102.7%         1.213        12.87   0.931      15.57   0.58
    44           4.48           101.3%         1.154        12.50   0.925      15.50   0.59
    50           5.55           100.7%         1.121        12.27   0.921      15.44   0.59
```

Both halves of the trade are visible and both matter. Widen past Ø28 and the
blowdown dissipates into manifold volume until the turbine runs at constant
pressure. Narrow past it and the primary chokes: volumetric efficiency falls to
0.873, IMEP falls with it — and **turbine power keeps rising**, because a high
mean back-pressure feeds the turbine well while strangling the engine. Reporting
turbine power beside VE is what stops an optimiser choosing the choked header.

The sweep is run high in the rev range on purpose. At 4500 rpm this engine draws
0.09 kg/s and a Ø26 primary is not a restriction at all, so the sweep reads as
"narrower is always better" — true right up until it is not.

### 4.6 Shaft dynamics

```
J·dω/dt = (P_turbine·η_mech − P_compressor − P_friction + P_assist)/ω
```

Integrated on **energy** rather than speed. Torque goes as `P/ω` and blows up as
ω → 0; kinetic energy `½Jω²` does not, and its derivative is just the net power.
That makes a stationary shaft a starting condition rather than a singularity —
which is precisely the case a spool prediction has to handle. Checked against
the closed form it implies: 20 000 → 120 000 rpm on 3 kW gives 79.32 ms exactly,
79.33 ms integrated.

Bearing friction is `P = C·μ_ratio·N^(n+1)` with n = 2, the default putting
~0.9 kW into the bearings at 150 000 rpm. It is a **fitted lumped number**, and
the oil-viscosity ratio is there so a cold-oil case runs against the same
calibration. An electric assist is one term in the same equation and needs
nothing else.

### 4.7 Wastegate, charge cooler, boost control

**The internal wastegate's scroll-division loss is modelled**, because plan §4.3
says omitting it overstates the twin-scroll benefit at exactly the high-load
condition where the gate is open. A shut gate retains 100% of the division; a
fully open internal port retains 35% by default; an external gate on its own
take-off retains all of it at any position.

**Charge air cooler heat soak** needs the core's thermal mass, and the
conductance to the coolant is where the vehicle's speed lives. Five pulls on a
dyno with no ram air:

```
pull 1: outlet 343.6 K    pull 3: 351.9 K    pull 5: 352.5 K
a steady ε-NTU model says 329.9 K on every one of them
```

That 22.6 K gap is the IAT climb a steady-state model hides — and it is a dyno
phenomenon before it is a road one, because a moving car's core rejects several
times as much.

**Boost control** is a PID with feed-forward and conditional-integration
anti-windup. The actuator's sign is the one that catches people out: solenoid
duty *bleeds pressure away* from the diaphragm, so more duty means the gate
opens later and boost runs higher.

### 4.8 What was found along the way

**The rotor boundary acted as a check valve.** A sign error in the zero-flow
pressure — `a_i + u_in·(γ−1)/2` instead of `a_i − u_in·(γ−1)/2` — put the
blocking pressure below the back-pressure whenever gas was leaving, which read
as permanent backflow. With backflow suppressed the manifold could not relieve
itself: the engine drew 0.023 kg/s against an expected 0.090, mean turbine-inlet
pressure climbed, and primaries went to NaN at the junction a few hundred
degrees in.

**The tell was that it got worse under mesh refinement** — at 6 mm cells every
diameter failed where at 12 mm only the narrow ones did. That is the signature
of an ill-posed boundary, not an under-resolved one, and it is the thing to
watch for next time.

**The junction was suspected first, and measured innocent.** A `Junction` in a
plain pipe passes a pulse of 69% of mean pressure with **0.07% error in
delivered mass** — the linearised constant-pressure solve is far better than its
derivation suggests at these amplitudes. That measurement is kept as
`JunctionUnderPulseTests` because the question will come up again.

**A conclusion was retracted.** Before the boundary was fixed, the evidence said
the volute's *contraction* rather than its volume opened the hysteresis loop.
With the boundary corrected the opposite is true — volume alone opens most of
it — and §4.2 above states the corrected result. The lesson is the same one
§1.11 of this document already carries: a result that rests on a component you
have not verified is a hypothesis, not a finding.

## 5. Forced-induction engine behaviour (Phase 14)

### 5.1 Fresh-charge tracking, and what it is an approximation of

`Cylinder.FreshChargeMass` follows air (plus its fuel, when port-injected) that
came in through an intake port and has not yet burned or left. Gas leaving takes
fresh charge with it in proportion to the cylinder's current fresh fraction;
the flame consumes it in proportion to the Wiebe increment; an intake port
delivers it and an exhaust port does not.

**It is single-zone, and that is a stated bound rather than a detail.** A real
cylinder with good scavenging is closer to displacement than to mixing — a jet
of fresh charge crosses to the exhaust valve while residuals sit in the corners
— so perfect mixing **under-states** blow-through. On the engine below it
reports under 1% where a measured DI turbo at the same overlap and scavenging
pressure shows several. `ScavengingAnalyser.ShortCircuitFraction` supplies the
other bound: at 1, every kilogram entering during overlap crosses untouched.
Where a real engine sits between them is port angle, valve shrouding and chamber
shape, none of which a 1D solver can resolve — so the tool brackets it and
refuses to pick.

Two things went wrong here and both are worth recording:

- The flame first consumed fresh charge as `mass × (1 − dxb)` each step. That
  compounds to `exp(−Σdxb) = 0.37`, leaving a third of the charge apparently
  unburned — **reported as 35% blow-through on an engine with zero overlap**,
  which is how it was found. Consumption is proportional to the charge present
  at ignition, not a repeated fraction of what is left.
- The Wiebe asymptote never reaches 1, so 0.67% survived every burn and scored
  as blow-through on every exhaust event. The burn now zeroes the tracker when
  its window closes, for the reason the existing comment beside it already gave:
  the missing fraction is the exponential tail, not charge sitting in the
  chamber.

### 5.2 Scavenging pressure ratio and the cam optimum

`ScavengingAnalyser` samples intake-port over exhaust-port pressure through
every overlap window and reports the mean, the peak, and the degrees spent above
1 — the window in which overlap scavenges instead of reverting.

Validation case 17, run at 2200 rpm on the same four-cylinder NA and with a
2.0 bar plenum, 230° cams, lobe centres moved symmetrically so overlap is
`230 − 2·LCA`:

```
                LCA   overlap   p_int/p_exh   torque N·m      VE   blow-through
 naturally      115         0           —          263.7   0.950           0.0%
 aspirated      105        20        0.929         275.4   0.970           0.0%   ← optimum
                 90        50        0.978         269.2   0.927           0.0%

 boosted        115         0           —          499.6   0.991           0.0%
 2.0 bar        105        20        1.097         517.7   1.009           0.0%
                 90        50        1.496         544.9   1.101           0.8%   ← optimum
```

The optima are 15° of lobe centre apart — 20° of overlap against 50° — and the
scavenging output is what explains it: every NA point sits below 1 and every
boosted point above. The positive-pressure window opens from 5.2° to 35.1° as
the lobe centres tighten, and the same 50° of overlap that costs the NA engine
6 N·m against its own optimum gains the boosted engine 27 N·m against its own.

Boost is imposed as a plenum condition; the exhaust side is not. Turbine
manifold pressure comes out of the flow the engine gives it, which is what makes
the scavenging pressure ratio a result rather than an assumption.

At zero and 10° nominal overlap the two valves never leave their seats together,
so the ratio is **undefined and reported as NaN** rather than as a number.

### 5.3 Blow-through, and charging it to the objective

Plan §4.6.3: *"Do not let the optimiser exploit free scavenging that the
modelled injection system cannot actually have."* So the cost is computed and
subtracted from the torque the optimiser sees:

| Injection | Fuel lost | Measured λ | TIT rise | Net torque at 50° overlap |
|---|---|---|---|---|
| Direct | none | reads **lean** — blow-through air with no fuel in it | none | 544.9 N·m |
| Port, mixed bound | 0.8% | unaffected — the fuel goes out with the air | 9.9 K | 540.4 N·m |
| Port, displacement bound | 12.2% | unaffected | 167 K | 468.8 N·m |

The lambda column is the useful asymmetry. Under DI the sensor is fooled and the
fuel bill is not; under port injection the sensor is fine and the fuel bill is
not. A closed loop trusting a wideband on a scavenging DI engine will richen an
engine that did not need it.

The short-circuit fraction re-attributes charge for **reporting and for the fuel
charge**; it does not re-solve the flow, so indicated torque is untouched by it.
That is asserted in the tests so the distinction cannot quietly erode.

### 5.4 The FSAE restrictor, upstream of the compressor

```
ṁ* = C_d·A·p₀·√(γ/(R·T₀))·(2/(γ+1))^((γ+1)/(2(γ−1)))
```

For the 20 mm petrol restrictor at C_d 0.96 on a standard day that is
**0.0715 kg/s** — checked against the hand calculation, not against the model's
own output. At λ = 1 on petrol it is 4.87 g/s of fuel, and nothing downstream
moves it: the 19 mm E85 throat is 90.25% of it (area goes as d²), a 35 °C day
takes 1.6% off and 1600 m takes 15%.

Two consequences the plan singles out, both reproduced:

- **The operating line moves.** Sub-atmospheric inlet pressure raises corrected
  flow, so the same 0.065 kg/s reads 5.8% further right on the map than it would
  at ambient — and the manifold pressure the engine gets is a ratio on a reduced
  inlet, not on ambient.
- **A choked restrictor turns shaft speed into a surge trajectory.** With mass
  flow pinned at the ceiling, every extra rpm is pressure ratio and nothing
  else: surge margin falls 174% → 46% from 150 000 to 270 000 rpm, walking the
  operating point straight at the surge line.

Diffuser recovery is a parameter, not a constant, because it is the cheapest
thing on the car to improve: at 60 g/s a sharp expansion leaves 84.0 kPa at the
compressor and a proper diffuser leaves 98.1. It does not move the choke
ceiling, and the test says so — that is the thing people most often hope is not
true.

### 5.5 Superchargers

A Roots blower has no internal compression: it carries a fixed volume round at
inlet pressure and the outlet compresses it isochorically, by back-flow. A screw
has a built-in volume ratio and compresses internally before the port opens.
Same pressure ratio, same flow, and the Roots delivers hotter charge.

```
    PR   Roots out   screw out   Roots η   screw η   Roots kW   screw kW
   1.3     329.3 K     341.4 K       74%       54%       16.2       22.5
   1.8     381.3 K     368.7 K       66%       77%       42.3       35.9
   2.2     422.8 K     390.6 K       60%       82%       62.3       46.2
```

**The crossover at PR ~1.6 is real and is reported rather than hidden.** A screw
with V_i = 1.9 is built for an internal ratio of 1.9^γ = 2.49; run it at 1.3 and
it compresses to 2.49 and blows back down, and even with the recovery that gives
back it has done more work than a Roots would. That is why screw blowers are
matched to their target boost and Roots blowers are not fussy. The blow-down
term is deliberately not clamped at zero: clamping it would charge the full
over-compression and make every screw look worse than a Roots everywhere.

The model recovers the *reversible* part of the blow-down; a real port recovers
less, so the screw's low-ratio penalty here is if anything optimistic.

A centrifugal is a compressor map on a fixed drive ratio, and the shape that
falls out is the point: boost above atmospheric goes as engine speed squared —
0.28 bar at 2000 rpm and 1.12 at 4000, a factor of four for a doubling. That is
a fundamentally different torque curve from a turbo at the same peak boost.

Parasitic power comes off crank torque at every speed whether the boost is
wanted or not, which is the other half of why a supercharged curve is shaped
differently.

### 5.6 Electric assist

One term in the shaft equation and nothing else — which is the test. 7 kW of
assist takes the synthetic shaft from 30 000 to 120 000 rpm in 32 ms for 227 J
(0.06 Wh) per event; the same shaft unassisted does not get there at all inside
five seconds on the same turbine and compressor powers.

### 5.7 Altitude and hot-day sensitivity

`AmbientCondition` carries the four cases that matter — standard day, 35 °C,
1600 m, and both at once — with the density ratio that scales a naturally
aspirated engine's torque directly and that a turbo only partly recovers by
spinning faster. Hot and high is worse than either alone, which is where a
sea-level match fails.

## 6. Transient forced-induction dynamics (Phase 15)

### 6.1 The transient driver and the coupling it closes

Phases 13 and 14 already built every piece a transient needs as independently
steppable state: `TurboShaft` integrates kinetic energy against turbine and
compressor power, `TurboThermalModel` integrates the three housing
temperatures against an arbitrary `dt`, `EngineSimulator.Step()` advances the
gas dynamics on its own CFL-limited clock, and `CompressorModel.Solve` reads a
map at whatever speed and flow it is given. Nothing among them assumed a fixed
operating point — what was missing was an orchestrator that ran them all
*together* against a live gas-dynamics solve instead of one each held at a
single steady condition. `WaveBench.Boost.Unsteady.TransientDriver` is that
orchestrator, and it adds no new physics: every `Advance()` call recovers the
solver's own `dt` from the `Time` delta across one `EngineSimulator.Step()`
(the same idiom `TurbochargedEngineRig` already uses for the turbine
coupling), solves the compressor at the shaft's current speed and a smoothed
intake mass flow, and hands the result to `TurbineStage.Integrate` and
`TurboThermalModel.Step` exactly as those methods already expected to be
called.

Two coupling choices are worth stating because they are modelling decisions,
not derivations:

- **The intake mass flow the compressor sees is smoothed**, with a short
  exponential average (`τ ≈ 10 ms` by default), rather than fed the raw
  pulsing port flow a poppet valve produces. A real compressor sits behind a
  plenum volume that damps the pulse; this v0.1 engine topology has no
  explicit intake plenum (§1.8's per-cylinder-runner-from-ambient
  simplification), so the smoothing stands in for it.
- **The driving profile's load fraction is the fraction of the compressor's
  *available* boost admitted**, not (as the naturally-aspirated
  `EngineBuilder.Build(intakeLoadFraction:)` path uses it) a fraction of
  ambient pressure. A real throttle plate sits downstream of the compressor;
  this topology has no separate throttle/plenum component to put it in
  (`EngineBuilder`'s own doc comment states the same simplification for the
  steady NA case), so `ThrottleStep` blends the intake reservoir linearly
  between ambient (closed) and the compressor's full delivery (wide open).
  `ThrottleStep` also ramps over a short window (3 ms by default) rather than
  jumping instantaneously: an literal Heaviside pressure jump on a
  `ReservoirBoundary` between two CFL-sized steps can demand a flux the
  previous step's timestep was never sized for, which drove the boundary cell
  non-physical the first time this was tried. Three milliseconds is still a
  step against a spool transient running tens of milliseconds, and it kept
  the solver well-posed.

**Mesh convergence.** The same scripted step-throttle transient, run to 30 ms
on the single-cylinder rig at two cell sizes:

```
              cells   steps    shaft rpm   boost
   24 mm cells   14   59 381    38 247     109.78 kPa
   12 mm cells   28   84 338    37 833     109.65 kPa
```

Rpm error 1.10%, boost error 0.12% — halving the cell size moved the answer by
about a percent, not a mesh-dependent divergence.

**Energy balance.** Summing `TurboShaft.NetPowerW · dt` independently, step by
step, against the same `dt` the driver itself used, and comparing it to the
shaft's own before/after ΔKE over a 30 ms run: `ΔKE = −2490.36 mJ` against
`Σ(NetPowerW·dt) = −2490.36 mJ`, error 0.0000% (the run's default initial
shaft speed, 40 000 rpm, is higher than this small single-cylinder engine's
low-load exhaust energy can sustain, so the shaft decelerates — the sign is
expected, and the point of the check is that the two independently-computed
numbers agree to numerical precision, not that the shaft spins up). This is a
coupling check, not a re-test of `TurboShaft`'s own integration — that is
`ShaftAndControlTests`' job, in isolation, and it already covers it.

### 6.2 Time-to-torque and the sensitivity band

`TimeToTorqueResult.Evaluate` runs the identical scripted transient three
times — a nominal case and two caller-supplied bounds on shaft inertia and
bearing friction — and reports the 90%-rise crossing times each one produces,
never an invented ± percentage on a single run. On the step-throttle case
above, widening the swept uncertainty from ±5% to ±40% on both inertia and
friction widened the boost band from 0.025 ms to 0.237 ms — the band responds
to the uncertainty it is given, which is what Part 14 Gotcha #25 asks for
("inertia and friction are rarely known accurately — show a sensitivity band,
not a single number"). The torque band did not move measurably over the same
30 ms window: at this rig's scale, indicated torque responds to the throttle
step itself (dominant, and identical across all three runs) well before the
turbo's own spool state has had time to make a second-order difference to it.
That is a real result of the window being short relative to the shaft's own
time constant here, not a defect in the band computation — a longer window or
a larger shaft inertia would be expected to open it up, and nothing before
this stage existed for a future run to test that with.

Torque here is **indicated**, not brake: no friction model (Chen–Flynn or
otherwise) is coupled into `TransientDriver`, so
`TransientSample.IndicatedTorqueNm` is `PerformanceMetrics.Torque` applied to
a sliding 720°-crank-angle window of piston work rather than a BMEP. Before a
full window has elapsed, the partial window is scaled by `720°/window` — an
extrapolation stated as one, not a measurement — so the very first samples of
a transient carry an estimate instead of a meaningless zero.

### 6.3 Repeat-run heat soak

`TurboThermalModel`'s housing state already carries over between calls to
`Step` — that was built in Phase 13. What Stage B adds is the other half of
"a second dyno pull is not the same as the first" (plan §4.7): the carried
heat has to actually change what the engine breathes, or carrying it over is
invisible. Each `TransientDriver.Advance()` call now adds the current step's
`TurboThermalState.CompressorAirHeatW` onto the compressor's aerodynamic
outlet temperature — `Δt = Q/(ṁ·c_p)`, the same heat-addition principle
`DiabaticCorrection` already uses for a held operating point, applied here to
the transient's own moving thermal state rather than a fresh `SolveSteady`
call — before that temperature is written into the intake boundary.

Demonstrating the effect needs a real gap in housing temperature between two
runs, and housing time constants (seconds to minutes) are far longer than a
CFL-limited gas-dynamics transient can affordably cover in a test — so the
verification holds the shared `TurboThermalModel` at a representative
on-engine hot-idle condition (900 K turbine inlet) between two scripted 30 ms
pulls, using `TurboThermalModel.Step`'s own documented ability to be called
"on the transient's own clock rather than the solver's" with an arbitrary
`dt`, standing in for the engine idling, still hot, between two logged dyno
pulls. Measured result: compressor housing 349.90 K after pull 1 → 370.32 K
after the hold → 370.21 K after pull 2; compressor outlet at t = 30 ms into
each pull, 503.64 K (pull 1) vs. 597.95 K (pull 2), Δ = 94.3 K. A control run
using two *independent* `TurboThermalModel` instances (nothing to carry over)
reproduces the same outlet temperature to within 0.0001 K, which is what
distinguishes "the housings carried heat" from "runs just differ for some
other reason."

### 6.4 What this stage does not check, and why

Phase 15's gate has three clauses. Clauses 2 and 3 (turbine acoustic
attenuation and OPI drop; surge flutter frequency physically derived) were met
in Stage A — see docs/acoustics.md §5. **Clause 1 — "transient spool within
15% of a measured case" — is a documented, deliberate deferral**, not an
oversight. A bounded search (a web search pass plus a dedicated research
pass) looked for a measured turbo-spool transient dataset redistributable
under a licence that would let its values live in this public repository —
CC-BY, CC0, or public domain. Two leads were investigated: Argonne National
Laboratory's Downloadable Dynamometer Database (permissively licensed with
attribution, but its public channel list — drive trace, dynamometer force,
engine speed, fuel economy/emissions — could not be confirmed to include a
boost/MAP or turbo-speed channel at transient-relevant sample rate) and Albin,
Ritter, Liberda & Abel, "Boost Pressure Control Strategy to Account for
Transient Behavior and Pumping Losses in a Two-Stage Turbocharged Air Path
Concept," *Energies* 9(7):530, 2016 (CC-BY by default as an MDPI journal, but
its transient boost-pressure traces could not be confirmed to be measured
on-engine data rather than simulation). Everything else found was either a
paywalled SAE/Elsevier/Springer paper — the same category this project
already refuses for compressor and turbine maps (plan §4.7) — or an
all-rights-reserved thesis. This matches CLAUDE.md's standing deferral list
("Validation cases needing measured data that is not here: ... 20 (transient
spool) ...").

What §6.1–6.3 above check instead — convergence under mesh refinement, energy
balance through the coupling, a boost rise that behaves sensibly under a step
throttle, a sensitivity band that widens with its own supplied uncertainty,
and a heat-soak effect that only appears when housing state is actually
shared — is self-consistency, and it is what CI actually gates on for this
stage. It is not a substitute for validation case 20; it is what is checkable
without one. If a suitable measured dataset is ever found or licensed,
validation case 20 and gate clause 1 can be closed without touching this
section's machinery — only `WaveBench.Validation` would gain a new case.
