using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels.Reporting;

/// <summary>One piece of a report. Data, not markup.</summary>
public abstract record ReportBlock;

/// <summary>A paragraph.</summary>
/// <param name="Text">The prose.</param>
/// <param name="Emphasis">Set for the sentence a reader must not skim past.</param>
public sealed record ReportProse(string Text, bool Emphasis = false) : ReportBlock;

/// <summary>Label/value pairs — most of a model dump.</summary>
public sealed record ReportFacts(IReadOnlyList<(string Label, string Value)> Pairs) : ReportBlock;

/// <summary>A table with a header row.</summary>
/// <param name="Headers">Column titles.</param>
/// <param name="Rows">Cells, already formatted.</param>
/// <param name="Caption">What the table shows, or null.</param>
public sealed record ReportTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? Caption = null) : ReportBlock;

/// <summary>
/// A figure. Carries the <see cref="PlotModel"/> itself rather than an image,
/// so the PDF and the HTML each render it natively and both match the screen.
/// </summary>
public sealed record ReportFigure(PlotModel Plot) : ReportBlock;

/// <summary>
/// A statement whose authority comes from somewhere else, with the somewhere
/// attached. Plan §8.4: a limit without a source is this tool's opinion, and a
/// report full of opinions cannot defend a decision.
/// </summary>
public sealed record ReportClaim(string Statement, string Source) : ReportBlock;

/// <summary>
/// Something the reader should not trust without knowing about it — a generic
/// default, an extrapolation, a deferred validation case. Always with what
/// would remove it.
/// </summary>
public sealed record ReportCaveat(string Title, string Detail, string Fix, bool Dominant) : ReportBlock;

/// <summary>A rendered audio file the report refers to (plan §8.4: audio links).</summary>
/// <param name="Label">What it is.</param>
/// <param name="FileName">Relative file name beside the report.</param>
/// <param name="Detail">Conditions it was rendered at.</param>
public sealed record ReportAudio(string Label, string FileName, string Detail) : ReportBlock;

/// <summary>One section, with an optional standfirst under the heading.</summary>
public sealed record ReportSection(string Title, IReadOnlyList<ReportBlock> Blocks, string? Standfirst = null);

/// <summary>
/// A complete report, as DATA.
///
/// The plan asks for "one-click PDF/HTML" (§8.4), which is two renderers, and
/// the only way two renderers of a long document stay in agreement is if
/// neither of them decides what the document says. Same argument as
/// <see cref="PlotModel"/>, one level up: a report that differed between its
/// formats is the bug this shape prevents.
///
/// It is explicitly built to be handed to an FSAE design-event judge, which
/// sets the standard for what must be in it: every number traceable to the
/// model that produced it, every limit to its source, and every weakness
/// stated by the tool rather than found by the judge.
/// </summary>
/// <param name="Title">The model's name.</param>
/// <param name="Subtitle">One line of what it is.</param>
/// <param name="Sections">In reading order.</param>
/// <param name="Generated">When, for the footer.</param>
public sealed record ReportDocument(
    string Title,
    string Subtitle,
    IReadOnlyList<ReportSection> Sections,
    DateTimeOffset Generated)
{
    /// <summary>Every figure in the report, in order.</summary>
    public IReadOnlyList<PlotModel> Figures =>
        Sections.SelectMany(s => s.Blocks).OfType<ReportFigure>().Select(f => f.Plot).ToList();

    /// <summary>Every caveat, worst first.</summary>
    public IReadOnlyList<ReportCaveat> Caveats =>
        Sections.SelectMany(s => s.Blocks).OfType<ReportCaveat>()
            .OrderByDescending(c => c.Dominant).ToList();

    /// <summary>Every sourced claim, for the citation list.</summary>
    public IReadOnlyList<ReportClaim> Claims =>
        Sections.SelectMany(s => s.Blocks).OfType<ReportClaim>().ToList();

    public ReportSection? Find(string title) =>
        Sections.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));
}
