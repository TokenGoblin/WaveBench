using System.Globalization;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels.Reporting;

/// <summary>
/// Assembles the report (plan §8.4: <i>"model dump, geometry drawings, all
/// plots, acoustics section, boost matching section, convergence and
/// mesh-sensitivity evidence, assumptions with citations, validation
/// statement, audio links — explicitly designed to be handed to an FSAE
/// design-event judge"</i>).
///
/// <b>What is missing is stated, never omitted.</b> A section with no run
/// behind it says so and says what to do about it, because a judge reading a
/// report cannot tell the difference between "this engine has no acoustic
/// problem" and "nobody ran the acoustics". Every section that depends on
/// something absent degrades to a sentence explaining the absence.
/// </summary>
public sealed class ReportBuilder(ProjectSession session, UserPreferences? preferences = null)
{
    private readonly UserPreferences _preferences = preferences ?? new UserPreferences();

    private EngineModelDocument Document => session.Document;

    /// <summary>The completed sweep, if one exists. Most of the report depends on it.</summary>
    public RunResult? Run { get; set; }

    /// <summary>
    /// The mesh-sensitivity study. Plan §5.3: <i>"Publishing this by default
    /// is what separates lab-grade from hobby-grade."</i>
    /// </summary>
    public MeshSensitivityResult? MeshStudy { get; set; }

    /// <summary>The acoustic comparison, if the Sound workspace has been set up.</summary>
    public SoundWorkspace? Sound { get; set; }

    /// <summary>Audio files rendered beside the report.</summary>
    public IList<ReportAudio> Audio { get; } = [];

    public ReportDocument Build()
    {
        var sections = new List<ReportSection>
        {
            Summary(),
            TheModel(),
            Geometry(),
            Performance(),
            Evidence(),
            Acoustics(),
        };

        if (Document.ForcedInduction.IsForced)
        {
            sections.Add(BoostMatching());
        }

        sections.Add(Assumptions());
        sections.Add(Validation());

        if (Audio.Count > 0)
        {
            sections.Add(new ReportSection("Audio", [.. Audio],
                "Rendered from this model. Each file carries a sidecar naming the model hash, the rpm profile "
                + "and the listener preset it was produced at."));
        }

        var engine = Document.Engine;
        return new ReportDocument(
            Document.Name,
            $"{Displacement():F0} cc · {engine.CylinderCount} cyl · {engine.BoreMm:F1} × {engine.StrokeMm:F1} mm "
            + $"· CR {engine.CompressionRatio:F1} · {Aspiration()}",
            sections,
            DateTimeOffset.Now);
    }

    // ---- Sections ---------------------------------------------------------

    private ReportSection Summary()
    {
        var blocks = new List<ReportBlock>();

        if (Run is { Points.Count: > 0 } run)
        {
            var peakTorque = run.Points.MaxBy(p => p.TorqueNm)!;
            var peakPower = run.Points.MaxBy(p => p.PowerW)!;
            var peakVe = run.Points.MaxBy(p => p.VolumetricEfficiency)!;
            var bestBsfc = run.Points.Where(p => double.IsFinite(p.BsfcGPerKwh)).MinBy(p => p.BsfcGPerKwh);

            blocks.Add(new ReportFacts(
            [
                ("Peak torque", $"{peakTorque.TorqueNm:F1} N·m at {peakTorque.Rpm:N0} rpm"),
                ("Peak power", $"{peakPower.PowerW / 1000.0:F1} kW at {peakPower.Rpm:N0} rpm"),
                ("Peak volumetric efficiency", $"{peakVe.VolumetricEfficiency:F3} at {peakVe.Rpm:N0} rpm"),
                ("Best BSFC", bestBsfc is null ? "—" : $"{bestBsfc.BsfcGPerKwh:F0} g/kWh at {bestBsfc.Rpm:N0} rpm"),
                ("Speeds solved", $"{run.Points.Count} from {run.Points.Min(p => p.Rpm):N0} to {run.Points.Max(p => p.Rpm):N0} rpm"),
            ]));

            blocks.Add(new ReportProse(
                $"Every figure in this report comes from a solve of the model dumped in the next section. "
                + $"The torque curve peaks at {peakTorque.Rpm:N0} rpm and power at {peakPower.Rpm:N0} rpm; "
                + "where those two are far apart the engine is tuned for a band rather than a point."));
        }
        else
        {
            blocks.Add(new ReportProse(
                "No solved sweep is attached to this report, so it contains the model and its geometry but no "
                + "performance, acoustic or matching results. Run a sweep and generate the report again.",
                Emphasis: true));
        }

        var guardrails = new Guardrails(session, _preferences);
        if (guardrails.Banner() is { } banner)
        {
            blocks.Add(new ReportProse(
                $"Before the numbers: {banner} The Assumptions section lists all of them with what would remove "
                + "each one.",
                Emphasis: true));
        }

        return new ReportSection("Summary", blocks,
            "What this engine does, and what to read the rest of this document with in mind.");
    }

