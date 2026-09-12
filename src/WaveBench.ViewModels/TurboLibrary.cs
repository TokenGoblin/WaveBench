using WaveBench.Boost;

namespace WaveBench.ViewModels;

/// <summary>
/// The turbochargers the app ships with (plan §8.3 Library → Turbos).
///
/// <b>Every map here is ANALYTIC, and that is a rule rather than a stopgap.</b>
/// Plan §4.7: <i>"Ship no manufacturer maps without written permission."</i> A
/// shipped library of digitised datasheets would make the whole application
/// un-redistributable, so these are closed-form surfaces in the size classes a
/// builder chooses between, each entry declaring exactly that in its
/// <see cref="TurboEntry.Source"/> and <see cref="TurboEntry.Licence"/>. They
/// are representative, not any real product, and the Boost workspace says so
/// on screen rather than leaving a user to assume otherwise.
///
/// The real library is the user's: <see cref="TurboDatabase"/> loads
/// contributed entries, and the map digitiser (plan §4.7) turns a JPEG of a
/// datasheet into one. This exists so the workspace has something to draw
/// before that happens.
///
/// <b>Not shared with the verification suite's own synthetic turbo.</b> That
/// one is an independent anchor for testing the map readers; using product
/// data to verify product code would make the test agree with whatever the
/// code does.
/// </summary>
public static class TurboLibrary
{
    /// <summary>The size the analytic family is parameterised about: a 60 mm compressor inducer.</summary>
    private const double ReferenceInducerMm = 60.0;

    /// <summary>Maximum shaft speed of the reference size, rpm. Tip speed is the limit, so N ∝ 1/D.</summary>
    private const double ReferenceMaxRpm = 165_000.0;

    /// <summary>Rotating inertia of the reference size, kg·m². Geometric similarity gives J ∝ D⁵.</summary>
    private const double ReferenceInertia = 3.1e-6;

    /// <summary>Corrected flow the reference turbine swallows at ER = 3, kg/s.</summary>
    private const double ReferenceTurbineChokedFlow = 0.235;

    public static IReadOnlyList<TurboEntry> Entries { get; } =
    [
        Build(35, 0.55, "Restricted class (FSAE): a 20 mm restrictor chokes near 0.07 kg/s, and a unit sized "
                        + "for it runs its whole operating line inside a map this small."),
        Build(46, 0.64, "1.4–1.8 L road engine: spools early, runs out of compressor above about 200 kW."),
        Build(54, 0.72, "1.8–2.2 L: the size most 2-litre builds land on."),
        Build(62, 0.82, "2.5–3.0 L, or a 2-litre chasing top-end power at the cost of response."),
        Build(71, 0.96, "Large single: high flow, late onset. Draws the opposite end of the spool/power trade."),
    ];

    public static IReadOnlyList<string> Names { get; } = Entries.Select(e => e.Turbo.Name).ToList();

