namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// Parameters for the cosmetic mechanical stem. Every one is a user knob.
/// </summary>
/// <param name="ValveTrainLevel">Valve-seating tick amplitude, relative to the tonal stems.</param>
/// <param name="TimingDriveLevel">Chain/gear whine amplitude.</param>
/// <param name="InjectorLevel">Injector click amplitude; zero for a carburetted engine.</param>
/// <param name="TimingDriveTeeth">Teeth on the crank sprocket — sets the whine order.</param>
/// <param name="TickDecayMs">Decay of one mechanical tick.</param>
public sealed record MechanicalCharacter(
    double ValveTrainLevel = 0.05,
    double TimingDriveLevel = 0.02,
    double InjectorLevel = 0.015,
    int TimingDriveTeeth = 19,
    double TickDecayMs = 3.5)
{
    public static MechanicalCharacter None { get; } = new(0.0, 0.0, 0.0);

    /// <summary>A loose, mechanically noisy engine — a big air-cooled single.</summary>
    public static MechanicalCharacter Clattery { get; } = new(0.14, 0.02, 0.03, 19, 5.0);

    /// <summary>A tight modern engine: you hear the injectors more than the valves.</summary>
    public static MechanicalCharacter Refined { get; } = new(0.025, 0.015, 0.02, 23, 2.5);
}

/// <summary>
/// The mechanical stem (plan §3.6): valve seating, timing-drive whine and
/// injector clicks.
///
/// <b>THIS IS COSMETIC AND PREDICTS NOTHING.</b> It is a parametric layer
/// whose levels are user knobs, not a structural or contact model. Nothing in
/// WaveBench solves for valve-seating velocity, chain dynamics or injector
/// solenoid motion, so no number here can be a prediction — and the plan
/// requires it be labelled as such wherever it is offered. Its purpose is
/// realism in an audition: a rendered engine with no mechanical layer sounds
/// synthetic in a way that distracts from the gas dynamics being judged.
/// It is a separate stem precisely so it can be soloed or muted, and so it
/// can never contaminate a metric or a compliance figure.
///
/// What IS physical is the timing: events are placed on the crank angle from
/// the engine's own geometry, so valve ticks land at the right rate and the
/// timing-drive whine sits at the right order. Only the amplitudes are
/// invented.
/// </summary>
public static class MechanicalLayer
{
    /// <summary>
    /// Render the mechanical stem over an rpm profile.
    ///
    /// Event rates follow the four-stroke cycle: each cylinder seats its
    /// valves once per 720°, the timing drive turns at half crank speed, and
    /// injectors fire once per cycle per cylinder.
    /// </summary>
    public static AudioStem Render(
        RpmProfile profile,
        int cylinderCount,
        int valvesPerCylinder,
        double sampleRate,
        ulong seed,
        MechanicalCharacter? character = null)
    {
        var c = character ?? new MechanicalCharacter();
        var count = (int)Math.Round(profile.Duration * sampleRate);
        var output = new float[count];

        if (count == 0 || c == MechanicalCharacter.None)
        {
            return new AudioStem("mechanical", output, sampleRate);
        }

        var state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        var decaySamples = Math.Max(1.0, c.TickDecayMs * 1e-3 * sampleRate);
        var tickLength = (int)(decaySamples * 5);

        // Crank angle is integrated the same way the synthesiser does it, so
        // the mechanical events stay locked to the tonal stems.
        var angle = 0.0;
        var previousAngle = 0.0;
        var whinePhase = 0.0;

        // Valve and injector events, in crank degrees within the 720° cycle.
        var events = new List<(double Angle, double Level, double Pitch)>();
        for (var cyl = 0; cyl < cylinderCount; cyl++)
        {
            var phase = cyl * 720.0 / cylinderCount;
            for (var v = 0; v < Math.Max(1, valvesPerCylinder); v++)
            {
                // Exhaust valve seats near BDC-ish, intake near the end of
                // its event; spread multiple valves slightly so a four-valve
                // head does not sound like one hammer blow.
                events.Add((Wrap(phase + 380.0 + v * 4.0), c.ValveTrainLevel, 2600.0));
                events.Add((Wrap(phase + 590.0 + v * 4.0), c.ValveTrainLevel * 0.8, 3300.0));
            }

            events.Add((Wrap(phase + 300.0), c.InjectorLevel, 4700.0));
        }

        for (var i = 0; i < count; i++)
        {
            var time = i / sampleRate;
            var rpm = profile.RpmAt(time);
            previousAngle = angle;
            angle += 6.0 * rpm / sampleRate;

            // Timing-drive whine: camshaft turns at half crank speed, so the
            // meshing frequency is teeth × (rpm/60) / 2.
            if (c.TimingDriveLevel > 0.0)
            {
                var whineHz = c.TimingDriveTeeth * rpm / 60.0 / 2.0;
                whinePhase += 2.0 * Math.PI * whineHz / sampleRate;
                if (whinePhase > 2.0 * Math.PI)
                {
                    whinePhase -= 2.0 * Math.PI;
                }

                // Slightly impure, or it sounds like a test tone.
                output[i] += (float)(c.TimingDriveLevel
                    * (Math.Sin(whinePhase) + 0.3 * Math.Sin(2.0 * whinePhase)));
            }

            if (angle >= 720.0)
            {
                angle -= 720.0;
                previousAngle -= 720.0;
            }

            // Fire any event crossed this sample.
            foreach (var (eventAngle, level, pitch) in events)
            {
                if (!Crossed(previousAngle, angle, eventAngle))
                {
                    continue;
                }

                // Scatter amplitude a little so repeats do not sound looped.
                var jitter = 0.75 + 0.5 * NextUniform(ref state);
                Deposit(output, i, tickLength, level * jitter, pitch, decaySamples, sampleRate, ref state);
            }
        }

        return new AudioStem("mechanical", output, sampleRate);
    }

    private static double Wrap(double angle) => angle - Math.Floor(angle / 720.0) * 720.0;

    /// <summary>Did the crank sweep past this angle between two samples?</summary>
    private static bool Crossed(double from, double to, double target) => from < target && to >= target;

    /// <summary>One damped, band-limited tick: a pitched impulse plus noise.</summary>
    private static void Deposit(
        float[] output, int start, int length, double level, double pitchHz,
        double decaySamples, double sampleRate, ref ulong state)
    {
        for (var k = 0; k < length && start + k < output.Length; k++)
        {
            var envelope = Math.Exp(-k / decaySamples);
            var tone = Math.Sin(2.0 * Math.PI * pitchHz * k / sampleRate);
            var noise = NextUniform(ref state) * 2.0 - 1.0;
            output[start + k] += (float)(level * envelope * (0.6 * tone + 0.4 * noise));
        }
    }

    private static double NextUniform(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return ((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
    }
}
