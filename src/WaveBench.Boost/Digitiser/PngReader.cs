using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WaveBench.Boost.Digitiser;

/// <summary>
/// An 8-bit RGBA raster, row-major from the top left.
///
/// The digitiser works on this rather than on any platform image type, so map
/// tracing runs identically in the CLI, in a test and behind the desktop UI —
/// and so <c>WaveBench.Boost</c> keeps its promise not to reach for a UI stack.
/// </summary>
public sealed class RasterImage(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>4 bytes per pixel: R, G, B, A.</summary>
    public byte[] Rgba { get; } = rgba;

    /// <summary>Pixel at (x, y). Out-of-range coordinates return transparent black.</summary>
    public (byte R, byte G, byte B, byte A) At(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return (0, 0, 0, 0);
        }

        var i = ((y * Width) + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
    }
}

/// <summary>
/// A minimal PNG decoder: the read side of <c>PngWriter</c>, and what lets the
/// map digitiser (plan §4.7) open the JPEG-or-PNG screenshots every builder
/// already has.
///
/// <b>Scope, stated rather than discovered:</b> non-interlaced PNG, bit depths
/// 1/2/4/8/16, colour types 0 (greyscale), 2 (truecolour), 3 (palette),
/// 4 (grey+alpha) and 6 (truecolour+alpha). Interlaced (Adam7) files and JPEG
/// are refused with a message saying so, rather than silently producing a
/// scrambled raster that would digitise into a plausible-looking wrong map.
/// </summary>
public static class PngReader
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static RasterImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        if (png.Length >= 2 && png[0] == 0xFF && png[1] == 0xD8)
        {
            throw new InvalidDataException(
                "This is a JPEG. WaveBench decodes PNG only; save the map image as PNG and try again. "
                + "(A JPEG's own compression also softens the thin curves the tracer keys on, so PNG is "
                + "the better source in any case.)");
        }

        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file: the 8-byte signature is missing.");
        }

        int width = 0, height = 0, bitDepth = 0, colourType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        var seenHeader = false;
        using var idat = new MemoryStream();

        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (length < 0 || offset + 12 + length > png.Length)
            {
                throw new InvalidDataException($"Truncated PNG: chunk at byte {offset} claims {length} bytes.");
            }

            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    bitDepth = data[8];
                    colourType = data[9];
                    interlace = data[12];
                    seenHeader = true;
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "tRNS" when colourType == 3:
                    paletteAlpha = data.ToArray();
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    offset = png.Length;
                    continue;
            }

            offset += 12 + length;
        }

        if (!seenHeader)
        {
            throw new InvalidDataException("PNG has no IHDR chunk.");
        }

        if (interlace != 0)
        {
            throw new InvalidDataException(
                "Interlaced (Adam7) PNGs are not supported. Re-save the image without interlacing.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"PNG declares a {width}×{height} image.");
        }

        var channels = colourType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unsupported PNG colour type {colourType}."),
        };

        if (bitDepth is not (1 or 2 or 4 or 8 or 16))
        {
            throw new InvalidDataException($"Unsupported PNG bit depth {bitDepth}.");
        }

        if (colourType == 3 && palette is null)
        {
            throw new InvalidDataException("Palette PNG has no PLTE chunk.");
        }

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        var scanlines = Unfilter(raw.ToArray(), width, height, channels, bitDepth);

        return new RasterImage(width, height,
            ToRgba(scanlines, width, height, channels, bitDepth, colourType, palette, paletteAlpha));
    }

    /// <summary>
    /// Reverse the per-row filters (PNG spec §9). Every filter but None refers
    /// to the byte one pixel to the left and/or the same byte on the row above,
    /// which is why this has to run before any pixel unpacking.
    /// </summary>
    private static byte[] Unfilter(byte[] bytes, int width, int height, int channels, int bitDepth)
    {
        var bitsPerPixel = channels * bitDepth;
        var stride = ((width * bitsPerPixel) + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);

        var expected = (long)(stride + 1) * height;
        if (bytes.Length < expected)
        {
            throw new InvalidDataException(
                $"PNG image data is short: {bytes.Length} bytes inflated, {expected} needed for "
                + $"{width}×{height} at {bitDepth} bits × {channels} channel(s).");
        }

        var output = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var filter = bytes[y * (stride + 1)];
            var src = (y * (stride + 1)) + 1;
            var dst = y * stride;
            var up = dst - stride;

            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? output[dst + x - bpp] : 0;
                int b = y > 0 ? output[up + x] : 0;
                int c = y > 0 && x >= bpp ? output[up + x - bpp] : 0;

                var value = filter switch
                {
                    0 => bytes[src + x],
                    1 => bytes[src + x] + a,
                    2 => bytes[src + x] + b,
                    3 => bytes[src + x] + ((a + b) / 2),
                    4 => bytes[src + x] + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Unknown PNG row filter {filter} on row {y}."),
                };

                output[dst + x] = (byte)value;
            }
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] ToRgba(
        byte[] scanlines, int width, int height, int channels, int bitDepth, int colourType,
        byte[]? palette, byte[]? paletteAlpha)
    {
        var bitsPerPixel = channels * bitDepth;
        var stride = ((width * bitsPerPixel) + 7) / 8;
        var rgba = new byte[width * height * 4];
        Span<int> sample = stackalloc int[4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var ch = 0; ch < channels; ch++)
                {
                    sample[ch] = Sample(scanlines, (y * stride), x, ch, channels, bitDepth);
                }

                var o = (((y * width) + x) * 4);
                switch (colourType)
                {
                    case 0:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = Scale(sample[0], bitDepth);
                        rgba[o + 3] = 255;
                        break;
                    case 2:
                        rgba[o] = Scale(sample[0], bitDepth);
                        rgba[o + 1] = Scale(sample[1], bitDepth);
                        rgba[o + 2] = Scale(sample[2], bitDepth);
                        rgba[o + 3] = 255;
                        break;
                    case 3:
                        var index = sample[0];
                        if (palette is null || (index * 3) + 2 >= palette.Length)
                        {
                            throw new InvalidDataException($"Palette index {index} is outside the PLTE chunk.");
                        }

                        rgba[o] = palette[index * 3];
                        rgba[o + 1] = palette[(index * 3) + 1];
                        rgba[o + 2] = palette[(index * 3) + 2];
                        rgba[o + 3] = paletteAlpha is not null && index < paletteAlpha.Length
                            ? paletteAlpha[index]
                            : (byte)255;
                        break;
                    case 4:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = Scale(sample[0], bitDepth);
                        rgba[o + 3] = Scale(sample[1], bitDepth);
                        break;
                    default:
                        rgba[o] = Scale(sample[0], bitDepth);
                        rgba[o + 1] = Scale(sample[1], bitDepth);
                        rgba[o + 2] = Scale(sample[2], bitDepth);
                        rgba[o + 3] = Scale(sample[3], bitDepth);
                        break;
                }
            }
        }

        return rgba;
    }

    /// <summary>One channel of one pixel, for any of the five legal bit depths.</summary>
    private static int Sample(byte[] scanlines, int rowStart, int x, int channel, int channels, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8:
                return scanlines[rowStart + (x * channels) + channel];
            case 16:
                var i = rowStart + (((x * channels) + channel) * 2);
                return (scanlines[i] << 8) | scanlines[i + 1];
            default:
                var bitIndex = ((x * channels) + channel) * bitDepth;
                var b = scanlines[rowStart + (bitIndex / 8)];
                var shift = 8 - bitDepth - (bitIndex % 8);
                return (b >> shift) & ((1 << bitDepth) - 1);
        }
    }

    /// <summary>Scale a sample of any depth onto 0–255, preserving full range at both ends.</summary>
    private static byte Scale(int sample, int bitDepth) => bitDepth switch
    {
        8 => (byte)sample,
        16 => (byte)(sample >> 8),
        _ => (byte)(sample * 255 / ((1 << bitDepth) - 1)),
    };
}
