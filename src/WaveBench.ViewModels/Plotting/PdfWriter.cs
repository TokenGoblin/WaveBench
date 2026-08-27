using System.Globalization;
using System.Text;

namespace WaveBench.ViewModels.Plotting;

/// <summary>Weight of a run of text on a PDF page.</summary>
public enum PdfFont
{
    Regular,
    Bold,
    Mono,
}

/// <summary>
/// A minimal PDF writer: A4 pages of left-aligned text in the base-14 fonts.
///
/// Written here rather than taken from a package for the same reason as
/// <see cref="PngWriter"/> — the Design Brief has to be exportable from the
/// headless CLI, and a report generator that needs a desktop stack to produce
/// a document is the wrong dependency. The base-14 fonts need no embedding, so
/// the whole format reduces to a handful of objects, a content stream of text
/// operators, and a cross-reference table.
///
/// It does one thing: text. No images, no vector graphics, no wrapping cleverer
/// than a width estimate. That is enough for a brief, and anything more would
/// be a typesetting engine wearing a PDF hat.
/// </summary>
public sealed class PdfWriter
{
    /// <summary>A4 in PostScript points.</summary>
    public const double PageWidth = 595.28;

    public const double PageHeight = 841.89;

    public const double Margin = 56.0;

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _content = new();
    private double _y = PageHeight - Margin;

    public PdfWriter() => _pages.Add(_content);

    /// <summary>Where the next line will be written, measured down from the top.</summary>
    public double Cursor => PageHeight - _y;

