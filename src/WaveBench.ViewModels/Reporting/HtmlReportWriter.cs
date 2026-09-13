using System.Globalization;
using System.Text;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels.Reporting;

/// <summary>
/// Renders a <see cref="ReportDocument"/> to a single self-contained HTML
/// file.
///
/// <b>Self-contained is the requirement, not a nicety.</b> A report is emailed,
/// attached to a submission, and opened on a machine that has never heard of
/// this tool — so there are no external stylesheets, no fonts to fetch and no
/// image files to lose. The figures are inline SVG from the same
/// <see cref="SvgPlotWriter"/> the export-all button uses, so they are real
/// vector graphics: searchable, zoomable, and identical to what was on screen.
/// </summary>
public static class HtmlReportWriter
{
    public static string Write(ReportDocument report, PlotPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var p = palette ?? PlotPalette.Default;
        var html = new StringBuilder();

        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append(CultureInfo.InvariantCulture, $"<title>{Escape(report.Title)} — WaveBench report</title>\n");
        html.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");

        html.Append("<header>\n");
        html.Append(CultureInfo.InvariantCulture, $"<h1>{Escape(report.Title)}</h1>\n");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"subtitle\">{Escape(report.Subtitle)}</p>\n");
        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"meta\">Generated {report.Generated:yyyy-MM-dd HH:mm} by WaveBench</p>\n");
        html.Append("</header>\n");

        // A contents list, because this document is long by design and a
        // reader looking for the turbo match should not have to scroll for it.
        html.Append("<nav><h2>Contents</h2><ol>\n");
        foreach (var section in report.Sections)
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<li><a href=\"#{Slug(section.Title)}\">{Escape(section.Title)}</a></li>\n");
        }

        html.Append("</ol></nav>\n");

        foreach (var section in report.Sections)
        {
            html.Append(CultureInfo.InvariantCulture, $"<section id=\"{Slug(section.Title)}\">\n");
            html.Append(CultureInfo.InvariantCulture, $"<h2>{Escape(section.Title)}</h2>\n");

            if (section.Standfirst is { Length: > 0 } standfirst)
            {
                html.Append(CultureInfo.InvariantCulture, $"<p class=\"standfirst\">{Escape(standfirst)}</p>\n");
            }

            foreach (var block in section.Blocks)
            {
                Append(html, block, p);
            }

            html.Append("</section>\n");
        }

        html.Append("<footer><p>Produced by WaveBench. Every figure is rendered from the model described in "
                    + "\"The model\"; nothing in this document was drawn by hand.</p></footer>\n");
        html.Append("</body>\n</html>\n");

        return html.ToString();
    }

    private static void Append(StringBuilder html, ReportBlock block, PlotPalette palette)
    {
        switch (block)
        {
            case ReportProse prose:
                html.Append(CultureInfo.InvariantCulture,
                    $"<p class=\"{(prose.Emphasis ? "emphasis" : "prose")}\">{Escape(prose.Text)}</p>\n");
                break;

            case ReportFacts facts:
                html.Append("<dl class=\"facts\">\n");
                foreach (var (label, value) in facts.Pairs)
                {
                    html.Append(CultureInfo.InvariantCulture,
                        $"<dt>{Escape(label)}</dt><dd>{Escape(value)}</dd>\n");
                }

                html.Append("</dl>\n");
                break;

            case ReportTable table:
                html.Append("<table>\n");
                if (table.Caption is { Length: > 0 } caption)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<caption>{Escape(caption)}</caption>\n");
                }

                html.Append("<thead><tr>");
                foreach (var header in table.Headers)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<th>{Escape(header)}</th>");
                }

                html.Append("</tr></thead>\n<tbody>\n");
                foreach (var row in table.Rows)
                {
                    html.Append("<tr>");
                    foreach (var cell in row)
                    {
                        html.Append(CultureInfo.InvariantCulture, $"<td>{Escape(cell)}</td>");
                    }

                    html.Append("</tr>\n");
                }

                html.Append("</tbody>\n</table>\n");
                break;

            case ReportFigure figure:
                html.Append("<figure>\n");
                html.Append(SvgPlotWriter.Write(figure.Plot, 880, 480, palette));
                html.Append("\n</figure>\n");
                break;

            case ReportClaim claim:
                html.Append(CultureInfo.InvariantCulture,
                    $"<p class=\"claim\">{Escape(claim.Statement)} <cite>{Escape(claim.Source)}</cite></p>\n");
                break;

            case ReportCaveat caveat:
                html.Append(CultureInfo.InvariantCulture,
                    $"<div class=\"caveat{(caveat.Dominant ? " dominant" : "")}\">"
                    + $"<h3>{Escape(caveat.Title)}</h3><p>{Escape(caveat.Detail)}</p>"
                    + $"<p class=\"fix\">{Escape(caveat.Fix)}</p></div>\n");
                break;

            case ReportAudio audio:
                html.Append(CultureInfo.InvariantCulture,
                    $"<p class=\"audio\"><a href=\"{Escape(audio.FileName)}\">{Escape(audio.Label)}</a> — "
                    + $"{Escape(audio.Detail)}</p>\n");
                break;
        }
    }

    /// <summary>
    /// Print styles included, because this document's destination is often a
    /// printer. Figures and caveats are kept off page breaks; the navigation
    /// is dropped, since a printed contents list of anchors is furniture.
    /// </summary>
    private const string Css = """
        :root { color-scheme: light; }
        body { margin: 0 auto; max-width: 60rem; padding: 2rem 1.5rem 4rem;
               font: 15px/1.6 "Segoe UI", system-ui, sans-serif; color: #1A1C1E; background: #FFF; }
        header { border-bottom: 2px solid #1A1C1E; padding-bottom: 1rem; margin-bottom: 2rem; }
        h1 { font-size: 2rem; margin: 0 0 .25rem; }
        h2 { font-size: 1.3rem; margin: 2.5rem 0 .5rem; border-bottom: 1px solid #E1E4E8; padding-bottom: .3rem; }
        h3 { font-size: 1rem; margin: 0 0 .25rem; }
        .subtitle { font-size: 1.05rem; color: #5F6368; margin: 0; }
        .meta, .standfirst { color: #5F6368; }
        .standfirst { font-style: italic; margin-top: 0; }
        .emphasis { border-left: 3px solid #0B6BCB; padding-left: .9rem; font-weight: 500; }
        nav { background: #F4F5F7; padding: 1rem 1.5rem; border-radius: 6px; }
        nav h2 { margin: 0 0 .5rem; border: 0; font-size: 1rem; }
        nav a { color: #0B6BCB; }
        dl.facts { display: grid; grid-template-columns: max-content 1fr; gap: .3rem 1.5rem; margin: 1rem 0; }
        dt { color: #5F6368; }
        dd { margin: 0; font-variant-numeric: tabular-nums; }
        table { border-collapse: collapse; width: 100%; margin: 1rem 0; font-size: .88rem; }
        caption { text-align: left; color: #5F6368; padding-bottom: .4rem; font-style: italic; }
        th, td { text-align: left; padding: .35rem .6rem; border-bottom: 1px solid #E1E4E8; }
        th { background: #F4F5F7; }
        td { font-variant-numeric: tabular-nums; }
        figure { margin: 1.5rem 0; }
        figure svg { max-width: 100%; height: auto; }
        .claim { margin: .4rem 0; padding-left: .9rem; border-left: 2px solid #E1E4E8; }
        cite { color: #5F6368; font-size: .85rem; }
        .caveat { background: #F4F5F7; border-left: 3px solid #C77700; padding: .8rem 1rem; margin: .8rem 0;
                  border-radius: 0 4px 4px 0; }
        .caveat.dominant { border-left-color: #C5221F; }
        .caveat .fix { margin: .4rem 0 0; color: #1E8E3E; }
        footer { margin-top: 3rem; padding-top: 1rem; border-top: 1px solid #E1E4E8; color: #5F6368;
                 font-size: .85rem; }
        @media print {
          nav { display: none; }
          body { max-width: none; font-size: 10.5pt; }
          figure, .caveat, table { break-inside: avoid; }
          h2 { break-after: avoid; }
        }
        """;

    private static string Slug(string title) =>
        new(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
