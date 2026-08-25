namespace WaveBench.Core.Solver;

/// <summary>
/// Surface treatment of a pipe wall as an (emissivity, external thermal
/// resistance) pair (plan §2.9). Shipped presets are editable engineering
/// defaults: emissivities are typical hot-surface values; resistances follow
/// from coating/wrap thickness over conductivity (e.g. 3 mm basalt wrap at
/// k ≈ 0.12 W/m·K → ≈ 0.025 m²K/W).
/// </summary>
public sealed record WallSurface(string Name, double Emissivity, double ExternalResistance)
{
    public static WallSurface BareStainless { get; } = new("Bare stainless (oxidised)", 0.80, 0.0);

    public static WallSurface CeramicCoated { get; } = new("Ceramic coated", 0.55, 2.0e-4);

    public static WallSurface Wrapped { get; } = new("Header wrap", 0.70, 2.5e-2);

    public static WallSurface Insulated { get; } = new("Insulated", 0.60, 1.0e-1);

    public static WallSurface WaterJacketed { get; } = new("Water jacketed", 0.30, 0.0);
}

/// <summary>
/// Per-cell pipe wall thermal node (plan §2.9):
///   (mc)_w dT_w/dt = h_in (T_gas − T_w) − (T_w − T_amb)/R_out − εσ(T_w⁴ − T_amb⁴)
/// per unit inner area, with R_out = 1/h_out + R_surface. Explicit
/// integration — wall time constants are orders of magnitude above the gas
/// timestep.
/// </summary>
public sealed class WallThermalModel
{
    private const double StefanBoltzmann = 5.670374419e-8;

    private readonly double _arealHeatCapacity;
    private readonly double _externalCoefficient;

    public WallThermalModel(
        int cellCount,
        WallSurface surface,
        double initialTemperature,
        double ambientTemperature,
        double arealHeatCapacity = 7900.0,   // 2 mm stainless: ρ·t·c ≈ 7900·0.002·500 J/(m²·K)
        double externalHeatTransferCoefficient = 15.0) // natural + light forced convection
    {
        Surface = surface;
        AmbientTemperature = ambientTemperature;
        _arealHeatCapacity = arealHeatCapacity;
        _externalCoefficient = externalHeatTransferCoefficient;
        Temperature = new double[cellCount];
        Array.Fill(Temperature, initialTemperature);
    }

    public WallSurface Surface { get; }

    public double AmbientTemperature { get; set; }

    /// <summary>Wall temperature per cell, K.</summary>
    public double[] Temperature { get; }

    /// <summary>Combined external conductance including the surface resistance, W/(m²·K).</summary>
    public double ExternalConductance =>
        1.0 / (1.0 / _externalCoefficient + Surface.ExternalResistance);

    public void Update(double dt, ReadOnlySpan<double> innerHeatTransferCoefficient, ReadOnlySpan<double> gasTemperature)
    {
        var uOut = ExternalConductance;
        for (var i = 0; i < Temperature.Length; i++)
        {
            var tw = Temperature[i];
            var qIn = innerHeatTransferCoefficient[i] * (gasTemperature[i] - tw);
            var qOut = uOut * (tw - AmbientTemperature);
            var qRad = Surface.Emissivity * StefanBoltzmann *
                       (tw * tw * tw * tw - Math.Pow(AmbientTemperature, 4));
            Temperature[i] = tw + dt * (qIn - qOut - qRad) / _arealHeatCapacity;
        }
    }
}
