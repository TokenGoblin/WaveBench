using System.Text;
using FluentAssertions;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;
using WaveBench.ViewModels.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// The Phase 25 gate's first clause: <i>a generated report is complete enough
/// to defend a design decision — including a sound and compliance decision and
/// a turbo match — without any other document.</i>
///
/// "Without any other document" is the testable part, and it is tested by
/// asking what a reader would have to go elsewhere for: the inputs, where each
/// came from, the figures, the evidence that the answer is not a grid
/// artefact, the limits with their sources, and an honest account of what has
/// not been checked.
/// </summary>
public class ReportGateTests(ITestOutputHelper output)
{
    /// <summary>An FSAE-shaped restricted four, which is what this report is aimed at.</summary>
    private static EngineModelDocument RestrictedFour() => new()
    {
        Name = "FSAE 600, turbocharged",
        Engine = new EngineSpec
        {
            BoreMm = 67, StrokeMm = 42.5, RodLengthMm = 90, CompressionRatio = 11.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 23, Count = 2, MaxLiftMm = 8, OpenDeg = 350, CloseDeg = 570 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 20, Count = 2, MaxLiftMm = 8, OpenDeg = 150, CloseDeg = 370 },
        IntakeRunner = new DuctSpec { LengthMm = 250, DiameterMm = 34 },
        ExhaustRunner = new DuctSpec { LengthMm = 450, DiameterMm = 32 },
        Combustion = new CombustionSpec { Fuel = "RON95", Lambda = 0.85 },
        Solver = new SolverSpec { CellSizeMm = 16.0, MinCycles = 2, MaxCycles = 5 },
        ForcedInduction = new ForcedInductionSpec
        {
            Aspiration = AspirationKinds.Turbocharged,
            TurboName = TurboLibrary.Names[0],
            TargetBoostKPa = 120,
            RestrictorFitted = true,
            RestrictorThroatMm = 20,
        },
    };

    private static (ProjectSession Session, ReportBuilder Builder) Case(bool withRun = true)
    {
        var document = RestrictedFour();
        var session = new ProjectSession(document);

        // A realistic mix of origins, so the "where did this come from" column
        // is exercised rather than reading Auto all the way down.
        session.EditByUser("Combustion.Lambda", 0.85);
        session.EditByImport("IntakeValves.MaxLiftMm", 8.0, "cam-measured.csv");
        session.EditByOptimiser("IntakeRunner.LengthMm", 250.0, "opt-2026-09-01");

        var builder = new ReportBuilder(session);

        if (withRun)
        {
            var speeds = new double[] { 4000, 6000, 8000, 10000 };
            builder.Run = new RunResult
            {
                ModelName = document.Name,
                Points = OperatingPointRunner.Sweep(session.Document, speeds),
            };
        }

        return (session, builder);
    }

    // ---- Completeness ------------------------------------------------------

    [Fact]
    public void Gate_the_report_carries_everything_the_plan_lists()
    {
        var (_, builder) = Case();
        var report = builder.Build();

        foreach (var section in report.Sections)
        {
            output.WriteLine($"{section.Title}: {section.Blocks.Count} blocks");
        }

        // Plan §8.4's own list, item by item.
        report.Find("The model").Should().NotBeNull("model dump");
        report.Find("Geometry").Should().NotBeNull("geometry");
        report.Find("Performance").Should().NotBeNull("all plots");
        report.Find("Acoustics").Should().NotBeNull("acoustics section");
        report.Find("Boost matching").Should().NotBeNull("boost matching section");
        report.Find("Convergence and mesh sensitivity").Should().NotBeNull("convergence and mesh evidence");
        report.Find("Assumptions and caveats").Should().NotBeNull("assumptions with citations");
        report.Find("Validation statement").Should().NotBeNull("validation statement");

        report.Figures.Should().NotBeEmpty();
        report.Claims.Should().NotBeEmpty("a limit without a source is this tool's opinion");
        report.Caveats.Should().NotBeEmpty("a report that only says what works is not evidence");
    }

    /// <summary>
    /// The model dump has to be COMPLETE, or a reader cannot reproduce the
    /// run — which is the whole claim "without any other document" makes.
    /// </summary>
    [Fact]
    public void Gate_every_input_field_appears_in_the_model_dump_with_its_origin()
    {
        var (_, builder) = Case();
        var table = builder.Build().Find("The model")!.Blocks.OfType<ReportTable>().Single();

        var labels = table.Rows.Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        var expected = FieldLocator.All.Select(f => f.Label).ToHashSet(StringComparer.Ordinal);

        output.WriteLine($"{table.Rows.Count} rows against {expected.Count} catalogued fields");

        expected.Except(labels).Should().BeEmpty("every editable field must be in the dump");
        table.Headers.Should().Contain("Origin");

        // The origins must be real, not all "Auto" — otherwise the column is
        // decoration.
        var origins = table.Rows.Select(r => r[3]).Distinct().ToList();
        output.WriteLine("origins: " + string.Join(" · ", origins));
        origins.Should().Contain(o => o.StartsWith("Imported", StringComparison.Ordinal));
        origins.Should().Contain(o => o.StartsWith("Optimised", StringComparison.Ordinal));
        origins.Should().Contain("You");
    }

