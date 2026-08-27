using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using WaveBench.ViewModels.Plotting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 19 gate, third criterion: "every plot exports to PNG and SVG".
///
/// One <see cref="PlotModel"/> feeds both the screen and the export, which is
/// the point — a figure in a report that disagrees with the figure on screen
/// is worse than no report.
/// </summary>
public class PlotExportTests(ITestOutputHelper output)
{
    private static PlotModel TorqueAndPower() => new()
    {
        Title = "Torque and power",
        Subtitle = "Example 360cc tuned single",
        XAxis = new PlotAxis("Engine speed", 3000, 9000, "rpm"),
        YAxis = new PlotAxis("Torque", 0, 56, "N·m"),
        RightAxis = new PlotAxis("Power", 0, 30, "kW"),
        Series =
        [
            new PlotSeries("Torque", [3000, 5000, 7000, 9000], [45.5, 50.6, 39.3, 22.4], "Brush.Accent"),
            new PlotSeries("Power", [3000, 5000, 7000, 9000], [14.3, 26.5, 28.8, 21.1], "Brush.Info",
                PlotSeriesKind.Dashed, RightAxis: true),
        ],
        Markers = [new PlotMarker(7000, "peak power")],
        Notes = ["Committed sweep, 6 cycles per point."],
    };

    // ---- PNG --------------------------------------------------------------

    [Fact]
    public void The_png_encoder_writes_a_file_a_decoder_would_accept()
    {
        const int w = 7;
        const int h = 5;
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(i % 251);
            rgba[i + 1] = 0x40;
            rgba[i + 2] = 0x80;
            rgba[i + 3] = 0xFF;
        }

        var png = PngWriter.Encode(rgba, w, h);

        // Signature.
        png.Take(8).Should().Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        // Walk the chunks and verify every CRC, exactly as a decoder does.
        // A wrong CRC is the failure mode that silently produces a file no
        // viewer will open, so checking the bytes matters more than checking
        // that the call returned something.
        var chunks = new List<string>();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = ReadBigEndian(png, offset);
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add(type);

            var crcOffset = offset + 8 + length;
            var declared = (uint)ReadBigEndian(png, crcOffset);
            var actual = Crc32(png.AsSpan(offset + 4, 4 + length));
            actual.Should().Be(declared, $"chunk {type} must carry a correct CRC-32");

