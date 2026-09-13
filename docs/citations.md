# Citations

Every empirical correlation in WaveBench names its source and its validity
range in the XML doc comment where it is implemented — that is a non-negotiable
of the build contract, not a convention. This file gathers them in one place so
a reader can check the provenance of a result without reading the source, and
so a generated report's citation list has something to point at.

**If a correlation is used outside its stated validity range, the output is
marked.** WaveBench does not silently extrapolate.

The wider bibliography the design drew on is in
[`WaveBench-Master-Plan.md`](WaveBench-Master-Plan.md), Part 13. What follows
is narrower and more useful: what the code actually computes with.

---

## Numerical scheme

| Used for | Source | Validity |
|---|---|---|
| Shock-tube verification of the Riemann solver | Sod, G. A. *J. Comput. Phys.* 27 (1978) 1–31 | Exact solution; the anchor, not a correlation |
| MUSCL-Hancock + HLLC construction | Toro, E. F. *Riemann Solvers and Numerical Methods for Fluid Dynamics*, 3rd ed., Springer 2009 | — |
| Well-balanced treatment of area change | Plan §2.2; verified against a stationary taper | Spurious velocity < 1e-10 m/s |

Measured behaviour of the scheme, including its −3 dB bandwidth at a given
mesh, is in [`numerics.md`](numerics.md).

## Pipe flow: friction and heat transfer

| Used for | Source | Validity |
|---|---|---|
| Darcy friction factor | Haaland, S. E. *J. Fluids Eng.* 105 (1983) 89–90 | Turbulent, Re > 4000; explicit approximation to Colebrook within 2% |
| Wall heat transfer in ducts | Colburn analogy, Nu = 0.023·Re^0.8·Pr^(1/3) | Fully developed turbulent pipe flow |
| Wall thermal node, solved between cycles | Plan §2.3 | A steel wall's time constant is ~10 s against a ~20 ms cycle |

## In-cylinder heat transfer

Three correlations ship; the model states which was used.

| Correlation | Source | Validity |
|---|---|---|
| Woschni | SAE 670931 (1967) | SI/CI engines near the conditions of Woschni's diesel data |
| Hohenberg | SAE 790825 (1979) | A later fit, generally better at part load |
| Annand | Annand, W. J. D., *Proc. IMechE* 177 (1963) | Nu = a·Re^0.7 with a ≈ 0.35–0.8 |

They disagree by a few per cent of heat loss, which is worth seeing rather
than hiding: the choice is exposed as a field.

## Combustion

| Used for | Source | Validity |
|---|---|---|
| Burn-rate law | Wiebe function, a = 5 convention (99.3% at the stated duration) | A shape, fitted per engine, not a prediction |
| Laminar flame speed | Metghalchi, M. & Keck, J. C. *Combustion and Flame* 48 (1982) 191–210 | Fuel-specific coefficients; the stated pressure/temperature range per fuel |
| Knock induction time | Douaud, A. M. & Eyzat, P., SAE 780080 (1978) | Native units are atm; a typed overload converts |
| Knock onset integral | Livengood, J. C. & Wu, P. C., *5th Symposium on Combustion* (1955) 347–356 | ∫dt/τ = 1 at onset. The RANKING between fuels is verified; the absolute value is not |

## Valves and restrictions

| Used for | Source | Validity |
|---|---|---|
| Compressible orifice flow | Heywood, J. B. *Internal Combustion Engine Fundamentals*, §6.3 | Through to choking |
| Generic valve C_d against lift | Plan §2.5, generic curve | **The dominant error source.** Replaced by measured flow-bench data when supplied |
| FSAE restrictor | Plan §4.6.4 | Chokes at theory; verified |

## Junctions and collectors

| Used for | Source | Validity |
|---|---|---|
| Three-leg junction loss | Idelchik, I. E. *Handbook of Hydraulic Resistance* — converging and diverging wye forms | Steady; branch-angle dependence carried by these forms |
| Junction as a shared boundary Riemann problem | Plan §2.7 | Verified to 0.07% under a pulse of 69% of mean pressure |
| Collector configurations and pulse timing | Blair, G. P. *Design and Simulation of Four-Stroke Engines*, SAE 1999, ch. 6 | — |

