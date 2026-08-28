namespace WaveBench.Boost.Thermal;

/// <summary>
/// Lumped-capacitance thermal properties of the three housings (plan §4.4).
///
/// Every conductance here is a <b>calibration parameter</b>, not a derived
/// quantity. They are exposed rather than buried because a turbocharger's
/// internal heat paths depend on the heat shield, the bearing type, whether the
/// centre housing is water-cooled and how the unit is mounted — none of which a
/// map file records. The defaults reproduce the compressor-side heat flux range
/// reported for small automotive units (several hundred to ~2 kW) and are
/// stated as such; a unit with measured housing temperatures should be
/// calibrated against them.
///
/// Reference for the method: Serrano, Olmeda, Arnau et al., lumped-capacitance
/// turbocharger heat-transfer models (e.g. SAE 2010-01-1064 and the subsequent
/// Olmeda/Dolz/Arnau/Reyes-Belmonte work). The three-node topology, the oil and
/// coolant rejection paths, and the separation of apparent from aerodynamic
/// efficiency all follow that family of models.
/// </summary>
public sealed record TurboThermalProperties
{
    /// <summary>Heat capacity of the turbine housing, J/K. ~1.5 kg of cast iron.</summary>
    public double TurbineHousingCapacity { get; init; } = 750.0;

    /// <summary>Bearing (centre) housing, J/K.</summary>
    public double BearingHousingCapacity { get; init; } = 450.0;

    /// <summary>Compressor housing, J/K. ~0.5 kg of aluminium.</summary>
    public double CompressorHousingCapacity { get; init; } = 450.0;

    /// <summary>Gas-to-turbine-housing conductance, W/K.</summary>
    public double TurbineGasConductance { get; init; } = 12.0;

    /// <summary>Turbine housing to bearing housing, W/K. Low by design — that is what the heat shield is for.</summary>
    public double TurbineToBearingConductance { get; init; } = 8.0;

    /// <summary>Bearing housing to compressor housing, W/K.</summary>
    public double BearingToCompressorConductance { get; init; } = 20.0;

    /// <summary>Compressor housing to the air passing through it, W/K. This is the term the correction turns on.</summary>
    public double CompressorAirConductance { get; init; } = 40.0;

    /// <summary>Oil rejection from the bearing housing, W/K, as ṁ_oil·c_p·ε.</summary>
    public double OilConductance { get; init; } = 25.0;

    /// <summary>Coolant rejection, W/K. Zero for an oil-cooled unit.</summary>
    public double CoolantConductance { get; init; }

    /// <summary>External convection from each housing to the engine bay, W/K.</summary>
    public double ExternalConductance { get; init; } = 2.0;

    /// <summary>Radiating area of the turbine housing, m².</summary>
    public double TurbineRadiatingArea { get; init; } = 0.020;

    /// <summary>Emissivity of the turbine housing. Oxidised cast iron.</summary>
    public double Emissivity { get; init; } = 0.80;

    /// <summary>A water-cooled centre housing: the usual answer to heat soak.</summary>
    public TurboThermalProperties WaterCooled() => this with { CoolantConductance = 60.0 };
}

/// <summary>The environment the turbocharger is sitting in.</summary>
/// <param name="AmbientK">Engine-bay air around the housings.</param>
/// <param name="OilInletK">Oil temperature into the bearing housing.</param>
/// <param name="CoolantK">Coolant temperature; ignored when the coolant conductance is zero.</param>
public sealed record TurboEnvironment(double AmbientK = 350.0, double OilInletK = 380.0, double CoolantK = 363.0)
{
    /// <summary>A gas stand: cooler bay, cooler oil, and a turbine inlet far below on-engine temperature.</summary>
    public static TurboEnvironment GasStand { get; } = new(298.15, 353.15, 353.15);
}

/// <summary>The three housing temperatures and the heat flows between them.</summary>
/// <param name="TurbineHousingK">T of the turbine housing.</param>
/// <param name="BearingHousingK">T of the centre housing.</param>
/// <param name="CompressorHousingK">T of the compressor housing.</param>
/// <param name="TurbineGasHeatW">Heat out of the exhaust gas into the turbine housing.</param>
/// <param name="CompressorAirHeatW">Heat into the air passing the compressor. The number the correction needs.</param>
/// <param name="OilHeatW">Heat carried away by the oil.</param>
public readonly record struct TurboThermalState(
    double TurbineHousingK,
    double BearingHousingK,
    double CompressorHousingK,
    double TurbineGasHeatW,
    double CompressorAirHeatW,
    double OilHeatW);

/// <summary>
/// Three-node lumped-capacitance model of the turbocharger housings
/// (plan §4.4), and the diabatic correction it drives (plan §4.2).
///
/// The point of it: <b>a gas-stand map's efficiency is not aerodynamic
/// efficiency.</b> The stand measures a temperature rise that already contains
/// heat conducted from the turbine end, so the map's η is an <i>apparent</i>
/// efficiency valid only under the stand's own thermal condition. Put the same
/// turbo on an engine, where turbine inlet temperature is 400–500 K higher, and
/// the compressor end runs hotter still — so the outlet air comes out above
/// what the raw map predicts. Users who size an intercooler off raw map numbers
/// under-size it.
/// </summary>
public sealed class TurboThermalModel(TurboThermalProperties? properties = null)
{
    private const double StefanBoltzmann = 5.670374419e-8;