    private ReportSection TheModel()
    {
        // Every editable field with its value AND its origin. The origin is
        // the half a judge cares about: a number somebody measured and a
        // number the tool defaulted are not the same evidence.
        var editor = new FieldEditor(session, _preferences);
        var rows = new List<IReadOnlyList<string>>();

        foreach (var field in FieldLocator.All)
        {
            if (!Document.ForcedInduction.IsForced
                && field.Path.StartsWith("ForcedInduction.", StringComparison.Ordinal))
            {
                continue;
            }

            var view = editor.View(field);
            rows.Add([field.Label, view.Display, view.DisplayUnit, Origin(view.Provenance)]);
        }

        var blocks = new List<ReportBlock>
        {
            new ReportTable(["Field", "Value", "Unit", "Origin"], rows,
                "Every editable field of the model. Origin is where the value came from."),
            new ReportProse(
                "\"Auto\" means the tool derived or defaulted it, \"You\" that it was typed, \"Imported\" that it "
                + "came from a file, \"Optimised\" that a search chose it, and \"Wizard\" that it came from an "
                + "answer in Simple mode. Nothing here is a number without a history."),
        };

        return new ReportSection("The model", blocks,
            "The complete input. Everything downstream is a consequence of this table.");
    }

    private ReportSection Geometry()
    {
        var manifold = new ManifoldWorkspace(session, _preferences);
        var blocks = new List<ReportBlock>();

        var design = new DesignWorkspace(session, _preferences);
        blocks.Add(new ReportFacts(
            design.Derived(DesignTab.Engine).Concat(design.Derived(DesignTab.Manifold))
                .Select(r => (r.Label, r.Value)).ToList()));

        if (manifold.Manifold is { Nodes.Count: > 0 } spec)
        {
            var rows = spec.Nodes.Select(n => (IReadOnlyList<string>)new[]
            {
                n.Id,
                n.Kind.ToString(),
                n.LengthMm > 0 ? $"{n.LengthMm:F0}" : "—",
                n.DiameterMm > 0 ? $"{n.DiameterMm:F1}" : "—",
                n.OutletDiameterMm > 0 ? $"{n.OutletDiameterMm:F1}" : "—",
            }).ToList();

            blocks.Add(new ReportTable(
                ["Component", "Kind", "Length (mm)", "Ø in (mm)", "Ø out (mm)"], rows,
                "The exhaust manifold as built on the canvas. These are the centreline dimensions a fabricator "
                + "needs; the same geometry exports to CSV/DXF for CAD."));
        }
        else
        {
            blocks.Add(new ReportProse(
                "This model has no collector graph: each cylinder runs its own primary straight to atmosphere. "
                + "That is a valid model rather than a missing one, and the runner dimensions above are the "
                + "whole of the exhaust geometry."));
        }

        foreach (var warning in manifold.Warnings())
        {
            blocks.Add(new ReportClaim(
                warning.Message + (warning.Suggestion is null ? "" : " " + warning.Suggestion),
                warning.Citation ?? "model validation"));
        }

        return new ReportSection("Geometry", blocks,
            "Dimensions as modelled, and every geometric limit the tool checks against a source.");
    }