    /// <summary>Room left on the page, points.</summary>
    public double Remaining => _y - Margin;

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _content = new StringBuilder();
        _pages.Add(_content);
        _y = PageHeight - Margin;
    }

    /// <summary>Move down without writing anything.</summary>
    public void Space(double points)
    {
        _y -= points;
        BreakIfNeeded(0);
    }

    /// <summary>
    /// Write one line at the given size, wrapping to the page width. Returns
    /// the number of lines actually written.
    /// </summary>
    public int Text(string text, double size = 10.0, PdfFont font = PdfFont.Regular, double indent = 0.0)
    {
        ArgumentNullException.ThrowIfNull(text);

        var width = PageWidth - (2 * Margin) - indent;
        var lines = Wrap(text, size, font, width);

        foreach (var line in lines)
        {
            BreakIfNeeded(size * 1.35);
            _y -= size * 1.35;

            _content.Append(CultureInfo.InvariantCulture,
                $"BT /{Resource(font)} {F(size)} Tf 1 0 0 1 {F(Margin + indent)} {F(_y)} Tm ({Escape(line)}) Tj ET\n");
        }

        return lines.Count;
    }

    /// <summary>A horizontal rule across the text column.</summary>
    public void Rule(double thickness = 0.5, double grey = 0.75)
    {
        BreakIfNeeded(8);
        _y -= 6;
        _content.Append(CultureInfo.InvariantCulture,
            $"{F(grey)} G {F(thickness)} w {F(Margin)} {F(_y)} m {F(PageWidth - Margin)} {F(_y)} l S\n");
        _y -= 4;
    }

    /// <summary>
    /// Two columns on one line — a label and its value — which is most of what
    /// a brief is.
    /// </summary>
    public void Row(string left, string right, double size = 10.0, PdfFont leftFont = PdfFont.Regular,
        PdfFont rightFont = PdfFont.Regular, double rightColumn = 260.0)
    {
        BreakIfNeeded(size * 1.35);
        _y -= size * 1.35;

        _content.Append(CultureInfo.InvariantCulture,
            $"BT /{Resource(leftFont)} {F(size)} Tf 1 0 0 1 {F(Margin)} {F(_y)} Tm ({Escape(left)}) Tj ET\n");
        _content.Append(CultureInfo.InvariantCulture,
            $"BT /{Resource(rightFont)} {F(size)} Tf 1 0 0 1 {F(Margin + rightColumn)} {F(_y)} Tm ({Escape(right)}) Tj ET\n");
    }

    /// <summary>Serialise the document.</summary>
    public byte[] Build()
    {
        // Object numbering: 1 catalogue, 2 page tree, then per page a page
        // object and a content stream, then the three fonts.
        var objects = new List<byte[]>();
        var pageIds = new List<int>();

        var firstPageId = 3;
        for (var i = 0; i < _pages.Count; i++)
        {
            pageIds.Add(firstPageId + (i * 2));
        }

        var fontBase = firstPageId + (_pages.Count * 2);

        objects.Add(Latin1($"<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Latin1(
            $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] "
            + $"/Count {_pages.Count} >>"));

        for (var i = 0; i < _pages.Count; i++)
        {
            var contentId = pageIds[i] + 1;
            objects.Add(Latin1(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] "
                + $"/Resources << /Font << /F1 {fontBase} 0 R /F2 {fontBase + 1} 0 R "
                + $"/F3 {fontBase + 2} 0 R >> >> /Contents {contentId} 0 R >>"));

            var stream = Latin1(_pages[i].ToString());
            var wrapper = new List<byte>();
            wrapper.AddRange(Latin1($"<< /Length {stream.Length} >>\nstream\n"));
            wrapper.AddRange(stream);
            wrapper.AddRange(Latin1("\nendstream"));
            objects.Add(wrapper.ToArray());
        }

        foreach (var face in new[] { "Helvetica", "Helvetica-Bold", "Courier" })
        {
            objects.Add(Latin1(
                $"<< /Type /Font /Subtype /Type1 /BaseFont /{face} /Encoding /WinAnsiEncoding >>"));
        }

        var pdf = new List<byte>();
        pdf.AddRange(Latin1("%PDF-1.4\n"));

        // A binary comment marks the file as containing binary data, which is
        // what stops naive tools transferring it in text mode and corrupting
        // it.
        pdf.AddRange([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);

        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Count);
            pdf.AddRange(Latin1($"{i + 1} 0 obj\n"));
            pdf.AddRange(objects[i]);
            pdf.AddRange(Latin1("\nendobj\n"));
        }

        var xref = pdf.Count;
        pdf.AddRange(Latin1($"xref\n0 {objects.Count + 1}\n"));
        pdf.AddRange(Latin1("0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            pdf.AddRange(Latin1($"{offset:D10} 00000 n \n"));
        }

        pdf.AddRange(Latin1(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));

        return pdf.ToArray();
    }

    private void BreakIfNeeded(double needed)
    {
        if (_y - needed < Margin)
        {
            NewPage();
        }
    }

    private static string Resource(PdfFont font) => font switch
    {
        PdfFont.Bold => "F2",
        PdfFont.Mono => "F3",
        _ => "F1",
    };

    /// <summary>
    /// Rough text width. The base-14 metrics are not carried here, so this
    /// uses a mean advance per character — which is enough to decide where to
    /// wrap and would not be enough to justify a margin.
    /// </summary>
    private static double Width(string text, double size, PdfFont font) =>
        text.Length * size * (font == PdfFont.Mono ? 0.60 : 0.50);

    private static List<string> Wrap(string text, double size, PdfFont font, double width)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            lines.Add("");
            return lines;
        }

        var words = text.Split(' ');
        var current = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Width(candidate, size, font) > width && current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
            else
            {
                current.Clear();
                current.Append(candidate);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>
    /// Escape the three characters that are structural inside a PDF string.
    /// </summary>
    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);

    /// <summary>
    /// Encode for the WinAnsi the fonts declare. Characters outside it are
    /// transliterated rather than dropped: a brief that silently loses its
    /// degree signs is worse than one that spells them out.
    ///
    /// <see cref="Encoding.Latin1"/> rather than code page 1252, which is not
    /// available in .NET without registering an encoding provider from a
    /// package. The two agree exactly over 0xA0–0xFF, which is where °, ·, ×
    /// and Ø live; CP1252's private block at 0x80–0x9F holds the smart quotes
    /// and dashes, and every one of those is transliterated to ASCII below
    /// before it can matter.
    /// </summary>
    private static byte[] Latin1(string text)
    {
        var mapped = text
            .Replace("●", "*", StringComparison.Ordinal)
            .Replace("○", ".", StringComparison.Ordinal)
            .Replace("↳", "->", StringComparison.Ordinal)
            .Replace("—", "-", StringComparison.Ordinal)
            .Replace("–", "-", StringComparison.Ordinal)
            .Replace("⇄", "<->", StringComparison.Ordinal)
            .Replace("⚠", "!", StringComparison.Ordinal)
            .Replace("Ø", "dia ", StringComparison.Ordinal)
            .Replace("’", "'", StringComparison.Ordinal)
            .Replace("“", "\"", StringComparison.Ordinal)
            .Replace("”", "\"", StringComparison.Ordinal);

        return Encoding.Latin1.GetBytes(mapped);
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
