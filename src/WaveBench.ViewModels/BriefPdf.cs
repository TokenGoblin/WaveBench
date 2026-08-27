using System.Globalization;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels;

/// <summary>
/// Lays a <see cref="DesignBrief"/> out as a PDF (plan §8.6: <i>"one
/// scrollable page, PDF-exportable"</i>).
///
/// The layout follows the same order as the screen — recommendation, number,
/// why, confidence — because a printed brief a user cannot map onto what they
/// were looking at is a different document, not an export of this one.
/// </summary>
public static class BriefPdf
{
    public static byte[] Render(DesignBrief brief, string? subtitle = null)
    {
        ArgumentNullException.ThrowIfNull(brief);

        var pdf = new PdfWriter();

        pdf.Text("Design Brief", 22, PdfFont.Bold);
        pdf.Text(brief.ModelName, 12);
        if (subtitle is { Length: > 0 })
        {
            pdf.Text(subtitle, 9);
        }

        pdf.Rule();
        pdf.Space(4);

        // The weakest link up front. A brief is only as good as its shakiest
        // input, and a reader should meet that before the numbers rather than
        // after them.
        pdf.Text(
            $"Overall confidence: {Word(brief.WeakestConfidence)} — this is the weakest of the "
            + "recommendations below, not an average of them.",
            9.5, PdfFont.Bold);
        pdf.Space(8);

        foreach (var group in brief.Groups)
        {
            pdf.Space(6);
            pdf.Text(group, 12, PdfFont.Bold);
            pdf.Space(2);

            foreach (var line in brief.Lines.Where(l => l.Group == group))
            {
                pdf.Row(line.Label, $"{line.Value}    {Dots(line.Confidence)} {line.ConfidenceWord}",
                    10.5, PdfFont.Regular, PdfFont.Mono);
                pdf.Text("-> " + line.Why, 9, PdfFont.Regular, indent: 14);
                pdf.Text(line.Basis, 8.5, PdfFont.Regular, indent: 14);
                pdf.Space(3);
            }
        }

        if (brief.Predictions.Count > 0)
        {
            pdf.Space(8);
            pdf.Text("PREDICTED", 12, PdfFont.Bold);
            pdf.Space(2);

            foreach (var prediction in brief.Predictions)
            {
                pdf.Row(prediction.Label, prediction.Format(), 10.5, PdfFont.Regular, PdfFont.Mono);
            }

            pdf.Space(3);
            pdf.Text(
                "Every prediction carries a band. Simple mode does not present a computed number as if "
                + "it were measured.",
                8.5);
        }

        if (brief.Sweep.Count > 0)
        {
            pdf.Space(8);
            pdf.Text("TORQUE AND POWER", 12, PdfFont.Bold);
            pdf.Space(2);
            pdf.Row("rpm", "torque (N.m)      power (kW)      VE", 9.5, PdfFont.Bold, PdfFont.Bold);

            foreach (var point in brief.Sweep)
            {
                pdf.Row(
                    point.Rpm.ToString("F0", CultureInfo.InvariantCulture),
                    $"{point.TorqueNm,8:F1}       {point.PowerW / 1000.0,8:F1}     {point.VolumetricEfficiency,6:F3}",
                    9.5, PdfFont.Regular, PdfFont.Mono);
            }
        }

        if (brief.BuildList.Count > 0)
        {
            pdf.Space(10);
            pdf.Text("BUILD LIST", 12, PdfFont.Bold);
            pdf.Space(2);

            foreach (var item in brief.BuildList)
            {
                pdf.Text($"{item.Quantity} x  {item.Description}", 10, PdfFont.Regular, indent: 8);
            }
        }

        if (brief.Caveats.Count > 0)
        {
            pdf.Space(10);
            pdf.Text("READ THIS BEFORE BUILDING", 12, PdfFont.Bold);
            pdf.Space(2);

            foreach (var caveat in brief.Caveats)
            {
                pdf.Text("- " + caveat, 9, PdfFont.Regular, indent: 8);
                pdf.Space(2);
            }
        }

        pdf.Space(12);
        pdf.Rule();
        pdf.Text(
            "Produced by WaveBench from a 1D unsteady gas-dynamics solve. The model behind this brief is "
            + "the same one Advanced mode opens; nothing here was computed a second way.",
            8);

        return pdf.Build();
    }

    private static string Dots(Confidence confidence) => confidence switch
    {
        Confidence.Good => "***.",
        Confidence.Fair => "**..",
        _ => "*...",
    };

    private static string Word(Confidence confidence) => confidence switch
    {
        Confidence.Good => "good",
        Confidence.Fair => "fair",
        _ => "rough",
    };
}
