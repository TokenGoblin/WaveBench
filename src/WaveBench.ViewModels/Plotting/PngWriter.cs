using System.Buffers.Binary;
using System.IO.Compression;

namespace WaveBench.ViewModels.Plotting;

/// <summary>
/// A minimal PNG encoder: 8-bit RGBA, no interlacing, one IDAT.
///
/// Exists so a plot can be rasterised without a UI framework — the CLI has to
/// export figures for a report, and pulling in WPF or System.Drawing to do it
/// would tie headless output to a desktop stack. It is also what lets an SVG
/// carry a heat map: 400 × 1440 cells is 576 000 rectangles, which no SVG
/// reader will open, so the field goes in as an embedded raster while the
/// axes, labels and markers around it stay true vectors.
///
/// PNG is a chunk container over a zlib stream (RFC 1950 / 2083). .NET has
/// both the deflate and the zlib framing, so the only thing to write by hand
/// is the chunk layout, the CRC-32 and the per-row filter byte.
/// </summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Encode an RGBA buffer, 4 bytes per pixel, row-major from the top left.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expected = (long)width * height * 4;
        if (rgba.Length != expected)
        {
            throw new ArgumentException(
                $"Expected {expected} bytes for {width}×{height} RGBA, got {rgba.Length}.", nameof(rgba));
        }

        // Filter type 0 (None) prefixed to every row. Real encoders pick a
        // filter per row to help the deflate; for plot output the size saved
        // is not worth the complexity of getting Paeth wrong.
        var raw = new byte[(long)height * (1 + ((long)width * 4)) is var n && n < int.MaxValue
            ? (int)n
            : throw new ArgumentOutOfRangeException(nameof(width), "Image is too large to encode.")];

        var stride = width * 4;
        for (var y = 0; y < height; y++)
        {
            var dst = y * (stride + 1);
            raw[dst] = 0;
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(dst + 1, stride));
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = buffer.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type 6 = truecolour with alpha
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // no interlace

        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);

        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        // CRC covers the type and the data, not the length.
        var crc = 0xFFFFFFFFu;
        crc = Accumulate(crc, typeBytes);
        crc = Accumulate(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
