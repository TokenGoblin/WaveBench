using WaveBench.Core.Solver;

namespace WaveBench.Core.EngineModel;

/// <summary>
/// 0D single-zone cylinder, open-system mass and energy conservation
/// (plan §2.5): dU = −p·dV + Σ ṁ·h (wall heat and combustion arrive in
/// Phase 6). Composition-resolved. Volume follows the exact slider-crank.
/// </summary>
public sealed class Cylinder
{
    private readonly IGasModel _gas;
    private readonly double[] _massBySpecies;
    private double _dMass;
    private double _dEnergy;
    private readonly double[] _dMassBySpecies;

    public Cylinder(
        IGasModel gas, CrankGeometry geometry, double phaseOffsetDeg,
        double pressure, double temperature, double[]? composition = null)
    {
        _gas = gas;
        Geometry = geometry;
        PhaseOffsetDeg = phaseOffsetDeg;
        _massBySpecies = new double[gas.SpeciesCount];
        _dMassBySpecies = new double[gas.SpeciesCount];

        if (gas.SpeciesCount > 0 && (composition is null || composition.Length != gas.SpeciesCount))
        {
            throw new ArgumentException("Composition must match the gas model.");
        }

        Span<double> y = composition is null ? default : composition.AsSpan();
        var v0 = geometry.Volume(phaseOffsetDeg);
        var rho = InitialDensity(pressure, temperature, y);
        Mass = rho * v0;
        Energy = _gas.TotalEnergy(rho, 0.0, pressure, y) * v0;
        for (var k = 0; k < gas.SpeciesCount; k++)
        {
            _massBySpecies[k] = Mass * composition![k];
        }

        State = _gas.FromConserved(rho, 0.0, Energy / v0, y, temperature);
        Volume = v0;
    }

    private double InitialDensity(double p, double t, ReadOnlySpan<double> y)
    {
        var r = _gas switch
        {
            MultiSpeciesGasModel m => m.GasConstant(y),
            PerfectGasModel pg => pg.Gas.SpecificGasConstant,
            _ => throw new NotSupportedException(),
        };
        return p / (r * t);
    }

    public CrankGeometry Geometry { get; }

    /// <summary>Crank phase offset of this cylinder (deg of the 720° cycle).</summary>
    public double PhaseOffsetDeg { get; }

    public double Mass { get; private set; }

    public double Energy { get; private set; }

    public double Volume { get; private set; }

    public GasState State { get; private set; }

    public double Pressure => State.P;

    public double Temperature => State.T;

    public double Density => Mass / Volume;

    /// <summary>Local crank angle of this cylinder (deg in [0,720)).</summary>
    public double LocalAngle(double engineAngleDeg)
    {
        var a = (engineAngleDeg + PhaseOffsetDeg) % 720.0;
        return a < 0 ? a + 720.0 : a;
    }

    public double SpecificEnthalpy => Energy / Mass + Pressure / Density;

    /// <summary>Work done by the gas on the piston since start, J (∫p·dV).</summary>
    public double CumulativeWork { get; private set; }

    // ---- Fired-cycle configuration (Phase 6) --------------------------------

    /// <summary>Burn schedule; null = motored.</summary>
    public ICombustionModel? Combustion { get; set; }

    /// <summary>Fuel lower heating value, J/kg (premixed charge).</summary>
    public double FuelLowerHeatingValue { get; set; }

    /// <summary>Fuel mass fraction of the trapped charge (= 1/(1+AFR) premixed).</summary>
    public double FuelChargeFraction { get; set; }

    /// <summary>Combustion efficiency (unburned fraction lost), default 0.98.</summary>
    public double CombustionEfficiency { get; set; } = 0.98;

    /// <summary>In-cylinder wall heat transfer; null = adiabatic.</summary>
    public HeatTransferCorrelation? HeatTransfer { get; set; }

    /// <summary>Area-averaged wall temperature, K (fixed input, plan §2.5).</summary>
    public double WallTemperature { get; set; } = 420.0;

    /// <summary>Octane number for the knock integral; null disables knock tracking.</summary>
    public double? KnockOctaneNumber { get; set; }

