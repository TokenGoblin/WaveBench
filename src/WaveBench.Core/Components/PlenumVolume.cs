using WaveBench.Core.Solver;

namespace WaveBench.Core.Components;

/// <summary>
/// 0D plenum/airbox (plan §2.7): open-system mass and energy balance over a
/// fixed volume, multiple ports, composition-resolved. The connectors
/// (orifice ports) compute the flows; the plenum integrates them. Auto-
/// promotion to 1D above an L/D threshold is a network-assembly concern
/// (Phase 5).
/// </summary>
public sealed class PlenumVolume
{
    private readonly IGasModel _gas;
    private readonly double[] _massBySpecies;
    private double _dMass;
    private double _dEnergy;
    private readonly double[] _dMassBySpecies;

    public PlenumVolume(IGasModel gas, double volume, double pressure, double temperature, double[]? composition = null)
    {
        _gas = gas;
        Volume = volume;
        _massBySpecies = new double[gas.SpeciesCount];
        _dMassBySpecies = new double[gas.SpeciesCount];

        if (gas.SpeciesCount > 0 && (composition is null || composition.Length != gas.SpeciesCount))
        {
            throw new ArgumentException("Composition must match the gas model's species count.");
        }

        Span<double> y = composition is null ? default : composition.AsSpan();

        var rho = pressure / (SpecificGasConstant(y) * temperature);
        Mass = rho * volume;
        Energy = _gas.TotalEnergy(rho, 0.0, pressure, y) * volume;
        for (var k = 0; k < gas.SpeciesCount; k++)
        {
            _massBySpecies[k] = Mass * composition![k];
        }

        LastState = _gas.FromConserved(rho, 0.0, Energy / volume, y, temperature);
    }

    public double Volume { get; }

    /// <summary>Total gas mass, kg.</summary>
    public double Mass { get; private set; }

    /// <summary>Total internal energy, J.</summary>
    public double Energy { get; private set; }

    public GasState LastState { get; private set; }

    public double Pressure => LastState.P;

    public double Temperature => LastState.T;

    public double Density => Mass / Volume;

    public IReadOnlyList<double> MassFractions
    {
        get
        {
            var y = new double[_gas.SpeciesCount];
            for (var k = 0; k < y.Length; k++)
            {
                y[k] = _massBySpecies[k] / Mass;
            }

            return y;
        }
    }

    private double SpecificGasConstant(ReadOnlySpan<double> y) =>
        _gas is MultiSpeciesGasModel m
            ? m.GasConstant(y)
            : ((PerfectGasModel)_gas).Gas.SpecificGasConstant;

    /// <summary>Enthalpy of the plenum gas, J/kg (for outflow energy flux).</summary>
    public double SpecificEnthalpy => (Energy / Mass) + Pressure / Density;

    /// <summary>Queue an inflow (dm &gt; 0, with the source enthalpy and composition) or outflow (dm &lt; 0).</summary>
    public void QueueFlow(double dm, double specificEnthalpyIn, ReadOnlySpan<double> compositionIn)
    {
        _dMass += dm;
        if (dm >= 0)
        {
            _dEnergy += dm * specificEnthalpyIn;
            for (var k = 0; k < _gas.SpeciesCount; k++)
            {
                _dMassBySpecies[k] += dm * compositionIn[k];
            }
        }
        else
        {
            _dEnergy += dm * SpecificEnthalpy;
            for (var k = 0; k < _gas.SpeciesCount; k++)
            {
                _dMassBySpecies[k] += dm * (_massBySpecies[k] / Mass);
            }
        }
    }

    /// <summary>Apply all queued flows and refresh the state (deterministic fixed order).</summary>
    public void Commit()
    {
        Mass += _dMass;
        Energy += _dEnergy;
        for (var k = 0; k < _gas.SpeciesCount; k++)
        {
            _massBySpecies[k] += _dMassBySpecies[k];
        }

        _dMass = 0;
        _dEnergy = 0;
        Array.Clear(_dMassBySpecies);

        if (Mass <= 0)
        {
            throw new InvalidOperationException("Plenum drained to non-physical mass.");
        }

        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        for (var k = 0; k < y.Length; k++)
        {
            y[k] = _massBySpecies[k] / Mass;
        }

        LastState = _gas.FromConserved(Mass / Volume, 0.0, Energy / Volume, y, LastState.T);
    }
}
