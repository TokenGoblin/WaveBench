using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using WaveBench.Boost;
using WaveBench.Boost.Digitiser;
using WaveBench.ViewModels.Plotting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification.Boost;

/// <summary>
/// Phase 12 gate, third criterion: <i>"the digitiser reproduces a synthetic map
/// from an image within 2%."</i>
///
/// The synthetic map is an analytic surface (<see cref="SyntheticTurbo"/>), so
/// "within 2%" is measured against the truth rather than against another
/// reading of the same picture.
/// </summary>
public class MapDigitiserTests(ITestOutputHelper output)
{
    private const double Tolerance = 0.02;

    [Fact]
    public void Gate_the_digitiser_reproduces_a_synthetic_map_from_an_image_within_two_percent()
    {
        var png = SyntheticMapImage.RenderPng();
        var image = PngReader.Decode(png);

        image.Width.Should().Be(SyntheticMapImage.Width);
        image.Height.Should().Be(SyntheticMapImage.Height);

        var result = MapDigitiser.Digitise(
            image,
            SyntheticMapImage.Calibration(),
            MapReference.SaeJ1826,
            SyntheticMapImage.SpeedLineTargets(),
            SyntheticMapImage.IslandTargets(),
            SyntheticMapImage.Peak(),
            name: "Digitised synthetic 60 mm",
            pointsPerLine: 12,
            provenance: "Rendered by SyntheticMapImage for this test");

        foreach (var w in result.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        result.Map.SpeedLines.Should().HaveCount(4);
        result.TracedPixelsPerLine.Should().AllSatisfy(t => t.Columns.Should().BeGreaterThan(150));

        double worstFlow = 0, worstPr = 0, worstEta = 0;

        output.WriteLine("");
        output.WriteLine("  N_corr   ṁ range read        vs true            PR err   η err");

        for (var l = 0; l < result.Map.SpeedLines.Count; l++)
        {
            var line = result.Map.SpeedLines[l];
            var fraction = SyntheticTurbo.SpeedFractions[l];

            // The traced extent against the true surge and choke flows. This is
            // the reading most sensitive to line width, because the ends of a
            // drawn curve are rounded caps that read slightly wide.
            var trueSurge = SyntheticTurbo.SurgeFlow(fraction);
            var trueChoke = SyntheticTurbo.ChokeFlow(fraction);
            var surgeError = Math.Abs(line.SurgeFlow - trueSurge) / trueSurge;
            var chokeError = Math.Abs(line.ChokeFlow - trueChoke) / trueChoke;

            worstFlow = Math.Max(worstFlow, Math.Max(surgeError, chokeError));

            double prError = 0, etaError = 0;

            foreach (var point in line.Points)
            {
                var truePr = SyntheticTurbo.PressureRatioAtFlow(fraction, point.CorrectedFlowKgPerS);
                prError = Math.Max(prError, Math.Abs(point.PressureRatio - truePr) / truePr);

                var trueEta = SyntheticTurbo.Efficiency(point.CorrectedFlowKgPerS, truePr);
                etaError = Math.Max(etaError, Math.Abs(point.Efficiency - trueEta) / trueEta);
            }

            worstPr = Math.Max(worstPr, prError);
            worstEta = Math.Max(worstEta, etaError);

            output.WriteLine(
                $"{line.CorrectedRpm,8:N0}  {line.SurgeFlow:F4}–{line.ChokeFlow:F4}   "
                + $"{trueSurge:F4}–{trueChoke:F4}   {prError,6:P2}  {etaError,6:P2}");
        }

        output.WriteLine("");
        output.WriteLine(
            $"worst: flow {worstFlow:P2}, pressure ratio {worstPr:P2}, efficiency {worstEta:P2} "
            + $"(gate: {Tolerance:P0})");

        worstFlow.Should().BeLessThan(Tolerance, "the traced flow range must be within 2% of the true one");
        worstPr.Should().BeLessThan(Tolerance, "every digitised pressure ratio must be within 2%");
        worstEta.Should().BeLessThan(Tolerance, "every digitised efficiency must be within 2%");

        // And the product of the exercise has to be a usable map, not just a
        // set of numbers close to the right ones.
        result.Map.Validate();
        var solved = CompressorModel.Solve(result.Map, 0.18, 130_000, 298.15, 101.325);
        var truth = CompressorModel.Solve(SyntheticTurbo.Compressor(), 0.18, 130_000, 298.15, 101.325);

        (Math.Abs(solved.PressureRatio - truth.PressureRatio) / truth.PressureRatio)
            .Should().BeLessThan(Tolerance);
        (Math.Abs(solved.PowerW - truth.PowerW) / truth.PowerW).Should().BeLessThan(Tolerance);

        output.WriteLine(
            $"solved at 0.18 kg/s, 130 000 rpm: digitised PR {solved.PressureRatio:F3} "
            + $"/ {solved.PowerW / 1000:F2} kW against true PR {truth.PressureRatio:F3} "
            + $"/ {truth.PowerW / 1000:F2} kW.");
    }

    [Fact]
    public void Gridlines_and_other_furniture_are_not_mistaken_for_curves()
    {
        // The rendered image has grey gridlines running the width and height of
        // the plot. A tracer that picked one up would put a dead-flat segment
        // into a speed line — plausible-looking and wrong.
        var image = PngReader.Decode(SyntheticMapImage.RenderPng());

        foreach (var target in SyntheticMapImage.SpeedLineTargets())
        {
            var traced = MapDigitiser.TraceByColumn(image, target.Colour);

            // A gridline would show as a long run at constant y, or as columns
            // outside the curve's own flow range.
            var ys = traced.Select(p => p.Y).ToList();
            var flat = 0;
            var run = 1;
            for (var i = 1; i < ys.Count; i++)
            {
                run = Math.Abs(ys[i] - ys[i - 1]) < 1e-9 ? run + 1 : 1;
                flat = Math.Max(flat, run);
            }

            flat.Should().BeLessThan(40,
                $"no part of the {target.CorrectedRpm:N0} rpm trace should be pixel-flat for 40 columns");
        }
    }

    [Fact]
    public void A_speed_line_whose_colour_is_absent_is_dropped_with_a_warning_not_invented()
    {
        var image = PngReader.Decode(SyntheticMapImage.RenderPng());

        var targets = SyntheticMapImage.SpeedLineTargets().ToList();
        targets.Add(new SpeedLineTarget(175_000, new ColourKey(255, 128, 0, 40)));

        var result = MapDigitiser.Digitise(
            image, SyntheticMapImage.Calibration(), MapReference.SaeJ1826,
            targets, SyntheticMapImage.IslandTargets(), SyntheticMapImage.Peak());

        result.Map.SpeedLines.Should().HaveCount(4, "the absent line must not appear in the map");
        result.Warnings.Should().Contain(w => w.Contains("175000", StringComparison.Ordinal)
                                              || w.Contains("175,000", StringComparison.Ordinal)
                                              || w.Contains("175", StringComparison.Ordinal));
    }

    [Fact]
    public void A_map_with_no_efficiency_information_at_all_is_refused()
    {
        var image = PngReader.Decode(SyntheticMapImage.RenderPng());

        var act = () => MapDigitiser.Digitise(
            image, SyntheticMapImage.Calibration(), MapReference.SaeJ1826,
            SyntheticMapImage.SpeedLineTargets(), []);

        act.Should().Throw<InvalidDataException>().WithMessage("*not a compressor map*");
    }

    [Fact]
    public void Two_calibration_ticks_too_close_together_are_refused()
    {
        // Every pixel of click error is multiplied by the axis length divided by
        // the tick separation. Two ticks a pixel apart turn a one-pixel slip
        // into a map that is wrong by a factor.
        var act = () => new AxisCalibration(400, 0.10, 400.5, 0.11).Validate("Flow axis");
        act.Should().Throw<InvalidDataException>().WithMessage("*pixels apart*");
    }

    [Fact]
    public void A_logarithmic_axis_calibrates_and_inverts()
    {
        var axis = new AxisCalibration(100, 0.01, 700, 1.0, AxisScale.Logarithmic);

        axis.Value(100).Should().BeApproximately(0.01, 1e-12);
        axis.Value(700).Should().BeApproximately(1.0, 1e-12);
        axis.Value(400).Should().BeApproximately(0.1, 1e-12, "the midpoint of two decades is one decade up");
        axis.Pixel(axis.Value(345.6)).Should().BeApproximately(345.6, 1e-9);
    }

    // ---- The PNG decoder itself -------------------------------------------

    [Fact]
    public void The_decoder_returns_exactly_what_the_encoder_was_given()
    {
        const int w = 23;
        const int h = 11;
        var rgba = new byte[w * h * 4];
        var rng = new Random(20260827);
        for (var i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)rng.Next(256);
        }

        var image = PngReader.Decode(PngWriter.Encode(rgba, w, h));

        image.Width.Should().Be(w);
        image.Height.Should().Be(h);
        image.Rgba.Should().Equal(rgba);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_png_row_filter_is_reversed_correctly(byte filter)
    {
        // PngWriter only ever writes filter 0, so round-tripping against it
        // would leave four of the five filter paths untested — and a real map
        // image saved by any real encoder will use all five.
        const int w = 16;
        const int h = 8;

        var rgb = new byte[w * h * 3];
        var rng = new Random(4242 + filter);
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)rng.Next(256);
        }