    [Fact]
    public void Gate_the_turbo_match_is_defensible_on_its_own()
    {
        var (_, builder) = Case();
        var boost = builder.Build().Find("Boost matching")!;

        foreach (var claim in boost.Blocks.OfType<ReportClaim>())
        {
            output.WriteLine($"· {claim.Statement}  [{claim.Source}]");
        }

        boost.Blocks.OfType<ReportFigure>().Should().NotBeEmpty("a match with no map on it is an assertion");
        boost.Blocks.OfType<ReportFacts>().Should().NotBeEmpty("which turbo, at what target");

        // The synthetic-map caveat is not optional: a judge reading efficiency
        // figures must know they are representative rather than measured.
        boost.Blocks.OfType<ReportCaveat>().Should()
            .Contain(c => c.Detail.Contains("analytic", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The compliance half of the gate. This tool cannot produce an absolute
    /// dB(A) yet, and the honest report says so rather than leaving a judge to
    /// infer a pass from silence.
    /// </summary>
    [Fact]
    public void Gate_the_sound_section_states_what_it_is_and_is_not_evidence_for()
    {
        var (_, builder) = Case();
        builder.Sound = new SoundWorkspace(
            WaveBench.Acoustics.SoundCases.M50Factory(),
            WaveBench.Acoustics.SoundCases.M50EqualLength(),
            new UserPreferences());

        var acoustics = builder.Build().Find("Acoustics")!;

        acoustics.Blocks.OfType<ReportFigure>().Should().HaveCountGreaterThanOrEqualTo(2);

        var caveat = acoustics.Blocks.OfType<ReportCaveat>().Single();
        output.WriteLine($"{caveat.Title}: {caveat.Detail}");
        caveat.Detail.Should().Contain("absolute");
        caveat.Fix.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Gate_mesh_sensitivity_is_published_rather_than_available_on_request()
    {
        var (session, builder) = Case();
        builder.MeshStudy = OperatingPointRunner.MeshSensitivity(session.Document, 8000);

        var evidence = builder.Build().Find("Convergence and mesh sensitivity")!;
        var facts = evidence.Blocks.OfType<ReportFacts>().Single();

        foreach (var (label, value) in facts.Pairs)
        {
            output.WriteLine($"{label,-28} {value}");
        }

        facts.Pairs.Should().Contain(p => p.Label.Contains("0.5×", StringComparison.Ordinal));
        facts.Pairs.Should().Contain(p => p.Label.Contains("2×", StringComparison.Ordinal));
        evidence.Blocks.OfType<ReportProse>().Should().Contain(p => p.Text.Contains("mesh", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A missing input must produce a sentence, never a silently short
    /// section: a judge cannot tell "no acoustic problem" from "nobody ran the
    /// acoustics" unless the report says which.
    /// </summary>
    [Fact]
    public void A_section_with_nothing_behind_it_says_so()
    {
        var (_, builder) = Case(withRun: false);
        var report = builder.Build();

        var performance = report.Find("Performance")!;
        var summary = report.Find("Summary")!;

        output.WriteLine(performance.Blocks.OfType<ReportProse>().First().Text);

        performance.Blocks.OfType<ReportFigure>().Should().BeEmpty();
        performance.Blocks.OfType<ReportProse>().Should().Contain(p => p.Text.Contains("No sweep"));
        summary.Blocks.OfType<ReportProse>().Should().Contain(p => p.Emphasis && p.Text.Contains("No solved sweep"));

        report.Find("Convergence and mesh sensitivity")!.Blocks.OfType<ReportProse>()
            .Should().Contain(p => p.Text.Contains("No mesh-sensitivity study"));
    }

    // ---- The two renderers agree -------------------------------------------

    /// <summary>
    /// One document, two formats. If either renderer decided what the report
    /// said, the two would drift — which is the same argument
    /// <see cref="PlotModel"/> makes one level down.
    /// </summary>
    [Fact]
    public void Both_formats_render_the_same_document()
    {
        var (session, builder) = Case();
        builder.MeshStudy = OperatingPointRunner.MeshSensitivity(session.Document, 8000);

        var report = builder.Build();
        var html = HtmlReportWriter.Write(report);
        var pdf = PdfReportWriter.Write(report);

        output.WriteLine($"HTML {html.Length:N0} chars · PDF {pdf.Length:N0} bytes · {report.Figures.Count} figures");

        foreach (var section in report.Sections)
        {
            html.Should().Contain(section.Title, $"'{section.Title}' is missing from the HTML");
        }

        // The PDF is Latin-1 encoded text operators, so its section headings
        // are readable in the bytes.
        var text = Encoding.Latin1.GetString(pdf);
        foreach (var section in report.Sections)
        {
            text.Should().Contain(Pdf(section.Title), $"'{section.Title}' is missing from the PDF");
        }

        pdf.Should().StartWith("%PDF-1.4"u8.ToArray());
        text.Should().EndWith("%%EOF\n");
    }

    [Fact]
    public void Every_figure_reaches_both_formats_as_vector_geometry()
    {
        var (_, builder) = Case();
        var report = builder.Build();

        report.Figures.Should().NotBeEmpty();

        var html = HtmlReportWriter.Write(report);
        var pdf = Encoding.Latin1.GetString(PdfReportWriter.Write(report));

        foreach (var figure in report.Figures)
        {
            html.Should().Contain(Escape(figure.Title), $"'{figure.Title}' is not in the HTML");
            pdf.Should().Contain(Pdf(figure.Title), $"'{figure.Title}' is not in the PDF");
        }

        // Vector, not raster: SVG paths in the HTML and PDF path operators in
        // the document. An embedded bitmap would print at screen resolution.
        html.Should().Contain("<svg").And.Contain("<polyline").And.Contain("<text");
        pdf.Should().MatchRegex(@"\d+(\.\d+)? \d+(\.\d+)? m ", "the PDF must contain drawn paths");

        output.WriteLine($"{report.Figures.Count} figures, both formats, vector in both");
    }

    /// <summary>
    /// A monospaced table only reads as a table if the columns line up.
    ///
    /// They did not: the WinAnsi fonts cannot carry Ø, so the writer
    /// transliterates it to "dia " — and the columns were measured on the
    /// ORIGINAL text, so every row naming a diameter pushed its later columns
    /// three characters right and some rows grew long enough to wrap. Found by
    /// extracting the text back out of the generated PDF and reading it.
    /// </summary>
    [Fact]
    public void Table_columns_line_up_in_the_characters_the_pdf_actually_holds()
    {
        var (_, builder) = Case();
        var report = builder.Build();
        var text = Encoding.Latin1.GetString(PdfReportWriter.Write(report));

        // The model dump is the table with the diameters in it.
        var rows = System.Text.RegularExpressions.Regex
            .Matches(text, @"\(((?:Intake|Exhaust)[^)]*?)\) Tj")
            .Select(m => m.Groups[1].Value)
            .Where(r => r.Contains("  ", StringComparison.Ordinal))
            .ToList();

        rows.Should().NotBeEmpty("the dump must contain the valve rows");

        // Where the VALUE begins — the first non-space after the gap that
        // follows the label. Not where the label's text ends, which differs
        // per row by construction and says nothing about alignment.
        var valueColumn = rows.Select(r =>
        {
            var gap = r.IndexOf("  ", StringComparison.Ordinal);
            var start = gap;
            while (start < r.Length && r[start] == ' ')
            {
                start++;
            }

            return start;
        }).ToList();

        foreach (var row in rows.Take(8))
        {
            output.WriteLine(row);
        }

        valueColumn.Distinct().Should().HaveCount(1,
            "every row of a monospaced table shares one column layout");

        rows.Should().OnlyContain(r => r.Length <= 96, "a row that wraps is a table that has stopped being one");
    }

    [Fact]
    public void The_html_is_self_contained()
    {
        var (_, builder) = Case();
        var html = HtmlReportWriter.Write(builder.Build());

        // Nothing to FETCH: no stylesheet link, no script, no external image,
        // no imported font. A report is read on a machine that has never heard
        // of this tool, and often with no network at all.
        //
        // Not "contains no http", which an earlier version of this asserted:
        // an SVG declares its own XML namespace by URI and never dereferences
        // it, so that assertion failed on a document with nothing to fetch.
        html.Should().NotContain("<link ").And.NotContain("<script");
        html.Should().NotContain("@import").And.NotContain("url(http");
        html.Should().NotMatchRegex(@"(src|href)=""https?:", "nothing may be loaded from the network");
        html.Should().Contain("<style>");
        html.Should().Contain("xmlns=\"http://www.w3.org/2000/svg\"", "the figures are inline SVG");
    }

    [Fact]
    public void The_validation_statement_lists_what_is_not_validated()
    {
        var (_, builder) = Case();
        var validation = builder.Build().Find("Validation statement")!;

        var open = validation.Blocks.OfType<ReportCaveat>().ToList();
        foreach (var caveat in open)
        {
            output.WriteLine($"open: {caveat.Title}");
        }

        open.Should().NotBeEmpty();
        open.Should().Contain(c => c.Title.Contains("spool", StringComparison.OrdinalIgnoreCase),
            "the transient-spool case is the standing gap and must be stated");

        validation.Blocks.OfType<ReportClaim>().Should().NotBeEmpty();
        validation.Blocks.OfType<ReportClaim>().Should().OnlyContain(c => c.Source.Length > 0);
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// Text as it appears inside a PDF content stream: transliterated to what
    /// the WinAnsi fonts can carry, then escaped. Both halves come from the
    /// writer rather than being restated here — an em dash becomes a hyphen in
    /// the document, and a test that did not know that would be looking for a
    /// string the format cannot hold.
    /// </summary>
    private static string Pdf(string text) =>
        PdfWriter.Transliterate(text)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
}