            offset = crcOffset + 4;
        }

        chunks.Should().Equal("IHDR", "IDAT", "IEND");
        offset.Should().Be(png.Length, "the chunk stream must end exactly at the end of the file");

        // IHDR: 8-bit truecolour with alpha, no interlace.
        ReadBigEndian(png, 16).Should().Be(w);
        ReadBigEndian(png, 20).Should().Be(h);
        png[24].Should().Be(8);
        png[25].Should().Be(6);
        png[28].Should().Be(0);

        output.WriteLine($"{w}×{h} RGBA -> {png.Length} bytes, chunks {string.Join(", ", chunks)}");
    }

    [Fact]
    public void The_png_encoder_round_trips_through_the_platform_decoder()
    {
        // Independent confirmation: whatever the platform's own PNG reader
        // makes of the file has to match what went in. Writing my own encoder
        // and then testing it only against my own expectations would prove
        // nothing.
        const int w = 16;
        const int h = 9;
        var rgba = new byte[w * h * 4];
        var rng = new Random(20260827);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)rng.Next(256);
            rgba[i + 1] = (byte)rng.Next(256);
            rgba[i + 2] = (byte)rng.Next(256);
            rgba[i + 3] = 255;
        }

        var png = PngWriter.Encode(rgba, w, h);
        var decoded = DecodePng(png, out var dw, out var dh);

        dw.Should().Be(w);
        dh.Should().Be(h);
        decoded.Should().Equal(rgba, "the decoded pixels must be exactly the pixels encoded");
    }

    [Fact]
    public void A_mis_sized_buffer_is_rejected_rather_than_written_as_garbage()
    {
        var act = () => PngWriter.Encode(new byte[10], 4, 4);
        act.Should().Throw<ArgumentException>().WithMessage("*Expected 64 bytes*");
    }

    // ---- SVG --------------------------------------------------------------

    [Fact]
    public void Gate_a_line_plot_exports_to_svg_with_its_data_intact()
    {
        var svg = SvgPlotWriter.Write(TorqueAndPower());

        svg.Should().StartWith("<svg").And.EndWith("</svg>\n");
        XmlIsWellFormed(svg).Should().BeTrue("an SVG that will not parse is not an export");

        svg.Should().Contain("Torque and power");
        svg.Should().Contain("Example 360cc tuned single");
        svg.Should().Contain("N·m").And.Contain("kW").And.Contain("rpm");
        svg.Should().Contain("peak power", "markers carry their label");
        svg.Should().Contain("Committed sweep", "notes travel with the figure");

        // Two polylines, one dashed — the second series must be
        // distinguishable without colour (plan §8.11).
        Regex.Matches(svg, "<polyline").Count.Should().Be(2);
        svg.Should().Contain("stroke-dasharray");
        svg.Should().Contain("Power — dashed", "the legend names the line style, not just the colour");

        output.WriteLine($"{svg.Length} bytes of SVG");
    }

    [Fact]
    public void A_second_axis_is_scaled_independently_of_the_first()
    {
        // 28.8 kW on a 0–30 axis and 28.8 N·m on a 0–56 axis must land in
        // different places, or the right-hand series is being drawn against
        // the left-hand scale — the bug that makes a power curve look like it
        // collapses.
        var plot = TorqueAndPower() with
        {
            Series =
            [
                new PlotSeries("A", [3000, 9000], [28.8, 28.8], "Brush.Accent"),
                new PlotSeries("B", [3000, 9000], [28.8, 28.8], "Brush.Info", PlotSeriesKind.Dashed, RightAxis: true),
            ],
        };

        var svg = SvgPlotWriter.Write(plot);
        var ys = Regex.Matches(svg, @"<polyline points=""[\d.]+,([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        ys.Should().HaveCount(2);
        output.WriteLine($"same value on two axes lands at y = {ys[0]:F1} and {ys[1]:F1}");
        ys[0].Should().NotBeApproximately(ys[1], 1.0);
    }

    [Fact]
    public void Gate_a_heat_map_exports_to_svg_as_an_embedded_raster_with_vector_axes()
    {
        // 400 cells × 1440 frames is 576 000 rectangles, which no reader will
        // open. The field goes in as a PNG data URI and everything around it
        // stays vector, so the figure still scales and its text is selectable.
        const int cols = 24;
        const int rows = 16;
        var values = new float[cols * rows];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                // A diagonal: the wave.
                values[(r * cols) + c] = Math.Abs(c - (r * 1.4f)) < 2 ? 1.6f : 1.0f;
            }
        }

        var plot = new PlotModel
        {
            Title = "x–t wave diagram",
            XAxis = new PlotAxis("Distance", 0, 0.6, "m"),
            YAxis = new PlotAxis("Crank angle", 0, 720, "°"),
            HeatMap = new HeatMapLayer(values, cols, rows, 1.0f, 1.6f, "p/p₀"),
            Markers = [new PlotMarker(0.0, "EVO"), new PlotMarker(0.6, "open end")],
        };

        var svg = SvgPlotWriter.Write(plot);

        XmlIsWellFormed(svg).Should().BeTrue();
        svg.Should().Contain("data:image/png;base64,");
        svg.Should().Contain("x–t wave diagram");
        svg.Should().Contain("EVO").And.Contain("Crank angle");

        // The embedded raster must be a real PNG of the right size.
        var base64 = Regex.Match(svg, @"base64,([A-Za-z0-9+/=]+)""").Groups[1].Value;
        var png = Convert.FromBase64String(base64);
        DecodePng(png, out var w, out var h);
        w.Should().Be(cols);
        h.Should().Be(rows);

        output.WriteLine($"heat map {cols}×{rows} embedded as {png.Length} bytes of PNG in {svg.Length} bytes of SVG");
    }

    [Fact]
    public void The_heat_map_raster_is_written_bottom_up_to_match_the_axis()
    {
        // SVG's y grows downward and the plot's y axis grows upward. Getting
        // this backwards mirrors the wave diagram in time and reverses every
        // diagonal — the diagram would then say a reflection arrives before
        // the pulse that caused it.
        const int cols = 4;
        const int rows = 2;

        // Row 0 (the BOTTOM of the plot) is the maximum, row 1 the minimum.
        var values = new float[] { 1, 1, 1, 1, 0, 0, 0, 0 };
        var plot = new PlotModel
        {
            Title = "orientation",
            XAxis = new PlotAxis("x", 0, 1),
            YAxis = new PlotAxis("y", 0, 1),
            HeatMap = new HeatMapLayer(values, cols, rows, 0f, 1f, "v"),
        };

        var svg = SvgPlotWriter.Write(plot);
        var png = Convert.FromBase64String(Regex.Match(svg, @"base64,([A-Za-z0-9+/=]+)""").Groups[1].Value);
        var pixels = DecodePng(png, out _, out _);

        // The image's FIRST row is the top of the picture, which must be the
        // field's LAST row.
        var topPixel = (pixels[0], pixels[1], pixels[2]);
        var bottomPixel = (pixels[cols * 4], pixels[(cols * 4) + 1], pixels[(cols * 4) + 2]);

        topPixel.Should().Be(SvgPlotWriter.HeatColour(0.0));
        bottomPixel.Should().Be(SvgPlotWriter.HeatColour(1.0));
    }

    [Fact]
    public void The_colour_scale_diverges_about_its_midpoint()
    {
        // A wave field is signed about its undisturbed state, so the neutral
        // value must render neutral. On a sequential scale a compression and
        // an expansion both read as "some amount of colour".
        var low = SvgPlotWriter.HeatColour(0.0);
        var mid = SvgPlotWriter.HeatColour(0.5);
        var high = SvgPlotWriter.HeatColour(1.0);

        output.WriteLine($"0.0 -> {low}, 0.5 -> {mid}, 1.0 -> {high}");

        mid.R.Should().BeGreaterThan(240);
        mid.G.Should().BeGreaterThan(240);
        mid.B.Should().BeGreaterThan(240);

        low.B.Should().BeGreaterThan(low.R, "the low end is blue");
        high.R.Should().BeGreaterThan(high.B, "the high end is red");
    }

    [Fact]
    public void An_exported_figure_uses_the_theme_it_was_exported_from()
    {
        // The model names tokens, not colours, so a dark-theme export comes
        // out dark. An export that silently reverts to light is a figure the
        // user did not see.
        var dark = new PlotPalette(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Brush.Canvas"] = "#101317",
            ["Brush.TextPrimary"] = "#E8EAED",
            ["Brush.Accent"] = "#7AB8FF",
        });

        var svg = SvgPlotWriter.Write(TorqueAndPower(), palette: dark);
        svg.Should().Contain("#101317").And.Contain("#7AB8FF");

        // An unknown token falls back rather than throwing: a plot must still
        // export if someone adds a series with a token the palette has not
        // been taught.
        var sparse = new PlotPalette();
        var act = () => SvgPlotWriter.Write(
            TorqueAndPower() with { Series = [new PlotSeries("x", [1], [1], "Brush.Nonexistent")] },
            palette: sparse);
        act.Should().NotThrow();
    }

    [Fact]
    public void Axis_ticks_come_out_on_round_numbers()
    {
        // 0, 2.5, 5 is read faster than 0, 2.37, 4.74 even though both are
        // correct.
        new PlotAxis("t", 0, 10).ResolvedTicks().Should().Equal(0, 2, 4, 6, 8, 10);
        new PlotAxis("t", 3000, 9000).ResolvedTicks().Should().Contain(6000);
        // And no accumulated drift: repeated addition gives 0.6000000000000001.
        new PlotAxis("t", 0, 0.6).ResolvedTicks().Should().Equal(0, 0.2, 0.4, 0.6);

        // The generic rule picks decimal steps, which is right for rpm and
        // metres and wrong for crank angle: 0/200/400/600 is not how an engine
        // is divided. That is what explicit ticks are for, and they win.
        new PlotAxis("θ", 0, 720).ResolvedTicks().Should().NotContain(360);
        new PlotAxis("θ", 0, 720, "°", [0, 180, 360, 540, 720])
            .ResolvedTicks().Should().Equal(0, 180, 360, 540, 720);

        // Degenerate ranges must not hang or divide by zero.
        new PlotAxis("t", 5, 5).ResolvedTicks().Should().Equal(5);
    }

    [Fact]
    public void A_title_becomes_a_safe_filename()
    {
        new PlotModel
        {
            Title = "x–t wave diagram: primary 1 @ 8,400 rpm",
            XAxis = new PlotAxis("x", 0, 1),
            YAxis = new PlotAxis("y", 0, 1),
        }.FileStem().Should().Be("x-t-wave-diagram-primary-1-8-400-rpm");
    }

    // ---- Helpers ----------------------------------------------------------

    private static bool XmlIsWellFormed(string text)
    {
        try
        {
            System.Xml.Linq.XDocument.Parse(text);
            return true;
        }
        catch (System.Xml.XmlException e)
        {
            throw new InvalidOperationException($"SVG is not well-formed XML: {e.Message}", e);
        }
    }

    private static int ReadBigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Minimal independent PNG reader for the round-trip check.</summary>
    private static byte[] DecodePng(byte[] png, out int width, out int height)
    {
        width = ReadBigEndian(png, 16);
        height = ReadBigEndian(png, 20);

        using var idat = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = ReadBigEndian(png, offset);
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
            {
                idat.Write(png, offset + 8, length);
            }

            offset += 12 + length;
        }

        idat.Position = 0;
        using var inflate = new System.IO.Compression.ZLibStream(
            idat, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        var stride = width * 4;
        var bytes = raw.ToArray();
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = bytes[y * (stride + 1)];
            filter.Should().Be(0, "this encoder writes filter type 0");
            Array.Copy(bytes, (y * (stride + 1)) + 1, pixels, y * stride, stride);
        }

        return pixels;
    }
}