    /// <summary>Optional stochastic cycle-to-cycle combustion variability (plan §3.4).</summary>
    public CycleVariability? Variability { get; set; }

    /// <summary>
    /// Blowby effective ring-gap orifice area, m² (plan §2.5); typical
    /// 0.2–0.6 mm² per cylinder. Mass leaks to the crankcase at ambient
    /// pressure and never returns. Zero disables.
    /// </summary>
    public double BlowbyEffectiveArea { get; set; }

    public double CrankcasePressure { get; set; } = 101_325.0;

    /// <summary>
    /// Crevice volume, m³ (plan §2.5): gas at cylinder pressure but wall
    /// temperature, exchanging mass with the bulk as pressure changes —
    /// the standard isothermal crevice model. Zero disables.
    /// </summary>
    public double CreviceVolume { get; set; }

    /// <summary>Cumulative blowby mass lost, kg.</summary>
    public double CumulativeBlowby { get; private set; }

    private double _creviceMass;

    public int CylinderIndex { get; set; }

    // ---- Per-cycle fired-state tracking ------------------------------------

    private double _previousBurnAngle = double.NaN;
    private double _previousBurnFraction;
    private bool _burnComplete;
    private double _cycleHeatRelease;
    private double _socPressure;
    private double _socTemperature;
    private double _socGamma;
    private long _cycleNumber;
    private double _phaseShift;
    private double _durationScale = 1.0;
    private double _energyScale = 1.0;

    /// <summary>Peak pressure since <see cref="ResetPeakPressure"/>, Pa (for Chen–Flynn).</summary>
    public double CyclePeakPressure { get; private set; }

    /// <summary>Fuel mass burned since start, kg (cumulative; take deltas per cycle).</summary>
    public double CumulativeFuelBurned { get; private set; }

    /// <summary>Livengood–Wu integral, cumulative across cycles (take deltas per cycle).</summary>
    public double CumulativeKnockIntegral { get; private set; }

    /// <summary>
    /// Heat lost to the walls since start, J, as a positive quantity
    /// (cumulative; take deltas per cycle). A standard engine metric in its
    /// own right, and what shows whether a zone-resolved heat-transfer model
    /// is actually changing anything.
    /// </summary>
    public double CumulativeHeatLoss { get; private set; }

    public void ResetPeakPressure() => CyclePeakPressure = State.P;

    /// <summary>Unburned-zone temperature (isentropic from start of compression), K.</summary>
    public double UnburnedTemperature { get; private set; }

    /// <summary>
    /// Burned-zone temperature, K, from the shared pressure and the volume
    /// the unburned zone does not occupy. Zero outside combustion.
    /// </summary>
    public double BurnedTemperature { get; private set; }

    /// <summary>Volume occupied by the burned zone, m³.</summary>
    public double BurnedVolume { get; private set; }

    /// <summary>
    /// Volume occupied by the unburned zone, m³. Exposed alongside
    /// <see cref="BurnedVolume"/> so the defining constraint of a two-zone
    /// model — that the two sum to the cylinder volume — can actually be
    /// checked by a caller, rather than merely asserted in a doc comment.
    /// </summary>
    public double UnburnedVolume { get; private set; }

    /// <summary>Burned mass fraction, 0–1.</summary>
    public double BurnedFraction { get; private set; }

    /// <summary>
    /// Resolve wall heat transfer by zone rather than from the bulk mean
    /// temperature (plan §2.4 Level 2).
    ///
    /// On by default, because the plan requires it and it is the more
    /// faithful model: a single mean temperature under-predicts heat loss
    /// while the flame is passing, since loss is linear in (T − T_wall) and
    /// the burned gas runs hundreds of K above the mean. Measured cost on a
    /// 600 cc single: 0.7–0.9% torque and 1–2 g/kWh BSFC, with volumetric
    /// efficiency unchanged — heat lost during the burn does not change how
    /// the engine breathes. Set false for the single-zone model.
    /// </summary>
    public bool TwoZoneHeatTransfer { get; set; } = true;

    public void CopyMassFractions(Span<double> y)
    {
        for (var k = 0; k < y.Length; k++)
        {
            y[k] = _massBySpecies[k] / Mass;
        }
    }

