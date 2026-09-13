using System.Globalization;
using System.Text;

namespace WaveBench.ViewModels.Plotting;

/// <summary>Horizontal placement of a text run relative to the x given.</summary>
public enum PdfAlign
{
    Left,
    Centre,
    Right,
}

/// <summary>
/// A box on a PDF page you can draw lines and text into, with the origin at
/// its bottom-left and y running up.
///
/// <b>Real vector geometry, not a picture of one.</b> A report that embedded
/// rasterised charts would print at whatever resolution it was rendered for
/// and could not be zoomed, searched or measured — and this report is meant to
/// be handed to a design-event judge (plan §8.4). A PDF content stream already
/// speaks paths and text, so the whole of a <see cref="PlotModel"/> maps onto
/// it directly.
/// </summary>
public sealed class PdfCanvas(StringBuilder content, double originX, double originY, double width, double height)
{
    public double Width => width;

    public double Height => height;

    /// <summary>Stroke colour for subsequent paths, as a <c>#rrggbb</c> string.</summary>
    public void Stroke(string hex) => content.Append(Rgb(hex)).Append(" RG\n");

    /// <summary>Fill colour for subsequent fills and text.</summary>
    public void Fill(string hex) => content.Append(Rgb(hex)).Append(" rg\n");

    public void LineWidth(double points) =>
        content.Append(CultureInfo.InvariantCulture, $"{F(points)} w\n");

    /// <summary>
    /// Dash pattern in points; empty restores a solid line. This is how a
    /// series stays distinguishable without colour when the report is printed
    /// in black and white, which is how most of them are read.
    /// </summary>
    public void Dash(params double[] pattern) =>
        content.Append(CultureInfo.InvariantCulture,
            $"[{string.Join(" ", pattern.Select(F))}] 0 d\n");

    public void Line(double x1, double y1, double x2, double y2) =>
        content.Append(CultureInfo.InvariantCulture,
            $"{X(x1)} {Y(y1)} m {X(x2)} {Y(y2)} l S\n");

    /// <summary>A stroked path through the points, skipping any that are not finite.</summary>
    public void Polyline(IReadOnlyList<(double X, double Y)> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var started = false;
        foreach (var (x, y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                // A gap rather than a line to nowhere: a NaN in a series is
                // missing data, and joining across it would draw a segment
                // that was never computed.
                started = false;
                continue;
            }

            content.Append(CultureInfo.InvariantCulture, $"{X(x)} {Y(y)} {(started ? "l" : "m")} ");
            started = true;
        }

        content.Append("S\n");
    }

    public void Rect(double x, double y, double w, double h, bool fill = false) =>
        content.Append(CultureInfo.InvariantCulture,
            $"{X(x)} {Y(y)} {F(w)} {F(h)} re {(fill ? "f" : "S")}\n");

    /// <summary>A filled dot, for scatter series and legend markers.</summary>
    public void Dot(double x, double y, double radius)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        // Four Bézier arcs. The magic constant is the usual circle
        // approximation: a control-point offset of 0.5523 × r is within 0.02%
        // of a true circle, which is finer than a printer resolves.
        const double K = 0.5523;
        var (cx, cy, k) = (X(x), Y(y), radius * K);

        content.Append(CultureInfo.InvariantCulture,
            $"{F(cx + radius)} {F(cy)} m "
            + $"{F(cx + radius)} {F(cy + k)} {F(cx + k)} {F(cy + radius)} {F(cx)} {F(cy + radius)} c "
            + $"{F(cx - k)} {F(cy + radius)} {F(cx - radius)} {F(cy + k)} {F(cx - radius)} {F(cy)} c "
            + $"{F(cx - radius)} {F(cy - k)} {F(cx - k)} {F(cy - radius)} {F(cx)} {F(cy - radius)} c "
            + $"{F(cx + k)} {F(cy - radius)} {F(cx + radius)} {F(cy - k)} {F(cx + radius)} {F(cy)} c f\n");
    }

    /// <summary>Text at a point, in the current fill colour.</summary>
    public void Text(double x, double y, string text, double size = 8.0,
        PdfFont font = PdfFont.Regular, PdfAlign align = PdfAlign.Left)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        var placed = align switch
        {
            PdfAlign.Centre => X(x) - (Measure(text, size, font) / 2.0),
            PdfAlign.Right => X(x) - Measure(text, size, font),
            _ => X(x),
        };

        content.Append(CultureInfo.InvariantCulture,
            $"BT /{Resource(font)} {F(size)} Tf 1 0 0 1 {F(placed)} {F(Y(y))} Tm ({Escape(text)}) Tj ET\n");
    }

    /// <summary>Text rotated a quarter turn anticlockwise, for a y-axis title.</summary>
    public void TextVertical(double x, double y, string text, double size = 8.0,
        PdfFont font = PdfFont.Regular)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        var centred = Y(y) - (Measure(text, size, font) / 2.0);
        content.Append(CultureInfo.InvariantCulture,
            $"BT /{Resource(font)} {F(size)} Tf 0 1 -1 0 {F(X(x))} {F(centred)} Tm ({Escape(text)}) Tj ET\n");
    }

    /// <summary>Width of a text run in points, matching <see cref="PdfWriter"/>'s estimate.</summary>
    public static double Measure(string text, double size, PdfFont font) =>
        text.Length * size * (font == PdfFont.Mono ? 0.6 : 0.5);

    // ---- Internals --------------------------------------------------------

    private double X(double x) => originX + x;

    private double Y(double y) => originY + y;

    private static string Resource(PdfFont font) => font switch
    {
        PdfFont.Bold => "F2",
        PdfFont.Mono => "F3",
        _ => "F1",
    };

    /// <summary>
    /// "#rrggbb" to PDF's three components in 0–1. Unknown input falls back to
    /// mid grey rather than throwing: a report that refuses to render because
    /// one series named a colour badly is worse than one drawn in grey.
    /// </summary>
    private static string Rgb(string hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return "0.4 0.4 0.4";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{F(((packed >> 16) & 0xFF) / 255.0)} {F(((packed >> 8) & 0xFF) / 255.0)} {F((packed & 0xFF) / 255.0)}");
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);

    private static string F(double v) =>
        (double.IsFinite(v) ? v : 0.0).ToString("0.###", CultureInfo.InvariantCulture);
}
