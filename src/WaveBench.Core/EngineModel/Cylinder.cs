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

    private double _previousLocalAngle = double.NaN;
    private double _previousBurnFraction;
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

    public void ResetPeakPressure() => CyclePeakPressure = State.P;

    /// <summary>Unburned-zone temperature (isentropic from start of compression), K.</summary>
    public double UnburnedTemperature { get; private set; }

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
        // Cycle wrap: local angle decreased → new cycle. Reset per-cycle state
        // and draw this cycle's variability perturbation.
        if (!double.IsNaN(_previousLocalAngle) && theta < _previousLocalAngle - 360.0)
        {
            _cycleNumber++;
            _previousBurnFraction = 0.0;
            _cycleHeatRelease = 0.0;
            _socPressure = 0.0;
            if (Variability is { } variability)
            {
                (_phaseShift, _durationScale, _energyScale) = variability.Draw(CylinderIndex, _cycleNumber);
            }
        }

        _previousLocalAngle = theta;

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

            // Unburned-zone temperature (isentropic from start of combustion)
            // and the Livengood–Wu integral, while unburned charge remains.
            if (KnockOctaneNumber is { } octane)
            {
                UnburnedTemperature = _socTemperature
                                      * Math.Pow(State.P / _socPressure, (_socGamma - 1.0) / _socGamma);
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
        return -h * area * (State.T - WallTemperature) * dt;
    }
}