    /// <summary>Queue a port flow: dm &gt; 0 into the cylinder with source enthalpy/composition.</summary>
    public void QueueFlow(double dm, double specificEnthalpyIn, ReadOnlySpan<double> compositionIn)
    {
        _dMass += dm;
        if (dm >= 0)
        {
            _dEnergy += dm * specificEnthalpyIn;
            for (var k = 0; k < _massBySpecies.Length; k++)
            {
                _dMassBySpecies[k] += dm * compositionIn[k];
            }
        }
        else
        {
            _dEnergy += dm * SpecificEnthalpy;
            for (var k = 0; k < _massBySpecies.Length; k++)
            {
                _dMassBySpecies[k] += dm * (_massBySpecies[k] / Mass);
            }
        }
    }

    /// <summary>
    /// Advance the cylinder over dt at engine speed ω: piston work −p·dV
    /// (midpoint volume derivative), queued port flows, and — when configured —
    /// Wiebe heat release, wall heat transfer and knock tracking.
    /// </summary>
    public void Step(double dt, double engineAngleDeg, double omegaRadPerSec)
    {
        var theta = LocalAngle(engineAngleDeg);
        var dVdTheta = Geometry.VolumeDerivative(theta);
        var dThetaRad = omegaRadPerSec * dt;
        var dV = dVdTheta * dThetaRad;

        var firedSources = FiredSources(theta, dt, omegaRadPerSec);
        ApplyBlowbyAndCrevice(dt);

        Energy += -Pressure * dV + _dEnergy + firedSources;
        CumulativeWork += Pressure * dV;
        Mass += _dMass;
        for (var k = 0; k < _massBySpecies.Length; k++)
        {
            _massBySpecies[k] += _dMassBySpecies[k];
        }

        _dMass = 0;
        _dEnergy = 0;
        Array.Clear(_dMassBySpecies);

        var thetaNew = theta + dThetaRad * 180.0 / Math.PI;
        Volume = Geometry.Volume(thetaNew);

        Span<double> y = _massBySpecies.Length > 0 ? stackalloc double[_massBySpecies.Length] : default;
        CopyMassFractions(y);
        var rho = Mass / Volume;
        State = _gas.FromConserved(rho, 0.0, Energy / Volume, y, State.T);
        CyclePeakPressure = Math.Max(CyclePeakPressure, State.P);
    }

