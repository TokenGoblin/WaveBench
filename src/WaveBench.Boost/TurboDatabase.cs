using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveBench.Boost;

/// <summary>
/// A database entry: the turbo, plus who it came from and under what licence.
///
/// The licence field is not bureaucracy. Plan §4.7: <i>"Ship no manufacturer
/// maps without written permission."</i> The database is user-populated, so
/// every entry has to carry its own provenance or the library becomes
/// un-shippable the moment one anonymous contribution lands in it.
/// </summary>
public sealed record TurboEntry
{
    public required Turbocharger Turbo { get; init; }

    /// <summary>Where the data came from — a datasheet, a gas stand, a digitised image.</summary>
    public required string Source { get; init; }

    /// <summary>The licence the contributor asserts for it.</summary>
    public required string Licence { get; init; }

    /// <summary>Free-form: frame size, A/R, trim, anything a builder searches on.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public void Validate()
    {
        Turbo.Validate();

        if (string.IsNullOrWhiteSpace(Source))
        {
            throw new InvalidDataException(
                $"{Turbo.Name}: every database entry must record where its data came from.");
        }

        if (string.IsNullOrWhiteSpace(Licence))
        {
            throw new InvalidDataException(
                $"{Turbo.Name}: every database entry must record the licence it is contributed under. "
                + "An entry without one cannot be redistributed.");
        }
    }
}

/// <summary>The engine's demand at one speed — what a candidate turbo has to satisfy.</summary>
/// <param name="EngineRpm">Engine speed.</param>
/// <param name="AirFlowKgPerS">Air the engine draws at this speed and the target boost.</param>
/// <param name="ExhaustFlowKgPerS">Air plus fuel leaving it.</param>
/// <param name="TurbineInletK">Turbine inlet temperature.</param>
/// <param name="TargetPressureRatio">The boost being asked for.</param>
public sealed record BoostDemandPoint(
    double EngineRpm,
    double AirFlowKgPerS,
    double ExhaustFlowKgPerS,
    double TurbineInletK,
    double TargetPressureRatio);

/// <summary>
/// How one candidate scored, with every reason it scored that way exposed.
///
/// Plan §4.7 is explicit: <i>"Always show the top five with their trade-offs,
/// never a single 'best'."</i> So this type carries the margins and the misses,
/// not just a number — the ranking is a starting point for a judgement, not a
/// substitute for one.
/// </summary>
public sealed record MatchCandidate
{
    public required TurboEntry Entry { get; init; }

    /// <summary>The matched operating line, point for point with the demand.</summary>
    public required IReadOnlyList<(BoostDemandPoint Demand, MatchPoint Match)> OperatingLine { get; init; }

    /// <summary>Worst surge margin along the line, %. Negative means it surges.</summary>
    public required double WorstSurgeMargin { get; init; }

    /// <summary>Worst choke margin along the line, %.</summary>
    public required double WorstChokeMargin { get; init; }

    /// <summary>Flow-weighted mean compressor efficiency along the line.</summary>
    public required double MeanEfficiency { get; init; }

    /// <summary>Highest engine-out back-pressure ratio (exhaust ÷ intake) along the line.</summary>
    public required double WorstBackPressureRatio { get; init; }

    /// <summary>Lowest engine speed at which the target pressure ratio is reached. NaN if never.</summary>
    public required double BoostOnsetRpm { get; init; }

    /// <summary>Highest shaft speed as a fraction of the map's rated maximum. Null when the map does not state one.</summary>
    public required double? PeakSpeedFraction { get; init; }

    /// <summary>Reasons this candidate is disqualified outright. Empty means it is viable.</summary>
    public required IReadOnlyList<string> Disqualifications { get; init; }

    /// <summary>Points along the line that had to be read from extrapolated map regions.</summary>
    public required int ExtrapolatedPoints { get; init; }

    /// <summary>The ranking score. Higher is better; see <see cref="TurboDatabase.Rank"/> for the weights.</summary>
    public required double Score { get; init; }

    public bool Viable => Disqualifications.Count == 0;
}

/// <summary>
/// The turbo library and auto-match (plan §4.7).
/// </summary>
public sealed class TurboDatabase
{
    private readonly List<TurboEntry> _entries = [];

    public IReadOnlyList<TurboEntry> Entries => _entries;

