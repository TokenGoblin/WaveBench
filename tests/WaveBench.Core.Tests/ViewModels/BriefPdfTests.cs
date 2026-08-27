using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Plotting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// PDF export of the Design Brief (plan §8.6, Phase 23).
///
/// A PDF that will not open is not an export, so these check the container the
/// way a reader does — header, object table, cross-reference offsets, trailer —
/// rather than checking that the call returned bytes.
/// </summary>
public class BriefPdfTests(ITestOutputHelper output)
{
    private static DesignBrief Brief()
    {
        var document = new EngineModelDocument
        {
            Name = "PDF fixture",
            Engine = new EngineSpec
            {
                BoreMm = 82, StrokeMm = 78, RodLengthMm = 133, CompressionRatio = 10.5, CylinderCount = 4,
            },
            IntakeValves = new ValveTrainSpec
            {
                HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580,
            },
            ExhaustValves = new ValveTrainSpec
            {
                HeadDiameterMm = 28, Count = 2, MaxLiftMm = 9.5, OpenDeg = 140, CloseDeg = 370,
            },
            IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
            ExhaustRunner = new DuctSpec { LengthMm = 500, DiameterMm = 38 },
            Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
            Solver = new SolverSpec { CellSizeMm = 14.0, MinCycles = 4, MaxCycles = 8 },
        };

        return BriefBuilder.Preview(new Wizard(new ProjectSession(document))
        {
            BandFromRpm = 4000,
            BandToRpm = 7000,
        });
    }

    [Fact]
    public void Gate_the_brief_exports_as_a_pdf_a_reader_would_accept()
    {
        var pdf = BriefPdf.Render(Brief(), "exported from the wizard");
        var text = Encoding.Latin1.GetString(pdf);

        output.WriteLine($"{pdf.Length} bytes");

        // Header, and the binary marker that stops naive tools transferring it
        // in text mode.
        Encoding.ASCII.GetString(pdf, 0, 8).Should().Be("%PDF-1.4");
        pdf[9].Should().Be((byte)'%', "the binary marker is itself a PDF comment");
        pdf.Skip(10).Take(4).Should().Equal([0xE2, 0xE3, 0xCF, 0xD3]);
        text.Should().EndWith("%%EOF\n");

        // Every object must be reachable, and every cross-reference offset must
        // land exactly on its object header. A wrong offset is the classic way
        // to produce a file that some readers open and others reject.
        var size = int.Parse(Regex.Match(text, @"/Size (\d+)").Groups[1].Value);
        var xrefStart = int.Parse(Regex.Match(text, @"startxref\s+(\d+)").Groups[1].Value);
        text.Substring(xrefStart, 4).Should().Be("xref");

        var entries = Regex.Matches(text[xrefStart..], @"(\d{10}) 00000 n");
        entries.Count.Should().Be(size - 1, "one entry per object, plus the free head");

        for (var i = 0; i < entries.Count; i++)
        {
            var offset = int.Parse(entries[i].Groups[1].Value);
            text[offset..].Should().StartWith($"{i + 1} 0 obj",
                $"cross-reference entry {i + 1} must point at object {i + 1}");
        }

        // Structure.
        text.Should().Contain("/Type /Catalog");
        text.Should().Contain("/Type /Pages");
        text.Should().Contain("/Type /Page ");
        text.Should().Contain("/BaseFont /Helvetica");
        text.Should().Contain("/Encoding /WinAnsiEncoding");

        // Each declared stream length must match the bytes actually there.
        foreach (Match match in Regex.Matches(text, @"<< /Length (\d+) >>\s*stream\r?\n"))
        {
            var declared = int.Parse(match.Groups[1].Value);
            var start = match.Index + match.Length;
            var end = text.IndexOf("\nendstream", start, StringComparison.Ordinal);
            (end - start).Should().Be(declared, "a stream's declared length must be its real length");
        }
    }

    [Fact]
    public void The_pdf_carries_the_content_of_the_brief()
    {
        var brief = Brief();
        var text = Encoding.Latin1.GetString(BriefPdf.Render(brief));

        text.Should().Contain("Design Brief");
        text.Should().Contain(brief.ModelName);

        foreach (var group in brief.Groups)
        {
            text.Should().Contain(group);
        }

        // Every recommendation's label and its why must both survive the
        // export. A PDF with the numbers and not the reasons is the version of
        // this document that is worth least.
        foreach (var line in brief.Lines)
        {
            text.Should().Contain(Escape(line.Label));
            var firstWords = string.Join(" ", line.Why.Split(' ').Take(4));
            text.Should().Contain(Escape(firstWords), $"the why for {line.Label} must be in the PDF");
        }

        foreach (var item in brief.BuildList)
        {
            text.Should().Contain(Escape(item.Description.Split(',')[0]));
        }

        text.Should().Contain("weakest of the recommendations");
    }

    [Fact]
    public void Characters_outside_the_font_encoding_are_transliterated_not_dropped()
    {
        // The brief is full of °, · and Ø, and the confidence indicator is ●○.
        // WinAnsi has some of those and not others; a silent drop would leave
        // "38.1 mm" reading as "38.1 mm" but "●●●○ good" reading as " good".
        var pdf = new PdfWriter();
        pdf.Text("●●●○ good · 38.1 mm Ø · 12° ↳ why — because");
        var text = Encoding.Latin1.GetString(pdf.Build());

        text.Should().Contain("***. good");
        text.Should().Contain("->");
        text.Should().Contain("dia");
        text.Should().NotContain("●");

        // The degree sign and the middle dot ARE in WinAnsi and must come
        // through as themselves.
        text.Should().Contain("12°");
        text.Should().Contain("·");
    }

    [Fact]
    public void Parentheses_in_the_text_do_not_break_the_file()
    {
        // An unescaped bracket ends the string operator early and corrupts
        // everything after it — the single easiest way to write a PDF that
        // will not open.
        var pdf = new PdfWriter();
        pdf.Text("Runner length (tuned) \\ and a ) stray bracket");
        var text = Encoding.Latin1.GetString(pdf.Build());

        text.Should().Contain(@"\(tuned\)");
        text.Should().Contain(@"\)");
        text.Should().EndWith("%%EOF\n");
    }

    [Fact]
    public void Long_content_flows_onto_further_pages()
    {
        var pdf = new PdfWriter();
        for (var i = 0; i < 200; i++)
        {
            pdf.Text($"Line {i}: the quick brown fox jumps over the lazy dog, repeatedly and at length.");
        }

        pdf.PageCount.Should().BeGreaterThan(2);

        var text = Encoding.Latin1.GetString(pdf.Build());
        Regex.Matches(text, "/Type /Page ").Count.Should().Be(pdf.PageCount);
        Regex.Match(text, @"/Count (\d+)").Groups[1].Value.Should().Be(pdf.PageCount.ToString());

        output.WriteLine($"{pdf.PageCount} pages");
    }

    private static string Escape(string text) => text
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);
}