    private ReportSection Performance()
    {
        if (Run is not { Points.Count: > 0 } run)
        {
            return new ReportSection("Performance",
                [new ReportProse("No sweep has been run, so there is no performance to report.")]);
        }

        var results = new ResultsWorkspace(run, _preferences);
        var blocks = new List<ReportBlock>();

        var rows = run.Points.Select(p => (IReadOnlyList<string>)new[]
        {
            $"{p.Rpm:N0}",
            $"{p.TorqueNm:F1}",
            $"{p.PowerW / 1000.0:F1}",
            $"{p.VolumetricEfficiency:F3}",
            $"{p.BmepPa / 1e5:F2}",
            double.IsFinite(p.BsfcGPerKwh) ? $"{p.BsfcGPerKwh:F0}" : "—",
            $"{p.CyclesToConvergence}",
        }).ToList();

        blocks.Add(new ReportTable(
            ["rpm", "Torque (N·m)", "Power (kW)", "VE", "BMEP (bar)", "BSFC (g/kWh)", "Cycles"], rows,
            "Every operating point solved, with the cycles each took to converge."));

        foreach (var plot in results.AllPlots())
        {
            // Heat maps are hundreds of thousands of cells and belong in the
            // SVG export, not in a PDF nobody can open.
            if (plot.HeatMap is not null)
            {
                blocks.Add(new ReportProse(
                    $"\"{plot.Title}\" is a field plot of {plot.HeatMap.Columns * plot.HeatMap.Rows:N0} cells. "
                    + "It is omitted here and exported as SVG alongside this report, because a vector figure of "
                    + "that many rectangles will not open."));
                continue;
            }

            blocks.Add(new ReportFigure(plot));
        }

        return new ReportSection("Performance", blocks,
            "What the engine makes, and the figures behind it.");
    }

    private ReportSection Evidence()
    {
        var blocks = new List<ReportBlock>();

        if (MeshStudy is { } mesh)
        {
            blocks.Add(new ReportFacts(
            [
                ("Torque at 0.5× cell size", $"{mesh.Fine.TorqueNm:F2} N·m"),
                ("Torque at 1× cell size", $"{mesh.Baseline.TorqueNm:F2} N·m"),
                ("Torque at 2× cell size", $"{mesh.Coarse.TorqueNm:F2} N·m"),
                ("Change on refinement", $"{mesh.FineRelativeChange:P2}"),
                ("Change on coarsening", $"{mesh.CoarseRelativeChange:P2}"),
            ]));

            blocks.Add(new ReportProse(
                mesh.Warning
                    ? "The answer moves by more than 1% with cell size, so it is not yet mesh-converged: refine "
                      + "the mesh before quoting these numbers."
                    : "The answer moves by less than 1% between half and double the cell size, so it is "
                      + "mesh-converged and the figures above are a property of the model rather than of the "
                      + "grid it was solved on.",
                Emphasis: mesh.Warning));

            blocks.Add(new ReportClaim(
                "Mesh sensitivity is published with every report rather than on request.",
                "plan §5.3"));
        }
        else
        {
            blocks.Add(new ReportProse(
                "No mesh-sensitivity study is attached. Without one, nothing here distinguishes a result from "
                + "an artefact of the grid — run `wavebench mesh` on this model and attach it.",
                Emphasis: true));
        }

        if (Run is { Points.Count: > 0 } run)
        {
            var worst = run.Points.MaxBy(p => p.CyclesToConvergence)!;
            blocks.Add(new ReportProse(
                $"Every operating point ran to a cyclically converged state; the slowest took "
                + $"{worst.CyclesToConvergence} cycles at {worst.Rpm:N0} rpm. Wall temperatures are solved "
                + "between cycles rather than within them, because a steel wall's time constant is about ten "
                + "seconds against a twenty-millisecond cycle."));
        }

        return new ReportSection("Convergence and mesh sensitivity", blocks,
            "Whether these numbers are a property of the engine or of the grid.");
    }

