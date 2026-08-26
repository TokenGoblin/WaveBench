using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveBench.Acoustics.Metrics;

/// <summary>
/// One measurement point in a noise-test procedure: the condition, the
/// limit, and how it is measured.
/// </summary>
public sealed record NoiseLimitPoint
{
    public required string Name { get; set; }

    public required double LimitDb { get; set; }

    public string Weighting { get; set; } = "C";

    public string TimeWeighting { get; set; } = "Fast";

    /// <summary>Fixed test speed in rpm, or null when derived (see <see cref="NoiseRuleSet.PistonSpeedLimit"/>).</summary>
    public double? Rpm { get; set; }

    /// <summary>"Idle" or "PistonSpeed" — how the test speed is established.</summary>
    public string SpeedBasis { get; set; } = "Idle";
}

/// <summary>
/// A versioned noise rule set (plan §3.8: "treat every limit as versioned
/// rules data in a JSON file, never a constant — they change annually").
/// Every compliance report records which rules year it was judged against.
/// </summary>
public sealed record NoiseRuleSet
{
    public required string Name { get; set; }

    /// <summary>Rules year — printed on every report.</summary>
    public required int Year { get; set; }

    /// <summary>Where the numbers came from, so a user can re-check them.</summary>
    public required string Source { get; set; }

    public required List<NoiseLimitPoint> Points { get; set; }

    /// <summary>Microphone distance, m.</summary>
    public double MicrophoneDistanceM { get; set; } = 0.5;

    /// <summary>Microphone angle from the outlet axis, degrees.</summary>
    public double MicrophoneAngleDeg { get; set; } = 45.0;

    /// <summary>
    /// Mean piston speed defining the high-speed test point, m/s
    /// (FSAE: 15.25 m/s). Null when the rule set has no derived speed.
    /// </summary>
    public double? PistonSpeedLimit { get; set; }

    /// <summary>Rounding applied to the derived test speed, rpm (FSAE: nearest 500).</summary>
    public double TestSpeedRoundingRpm { get; set; } = 500.0;

    /// <summary>
    /// Derived test speed from the stroke (plan §3.8):
    /// N_test = piston-speed-limit × 30000 / stroke_mm, rounded to the
    /// nearest <see cref="TestSpeedRoundingRpm"/>.
    /// </summary>
    public double TestSpeedRpm(double strokeMm)
    {
        if (PistonSpeedLimit is not { } limit)
        {
            throw new InvalidOperationException($"Rule set '{Name}' has no piston-speed-derived test point.");
        }

        var raw = limit * 30_000.0 / strokeMm;
        return Math.Round(raw / TestSpeedRoundingRpm) * TestSpeedRoundingRpm;
    }

    public string Save() => JsonSerializer.Serialize(this, RulesJsonContext.Default.NoiseRuleSet);

    public static NoiseRuleSet Load(string json) =>
        JsonSerializer.Deserialize(json, RulesJsonContext.Default.NoiseRuleSet)
        ?? throw new InvalidDataException("Rule set deserialised to null.");

    /// <summary>
    /// Formula SAE / Formula Student static noise test (plan §3.8).
    /// <b>Verify against the live rulebook before a competition</b> — these
    /// change annually and the report records the year for exactly that
    /// reason. Shipped as a starting point, not an authority.
    /// </summary>
    public static NoiseRuleSet FormulaSae2024 { get; } = new()
    {
        Name = "Formula SAE static noise test",
        Year = 2024,
        Source = "FSAE rules IN9 (static noise); measurement per ISO 5130. VERIFY against the live rulebook.",
        MicrophoneDistanceM = 0.5,
        MicrophoneAngleDeg = 45.0,
        PistonSpeedLimit = 15.25,
        TestSpeedRoundingRpm = 500.0,
        Points =
        [
            new NoiseLimitPoint { Name = "Idle", LimitDb = 103.0, Weighting = "C", TimeWeighting = "Fast", SpeedBasis = "Idle" },
            new NoiseLimitPoint { Name = "Test speed", LimitDb = 110.0, Weighting = "C", TimeWeighting = "Fast", SpeedBasis = "PistonSpeed" },
        ],
    };

