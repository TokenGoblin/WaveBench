using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveBench.Boost;

/// <summary>
/// The conditions a map's corrected quantities are referred to.
///
/// <b>Required, never defaulted.</b> Plan §4.2 calls assuming these "a classic
/// silent 5% error", and it is right: manufacturers publish against 25 °C /
/// 100 kPa, 15 °C / 101.325 kPa, 20 °C / 98 kPa and others. Reading a map
/// referred to 298 K as though it were 288 K scales every corrected speed by
/// √(298/288) — 1.7% on speed, and more once it propagates through the
/// pressure ratio. A map file without reference conditions is not usable data,
/// so this type has no default and the loader refuses a map that omits it.
/// </summary>
/// <param name="TemperatureK">Reference total temperature.</param>
/// <param name="PressureKPa">Reference total pressure.</param>
public sealed record MapReference(double TemperatureK, double PressureKPa)
{
    /// <summary>SAE J1826's own reference: 25 °C, 100 kPa.</summary>
    public static MapReference SaeJ1826 { get; } = new(298.15, 100.0);

    /// <summary>ISO 1940 / "standard day": 15 °C, 101.325 kPa.</summary>
    public static MapReference StandardDay { get; } = new(288.15, 101.325);

    public void Validate(string what)
    {
        if (TemperatureK is <= 0 or > 400)
        {
            throw new InvalidDataException(
                $"{what}: reference temperature {TemperatureK} K is not a plausible gas-stand condition.");
        }

        if (PressureKPa is <= 0 or > 200)
        {
            throw new InvalidDataException(
                $"{what}: reference pressure {PressureKPa} kPa is not a plausible gas-stand condition.");
        }
    }
}

/// <summary>
/// SAE J1826 corrected quantities (plan §4.2).
///
/// Correction is what makes a map measured on one day usable on another: it
/// collapses the family of curves an inlet condition would otherwise produce
/// onto one set of speed lines.
/// </summary>
public static class Corrected
{
    /// <summary>ṁ_corr = ṁ·√(T₀₁/T_ref)/(p₀₁/p_ref).</summary>
    public static double Flow(double massFlow, double inletTemperatureK, double inletPressureKPa,
        MapReference reference) =>
        massFlow * Math.Sqrt(inletTemperatureK / reference.TemperatureK) / (inletPressureKPa / reference.PressureKPa);

    /// <summary>The inverse: actual mass flow from a corrected one.</summary>
    public static double ActualFlow(double correctedFlow, double inletTemperatureK, double inletPressureKPa,
        MapReference reference) =>
        correctedFlow / Math.Sqrt(inletTemperatureK / reference.TemperatureK) * (inletPressureKPa / reference.PressureKPa);

    /// <summary>N_corr = N/√(T₀₁/T_ref).</summary>
    public static double Speed(double rpm, double inletTemperatureK, MapReference reference) =>
        rpm / Math.Sqrt(inletTemperatureK / reference.TemperatureK);

    /// <summary>The inverse.</summary>
    public static double ActualSpeed(double correctedRpm, double inletTemperatureK, MapReference reference) =>
        correctedRpm * Math.Sqrt(inletTemperatureK / reference.TemperatureK);
}

/// <summary>One measured point on a compressor speed line.</summary>
/// <param name="CorrectedFlowKgPerS">Corrected mass flow.</param>
/// <param name="PressureRatio">Total-to-total pressure ratio.</param>
/// <param name="Efficiency">Isentropic total-to-total efficiency, 0–1.</param>
public sealed record CompressorPoint(double CorrectedFlowKgPerS, double PressureRatio, double Efficiency);

/// <summary>
/// One constant-corrected-speed line, ordered from the surge end (lowest flow)
/// to the choke end.
/// </summary>
public sealed record CompressorSpeedLine(double CorrectedRpm, IReadOnlyList<CompressorPoint> Points)
{
    public double SurgeFlow => Points[0].CorrectedFlowKgPerS;

    public double ChokeFlow => Points[^1].CorrectedFlowKgPerS;
}

/// <summary>
/// A compressor map (plan §4.2): speed lines in corrected quantities, with the
/// reference conditions they were measured against.
/// </summary>
public sealed record CompressorMap
{
    public required string Name { get; init; }

