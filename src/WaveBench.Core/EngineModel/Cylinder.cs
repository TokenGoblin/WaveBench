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
    /// (midpoint volume derivative) plus the queued port flows.
    /// </summary>
    public void Step(double dt, double engineAngleDeg, double omegaRadPerSec)
    {
        var theta = LocalAngle(engineAngleDeg);
        var dVdTheta = Geometry.VolumeDerivative(theta);
        var dThetaRad = omegaRadPerSec * dt;
        var dV = dVdTheta * dThetaRad;

        Energy += -Pressure * dV + _dEnergy;
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
    }
}