    public static TurboEntry? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Entries.FirstOrDefault(e => string.Equals(e.Turbo.Name, name, StringComparison.OrdinalIgnoreCase))
              ?? Entries.FirstOrDefault(e => e.Turbo.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static Turbocharger? TurboFor(string? name) => Find(name)?.Turbo;

    /// <summary>
    /// Load the shipped entries into a database, so auto-match and the
    /// user's own contributed turbos are ranked by the same code against the
    /// same demand curve.
    /// </summary>
    public static TurboDatabase AsDatabase()
    {
        var database = new TurboDatabase();
        foreach (var entry in Entries)
        {
            database.Add(entry);
        }

        return database;
    }

    // ---- The analytic family ----------------------------------------------

    /// <summary>
    /// One size of the family, scaled from the reference by geometric
    /// similarity: flow with the inducer AREA, speed with 1/D so tip speed
    /// (and therefore pressure ratio) is preserved, inertia with D⁵.
    /// </summary>
    private static TurboEntry Build(double inducerMm, double areaRatio, string note)
    {
        var flowScale = Math.Pow(inducerMm / ReferenceInducerMm, 2.0);
        var speedScale = ReferenceInducerMm / inducerMm;
        var name = $"Analytic {inducerMm:F0} mm";

        var turbo = new Turbocharger
        {
            Name = name,
            Compressor = Compressor(name, flowScale, speedScale),
            Turbine = Turbine(name, flowScale, speedScale, areaRatio, inducerMm),
            ShaftInertia = ReferenceInertia * Math.Pow(inducerMm / ReferenceInducerMm, 5.0),
            MechanicalEfficiency = 0.97,
            MaxTurbineInletK = 1223.15,
            Provenance = "Analytic surface — not a product. See TurboLibrary.",
        };

        return new TurboEntry
        {
            Turbo = turbo,
            Source = "Generated analytically by WaveBench (TurboLibrary) — not measured, not a manufacturer map.",
            Licence = "Part of WaveBench; redistributable because nothing in it was measured by anyone else.",
            Tags = [$"{inducerMm:F0} mm", $"A/R {areaRatio:F2}", "analytic", note],
        };
    }

    /// <summary>
    /// Speed lines of the reference compressor, scaled.
    ///
    /// The shape is chosen to be right rather than to be pretty: pressure
    /// ratio goes as N² because Euler work goes as tip speed squared; the
    /// line is nearly flat at the surge end and steepens toward choke, which
    /// is what a centrifugal characteristic does; and the efficiency field is
    /// a quadratic island, which is what one looks like near its own peak.
    /// </summary>
    private static CompressorMap Compressor(string name, double flowScale, double speedScale)
    {
        double[] fractions = [0.50, 0.625, 0.75, 0.875, 1.00];
        const int pointsPerLine = 11;

        return new CompressorMap
        {
            Name = $"{name} compressor",
            Reference = MapReference.SaeJ1826,
            MaxSpeedRpm = ReferenceMaxRpm * speedScale,
            Provenance = "Analytic surface — not a product. See TurboLibrary.",
            SpeedLines = fractions.Select(f =>
            {
                var lo = SurgeFlow(f) * flowScale;
                var hi = ChokeFlow(f) * flowScale;

                var points = Enumerable.Range(0, pointsPerLine).Select(i =>
                {
                    var u = i / (pointsPerLine - 1.0);
                    var flow = lo + ((hi - lo) * u);
                    var pressureRatio = PressureRatio(f, u);
                    return new CompressorPoint(flow, pressureRatio, Efficiency(flow / flowScale, pressureRatio));
                }).ToList();

                return new CompressorSpeedLine(f * ReferenceMaxRpm * speedScale, points);
            }).ToList(),
        };
    }

    /// <summary>Surge-end corrected flow of a speed line, kg/s at the reference size.</summary>
    private static double SurgeFlow(double speedFraction) => 0.05 + (0.10 * speedFraction * speedFraction);

    /// <summary>Choke-end corrected flow, kg/s at the reference size.</summary>
    private static double ChokeFlow(double speedFraction) => 0.11 + (0.20 * speedFraction);

    /// <summary>Pressure ratio from the surge end (u = 0) to the choke end (u = 1) of a speed line.</summary>
    private static double PressureRatio(double speedFraction, double u)
    {
        var surge = 1.0 + (2.4 * speedFraction * speedFraction);
        return surge - ((surge - 1.0) * 0.45 * Math.Pow(Math.Max(0.0, u), 1.8));
    }

    /// <summary>The efficiency island: η = η_peak − k·ρ² about the peak, in reference-size flow.</summary>
    private static double Efficiency(double referenceFlow, double pressureRatio)
    {
        var a = (referenceFlow - 0.19) / 0.152;
        var b = (pressureRatio - 2.40) / 1.3448;
        var rho2 = (a * a) + (b * b);
        return Math.Clamp(0.80 - (0.10 * rho2), 0.35, 0.80);
    }

    /// <summary>
    /// The turbine: an orifice-like swallowing characteristic with a mild
    /// fall-off as wheel speed rises and the relative flow angle worsens, and
    /// a total-to-static efficiency peaking near the design blade-speed ratio.
    /// </summary>
    private static TurbineMap Turbine(string name, double flowScale, double speedScale, double areaRatio, double inducerMm)
    {
        double[] speeds = [40_000.0, 60_000.0, 80_000.0, 100_000.0];
        double[] expansionRatios = [1.2, 1.5, 1.8, 2.2, 2.6, 3.0, 3.5];

        return new TurbineMap
        {
            Name = $"{name} turbine, A/R {areaRatio:F2}",
            Reference = MapReference.SaeJ1826,
            AreaRatio = areaRatio,
            // Turbine wheels in a matched cartridge run a little smaller than
            // the compressor inducer; the ratio matters because BSR is read
            // off it (plan §4.3) and a guessed wheel size makes BSR a guess.
            RotorDiameterM = 0.92 * inducerMm / 1000.0,
            Provenance = "Analytic surface — not a product. See TurboLibrary.",
            SpeedLines = speeds.Select(n => new TurbineSpeedLine(
                n * speedScale,
                expansionRatios.Select(er => new TurbinePoint(
                    er,
                    TurbineFlow(n, er) * flowScale,
                    TurbineEfficiency(n, er))).ToList())).ToList(),
        };
    }

    private static double TurbineFlow(double correctedRpm, double expansionRatio)
    {
        var shape = Math.Sqrt(Math.Max(0.0, 1.0 - (1.0 / (expansionRatio * expansionRatio))))
                    / Math.Sqrt(1.0 - (1.0 / 9.0));
        return ReferenceTurbineChokedFlow * shape * (1.0 - (0.06 * ((correctedRpm / 80_000.0) - 0.5)));
    }

    private static double TurbineEfficiency(double correctedRpm, double expansionRatio)
    {
        var n = correctedRpm / 80_000.0;
        var eta = 0.72 - (0.10 * Math.Pow(expansionRatio - 2.0, 2.0)) - (0.15 * Math.Pow(n - 0.75, 2.0));
        return Math.Clamp(eta, 0.25, 0.78);
    }

    /// <summary>
    /// A turbine map re-housed at a different volute A/R.
    ///
    /// <b>A first-order scaling, and it is labelled as one everywhere it is
    /// used.</b> To first order a radial turbine's swallowing capacity is
    /// proportional to the volute's A/R — a bigger volute passes more flow at
    /// a given expansion ratio, which is why a large A/R spools late and holds
    /// the top end (Watson &amp; Janota, <i>Turbocharging the Internal
    /// Combustion Engine</i>, 1982, ch. 3; Baines, <i>Fundamentals of
    /// Turbocharging</i>, 2005). It is NOT a re-map: the efficiency field is
    /// carried across unchanged, when in reality a different housing moves the
    /// incidence angle and therefore the efficiency peak too. Valid for
    /// comparing housings in the same family, which is what plan §4.7's sweep
    /// asks for, and not for predicting an absolute number from a housing that
    /// was never measured. Fitted to no dataset.
    /// </summary>
    /// <param name="map">The measured (here, analytic) map.</param>
    /// <param name="areaRatio">The housing being considered, m.</param>
    public static TurbineMap Rehoused(TurbineMap map, double areaRatio)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(areaRatio);

        if (map.AreaRatio is not { } baseline || baseline <= 0 || Math.Abs(areaRatio - baseline) < 1e-9)
        {
            return map;
        }

        var scale = areaRatio / baseline;

        return map with
        {
            Name = $"{map.Name} → A/R {areaRatio:F2}",
            AreaRatio = areaRatio,
            Provenance = $"{map.Provenance} Capacity scaled ×{scale:F3} from A/R {baseline:F2} "
                         + "(first-order A/R scaling; efficiency field unchanged).",
            SpeedLines = map.SpeedLines
                .Select(l => new TurbineSpeedLine(
                    l.CorrectedRpm,
                    l.Points.Select(p => p with { CorrectedFlowKgPerS = p.CorrectedFlowKgPerS * scale }).ToList()))
                .ToList(),
        };
    }
}