    /// <summary>Combustion heat release, wall heat and knock bookkeeping, J for this step.</summary>
    private double FiredSources(double theta, double dt, double omega)
    {
        // Per-cycle reset, at GAS-EXCHANGE TDC rather than at the local-angle
        // wrap.
        //
        // The wrap sits at firing TDC, which is in the middle of the burn: a
        // spark at −15° puts the window at local 705°→720°→40°. Resetting
        // there meant _previousBurnFraction still held the previous cycle's
        // 0.9933 for the whole pre-TDC portion, so dxb was clamped to zero and
        // NO fuel burned before TDC — and then the entire accumulated fraction
        // was released in the single step after the wrap. Measured before this
        // fix: 9.7% of the cycle's fuel in one step at −15° spark, 56.0% at
        // −30°, with peak pressure 152.6 bar against 99.4 bar. Spark-timing
        // sensitivity was not being modelled at all, and the SOC reference the
        // zone split and knock integral key off was frozen at TDC rather than
        // at spark.
        //
        // Cycling on the burn-window coordinate instead puts the boundary at
        // local 360° — gas-exchange TDC, the point furthest from combustion,
        // and after the previous burn has fully finished.
        var burnAngle = BurnWindowAngle(theta);
        if (!double.IsNaN(_previousBurnAngle) && burnAngle < _previousBurnAngle - 360.0)
        {
            _cycleNumber++;
            _previousBurnFraction = 0.0;
            _cycleHeatRelease = 0.0;
            _socPressure = 0.0;
            _burnComplete = false;
            BurnedFraction = 0.0;
            BurnedTemperature = 0.0;
            BurnedVolume = 0.0;
            UnburnedVolume = 0.0;
            if (Variability is { } variability)
            {
                (_phaseShift, _durationScale, _energyScale) = variability.Draw(CylinderIndex, _cycleNumber);
            }
        }

        _previousBurnAngle = burnAngle;

        if (Combustion is null)
        {
            return WallHeat(theta, dt, omega);
        }

        var effective = Combustion is WiebeCombustion wiebe
            ? wiebe with
            {
                StartAngleDeg = wiebe.StartAngleDeg + _phaseShift,
                DurationDeg = wiebe.DurationDeg * _durationScale,
            }
            : Combustion;

        var xb = effective.BurnFraction(theta);
        var dxb = Math.Max(0.0, xb - _previousBurnFraction);
        _previousBurnFraction = Math.Max(xb, _previousBurnFraction);

        // The Wiebe asymptote is 1 − e^(−a) = 0.9933 at a = 5, never 1, so
        // "burned fraction reached 1" is a condition that never fires. The
        // burn is over when its WINDOW is over; the missing 0.67% is the
        // exponential tail, not unburned charge sitting in the chamber.
        if (_socPressure > 0.0 && burnAngle >= effective.StartAngleDeg + effective.DurationDeg)
        {
            _burnComplete = true;
        }

        var heat = 0.0;
        if (dxb > 0)
        {
            if (_cycleHeatRelease == 0.0)
            {
                // Start of combustion: freeze the charge energy budget and the
                // unburned-zone reference state (isentropic compression from
                // here on — plan §2.4 knock formulation).
                var fuelMass = Mass * FuelChargeFraction;
                _cycleHeatRelease = fuelMass * FuelLowerHeatingValue * CombustionEfficiency * _energyScale;
                _socPressure = State.P;
                _socTemperature = State.T;
                _socGamma = State.Gamma;
            }

            CumulativeFuelBurned += dxb * Mass * FuelChargeFraction;
            heat = _cycleHeatRelease * dxb;
        }

        // Zone split from start of combustion onward — keyed on the SOC
        // reference actually having been recorded, which is what "combustion
        // has started" means. The zones are then available to the knock
        // integral, to the wall-heat model, and as outputs, not only when
        // knock happens to be tracked.
        if (_socPressure > 0.0)
        {
            UpdateZones(xb);

            // Livengood–Wu, while unburned charge remains.
            if (dxb > 0 && KnockOctaneNumber is { } octane)
            {
                var tau = WaveBench.Core.Thermo.Fuels.KnockModel.InductionTime(
                    octane, State.P, UnburnedTemperature);
                CumulativeKnockIntegral += dt / tau;
            }
        }

        return heat + WallHeat(theta, dt, omega);
    }

    private void ApplyBlowbyAndCrevice(double dt)
    {
        if (BlowbyEffectiveArea > 0 && Pressure > CrankcasePressure)
        {
            var r = Pressure / (Density * Temperature);
            var mDot = Components.CompressibleOrifice.MassFlow(
                1.0, BlowbyEffectiveArea, Pressure, Temperature, CrankcasePressure,
                State.Gamma, r);
            var dm = Math.Min(mDot * dt, 0.01 * Mass);
            CumulativeBlowby += dm;
            _dMass -= dm;
            _dEnergy -= dm * SpecificEnthalpy;
        }

        if (CreviceVolume > 0)
        {
            // Isothermal crevice at wall temperature: m = p·V/(R·T_wall).
            var r = Pressure / (Density * Temperature);
            var target = Pressure * CreviceVolume / (r * WallTemperature);
            var dm = target - _creviceMass;
            _creviceMass = target;
            _dMass -= dm;
            // Into the crevice: bulk loses enthalpy; back out: gas returns at
            // wall temperature.
            var cp = State.Gamma * r / (State.Gamma - 1.0);
            _dEnergy -= dm > 0 ? dm * SpecificEnthalpy : dm * cp * WallTemperature;
        }
    }

