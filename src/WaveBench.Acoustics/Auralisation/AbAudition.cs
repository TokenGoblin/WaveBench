namespace WaveBench.Acoustics.Auralisation;

/// <summary>How the audition blends across a switch.</summary>
public enum CrossfadeLaw
{
    /// <summary>
    /// Gains sum to one. Correct for CORRELATED sources — which two renders of
    /// the same engine at the same speed are, since both are phase-locked to
    /// the same crank.
    /// </summary>
    EqualGain,

    /// <summary>
    /// Squared gains sum to one. Correct for uncorrelated sources; on
    /// correlated ones it lifts the level by up to 3 dB mid-fade.
    /// </summary>
    EqualPower,
}

/// <summary>
/// A gapless, loudness-matched A/B audition (plan §8.4: <i>"A/B is one
/// keystroke, gapless, level-matched"</i>).
///
/// <b>Why both halves matter.</b> Two exhaust designs almost never render at
/// the same loudness, and a listener asked to choose between them will pick
/// the louder one essentially every time — so an unmatched A/B does not
/// compare the designs, it measures which one is louder. Matching removes
/// that, and the true level difference is reported separately rather than
/// discarded, because how loud a design is remains a real fact about it.
///
/// Gapless means the switch keeps the playback POSITION: the two streams run
/// on one clock and switching swaps which is audible. Restarting B from zero
/// would compare a different part of the cycle, and a listener would hear the
/// restart rather than the design.
/// </summary>
public sealed class AbAudition
{
    private readonly float[] _a;
    private readonly float[] _b;

    /// <param name="a">First design.</param>
    /// <param name="b">Second design.</param>
    /// <param name="targetLufs">Loudness both are brought to.</param>
    public AbAudition(AudioStem a, AudioStem b, double targetLufs = -23.0)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (Math.Abs(a.SampleRate - b.SampleRate) > 1e-9)
        {
            throw new ArgumentException(
                $"A and B must share a sample rate; got {a.SampleRate} and {b.SampleRate}.", nameof(b));
        }

        if (a.Samples.Length != b.Samples.Length)
        {
            // Not a convenience check. A switch maps one playback position
            // onto both streams, so unequal lengths mean the position means
            // two different things and the comparison is no longer of the same
            // moment in the cycle.
            throw new ArgumentException(
                $"A and B must be the same length to be auditioned against each other; "
                + $"got {a.Samples.Length} and {b.Samples.Length} samples.", nameof(b));
        }

        var (matchedA, matchedB, gainA, gainB, difference) = Loudness.MatchPair(a, b, targetLufs);

        A = matchedA;
        B = matchedB;
        _a = matchedA.Samples;
        _b = matchedB.Samples;
        GainDbA = gainA;
        GainDbB = gainB;
        TrueDifferenceLu = difference;
        SampleRate = a.SampleRate;
    }

    public AudioStem A { get; }

    public AudioStem B { get; }

    public double SampleRate { get; }

    public int Length => _a.Length;

    /// <summary>Gain applied to A to reach the target, dB.</summary>
    public double GainDbA { get; }

    public double GainDbB { get; }

    /// <summary>
    /// How much louder A actually was than B before matching, in loudness
    /// units. Kept and shown, because "they sound the same loudness now" is a
    /// property of the audition, not of the designs.
    /// </summary>
    public double TrueDifferenceLu { get; }

    /// <summary>Crossfade length, milliseconds. Long enough to kill the click, short enough to feel instant.</summary>
    public double CrossfadeMs { get; set; } = 5.0;

    public CrossfadeLaw Law { get; set; } = CrossfadeLaw.EqualGain;

    /// <summary>
    /// Render a switch timeline into one continuous stem.
    ///
    /// The output is the same length as the inputs, sample for sample: a
    /// switch consumes no time and inserts none. That is what "gapless" has to
    /// mean for a comparison — the alternative is a listener hearing the join
    /// instead of the difference.
    /// </summary>
    /// <param name="switchesAtSeconds">
    /// Times at which to swap sources, in order. Playback starts on A.
    /// </param>
    /// <param name="name">Name for the rendered stem.</param>
    public AudioStem Render(IReadOnlyList<double> switchesAtSeconds, string name = "A/B")
    {
        ArgumentNullException.ThrowIfNull(switchesAtSeconds);

        var output = new float[_a.Length];
        var fadeSamples = Math.Max(1, (int)Math.Round(CrossfadeMs * 1e-3 * SampleRate));

        // Which source is live at each sample, resolved once.
        var onB = false;
        var next = 0;
        var switchAt = switchesAtSeconds
            .Select(t => Math.Clamp((int)Math.Round(t * SampleRate), 0, _a.Length))
            .OrderBy(s => s)
            .ToList();

        for (var i = 0; i < output.Length; i++)
        {
            while (next < switchAt.Count && i >= switchAt[next])
            {
                onB = !onB;
                next++;
            }

            var from = onB ? _b : _a;

            // Inside a fade window that started at the most recent switch?
            var sinceSwitch = next > 0 ? i - switchAt[next - 1] : int.MaxValue;
            if (sinceSwitch >= 0 && sinceSwitch < fadeSamples)
            {
                var to = onB ? _b : _a;
                var away = onB ? _a : _b;
                var t = (sinceSwitch + 1.0) / fadeSamples;
                var (gTo, gAway) = Gains(t);
                output[i] = (float)((to[i] * gTo) + (away[i] * gAway));
                continue;
            }

            output[i] = from[i];
        }

        return new AudioStem(name, output, SampleRate);
    }

    /// <summary>
    /// Which source is audible at a time, for the UI's indicator. Playback
    /// starts on A and each switch toggles.
    /// </summary>
    public bool IsOnB(double seconds, IReadOnlyList<double> switchesAtSeconds) =>
        switchesAtSeconds.Count(s => s <= seconds) % 2 == 1;

    private (double To, double Away) Gains(double t) => Law switch
    {
        CrossfadeLaw.EqualPower => (Math.Sqrt(t), Math.Sqrt(1.0 - t)),
        _ => (t, 1.0 - t),
    };
}
