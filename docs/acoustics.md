# WaveBench acoustics documentation

Source of truth: the master plan, Part 3. Records what is implemented, with
sources and measured verification results. Grows with the phases, in the
same commit as the code.

## 1. Linear acoustics engine — TMM (Phase 8)

Frequency-domain transfer-matrix method (plan §3.3) in the [p, U] (pressure,
volume velocity) convention, so area enters through each element's
characteristic impedance ρc/S and junctions are natural. Complex arithmetic
via `System.Numerics.Complex`; the medium state (a, ρ, T, γ, Pr) comes from
the same gas state the nonlinear solver uses — never a fixed 343 m/s.

### Elements (`WaveBench.Acoustics`)

- **Uniform duct** with mean flow and damping:
  T = [[cos k_cL, jZ·sin k_cL], [j/Z·sin k_cL, cos k_cL]]·e^(−jM·k_cL),
  k_c = k/(1−M²), complex k = ω/c − jα with the classical Kirchhoff
  wide-tube visco-thermal attenuation α = (1/rc)·√(νω/2)·(1+(γ−1)/√Pr)
  (Pierce, *Acoustics* §10-5).
- **Area discontinuity**: continuity of p and U plus the series inertial end
  correction L_a = ρδ/S_small, δ ≈ 0.6·r_small·(1 − S_small/S_large).
- **Quarter-wave stub** (shunt): Z = −j(ρc/S)·cot(k·L_eff), end-corrected,
  damped.
- **Helmholtz resonator** (shunt): Z = R + j(ωM_a − 1/ωC_a) with
  M_a = ρL_eff/S, C_a = V/(ρc²), neck visco-thermal R.
- **Levine–Schwinger radiation** (§3.5): unflanged Z = (ρc/S)[(ka)²/4 +
  j·0.6133·ka], flanged [(ka)²/2 + j·0.8216·ka]; low-ka forms valid to
  ka ≈ 1.5 (directivity takes over above — Phase 9).
- **Terminations**: anechoic, unflanged/flanged open, rigid,
  pressure-release.

`AcousticNetwork` chains elements and produces transmission loss (four-pole
formula with port impedances), insertion loss vs a reference chain, input
impedance (the quantity that also governs scavenging — plan §3.3), and the
pressure transfer function. Conical/tapered geometry is handled as segmented
ducts (≥ 20 segments per wavelength, plan §3.3); perforated and dissipative
elements arrive with the silencer work.

### Verification results (§6.1)

| Test | Tolerance | Result |
|---|---|---|
| Expansion chamber TL vs 10·log₁₀[1+¼(m−1/m)²sin²kL] | 0.1 dB | pass, 50–2000 Hz |
| Quarter-wave stub modes (2n−1)c/4L | 1% | pass (n = 1, 2) |
| Helmholtz resonance (c/2π)√(S/VL_eff) | 2% | pass, TL > 20 dB at f₀ |
| Convective shift f_n = n·c(1−M²)/2L at M = 0.2 | 0.5% | pass |
| Levine–Schwinger end correction (open-pipe resonance shift) | published curve | pass; R ∝ (ka)² exact |
| Reciprocity (reversed chain) | 1e-9 | pass |
| 20-element network, 512 frequencies 1–10 kHz | < 10 ms | **4.3 ms** |

### TMM vs the nonlinear solver (the §6.1 headline check)

The same smooth-taper expansion chamber (d40 → d80 × 0.2 m, 40 mm
smoothstep transitions) solved both ways: nonlinear FV pulse transmission
(straight-duct reference run cancels every shared numerical artefact) versus
the segmented-duct TMM. **Worst deviation 200–2500 Hz: 0.45 dB at the
2.5 mm production mesh (0.24 dB at 1.25 mm)** — inside the 1 dB gate with
margin. At large amplitude the divergence between the methods *is* the
nonlinearity, and the UI must present it as such.

**Abrupt-step finding (documented limitation):** meshing a sudden area
discontinuity as resolved A(x) geometry converges only first order at the
slope discontinuity (single-step transmission 18.5 → 18.8 Pa toward the
plane-wave 20.0 under refinement). This is out of contract by design — plan
§2.7 treats sudden expansions/contractions as boundary components
(Borda–Carnot), not meshed geometry. Model abrupt steps with components;
mesh only smooth profiles.