    public required MapReference Reference { get; init; }

    /// <summary>Speed lines, ascending in corrected speed.</summary>
    public required IReadOnlyList<CompressorSpeedLine> SpeedLines { get; init; }

    /// <summary>Maximum permitted shaft speed, rpm. Null when the datasheet does not state one.</summary>
    public double? MaxSpeedRpm { get; init; }

    /// <summary>Where the data came from — a datasheet, a digitised image, a gas stand.</summary>
    public string Provenance { get; init; } = "";

    public double LowestSpeed => SpeedLines[0].CorrectedRpm;

    public double HighestSpeed => SpeedLines[^1].CorrectedRpm;

    /// <summary>
    /// Structural checks. A map that interpolates wrongly is worse than one
    /// that is refused, because the wrongness surfaces as a plausible
    /// operating line.
    /// </summary>
    public void Validate()
    {
        Reference.Validate(Name);

        if (SpeedLines.Count < 2)
        {
            throw new InvalidDataException($"{Name}: a map needs at least two speed lines to interpolate between.");
        }

        for (var i = 0; i < SpeedLines.Count; i++)
        {
            var line = SpeedLines[i];

            if (i > 0 && line.CorrectedRpm <= SpeedLines[i - 1].CorrectedRpm)
            {
                throw new InvalidDataException(
                    $"{Name}: speed lines must ascend; {line.CorrectedRpm} follows {SpeedLines[i - 1].CorrectedRpm}.");
            }

            if (line.Points.Count < 2)
            {
                throw new InvalidDataException(
                    $"{Name}: the {line.CorrectedRpm:F0} rpm line has {line.Points.Count} point(s); at least two "
                    + "are needed to span it.");
            }

            for (var p = 1; p < line.Points.Count; p++)
            {
                if (line.Points[p].CorrectedFlowKgPerS <= line.Points[p - 1].CorrectedFlowKgPerS)
                {
                    throw new InvalidDataException(
                        $"{Name}: the {line.CorrectedRpm:F0} rpm line is not ordered by flow at index {p}. "
                        + "Points must run from the surge end to the choke end.");
                }
            }

            foreach (var point in line.Points)
            {
                if (point.PressureRatio < 1.0)
                {
                    throw new InvalidDataException(
                        $"{Name}: pressure ratio {point.PressureRatio:F3} is below 1 — a compressor cannot "
                        + "reduce pressure.");
                }

                if (point.Efficiency is <= 0 or > 1)
                {
                    throw new InvalidDataException(
                        $"{Name}: efficiency {point.Efficiency:F3} is outside (0, 1].");
                }
            }
        }
    }

    /// <summary>The surge line: the low-flow end of every speed line.</summary>
    public IReadOnlyList<(double Flow, double PressureRatio)> SurgeLine() =>
        SpeedLines.Select(l => (l.Points[0].CorrectedFlowKgPerS, l.Points[0].PressureRatio)).ToList();

    /// <summary>The choke line: the high-flow end of every speed line.</summary>
    public IReadOnlyList<(double Flow, double PressureRatio)> ChokeLine() =>
        SpeedLines.Select(l => (l.Points[^1].CorrectedFlowKgPerS, l.Points[^1].PressureRatio)).ToList();

    public string Save() => JsonSerializer.Serialize(this, BoostJson.Default.CompressorMap);

    public static CompressorMap Load(string json)
    {
        var map = JsonSerializer.Deserialize(json, BoostJson.Default.CompressorMap)
                  ?? throw new InvalidDataException("Compressor map deserialised to null.");
        map.Validate();
        return map;
    }
}

/// <summary>
/// One measured point on a turbine map.
/// </summary>
/// <param name="ExpansionRatio">Total-to-static expansion ratio p₀₃/p₄.</param>
/// <param name="CorrectedFlowKgPerS">Corrected mass flow.</param>
/// <param name="Efficiency">Total-to-static isentropic efficiency, 0–1.</param>
public sealed record TurbinePoint(double ExpansionRatio, double CorrectedFlowKgPerS, double Efficiency);