    private double WallHeat(double theta, double dt, double omega)
    {
        if (HeatTransfer is not { } correlation)
        {
            return 0.0;
        }

        var rpm = omega * 60.0 / (2.0 * Math.PI);
        var h = InCylinderHeatTransfer.Coefficient(
            correlation, Geometry.Bore, State.P, State.T,
            Geometry.MeanPistonSpeed(rpm), Volume);

        // Exposed area: head + piston crown + instantaneous liner band.
        var area = 2.0 * Geometry.PistonArea + Math.PI * Geometry.Bore * Geometry.PistonPosition(theta);

        if (!TwoZoneHeatTransfer || !ZonesResolved)
        {
            var singleZone = -h * area * (State.T - WallTemperature) * dt;
            CumulativeHeatLoss -= singleZone;
            return singleZone;
        }

        // Two-zone split (plan §2.4 Level 2). During the burn the mean gas
        // temperature is not the temperature touching the wall: the burned
        // zone runs hundreds of K above it and the unburned zone well below,
        // and heat loss is linear in (T − T_wall), so a single mean
        // systematically UNDER-predicts loss while the flame is passing.
        //
        // Wall area is apportioned by volume fraction, which is Heywood's
        // simple two-zone treatment (Internal Combustion Engine Fundamentals,
        // §12.4): the burned gas occupies V_b/V of the chamber and so, on
        // average, contacts that fraction of the surface. This is an
        // approximation — the real split depends on flame geometry and where
        // the plug sits — and it is the reason this is not claimed as more
        // than a zone-resolved heat-transfer model.
        var burnedVolumeFraction = Math.Clamp(BurnedVolume / Math.Max(Volume, 1e-12), 0.0, 1.0);
        var burnedLoss = h * area * burnedVolumeFraction * (BurnedTemperature - WallTemperature);
        var unburnedLoss = h * area * (1.0 - burnedVolumeFraction) * (UnburnedTemperature - WallTemperature);
        var loss = -(burnedLoss + unburnedLoss) * dt;
        CumulativeHeatLoss -= loss;
        return loss;
    }

    /// <summary>
    /// Burned mass fraction below which the zones are NOT resolved.
    ///
    /// At flame initiation the burned mass goes to zero, and
    /// T_b = p·V_b/(m_b·R) is a 0/0. The two limits do not approach at the
    /// same rate — the unburned zone's isentropic temperature is only
    /// approximately consistent with the mean state at the instant the burn
    /// starts — so the quotient is violently ill-conditioned: it was observed
    /// assigning 73% of the chamber volume to a zone of essentially zero mass
    /// and returning 5.6e10 K, which then poisoned the energy balance for the
    /// rest of the run. Below this fraction the charge is treated as one
    /// zone, which is also the physically honest answer: a kernel this small
    /// cannot dominate wall heat transfer.
    /// </summary>
    private const double MinimumResolvedBurnedFraction = 0.01;

    /// <summary>
    /// Backstop ceiling on a zone temperature, K.
    ///
    /// This is NOT a statement about combustion — it exists only to stop the
    /// kernel singularity above, which overshoots by seven orders of
    /// magnitude, from propagating if it ever escapes the fraction guard. It
    /// is deliberately far above anything a real charge reaches: a first
    /// attempt at 4000 K rejected the Yin validation case, whose MEAN
    /// temperature legitimately reaches 4013 K on the perfect-gas model, so a
    /// ceiling tight enough to police physics is tight enough to break honest
    /// results.
    /// </summary>
    private const double MaximumZoneTemperature = 10_000.0;

    /// <summary>
    /// Whether the burned/unburned split is currently meaningful. False
    /// before the kernel is established, after the burn window has closed
    /// (when there is only burned gas), and whenever the split failed its own
    /// validity check.
    /// </summary>
    public bool ZonesResolved { get; private set; }

