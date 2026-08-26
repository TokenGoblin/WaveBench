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
- **Cam** (`CamProfile`): measured-table import (CSV, mm/m inferred) and a
  generic harmonic analytic profile (flagged generic; polydyne generator
  pending), event detection, effective-closing angle at a lift fraction.
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
  ~1.27 real) indicated efficiency comes out high (~49% → BSFC ~170 g/kWh);
  this is precisely the §2.2 argument for species-resolved burned-gas
  properties, and the multi-species model closes it.
- **Quasi-two-zone knock tracking**: unburned-zone temperature by isentropic
  compression from start of combustion (plan §2.4), feeding Douaud–Eyzat +
  Livengood–Wu during the burn. Gate: at fixed geometry the knock integrals
  rank RON95 (6.33) &gt; E85 (4.36) &gt; M100 (3.97) — correct qualitative
  ordering.
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
  CoV 2.4%.

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
