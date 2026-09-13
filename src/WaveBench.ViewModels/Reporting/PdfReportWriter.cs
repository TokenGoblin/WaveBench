using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels.Reporting;

/// <summary>
/// Renders a <see cref="ReportDocument"/> to PDF — the format a design-event
/// submission is actually handed over in.
///
/// The figures are drawn as vector geometry by <see cref="PdfPlotWriter"/>
/// rather than embedded as images, so the document prints at the printer's
/// resolution rather than the screen's, and every series keeps the line style
/// that makes it readable in black and white.
///
/// No dependency: the writer is <see cref="PdfWriter"/>, about three hundred
/// lines of base-14 fonts and a cross-reference table. A report generator that
/// needed a desktop stack could not run from the headless CLI, and the CLI is
/// how this gets produced in a build script the night before a submission.
/// </summary>
public static class PdfReportWriter
{
    public static byte[] Write(ReportDocument report, PlotPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var p = palette ?? PlotPalette.Default;
        var pdf = new PdfWriter();

        pdf.Text(report.Title, 20, PdfFont.Bold);
        pdf.Text(report.Subtitle, 10.5);
        pdf.Space(4);
        pdf.Text($"Generated {report.Generated:yyyy-MM-dd HH:mm} by WaveBench", 8.5);
        pdf.Rule();
        pdf.Space(6);

        pdf.Text("Contents", 12, PdfFont.Bold);
        foreach (var section in report.Sections)
        {
            pdf.Text("· " + section.Title, 9.5, indent: 10);
        }

        foreach (var section in report.Sections)
        {
            // Each section starts a page. A report read on paper is navigated
            // by flicking, and a heading two lines above the bottom of a page
            // is a heading nobody finds.
            pdf.NewPage();
            pdf.Text(section.Title, 15, PdfFont.Bold);

            if (section.Standfirst is { Length: > 0 } standfirst)
            {
                pdf.Text(standfirst, 9.5);
            }

            pdf.Rule();
            pdf.Space(4);

            foreach (var block in section.Blocks)
            {
                Append(pdf, block, p);
            }
        }

        return pdf.Build();
    }

    private static void Append(PdfWriter pdf, ReportBlock block, PlotPalette palette)
    {
        switch (block)
        {
            case ReportProse prose:
                pdf.Space(3);
                pdf.Text(prose.Text, 9.5, prose.Emphasis ? PdfFont.Bold : PdfFont.Regular);
                pdf.Space(3);
                break;

            case ReportFacts facts:
                pdf.Space(3);
                foreach (var (label, value) in facts.Pairs)
                {
                    pdf.Row(label, value, 9.5, rightFont: PdfFont.Bold);
                }

                pdf.Space(4);
                break;

            case ReportTable table:
                AppendTable(pdf, table);
                break;

            case ReportFigure figure:
                pdf.Space(4);
                pdf.Draw(PdfPlotWriter.HeightFor(figure.Plot), canvas =>
                    PdfPlotWriter.Draw(canvas, figure.Plot, palette));
                break;

            case ReportClaim claim:
                pdf.Text("· " + claim.Statement, 9, indent: 10);
                pdf.Text(claim.Source, 8, PdfFont.Mono, indent: 20);
                pdf.Space(2);
                break;

            case ReportCaveat caveat:
                pdf.Space(4);
                pdf.Text((caveat.Dominant ? "!! " : "! ") + caveat.Title, 10, PdfFont.Bold);
                pdf.Text(caveat.Detail, 9, indent: 10);
                pdf.Text("Fix: " + caveat.Fix, 9, PdfFont.Bold, indent: 10);
                pdf.Space(4);
                break;

            case ReportAudio audio:
                pdf.Row(audio.Label, audio.FileName, 9.5, rightFont: PdfFont.Mono);
                pdf.Text(audio.Detail, 8.5, indent: 10);
                break;
        }
    }

    /// <summary>
    /// A table as fixed-width columns in a monospaced face.
    ///
    /// Not ruled boxes: PDF has no table model, so drawing one means measuring
    /// every cell and stroking every edge, and the result is a worse table
    /// than a monospaced grid for a document that is mostly numbers. Columns
    /// are sized from the widest cell actually present, so nothing is clipped
    /// to make a guess come out right.
    /// </summary>
    private static void AppendTable(PdfWriter pdf, ReportTable table)
    {
        pdf.Space(4);

        if (table.Caption is { Length: > 0 } caption)
        {
            pdf.Text(caption, 8.5);
        }

        // Measure the characters the DOCUMENT will contain, not the ones the
        // caller passed.
        //
        // The WinAnsi fonts cannot carry Ø, so the writer transliterates it to
        // "dia " — one character becoming four. Padding the original and
        // transliterating afterwards therefore pushed every later column three
        // characters right on exactly the rows that had a diameter in them,
        // and lengthened those rows enough to wrap. Transliterating first
        // makes the column widths true.
        var headers = table.Headers.Select(PdfWriter.Transliterate).ToList();
        var rows = table.Rows
            .Select(r => (IReadOnlyList<string>)r.Select(PdfWriter.Transliterate).ToList())
            .ToList();

        var columns = headers.Count;
        var widths = new int[columns];

        for (var i = 0; i < columns; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < Math.Min(columns, row.Count); i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        // 7 pt Courier is 0.6 em per character, so the column budget is what
        // fits the text width. Over that, the last column takes the squeeze
        // rather than every column losing a character.
        const double Size = 7.5;
        var budget = (int)((PdfWriter.PageWidth - (2 * PdfWriter.Margin)) / (Size * 0.6));
        var total = widths.Sum() + (columns * 2);
        if (total > budget && columns > 0)
        {
            widths[^1] = Math.Max(6, widths[^1] - (total - budget));
        }

        pdf.Text(Line(headers, widths), Size, PdfFont.Mono);
        pdf.Text(new string('-', Math.Min(budget, widths.Sum() + (columns * 2))), Size, PdfFont.Mono);

        foreach (var row in rows)
        {
            pdf.Text(Line(row, widths), Size, PdfFont.Mono);
        }

        pdf.Space(4);
    }

    private static string Line(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new List<string>(widths.Length);
        for (var i = 0; i < widths.Length; i++)
        {
            var cell = i < cells.Count ? cells[i] : "";
            if (cell.Length > widths[i])
            {
                cell = cell[..widths[i]];
            }

            parts.Add(cell.PadRight(widths[i]));
        }

        return string.Join("  ", parts).TrimEnd();
    }
}