        var image = PngReader.Decode(EncodeTruecolour(rgb, w, h, filter));

        for (var p = 0; p < w * h; p++)
        {
            image.Rgba[(p * 4) + 0].Should().Be(rgb[(p * 3) + 0], $"pixel {p} red, filter {filter}");
            image.Rgba[(p * 4) + 1].Should().Be(rgb[(p * 3) + 1], $"pixel {p} green, filter {filter}");
            image.Rgba[(p * 4) + 2].Should().Be(rgb[(p * 3) + 2], $"pixel {p} blue, filter {filter}");
            image.Rgba[(p * 4) + 3].Should().Be(255);
        }
    }

    [Fact]
    public void A_four_bit_palette_image_decodes()
    {
        // Datasheet screenshots are very often saved as small palettes, and a
        // decoder that only handles 8-bit truecolour would refuse most of what
        // users actually have.
        const int w = 6;
        const int h = 3;
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        byte[] indices = [0, 1, 2, 3, 0, 1, 1, 2, 3, 0, 1, 2, 2, 3, 0, 1, 2, 3];

        var image = PngReader.Decode(EncodePalette4(indices, palette, w, h));

        for (var p = 0; p < w * h; p++)
        {
            var i = indices[p];
            image.Rgba[(p * 4) + 0].Should().Be(palette[i * 3]);
            image.Rgba[(p * 4) + 1].Should().Be(palette[(i * 3) + 1]);
            image.Rgba[(p * 4) + 2].Should().Be(palette[(i * 3) + 2]);
        }
    }

    [Fact]
    public void A_jpeg_is_refused_by_name_rather_than_producing_noise()
    {
        var jpeg = new byte[64];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;

        var act = () => PngReader.Decode(jpeg);
        act.Should().Throw<InvalidDataException>().WithMessage("*JPEG*");
    }

    [Fact]
    public void An_interlaced_png_is_refused_rather_than_scrambled()
    {
        var png = PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);
        png[28] = 1;                                  // IHDR interlace method
        FixCrc(png, 8);

        var act = () => PngReader.Decode(png);
        act.Should().Throw<InvalidDataException>().WithMessage("*nterlaced*");
    }

    // ---- Hand-built PNGs, so the decoder is not tested against its own twin --

    private static byte[] EncodeTruecolour(byte[] rgb, int width, int height, byte filter)
    {
        var stride = width * 3;
        var raw = new byte[(stride + 1) * height];

        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = filter;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= 3 ? rgb[(y * stride) + x - 3] : 0;
                int b = y > 0 ? rgb[((y - 1) * stride) + x] : 0;
                int c = y > 0 && x >= 3 ? rgb[((y - 1) * stride) + x - 3] : 0;

                var value = filter switch
                {
                    0 => rgb[(y * stride) + x],
                    1 => rgb[(y * stride) + x] - a,
                    2 => rgb[(y * stride) + x] - b,
                    3 => rgb[(y * stride) + x] - ((a + b) / 2),
                    _ => rgb[(y * stride) + x] - Paeth(a, b, c),
                };

                raw[(y * (stride + 1)) + 1 + x] = (byte)value;
            }
        }

        return Assemble(width, height, 8, 2, raw, null);
    }

    private static byte[] EncodePalette4(byte[] indices, byte[] palette, int width, int height)
    {
        var stride = ((width * 4) + 7) / 8;
        var raw = new byte[(stride + 1) * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = indices[(y * width) + x];
                var byteIndex = (y * (stride + 1)) + 1 + (x / 2);
                raw[byteIndex] |= (byte)(x % 2 == 0 ? index << 4 : index);
            }
        }

        return Assemble(width, height, 4, 3, raw, palette);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Assemble(int width, int height, byte bitDepth, byte colourType, byte[] raw, byte[]? palette)
    {
        using var file = new MemoryStream();
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colourType;
        WriteChunk(file, "IHDR", ihdr);

        if (palette is not null)
        {
            WriteChunk(file, "PLTE", palette);
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);

        return file.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32([.. typeBytes, .. data]);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static void FixCrc(byte[] png, int chunkOffset)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(chunkOffset));
        var crc = Crc32(png.AsSpan(chunkOffset + 4, 4 + length));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(chunkOffset + 8 + length), crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