    private ReportSection Acoustics()
    {
        if (Sound is not { } sound)
        {
            return new ReportSection("Acoustics",
            [
                new ReportProse(
                    "No acoustic case is attached to this report. The Sound workspace computes order structure, "
                    + "transmission loss and character from the same model; attach it to include a sound "
                    + "decision here."),
            ]);
        }

        var blocks = new List<ReportBlock>
        {
            new ReportFigure(sound.OrderSpectrumChart()),
            new ReportFigure(sound.TransmissionLoss()),
            new ReportProse(
                "Engine noise arrives in multiples of shaft speed, so the order spectrum — not a frequency "
                + "spectrum at one speed — is what describes the character. A silencer does not turn the volume "
                + "down evenly: the transmission-loss curve is what gets through, per frequency, and that is "
                + "what the exhaust sounds like."),
            new ReportClaim(
                "Order Purity Index is reported as a curve against rpm, as the single number separating a clean "
                + "howl from a warble.",
                "plan §3.3"),
        };

        // The compliance verdict is the one a judge will look for, and it is
        // the one this tool does not yet have an absolute level for. Saying so
        // is the only honest option: a verdict from an order structure with no
        // absolute level would be a number with nothing behind it.
        blocks.Add(new ReportCaveat(
            "No absolute sound-pressure verdict",
            "The acoustic model resolves order structure and transmission loss, which is what a design decision "
            + "between two exhausts turns on. It does not yet produce an absolute dB(A) at a measurement point, "
            + "so this report states no pass or fail against a rules limit.",
            "Measure the built engine at the rules geometry, or treat the comparison above as relative only.",
            Dominant: false));

        return new ReportSection("Acoustics", blocks,
            "What it will sound like, and what that is and is not evidence for.");
    }

    private ReportSection BoostMatching()
    {
        var boost = new BoostWorkspace(session, _preferences) { Run = Run };
        var blocks = new List<ReportBlock>();

        var spec = Document.ForcedInduction;
        blocks.Add(new ReportFacts(
        [
            ("Aspiration", spec.Aspiration),
            ("Turbocharger", string.IsNullOrWhiteSpace(spec.TurboName) ? "none selected" : spec.TurboName),
            ("Boost target", $"{spec.TargetBoostKPa:F0} kPa gauge"),
            ("Turbine A/R", $"{spec.TurbineAreaRatio:F2}"),
            ("Charge cooler", spec.ChargeCooler),
            ("Boost control", spec.BoostControl),
        ]));

        foreach (var plot in boost.AllPlots().Where(p => p.HeatMap is null))
        {
            blocks.Add(new ReportFigure(plot));
        }

        foreach (var readout in boost.Derived(BoostTab.Compressor).Concat(boost.Derived(BoostTab.Turbine)))
        {
            if (readout.Warning is { } warning)
            {
                blocks.Add(new ReportProse($"{readout.Label}: {readout.Value} — {warning}", Emphasis: true));
            }
        }

        foreach (var warning in boost.Warnings())
        {
            blocks.Add(new ReportClaim(
                warning.Message + (warning.Suggestion is null ? "" : " " + warning.Suggestion),
                warning.Citation ?? "compressor map"));
        }

        blocks.Add(new ReportCaveat(
            "The map is analytic, not a manufacturer's",
            "Every turbocharger in the shipped library is an analytic surface. This tool ships no manufacturer "
            + "maps without written permission, so the trends here are right and the absolute efficiencies are "
            + "representative rather than measured.",
            "Digitise the real map from the manufacturer's published sheet in Library → Turbos.",
            Dominant: true));

        return new ReportSection("Boost matching", blocks,
            "Where this engine sits on this compressor, at every speed it runs.");
    }

    private ReportSection Assumptions()
    {
        var guardrails = new Guardrails(session, _preferences);
        var blocks = new List<ReportBlock>();

        foreach (var caveat in guardrails.All())
        {
            blocks.Add(new ReportCaveat(caveat.Title, caveat.Detail, caveat.Fix,
                caveat.Weight == CaveatWeight.Dominant));
        }

        blocks.Add(new ReportProse(
            "The largest error in a simulation like this is almost never the physics; it is the data the user "
            + "did not supply. Everything above is something this tool can see about itself, stated before "
            + "anybody has to find it."));

        return new ReportSection("Assumptions and caveats", blocks,
            "Everything that would make a number here wrong, and what would fix each one.");
    }

    private static ReportSection Validation()
    {
        var blocks = new List<ReportBlock>
        {
            new ReportProse(
                "The solver is verified against analytical cases with known answers — shock tube, steady "
                + "friction, steady heat transfer, isentropic nozzle flow, organ-pipe resonance — and validated "
                + "against published experimental cases where a redistributable dataset exists. The verification "
                + "suite runs on every commit."),
        };

        foreach (var (claim, source) in ValidationRegister.Verified)
        {
            blocks.Add(new ReportClaim(claim, source));
        }

        blocks.Add(new ReportProse(
            "What has NOT been validated is listed below. It is listed because a report that only says what "
            + "works is not evidence.",
            Emphasis: true));

        foreach (var (title, detail, fix) in ValidationRegister.Open)
        {
            blocks.Add(new ReportCaveat(title, detail, fix, Dominant: false));
        }

        return new ReportSection("Validation statement", blocks,
            "What has been checked against something outside this tool, and what has not.");
    }

