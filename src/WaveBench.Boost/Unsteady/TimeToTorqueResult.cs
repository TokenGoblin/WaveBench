namespace WaveBench.Boost.Unsteady;

/// <summary>
/// Time-to-90%-boost, time-to-90%-torque, and the sensitivity band a single
/// run cannot give (plan §4.7; Part 14 Gotcha #25: "Transient results depend
/// on inertia, friction and thermal states users rarely know accurately. Show
/// a sensitivity band, not a single number.").
///
/// The band is never an invented ± percentage: <see cref="Evaluate"/> runs
/// the SAME scripted transient three times — nominal, and at caller-supplied
/// fast/slow bounds on shaft inertia and bearing friction — and reports the
/// spread the physics itself produces.
/// </summary>
/// <param name="TimeTo90PercentBoostS">Nominal time to 90% of the run's boost rise.</param>
/// <param name="TimeTo90PercentTorqueS">Nominal time to 90% of the run's torque rise.</param>
/// <param name="TimeTo90PercentBoostLowS">Fastest boost-rise time across the two bound runs.</param>
/// <param name="TimeTo90PercentBoostHighS">Slowest boost-rise time across the two bound runs.</param>
/// <param name="TimeTo90PercentTorqueLowS">Fastest torque-rise time across the two bound runs.</param>
/// <param name="TimeTo90PercentTorqueHighS">Slowest torque-rise time across the two bound runs.</param>
public sealed record TimeToTorqueResult(
    double TimeTo90PercentBoostS,
    double TimeTo90PercentTorqueS,
    double TimeTo90PercentBoostLowS,
    double TimeTo90PercentBoostHighS,
    double TimeTo90PercentTorqueLowS,
    double TimeTo90PercentTorqueHighS)
{
    public double BoostBandWidthS => TimeTo90PercentBoostHighS - TimeTo90PercentBoostLowS;

    public double TorqueBandWidthS => TimeTo90PercentTorqueHighS - TimeTo90PercentTorqueLowS;

    /// <summary>
    /// Run a nominal driver plus two bound drivers (typically a fast bound —
    /// low inertia, low friction — and a slow bound — high inertia, high
    /// friction) under the same profile for the same duration, and reduce
    /// each run to its 90%-rise crossing times.
    /// </summary>
    /// <param name="buildNominal">Builds the driver for the best-estimate case.</param>
    /// <param name="buildBoundA">Builds the driver for one uncertainty bound.</param>
    /// <param name="buildBoundB">Builds the driver for the other uncertainty bound.</param>
    /// <param name="profile">The driving profile every run replays identically.</param>
    /// <param name="durationSeconds">How long each run advances before being reduced.</param>
    public static TimeToTorqueResult Evaluate(
        Func<TransientDriver> buildNominal,
        Func<TransientDriver> buildBoundA,
        Func<TransientDriver> buildBoundB,
        DrivingProfile profile,
        double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(buildNominal);
        ArgumentNullException.ThrowIfNull(buildBoundA);
        ArgumentNullException.ThrowIfNull(buildBoundB);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);

        var nominal = RunOne(buildNominal(), profile, durationSeconds);
        var boundA = RunOne(buildBoundA(), profile, durationSeconds);
        var boundB = RunOne(buildBoundB(), profile, durationSeconds);

        return new TimeToTorqueResult(
            nominal.BoostS,
            nominal.TorqueS,
            Math.Min(boundA.BoostS, boundB.BoostS),
            Math.Max(boundA.BoostS, boundB.BoostS),
            Math.Min(boundA.TorqueS, boundB.TorqueS),
            Math.Max(boundA.TorqueS, boundB.TorqueS));
    }

    private static (double BoostS, double TorqueS) RunOne(
        TransientDriver driver, DrivingProfile profile, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(driver);

        var samples = new List<TransientSample>();
        TransientSample sample;
        do
        {
            sample = driver.Advance(profile);
            samples.Add(sample);
        }
        while (sample.TimeSeconds < durationSeconds);

        return (CrossingTime(samples, s => s.BoostPressurePa), CrossingTime(samples, s => s.IndicatedTorqueNm));
    }

    private static double CrossingTime(IReadOnlyList<TransientSample> samples, Func<TransientSample, double> select)
    {
        var start = select(samples[0]);
        var end = select(samples[^1]);
        var threshold = start + (0.9 * (end - start));

        // A falling quantity (end < start) crosses DOWN through the
        // threshold; either way, this is the first sample past it.
        var found = end >= start
            ? samples.FirstOrDefault(s => select(s) >= threshold, samples[^1])
            : samples.FirstOrDefault(s => select(s) <= threshold, samples[^1]);

        return found.TimeSeconds;
    }
}