Bassett, Winterbone & Pearson (2001) UNSTEADY junction coefficients are a
documented standing deferral: branch-angle dependence is currently carried by
the Idelchik forms. See [`physics.md`](physics.md).

## Acoustics

| Used for | Source | Validity |
|---|---|---|
| Open-end radiation impedance | Levine, H. & Schwinger, J. *Physical Review* 73 (1948) 383–406 | Low-ka form used below the stated limit |
| Transfer-matrix method | Munjal, M. L. *Acoustics of Ducts and Mufflers*, 2nd ed., Wiley 2014 | Plane-wave regime, below the first cross-mode |
| Atmospheric absorption | ISO 9613-1 | — |
| Frequency weighting and time constants | IEC 61672-1 | — |
| Fractional-octave bands | IEC 61260-1 | — |
| Loudness normalisation for A/B | ITU-R BS.1770 / EBU R128 | — |
| Stationary vehicle noise geometry | ISO 5130, SAE J1287 | Referenced for geometry; **no absolute level is claimed** |

The TMM is cross-validated against the nonlinear solver to 0.45 dB. Four
psychoacoustic metrics — ISO 532-3, ECMA-418-2, fluctuation strength and
DIN 45681 — are **not implemented**: each needs verification against published
reference signals that cannot be redistributed. See
[`acoustics.md`](acoustics.md).

## Turbomachinery

| Used for | Source | Validity |
|---|---|---|
| Corrected mass flow and speed | SAE J1826 (2022), Turbocharger Gas Stand Test Code | Defines surge, soft-surge and choke lines, and the reference conditions a map must state |
| Pulse vs constant-pressure turbocharging | Watson, N. & Janota, M. S. *Turbocharging the Internal Combustion Engine*, Macmillan 1982 | — |
| Blade speed ratio, turbine efficiency character | Baines, N. C. *Fundamentals of Turbocharging*, Concepts NREC 2005, ch. 5 | Radial turbines; peak near U/C ≈ 0.7 |
| Housing rescaling by area ratio | Watson & Janota 1982; Baines 2005 | Corrected flow scales with A/R; the efficiency field is left unchanged |
| Surge dynamics | Greitzer, E. M. *ASME J. Eng. Power* 98 (1976); Moore, F. K. & Greitzer, E. M. (1986) | B-parameter form; scaling below critical B |

**A map's reference conditions are required and never defaulted.** The two
common gas-stand references are 1.69% apart in corrected speed before that
propagates into pressure ratio, and the error is invisible in the answer.

**Every turbocharger in the shipped library is analytic**, with its own
`Source` and `Licence` saying so. WaveBench ships no manufacturer maps without
written permission (plan §4.7).

## Correction factors and standards

| Used for | Source |
|---|---|
| Torque and power correction | SAE J1349 |
| Standard atmosphere | U.S. Standard Atmosphere (1976) |
| Physical constants | CODATA 2018 |
| Atomic weights | IUPAC 2021 |
| Quantities and units | ISO 80000-4 |
| Rotor balance grades | ISO 1940 |

## Design limits quoted in warnings

Every design warning names the source of its limit, because a limit without
one is this tool's opinion.

| Limit | Source |
|---|---|
| Diffuser half-angle ≤ 7° before separation | Claywell, M. & Horkheimer, D., SAE 2006-01-3654 |
| Merge collector branch angle 10–30° | Idelchik, converging wye |
| Collector area ratio 0.7–1.0 × combined primaries | Blair 1999, ch. 6 |
| Surge margin ≥ 10% | Plan §4.2 |
| Turbine inlet temperature against the rated limit | Plan §4.3 |
| L/D above about 1 for plane-wave theory | Plan §5.3 |

## Validation cases

What has been checked against something outside this tool, and what has not,
is stated in every generated report and in [`physics.md`](physics.md). The
standing open cases are:

- **Transient spool against a measured case.** No redistributable dataset
  found; self-consistency (mesh convergence, energy balance, sensitivity-band
  behaviour) is checked in its place.
- **Measured order levels.** Order structure is verified; absolute measured
  levels are not.
- **Diabatic correction against a measured on-engine outlet temperature.**

The first published-data case is the open-access CSU thesis runner-length
study, reproduced in [`validation/`](../validation/).