    public void Add(TurboEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Validate();

        if (_entries.Any(e => string.Equals(e.Turbo.Name, entry.Turbo.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"A turbo named '{entry.Turbo.Name}' is already in the library.");
        }

        _entries.Add(entry);
    }

    public string Save() => JsonSerializer.Serialize(_entries, DatabaseJson.Default.ListTurboEntry);

    public static TurboDatabase Load(string json)
    {
        var entries = JsonSerializer.Deserialize(json, DatabaseJson.Default.ListTurboEntry)
                      ?? throw new InvalidDataException("Turbo database deserialised to null.");

        var db = new TurboDatabase();
        foreach (var entry in entries)
        {
            db.Add(entry);
        }

        return db;
    }

    /// <summary>
    /// Rank every turbo in the library against a demand curve.
    /// </summary>
    /// <param name="demand">The engine's requirement, ordered by speed.</param>
    /// <param name="ambientK">Compressor inlet temperature.</param>
    /// <param name="ambientKPa">Compressor inlet pressure. Below ambient behind an FSAE restrictor.</param>
    /// <param name="exhaustBackKPa">Pressure downstream of the turbine.</param>
    /// <param name="requiredSurgeMargin">
    /// Surge margin below which a candidate is disqualified rather than merely penalised, %.
    /// 10% is the usual industrial rule; it is a parameter because a race engine on a dyno
    /// and a road car in traffic do not deserve the same answer.
    /// </param>
    public IReadOnlyList<MatchCandidate> Rank(
        IReadOnlyList<BoostDemandPoint> demand,
        double ambientK = 298.15,
        double ambientKPa = 101.325,
        double exhaustBackKPa = 101.325,
        double requiredSurgeMargin = 10.0)
    {
        ArgumentNullException.ThrowIfNull(demand);

        if (demand.Count == 0)
        {
            throw new ArgumentException("A match needs at least one demand point.", nameof(demand));
        }

        return _entries
            .Select(e => Evaluate(e, demand, ambientK, ambientKPa, exhaustBackKPa, requiredSurgeMargin))
            .OrderByDescending(c => c.Viable)
            .ThenByDescending(c => c.Score)
            .ToList();
    }

    private static MatchCandidate Evaluate(
        TurboEntry entry,
        IReadOnlyList<BoostDemandPoint> demand,
        double ambientK, double ambientKPa, double exhaustBackKPa,
        double requiredSurgeMargin)
    {
        var line = new List<(BoostDemandPoint, MatchPoint)>(demand.Count);

        foreach (var point in demand)
        {
            line.Add((point, ShaftBalance.Match(
                entry.Turbo, point.AirFlowKgPerS, point.ExhaustFlowKgPerS,
                ambientK, ambientKPa, point.TurbineInletK, exhaustBackKPa)));
        }

        var worstSurge = line.Min(p => p.Item2.Compressor.SurgeMarginPercent);
        var worstChoke = line.Min(p => p.Item2.Compressor.ChokeMarginPercent);
        var worstBack = line.Max(p => p.Item2.ExpansionRatio / Math.Max(1e-6, p.Item2.Compressor.PressureRatio));
        var extrapolated = line.Count(p => p.Item2.Compressor.IsExtrapolated || p.Item2.Turbine.IsExtrapolated);

        var flowSum = line.Sum(p => p.Item1.AirFlowKgPerS);
        var meanEfficiency = flowSum > 0
            ? line.Sum(p => p.Item1.AirFlowKgPerS * p.Item2.Compressor.Efficiency) / flowSum
            : 0.0;

        var onset = line
            .Where(p => p.Item2.Compressor.PressureRatio >= p.Item1.TargetPressureRatio * 0.98)
            .Select(p => p.Item1.EngineRpm)
            .DefaultIfEmpty(double.NaN)
            .Min();

        var maxSpeed = entry.Turbo.Compressor.MaxSpeedRpm;
        var peakSpeed = line.Max(p => p.Item2.ShaftRpm);
        var speedFraction = maxSpeed is > 0 ? peakSpeed / maxSpeed : (double?)null;

        var disqualifications = new List<string>();

        if (worstSurge < requiredSurgeMargin)
        {
            disqualifications.Add(worstSurge < 0
                ? $"Surges: the operating line crosses the surge line by {-worstSurge:F1}%."
                : $"Surge margin falls to {worstSurge:F1}%, below the {requiredSurgeMargin:F0}% required.");
        }

        if (worstChoke < 0)
        {
            disqualifications.Add($"Chokes: the compressor runs {-worstChoke:F1}% past its choke line.");
        }

        if (speedFraction is > 1.0)
        {
            disqualifications.Add(
                $"Overspeeds: {peakSpeed:F0} rpm against a rated {maxSpeed:F0} rpm ({speedFraction:P0}).");
        }

        if (entry.Turbo.MaxTurbineInletK is { } maxTit && demand.Any(d => d.TurbineInletK > maxTit))
        {
            disqualifications.Add(
                $"Turbine inlet temperature exceeds the rated {maxTit:F0} K.");
        }

        if (line.Any(p => !p.Item2.Converged))
        {
            disqualifications.Add(
                "The shaft does not balance anywhere in the searched speed range at one or more points — the "
                + "turbine cannot drive this compressor against this demand.");
        }

        // The score. Efficiency dominates because it is what the match is FOR;
        // back-pressure is next because it is the cost the boost figure hides;
        // surge margin is a diminishing return past the requirement, so it is
        // capped rather than rewarded without limit — otherwise the ranking
        // prefers an oversized turbo that never spools.
        var score =
            (100.0 * meanEfficiency)
            - (40.0 * Math.Max(0.0, worstBack - 1.0))
            + (0.30 * Math.Min(worstSurge, 30.0))
            - (2.0 * extrapolated)
            - (double.IsNaN(onset) ? 25.0 : 0.0);

        return new MatchCandidate
        {
            Entry = entry,
            OperatingLine = line,
            WorstSurgeMargin = worstSurge,
            WorstChokeMargin = worstChoke,
            MeanEfficiency = meanEfficiency,
            WorstBackPressureRatio = worstBack,
            BoostOnsetRpm = onset,
            PeakSpeedFraction = speedFraction,
            Disqualifications = disqualifications,
            ExtrapolatedPoints = extrapolated,
            Score = score,
        };
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<TurboEntry>))]
public partial class DatabaseJson : JsonSerializerContext;
