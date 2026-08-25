using System.Globalization;

namespace WaveBench.Core.Thermo;

/// <summary>
/// Parses CHEMKIN-format two-range NASA-7 thermo data (the format of both
/// GRI-Mech thermo30.dat and the Burcat database). Handles both integer
/// ("C   1H   4") and decimal ("C  7.H  8.") element-count fields, and ignores
/// anything after column 60 on the fourth line (Burcat appends h298/R there).
/// </summary>
public static class ChemkinThermoParser
{
    public static IReadOnlyList<Species> Parse(TextReader reader)
    {
        var species = new List<Species>();
        var block = new List<string>(4);

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0 || trimmed[0] == '!' ||
                trimmed.StartsWith("THERMO", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            block.Add(line);
            if (block.Count == 4)
            {
                species.Add(ParseBlock(block));
                block.Clear();
            }
        }

        if (block.Count != 0)
        {
            throw new FormatException($"Thermo data ended mid-block ({block.Count} of 4 lines).");
        }

        return species;
    }

    private static Species ParseBlock(List<string> block)
    {
        var header = block[0].PadRight(80);
        var name = header[..18].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var elements = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 4; i++)
        {
            var field = header.Substring(24 + i * 5, 5);
            var symbol = field[..2].Trim();
            var countText = field[2..].Trim().TrimEnd('.');
            if (symbol.Length == 0 || symbol == "0" || countText.Length == 0)
            {
                continue;
            }

            var count = double.Parse(countText, CultureInfo.InvariantCulture);
            if (count > 0)
            {
                elements[symbol] = elements.GetValueOrDefault(symbol) + count;
            }
        }

        var tLow = ParseDouble(header, 45, 10);
        var tHigh = ParseDouble(header, 55, 10);
        var tMidText = header.Substring(65, 8).Trim().TrimEnd('.');
        var tMid = tMidText.Length == 0 ? 1000.0 : double.Parse(tMidText, CultureInfo.InvariantCulture);

        // Line 2: a1..a5 upper. Line 3: a6,a7 upper + a1..a3 lower. Line 4: a4..a7 lower.
        var line2 = block[1].PadRight(80);
        var line3 = block[2].PadRight(80);
        var line4 = block[3].PadRight(80);
        var c = new double[15];
        for (var i = 0; i < 5; i++)
        {
            c[i] = Coefficient(line2, i);
        }

        for (var i = 0; i < 5; i++)
        {
            c[5 + i] = Coefficient(line3, i);
        }

        for (var i = 0; i < 4; i++)
        {
            c[10 + i] = Coefficient(line4, i);
        }

        var upper = new Nasa7Coefficients(c[0], c[1], c[2], c[3], c[4], c[5], c[6]);
        var lower = new Nasa7Coefficients(c[7], c[8], c[9], c[10], c[11], c[12], c[13]);

        return new Species(name, elements, tLow, tMid, tHigh, lower, upper);
    }

    private static double Coefficient(string paddedLine, int index) =>
        double.Parse(paddedLine.AsSpan(index * 15, 15).Trim(), CultureInfo.InvariantCulture);

    private static double ParseDouble(string text, int start, int length) =>
        double.Parse(text.Substring(start, length).Trim(), CultureInfo.InvariantCulture);
}
