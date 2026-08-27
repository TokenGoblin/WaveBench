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

    /// <summary>
    /// A jacket held at coolant temperature. The wall is not free to float:
    /// callers should also clamp its temperature, which
    /// <see cref="WallThermalModel.FixedTemperature"/> does.
    /// </summary>
    public static WallSurface WaterJacketed { get; } = new("Water jacketed", 0.30, 0.0);

    /// <summary>Every shipped preset, in the order the UI offers them.</summary>
    public static IReadOnlyList<WallSurface> Presets { get; } =
        [BareStainless, CeramicCoated, WaterJacketed, Wrapped, Insulated];

    /// <summary>
    /// Resolve a preset from a model document's string. Matching is by any
    /// unambiguous prefix of the name, case-insensitively, so "wrapped",
    /// "Header wrap" and "wrap" all reach the same preset and a document
    /// stays readable.
    /// </summary>
    public static WallSurface ByName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        var matches = Presets
            .Where(p => p.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                        || trimmed.Contains(p.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        throw new ArgumentException(
            $"Unknown pipe surface treatment '{name}'. Choose one of: "
            + string.Join(", ", Presets.Select(p => p.Name)) + ".",
            nameof(name));
    }
}

/// <summary>How a <see cref="WallThermalModel"/> advances its temperature.</summary>
public enum WallUpdate
{
    /// <summary>
    /// Explicit integration every gas step. Physically direct, but a steel
    /// wall's time constant is on the order of ten seconds against a 20 ms
    /// cycle, so reaching the cyclic-steady temperature takes hundreds of
    /// cycles — far more than an engine run does.
    /// </summary>
    Transient,