    /// <summary>
    /// Updates the burned/unburned zone split for the current state. Both
    /// zones share the cylinder pressure and their volumes sum to the
    /// cylinder volume, which is what makes this a two-zone model rather than
    /// two independent gases.
    ///
    /// The unburned zone is compressed isentropically from the start of
    /// combustion (plan §2.4), which is the same construction the knock
    /// integral already used; the burned zone then takes whatever volume is
    /// left, and its temperature follows from the ideal-gas law at the shared
    /// pressure. Nothing here alters the pressure solve — the total energy
    /// balance is unchanged — so the zones are diagnostic plus, when
    /// <see cref="TwoZoneHeatTransfer"/> is set, an input to wall heat loss.
    /// </summary>
    private void UpdateZones(double burnedFraction)
    {
        BurnedFraction = Math.Clamp(burnedFraction, 0.0, 1.0);
        ZonesResolved = false;

        if (BurnedFraction < MinimumResolvedBurnedFraction || _socPressure <= 0.0)
        {
            UnburnedTemperature = State.T;
            BurnedTemperature = 0.0;
            BurnedVolume = 0.0;
            UnburnedVolume = Volume;
            return;
        }

        UnburnedTemperature = _socTemperature
                              * Math.Pow(State.P / _socPressure, (_socGamma - 1.0) / _socGamma);

        if (_burnComplete)
        {
            // The window has closed: everything that is going to burn has.
            // Carrying the Wiebe's 0.67% shortfall onward as a real unburned
            // zone invents a pocket of gas that cools isentropically to below
            // wall temperature and then feeds heat back INTO the charge
            // through the exhaust stroke.
            BurnedFraction = 1.0;
            UnburnedTemperature = State.T;
            BurnedTemperature = State.T;
            BurnedVolume = Volume;
            UnburnedVolume = 0.0;
            return;
        }

        // Specific gas constant of the current charge, from the state itself
        // so a species-resolved mixture is respected.
        var r = Density > 0 && State.T > 0 ? State.P / (Density * State.T) : 0.0;
        var unburnedMass = (1.0 - BurnedFraction) * Mass;
        var burnedMass = BurnedFraction * Mass;

        // Fall back to the single-zone view rather than propagating a bad
        // number: these zones feed heat transfer, and one NaN there poisons
        // the whole energy balance for the rest of the run.
        if (!(r > 0) || !(State.P > 0) || !double.IsFinite(UnburnedTemperature) || burnedMass <= 0)
        {
            FallBackToSingleZone();
            return;
        }

        var unburnedVolume = unburnedMass * r * UnburnedTemperature / State.P;
        var burnedVolume = Volume - unburnedVolume;
        var burnedTemperature = burnedVolume > 0
            ? State.P * burnedVolume / (burnedMass * r)
            : double.NaN;

        // Validity check, NOT a clamp. Clamping a bad temperature into range
        // leaves p·V_b = m_b·R·T_b silently violated — the two "zones" stop
        // being a partition of the charge while still being fed to the heat
        // transfer model, and a ceiling value of 10,000 K against a realistic
        // mean would drive an order-of-magnitude heat flux and wreck the
        // energy balance it exists to protect. If the split is not physical,
        // there is no split.
        var plausible = double.IsFinite(burnedTemperature)
                        && burnedVolume > 0.0
                        && burnedVolume <= Volume
                        && burnedTemperature > State.T
                        && burnedTemperature < MaximumZoneTemperature
                        && UnburnedTemperature < State.T;

        if (!plausible)
        {
            FallBackToSingleZone();
            return;
        }

        BurnedVolume = burnedVolume;
        UnburnedVolume = unburnedVolume;
        BurnedTemperature = burnedTemperature;
        ZonesResolved = true;
    }

    /// <summary>Abandon the split for this step and report the bulk state.</summary>
    private void FallBackToSingleZone()
    {
        ZonesResolved = false;
        UnburnedTemperature = State.T;
        BurnedTemperature = State.T;
        BurnedVolume = BurnedFraction * Volume;
        UnburnedVolume = Volume - BurnedVolume;
    }

    /// <summary>
    /// Local angle mapped onto the combustion window's coordinate: 0 is
    /// firing TDC and angles past 360° become negative, so a burn starting
    /// before TDC is a continuous interval rather than one straddling a wrap.
    /// This is the same mapping <see cref="WiebeCombustion"/> uses, and
    /// cycling the per-cycle burn state on it puts the reset at gas-exchange
    /// TDC — as far from combustion as the cycle allows.
    /// </summary>
    private static double BurnWindowAngle(double localAngleDeg)
    {
        var a = localAngleDeg % 720.0;
        if (a < 0)
        {
            a += 720.0;
        }

        return a > 360.0 ? a - 720.0 : a;
    }
}