    // ---- Helpers ----------------------------------------------------------

    private double Displacement() =>
        Math.PI / 4.0 * Math.Pow(Document.Engine.BoreMm / 1000.0, 2)
        * (Document.Engine.StrokeMm / 1000.0) * Document.Engine.CylinderCount * 1e6;

    private string Aspiration() =>
        Document.ForcedInduction.IsForced
            ? $"{Document.ForcedInduction.Aspiration}, {Document.ForcedInduction.TargetBoostKPa:F0} kPa"
            : "naturally aspirated";

    private static string Origin(ProvenanceEntry entry) =>
        entry.Origin switch
        {
            Provenance.You => "You",
            Provenance.Imported => $"Imported ({entry.SourceRef})",
            Provenance.Optimised => $"Optimised ({entry.SourceRef})",
            Provenance.Wizard => "Wizard",
            _ => entry.Derivation is { Length: > 0 } d ? $"Auto — {d}" : "Auto",
        };
}

/// <summary>
/// What this tool has checked against something outside itself, and what it
/// has not.
///
/// Kept as DATA in one place so the report, the docs and the README cannot
/// drift apart — a validation statement that disagrees with the repository it
/// came from is worse than none.
/// </summary>
public static class ValidationRegister
{
    public static IReadOnlyList<(string Claim, string Source)> Verified { get; } =
    [
        ("Shock-tube density, velocity and pressure match the exact Riemann solution.",
            "Sod, J. Comput. Phys. 27 (1978)"),
        ("Steady pipe friction matches Darcy–Weisbach within 1%.",
            "Haaland, J. Fluids Eng. 105 (1983)"),
        ("Steady wall heat transfer matches the analytical solution within 1%.",
            "Colburn analogy; plan §2.3"),
        ("Isentropic nozzle mass flow matches the compressible-flow relations through to choking.",
            "plan §2.5"),
        ("Organ-pipe resonances of a closed-open duct land on the analytical n·c/4L series.",
            "plan §6.1"),
        ("Junction behaviour under a pulse of 69% of mean pressure is accurate to 0.07%.",
            "JunctionUnderPulseTests"),
        ("Compressor and turbine corrected quantities follow the gas-stand test code.",
            "SAE J1826 (2022)"),
        ("Torque and power correction factors follow the published standard.",
            "SAE J1349"),
    ];

    public static IReadOnlyList<(string Title, string Detail, string Fix)> Open { get; } =
    [
        ("Transient spool is not validated against a measured case",
            "The transient model is self-consistent — mesh-converged, energy-balanced, and its sensitivity band "
            + "behaves as the physics requires — but a bounded search found no redistributable measured "
            + "transient-spool dataset to compare it against.",
            "Compare against your own logged spool data before quoting a time-to-torque figure."),

        ("Measured order levels are not validated",
            "Order STRUCTURE is verified against the acoustic model; absolute measured order levels are not, "
            + "because no redistributable dataset is available.",
            "Treat acoustic comparisons as relative between designs rather than absolute."),

        ("Four psychoacoustic metrics are not implemented",
            "ISO 532-3 loudness, ECMA-418-2 tonality, fluctuation strength and DIN 45681 tonal adjustment are "
            + "deferred: each needs verification against published reference signals that cannot be "
            + "redistributed.",
            "Character is reported through the metrics that ARE verified; do not infer a loudness figure."),

        ("Blow-through is reported as a bracket, not a prediction",
            "The cylinder is single-zone and mixes perfectly, which is the lower bound; the "
            + "perfect-displacement upper bound is reported beside it. What lies between is port and chamber "
            + "geometry a one-dimensional solver cannot resolve.",
            "Read the bracket as a bracket. The fuel cost of the upper bound is charged to net torque, so the "
            + "optimiser cannot buy scavenging the engine may not have."),
    ];
}