    /// <summary>SAE J1287-style stationary reference (plan §3.8).</summary>
    public static NoiseRuleSet SaeJ1287 { get; } = new()
    {
        Name = "SAE J1287 stationary",
        Year = 2024,
        Source = "SAE J1287 measurement geometry; limit is user-defined by jurisdiction.",
        MicrophoneDistanceM = 0.5,
        MicrophoneAngleDeg = 45.0,
        Points =
        [
            new NoiseLimitPoint { Name = "Stationary", LimitDb = 96.0, Weighting = "A", TimeWeighting = "Fast", SpeedBasis = "Idle" },
        ],
    };
}

/// <summary>Result at one measurement point, with the honesty band the plan requires.</summary>
public sealed record ComplianceResult(
    string PointName,
    double MeasuredDb,
    double LimitDb,
    double UncertaintyDb,
    double? TestSpeedRpm)
{
    /// <summary>Positive = under the limit.</summary>
    public double MarginDb => LimitDb - MeasuredDb;

    /// <summary>Best-estimate verdict — see <see cref="Verdict"/> for the honest one.</summary>
    public bool PassesNominally => MeasuredDb <= LimitDb;

    /// <summary>
    /// The verdict the UI must show. With a ±U band, a margin smaller than U
    /// is not a pass or a fail — it is "too close to call", and the plan is
    /// explicit that no student should fail scrutineering because the
    /// software sounded confident (§3.8).
    /// </summary>
    public ComplianceVerdict Verdict =>
        MarginDb > UncertaintyDb ? ComplianceVerdict.Pass
        : MarginDb < -UncertaintyDb ? ComplianceVerdict.Fail
        : ComplianceVerdict.TooCloseToCall;

    public string Describe() =>
        $"{PointName}: {MeasuredDb:F1} ±{UncertaintyDb:F1} dB vs {LimitDb:F0} dB limit " +
        $"→ margin {MarginDb:+0.0;-0.0} dB [{Verdict}]" +
        (TestSpeedRpm is { } rpm ? $" @ {rpm:F0} rpm" : "");
}

public enum ComplianceVerdict
{
    Pass,
    TooCloseToCall,
    Fail,
}

/// <summary>
/// Evaluates a model against a rule set (plan §3.8).
///
/// <b>Honesty requirement, implemented rather than described:</b> absolute
/// SPL prediction from a 1D code is good to roughly ±3 dB at best and worse
/// for broadband content, so every result carries an uncertainty band and a
/// three-way verdict. The tool predicts DIFFERENCES between designs far
/// better than absolute compliance, and the API makes that hard to forget.
/// </summary>
public static class ComplianceCheck
{
    /// <summary>Default uncertainty for tonal-dominated exhaust prediction, dB (plan §3.8).</summary>
    public const double DefaultUncertaintyDb = 3.0;

    /// <summary>Broadband-dominated content is worse; the plan says so explicitly.</summary>
    public const double BroadbandUncertaintyDb = 5.0;

    /// <summary>
    /// Evaluate every point in the rule set. <paramref name="levelAt"/>
    /// supplies the measured level (dB, in the point's weighting) for a given
    /// engine speed — normally the auralisation chain at the rule set's
    /// microphone geometry.
    /// </summary>
    public static IReadOnlyList<ComplianceResult> Evaluate(
        NoiseRuleSet rules,
        double strokeMm,
        double idleRpm,
        Func<double, NoiseLimitPoint, double> levelAt,
        double uncertaintyDb = DefaultUncertaintyDb)
    {
        var results = new List<ComplianceResult>();
        foreach (var point in rules.Points)
        {
            var rpm = point.SpeedBasis switch
            {
                "Idle" => point.Rpm ?? idleRpm,
                "PistonSpeed" => rules.TestSpeedRpm(strokeMm),
                _ => point.Rpm ?? throw new InvalidOperationException(
                    $"Point '{point.Name}' has no way to establish its test speed."),
            };

            results.Add(new ComplianceResult(point.Name, levelAt(rpm, point), point.LimitDb, uncertaintyDb, rpm));
        }

        return results;
    }

    /// <summary>The governing point: the smallest margin decides (plan §3.8, "highest reading governs").</summary>
    public static ComplianceResult Governing(IReadOnlyList<ComplianceResult> results) =>
        results.MinBy(r => r.MarginDb)
        ?? throw new ArgumentException("No results to judge.", nameof(results));
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NoiseRuleSet))]
public partial class RulesJsonContext : JsonSerializerContext;