    public TurboThermalProperties Properties { get; } = properties ?? new TurboThermalProperties();

    /// <summary>Current housing temperatures, for the transient path.</summary>
    public double TurbineHousingK { get; private set; } = 350.0;

    public double BearingHousingK { get; private set; } = 350.0;

    public double CompressorHousingK { get; private set; } = 350.0;

    /// <summary>Set all three nodes, e.g. to start a cold-start or heat-soak run.</summary>
    public void SetHousings(double turbineK, double bearingK, double compressorK)
    {
        TurbineHousingK = turbineK;
        BearingHousingK = bearingK;
        CompressorHousingK = compressorK;
    }

    /// <summary>
    /// Advance the housing temperatures by dt against the current gas
    /// conditions. Housing time constants are minutes, so this is stepped on
    /// the transient's own clock rather than the solver's — but it is what
    /// makes a second dyno pull differ from the first.
    /// </summary>
    public TurboThermalState Step(
        double dt,
        double turbineInletK,
        double meanCompressorAirK,
        TurboEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var p = Properties;
        var (dTurbine, dBearing, dCompressor, state) =
            Derivatives(TurbineHousingK, BearingHousingK, CompressorHousingK,
                turbineInletK, meanCompressorAirK, environment);

        TurbineHousingK += dTurbine / p.TurbineHousingCapacity * dt;
        BearingHousingK += dBearing / p.BearingHousingCapacity * dt;
        CompressorHousingK += dCompressor / p.CompressorHousingCapacity * dt;

        return state;
    }

    /// <summary>
    /// The steady housing temperatures for a held operating point, by damped
    /// fixed-point iteration on the three balances. Radiation makes it
    /// nonlinear, which is why it is iterated rather than inverted.
    /// </summary>
    public TurboThermalState SolveSteady(
        double turbineInletK, double meanCompressorAirK, TurboEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var t = Math.Max(turbineInletK * 0.7, environment.AmbientK);
        var b = environment.OilInletK;
        var c = environment.AmbientK + 30.0;

        TurboThermalState state = default;

        for (var i = 0; i < 400; i++)
        {
            var (dT, dB, dC, s) = Derivatives(t, b, c, turbineInletK, meanCompressorAirK, environment);
            state = s;

            // Step each node by its imbalance divided by the total conductance
            // leaving it — a Newton step in all but name, damped because the
            // radiation term stiffens the turbine node.
            t += 0.5 * dT / TurbineNodeConductance(t, environment);
            b += 0.5 * dB / BearingNodeConductance();
            c += 0.5 * dC / CompressorNodeConductance();

            if (Math.Abs(dT) + Math.Abs(dB) + Math.Abs(dC) < 1e-6)
            {
                break;
            }
        }

        TurbineHousingK = t;
        BearingHousingK = b;
        CompressorHousingK = c;
        return state;
    }

    private (double Turbine, double Bearing, double Compressor, TurboThermalState State) Derivatives(
        double t, double b, double c, double turbineInletK, double meanCompressorAirK, TurboEnvironment env)
    {
        var p = Properties;

        var gasToTurbine = p.TurbineGasConductance * (turbineInletK - t);
        var turbineToBearing = p.TurbineToBearingConductance * (t - b);
        var bearingToCompressor = p.BearingToCompressorConductance * (b - c);
        var compressorToAir = p.CompressorAirConductance * (c - meanCompressorAirK);
        var toOil = p.OilConductance * (b - env.OilInletK);
        var toCoolant = p.CoolantConductance * (b - env.CoolantK);

        var radiation = p.Emissivity * StefanBoltzmann * p.TurbineRadiatingArea
                        * (Math.Pow(t, 4.0) - Math.Pow(env.AmbientK, 4.0));

        var turbine = gasToTurbine - turbineToBearing - (p.ExternalConductance * (t - env.AmbientK)) - radiation;
        var bearing = turbineToBearing - bearingToCompressor - toOil - toCoolant
                      - (p.ExternalConductance * (b - env.AmbientK));
        var compressor = bearingToCompressor - compressorToAir - (p.ExternalConductance * (c - env.AmbientK));

        return (turbine, bearing, compressor,
            new TurboThermalState(t, b, c, gasToTurbine, compressorToAir, toOil));
    }

    private double TurbineNodeConductance(double t, TurboEnvironment env)
    {
        var p = Properties;
        var radiative = 4.0 * p.Emissivity * StefanBoltzmann * p.TurbineRadiatingArea * Math.Pow(Math.Max(t, 1.0), 3.0);
        return p.TurbineGasConductance + p.TurbineToBearingConductance + p.ExternalConductance + radiative;
    }

    private double BearingNodeConductance()
    {
        var p = Properties;
        return p.TurbineToBearingConductance + p.BearingToCompressorConductance + p.OilConductance
               + p.CoolantConductance + p.ExternalConductance;
    }

    private double CompressorNodeConductance()
    {
        var p = Properties;
        return p.BearingToCompressorConductance + p.CompressorAirConductance + p.ExternalConductance;
    }
}
