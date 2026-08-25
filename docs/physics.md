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
