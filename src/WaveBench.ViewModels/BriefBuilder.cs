using System.Globalization;
using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>Progress of the wizard's compute step.</summary>
/// <param name="Stage">What is happening.</param>
/// <param name="Completed">Evaluations finished.</param>
/// <param name="Total">Evaluations planned.</param>
/// <param name="BestSoFar">Objective value of the best design so far.</param>
public sealed record BriefProgress(string Stage, int Completed, int Total, double BestSoFar);

/// <summary>
/// The bounded optimisation and the Design Brief (plan §8.6, Phase 23).
///
/// <b>A search, not a lookup.</b> The plan is explicit about this, and the
/// reason is that the analytical seed is a single-degree-of-freedom estimate:
/// it places one resonance at one speed and knows nothing about how the intake
/// and exhaust interact, what the cam window does to either, or where the
/// packaging limit bites. The search is small — a coordinate sweep over four
/// variables around the seed — but it is a real evaluation of the real solver
/// at each point, which is why the brief's numbers match what Advanced mode
/// produces from the same model.
/// </summary>
public static class BriefBuilder
{
    /// <summary>
    /// Run the wizard's compute step: fill the model, search the free
    /// geometry, verify the winner, and write the brief.
    /// </summary>
    /// <param name="wizard">Answers to build from.</param>
    /// <param name="quick">
    /// Coarser mesh and fewer cycles. The plan budgets the whole brief at five
    /// minutes; a full-fidelity search would spend that on the search alone.
    /// The WINNER is always re-verified at full fidelity, so the accuracy of
    /// the reported numbers does not depend on this.
    /// </param>
    /// <param name="progress">Reports each evaluation, for the job tray.</param>
    /// <param name="cancellationToken">Accept-current-best at any point.</param>
    public static DesignBrief Build(
        Wizard wizard,
        bool quick = true,
        IProgress<BriefProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wizard);

        wizard.Apply();
        var document = wizard.Session.Document;

        var seed = wizard.SeedGeometry();
        var candidates = Candidates(wizard, seed);

        progress?.Report(new BriefProgress("seeding", 0, candidates.Count + 1, 0));

        // Score at the target speed only during the search: the sweep that
        // makes the brief's curves comes after, on the winner alone.
        var searchDocument = quick
            ? document with { Solver = document.Solver with { CellSizeMm = 14.0, MinCycles = 4, MaxCycles = 8 } }
            : document;