/// <summary>One constant-corrected-speed line of a turbine map, ascending in expansion ratio.</summary>
public sealed record TurbineSpeedLine(double CorrectedRpm, IReadOnlyList<TurbinePoint> Points);

/// <summary>
/// A turbine map (plan §4.3).
///
/// Turbine maps are published far more sparsely than compressor maps — often
/// two or three speed lines over a narrow expansion-ratio range — which is
/// exactly why extrapolation has to be physical rather than a spline
/// extension, and why the extrapolated region has to be visible.
/// </summary>
public sealed record TurbineMap
{
    public required string Name { get; init; }

    public required MapReference Reference { get; init; }

    public required IReadOnlyList<TurbineSpeedLine> SpeedLines { get; init; }

    /// <summary>Volute area ratio, m — the A/R a builder chooses between.</summary>
    public double? AreaRatio { get; init; }

    public string Provenance { get; init; } = "";

    public void Validate()
    {
        Reference.Validate(Name);

        if (SpeedLines.Count < 1)
        {
            throw new InvalidDataException($"{Name}: a turbine map needs at least one speed line.");
        }

        foreach (var line in SpeedLines)
        {
            if (line.Points.Count < 2)
            {
                throw new InvalidDataException(
                    $"{Name}: the {line.CorrectedRpm:F0} rpm line needs at least two points.");
            }

            for (var p = 1; p < line.Points.Count; p++)
            {
                if (line.Points[p].ExpansionRatio <= line.Points[p - 1].ExpansionRatio)
                {
                    throw new InvalidDataException(
                        $"{Name}: the {line.CorrectedRpm:F0} rpm line is not ordered by expansion ratio.");
                }
            }

            foreach (var point in line.Points)
            {
                if (point.ExpansionRatio < 1.0)
                {
                    throw new InvalidDataException(
                        $"{Name}: expansion ratio {point.ExpansionRatio:F3} is below 1 — a turbine cannot "
                        + "raise pressure.");
                }

                if (point.Efficiency is <= 0 or > 1)
                {
                    throw new InvalidDataException($"{Name}: efficiency {point.Efficiency:F3} is outside (0, 1].");
                }
            }
        }
    }

    public string Save() => JsonSerializer.Serialize(this, BoostJson.Default.TurbineMap);

    public static TurbineMap Load(string json)
    {
        var map = JsonSerializer.Deserialize(json, BoostJson.Default.TurbineMap)
                  ?? throw new InvalidDataException("Turbine map deserialised to null.");
        map.Validate();
        return map;
    }
}

/// <summary>
/// A turbocharger as the database holds it (plan §4.7): both maps, the
/// mechanical properties, and where the data came from.
/// </summary>
public sealed record Turbocharger
{
    public required string Name { get; init; }

    public required CompressorMap Compressor { get; init; }

    public required TurbineMap Turbine { get; init; }

    /// <summary>Rotating inertia, kg·m². Sets spool time; the single most important transient number.</summary>
    public double ShaftInertia { get; init; } = 3.0e-6;

    /// <summary>Mechanical efficiency of the bearing system at rated speed.</summary>
    public double MechanicalEfficiency { get; init; } = 0.97;

    /// <summary>Maximum permitted turbine inlet temperature, K.</summary>
    public double? MaxTurbineInletK { get; init; }

    public string Provenance { get; init; } = "";

    public void Validate()
    {
        Compressor.Validate();
        Turbine.Validate();

        if (ShaftInertia <= 0)
        {
            throw new InvalidDataException($"{Name}: shaft inertia must be positive.");
        }

        if (MechanicalEfficiency is <= 0 or > 1)
        {
            throw new InvalidDataException($"{Name}: mechanical efficiency is outside (0, 1].");
        }
    }

    public string Save() => JsonSerializer.Serialize(this, BoostJson.Default.Turbocharger);

    public static Turbocharger Load(string json)
    {
        var turbo = JsonSerializer.Deserialize(json, BoostJson.Default.Turbocharger)
                    ?? throw new InvalidDataException("Turbocharger deserialised to null.");
        turbo.Validate();
        return turbo;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompressorMap))]
[JsonSerializable(typeof(TurbineMap))]
[JsonSerializable(typeof(Turbocharger))]
public partial class BoostJson : JsonSerializerContext;