    /// <summary>
    /// Hold the temperature fixed within a cycle and solve the cycle-average
    /// energy balance between cycles (plan §2.9: <i>"iterate wall temperatures
    /// to convergence across cycles"</i>).
    ///
    /// This is both faster and MORE faithful than transient integration for a
    /// steady operating point: the wall genuinely is near-isothermal over one
    /// cycle, and the answer no longer depends on the wall's heat capacity —
    /// which, being a guess about wall thickness, should not be setting the
    /// result.
    /// </summary>
    CyclicSteady,
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
        _sumH = new double[cellCount];
        _sumHT = new double[cellCount];
    }

    private readonly double[] _sumH;
    private readonly double[] _sumHT;
    private double _sumTime;

    /// <summary>
    /// Worst per-cell residual of the cycle-average balance at the temperature
    /// now IN EFFECT, W/m².
    ///
    /// One definition serving two purposes. For a wall free to float it is a
    /// check on the solve: Newton drives it to essentially zero, and anything
    /// else means the temperature adopted does not satisfy the balance it was
    /// solved from. For a <see cref="FixedTemperature"/> wall it is the net
    /// heat the user's imposed temperature is pushing through the wall — the
    /// honest measure of how far that imposition sits from equilibrium.
    /// </summary>
    public double LastResidual { get; private set; }

    public WallSurface Surface { get; }

    public double AmbientTemperature { get; set; }

    /// <summary>Wall temperature per cell, K.</summary>
    public double[] Temperature { get; }

    public WallUpdate Mode { get; set; } = WallUpdate.Transient;

    /// <summary>
    /// Hold the wall at its initial temperature: a water jacket, or a measured
    /// wall the user is imposing rather than predicting. Accumulation still
    /// happens so <see cref="LastResidual"/> reports how far the imposed
    /// temperature is from the balance.
    /// </summary>
    public bool FixedTemperature { get; set; }

    /// <summary>
    /// Largest per-cell change at the last <see cref="SolveCyclicSteady"/>, K.
    /// The engine's convergence loop watches this: a converged gas state over
    /// a wall still marching toward its own temperature is not a converged
    /// operating point.
    /// </summary>
    public double LastChange { get; private set; } = double.PositiveInfinity;

    /// <summary>Combined external conductance including the surface resistance, W/(m²·K).</summary>
    public double ExternalConductance =>
        1.0 / (1.0 / _externalCoefficient + Surface.ExternalResistance);

    public void Update(double dt, ReadOnlySpan<double> innerHeatTransferCoefficient, ReadOnlySpan<double> gasTemperature)
    {
        if (Mode == WallUpdate.CyclicSteady)
        {
            Accumulate(dt, innerHeatTransferCoefficient, gasTemperature);
            return;
        }

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

    /// <summary>
    /// Accumulate ∫h dt and ∫h·T_gas dt. Both are needed separately, and
    /// neither can be reconstructed from a mean of the other: h swings by
    /// orders of magnitude across a cycle, so the gas temperature that matters
    /// is the one weighted by how hard the gas was scrubbing at the time.
    /// </summary>
    private void Accumulate(double dt, ReadOnlySpan<double> h, ReadOnlySpan<double> gasTemperature)
    {
        for (var i = 0; i < Temperature.Length; i++)
        {
            _sumH[i] += h[i] * dt;
            _sumHT[i] += h[i] * gasTemperature[i] * dt;
        }

        _sumTime += dt;
    }

    /// <summary>
    /// Solve each cell's cycle-average steady balance for the wall
    /// temperature and adopt it, returning the largest change in K.
    ///
    ///   h̄·(T̄_gas − T_w) = U_out·(T_w − T_amb) + εσ·(T_w⁴ − T_amb⁴)
    ///
    /// where h̄ = (1/Δt)∫h dt and h̄·T̄_gas = (1/Δt)∫h·T_gas dt. Because T_w is
    /// constant over the cycle, that substitution is exact rather than a
    /// linearisation.
    ///
    /// The left side falls and the right side rises with T_w, so the residual
    /// is strictly decreasing and Newton from the current temperature cannot
    /// wander off. Call once per cycle; the accumulators reset.
    /// </summary>
    public double SolveCyclicSteady()
    {
        if (_sumTime <= 0)
        {
            return LastChange = 0.0;
        }

        var uOut = ExternalConductance;
        var emissivity = Surface.Emissivity * StefanBoltzmann;
        var ambient4 = Math.Pow(AmbientTemperature, 4);
        var change = 0.0;
        var worstResidual = 0.0;

        double Residual(double tw, double hBar, double hTBar) =>
            hTBar - (hBar * tw) - (uOut * (tw - AmbientTemperature))
            - (emissivity * ((tw * tw * tw * tw) - ambient4));

        for (var i = 0; i < Temperature.Length; i++)
        {
            var hBar = _sumH[i] / _sumTime;
            var hTBar = _sumHT[i] / _sumTime;

            // A cell that saw no flow at all has nothing to say about where
            // the wall should sit; leave it where it is rather than dragging
            // it to ambient on no evidence.
            if (hBar <= 1e-12)
            {
                continue;
            }

            var tw = Temperature[i];
            for (var iteration = 0; iteration < 40; iteration++)
            {
                var residual = Residual(tw, hBar, hTBar);
                var slope = -hBar - uOut - (4.0 * emissivity * tw * tw * tw);
                var step = residual / slope;
                tw -= step;

                // The wall balances between two reservoirs, so the root always
                // lies between ambient and the flow-weighted gas mean —
                // whichever way round those happen to be. Bracketing on both
                // is exact, and it stops a wild first step taking the quartic
                // negative. Clamping the low side at ambient instead would
                // silently pin an intake wall whose gas genuinely runs
                // sub-ambient after expanding down the runner.
                var gasMean = hTBar / hBar;
                tw = Math.Clamp(
                    tw,
                    Math.Min(AmbientTemperature, gasMean),
                    Math.Max(AmbientTemperature, gasMean));

                if (Math.Abs(step) < 1e-9)
                {
                    break;
                }
            }

            change = Math.Max(change, Math.Abs(tw - Temperature[i]));
            if (!FixedTemperature)
            {
                Temperature[i] = tw;
            }

            // At the temperature now in effect — the solved one, or the
            // imposed one when the wall is held.
            worstResidual = Math.Max(worstResidual, Math.Abs(Residual(Temperature[i], hBar, hTBar)));
        }

        Array.Clear(_sumH);
        Array.Clear(_sumHT);
        _sumTime = 0.0;
        LastResidual = worstResidual;

        return LastChange = FixedTemperature ? 0.0 : change;
    }
}