        var best = candidates[0];
        var bestScore = double.NegativeInfinity;
        var evaluated = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trial = Apply(searchDocument, candidate);
            var score = Score(trial, wizard);

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }

            evaluated++;
            progress?.Report(new BriefProgress(
                $"{candidate.IntakeLengthMm:F0} mm intake, {candidate.PrimaryLengthMm:F0} mm primary",
                evaluated, candidates.Count + 1, bestScore));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BriefProgress("verifying the winner", candidates.Count, candidates.Count + 1, bestScore));

        // Commit the winner to the model as an optimiser result, so it carries
        // the provenance that protects it from a later wizard re-run.
        wizard.Session.EditByOptimiser("IntakeRunner.LengthMm", best.IntakeLengthMm, "wizard");
        wizard.Session.EditByOptimiser("IntakeRunner.DiameterMm", best.IntakeDiameterMm, "wizard");
        wizard.Session.EditByOptimiser("ExhaustRunner.LengthMm", best.PrimaryLengthMm, "wizard");
        wizard.Session.EditByOptimiser("ExhaustRunner.DiameterMm", best.PrimaryDiameterMm, "wizard");

        var verified = wizard.Session.Document;
        var sweep = OperatingPointRunner.Sweep(verified, SweepPoints(wizard)).ToList();

        progress?.Report(new BriefProgress("done", candidates.Count + 1, candidates.Count + 1, bestScore));

        return Compose(wizard, verified, best, sweep, quick);
    }

    /// <summary>
    /// A preview brief with no solve at all — the plan's under-one-second
    /// first preview. Every line is the analytical seed, and every line says
    /// so: nothing here is presented at a confidence it has not earned.
    /// </summary>
    public static DesignBrief Preview(Wizard wizard)
    {
        ArgumentNullException.ThrowIfNull(wizard);

        var seed = wizard.SeedGeometry();
        var candidate = new Candidate(
            seed.IntakeLengthMm, seed.IntakeDiameterMm, seed.PrimaryLengthMm, seed.PrimaryDiameterMm);

        return Compose(wizard, wizard.Session.Document, candidate, [], quick: true, preview: true);
    }

    // ---- The search -------------------------------------------------------

    private readonly record struct Candidate(
        double IntakeLengthMm,
        double IntakeDiameterMm,
        double PrimaryLengthMm,
        double PrimaryDiameterMm);

    /// <summary>
    /// Candidates around the seed. A coordinate sweep rather than a full grid:
    /// four variables at five levels each is 625 solves, which does not fit
    /// the five-minute budget, and the interactions between runner length and
    /// primary length are weak enough that walking each axis about the seed
    /// finds the same neighbourhood.
    /// </summary>
    private static List<Candidate> Candidates(Wizard wizard, Wizard.SeedResult seed)
    {
        var list = new List<Candidate>
        {
            new(seed.IntakeLengthMm, seed.IntakeDiameterMm, seed.PrimaryLengthMm, seed.PrimaryDiameterMm),
        };

        foreach (var scale in new[] { 0.8, 0.9, 1.1, 1.25 })
        {
            var length = Math.Clamp(seed.IntakeLengthMm * scale, 80, wizard.PackagingLimitMm);
            list.Add(new Candidate(
                Math.Round(length), seed.IntakeDiameterMm, seed.PrimaryLengthMm, seed.PrimaryDiameterMm));
        }

        foreach (var scale in new[] { 0.85, 1.15 })
        {
            list.Add(new Candidate(
                seed.IntakeLengthMm,
                Math.Round(seed.IntakeDiameterMm * scale, 1),
                seed.PrimaryLengthMm,
                seed.PrimaryDiameterMm));
        }

        foreach (var scale in new[] { 0.8, 1.2 })
        {
            list.Add(new Candidate(
                seed.IntakeLengthMm,
                seed.IntakeDiameterMm,
                Math.Round(Math.Clamp(seed.PrimaryLengthMm * scale, 150, wizard.PackagingLimitMm * 1.4)),
                seed.PrimaryDiameterMm));
        }

        return list;
    }

    private static EngineModelDocument Apply(EngineModelDocument document, Candidate c) => document with
    {
        IntakeRunner = document.IntakeRunner with
        {
            LengthMm = c.IntakeLengthMm,
            DiameterMm = c.IntakeDiameterMm,
        },
        ExhaustRunner = document.ExhaustRunner with
        {
            LengthMm = c.PrimaryLengthMm,
            DiameterMm = c.PrimaryDiameterMm,
        },
    };

    /// <summary>
    /// Objective: area under the torque curve INSIDE the chosen band.
    ///
    /// Not peak torque, and not peak power. A design that makes one enormous
    /// number at one speed and nothing either side is worse to drive than a
    /// flatter one, and area under the curve across the band the user actually
    /// asked for is the quantity that says so. A knocking design scores zero
    /// however much torque it claims.
    /// </summary>
    private static double Score(EngineModelDocument document, Wizard wizard)
    {
        var points = SweepPoints(wizard);
        var results = OperatingPointRunner.Sweep(document, points);

        if (results.Any(r => r.KnockIntegral >= 1.0))
        {
            return 0.0;
        }

        var area = 0.0;
        for (var i = 1; i < results.Count; i++)
        {
            area += 0.5 * (results[i].TorqueNm + results[i - 1].TorqueNm) * (points[i] - points[i - 1]);
        }

        return area;
    }

    private static IReadOnlyList<double> SweepPoints(Wizard wizard)
    {
        var points = new List<double>();
        var step = Math.Max(250.0, (wizard.BandToRpm - wizard.BandFromRpm) / 4.0);
        for (var rpm = wizard.BandFromRpm; rpm <= wizard.BandToRpm + 1; rpm += step)
        {
            points.Add(rpm);
        }

        return points.Count >= 2 ? points : [wizard.BandFromRpm, wizard.BandToRpm];
    }

    // ---- The brief --------------------------------------------------------

    private static DesignBrief Compose(
        Wizard wizard,
        EngineModelDocument document,
        Candidate winner,
        IReadOnlyList<OperatingPointResult> sweep,
        bool quick,
        bool preview = false)
    {
        var target = wizard.TargetRpm();
        var lines = new List<BriefLine>();

        // Confidence is not decoration. A geometry that came out of a solved
        // search is a different claim from one that came out of a formula, and
        // the brief has to say which.
        var geometryConfidence = preview ? Confidence.Fair : Confidence.Good;
        var geometryBasis = preview
            ? "Analytical estimate only — no solve has run yet."
            : quick
                ? "Chosen by a search on the solver, then verified at full mesh."
                : "Chosen by a search on the solver at full mesh.";

        lines.Add(new BriefLine(
            "INTAKE", "Runner length", $"{winner.IntakeLengthMm:F0} mm",
            $"Puts the wave return at {target:F0} rpm, the part of your band the torque shape asks for.",
            geometryConfidence, geometryBasis));

        lines.Add(new BriefLine(
            "INTAKE", "Runner diameter", $"{winner.IntakeDiameterMm:F1} mm",
            "Keeps mean port velocity near 90 m/s at the tuned speed; larger flows better at the top and "
            + "loses the inertia that fills the midrange.",
            geometryConfidence, geometryBasis));

        lines.Add(new BriefLine(
            "EXHAUST", "Primary length", $"{winner.PrimaryLengthMm:F0} mm",
            "Brings the reflected expansion back to the port while the exhaust valve is still open, which "
            + "is what scavenges the cylinder.",
            geometryConfidence, geometryBasis));

        lines.Add(new BriefLine(
            "EXHAUST", "Primary diameter", $"{winner.PrimaryDiameterMm:F1} mm",
            "Small enough to keep the blowdown pulse strong, large enough not to raise pumping work at the "
            + "top of the band.",
            geometryConfidence, geometryBasis));

        var (intakeOpen, intakeClose, exhaustOpen, exhaustClose) = Wizard.CamEvents(wizard.Cam);
        var overlap = exhaustClose - intakeOpen;

        lines.Add(new BriefLine(
            "CAM", "Character", wizard.Cam.ToString(),
            $"{intakeClose - intakeOpen:F0}° intake duration with {overlap:F0}° of overlap.",
            Confidence.Rough,
            "Representative figures for this character, not a measured profile. Import a real cam and this "
            + "becomes a measurement."));

        lines.Add(new BriefLine(
            "CAM", "Overlap", $"{overlap:F0}°",
            overlap > 40
                ? "Wide: gives the exhaust wave a long window to pull on the intake, at the cost of idle "
                  + "quality and low-speed torque."
                : "Modest: keeps idle clean and low-speed torque intact, and limits how much the exhaust "
                  + "wave can help.",
            Confidence.Rough, "Follows from the cam character above."));

        lines.Add(new BriefLine(
            "HEAD", "Intake valve", $"{document.IntakeValves.HeadDiameterMm:F1} mm",
            "Valve area is the limit everything else works inside — no runner length rescues a head that "
            + "cannot flow.",
            wizard.IntakeValveMm > 0 ? Confidence.Good : Confidence.Rough,
            wizard.IntakeValveMm > 0
                ? "As entered."
                : "Derived from the bore at 0.40 × B. Measure the real head and this becomes exact."));

        var predictions = new List<Prediction>();
        var caveats = new List<string>();

        if (sweep.Count > 0)
        {
            var peakTorque = sweep.MaxBy(p => p.TorqueNm)!;
            var peakPower = sweep.MaxBy(p => p.PowerW)!;

            // The band is wide and honest. §6.2 has the model 5–10% out
            // against measured data on the cases validated so far, and the
            // generic Cd map is the largest single contributor.
            const double band = 0.08;

            predictions.Add(new Prediction("Peak torque", peakTorque.TorqueNm, "N·m", band, peakTorque.Rpm));
            predictions.Add(new Prediction("Peak power", peakPower.PowerW / 1000.0, "kW", band, peakPower.Rpm));

            var worstKnock = sweep.Max(p => p.KnockIntegral);
            if (worstKnock >= 1.0)
            {
                caveats.Add($"This design reaches the knock integral ({worstKnock:F2}) inside your band. "
                            + "Lower the compression, richen the mixture, or retard the spark before building it.");
            }
        }
        else
        {
            caveats.Add("No solve has run yet — these are analytical estimates and carry no performance "
                        + "prediction. Press Compute for numbers.");
        }

        caveats.Add("Discharge coefficients are generic. They are the largest single source of error in a "
                    + "prediction like this; importing flow-bench data narrows the band more than any other "
                    + "single input.");

        if (wizard.ForcedInduction)
        {
            caveats.Add(Wizard.AspirationNote);
        }

        foreach (var issue in wizard.Check())
        {
            caveats.Add(issue.Message);
        }

        return new DesignBrief
        {
            ModelName = document.Name,
            Lines = lines,
            Predictions = predictions,
            BuildList = BuildList(wizard, document, winner),
            Caveats = caveats,
            Sweep = sweep,
        };
    }

    /// <summary>
    /// The build list, in the units a fabricator orders in (plan §8.6: "the
    /// build list has real dimensions and tube sizes").
    /// </summary>
    private static IReadOnlyList<BuildItem> BuildList(
        Wizard wizard, EngineModelDocument document, Candidate winner)
    {
        var items = new List<BuildItem>
        {
            new(wizard.Cylinders,
                $"{NearestTube(winner.PrimaryDiameterMm)} OD × {winner.PrimaryLengthMm:F0} mm primaries, "
                + "mandrel bent, 1.5 mm wall"),
            new(wizard.Cylinders,
                $"{winner.IntakeDiameterMm:F0} mm ID × {winner.IntakeLengthMm:F0} mm intake runners"),
        };

        if (wizard.Cylinders > 1)
        {
            items.Add(new BuildItem(1,
                $"{wizard.Cylinders}-into-1 collector, {NearestTube(winner.PrimaryDiameterMm * 1.5)} OD"));
        }

        items.Add(new BuildItem(wizard.Cylinders * 2,
            $"{document.IntakeValves.HeadDiameterMm:F0} mm intake / "
            + $"{document.ExhaustValves.HeadDiameterMm:F0} mm exhaust valves"));

        return items;
    }

    /// <summary>
    /// Snap to a tube size that can actually be bought. A recommendation of
    /// "38.4 mm" is a number; 1.5 inch is a thing on a shelf.
    /// </summary>
    private static string NearestTube(double diameterMm)
    {
        double[] inches = [1.25, 1.375, 1.5, 1.625, 1.75, 1.875, 2.0, 2.25, 2.5, 2.75, 3.0];
        var wanted = diameterMm / 25.4;
        var nearest = inches.MinBy(i => Math.Abs(i - wanted));
        return $"{nearest.ToString("0.###", CultureInfo.InvariantCulture)}\" ({nearest * 25.4:F1} mm)";
    }
}
