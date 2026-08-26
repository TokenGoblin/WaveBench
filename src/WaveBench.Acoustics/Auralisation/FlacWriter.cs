using System.Security.Cryptography;

namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// MSB-first bit writer for the FLAC bitstream.
/// </summary>
internal sealed class BitWriter(Stream stream)
{
    private readonly List<byte> _bytes = [];
    private int _accumulator;
    private int _bitsHeld;

    /// <summary>Bytes written so far, for CRC coverage.</summary>
    public IReadOnlyList<byte> Bytes => _bytes;

    public int ByteCount => _bytes.Count;

    public bool IsByteAligned => _bitsHeld == 0;

    public void WriteBits(ulong value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            _accumulator = (_accumulator << 1) | (int)((value >> i) & 1UL);
            if (++_bitsHeld != 8)
            {
                continue;
            }

            _bytes.Add((byte)_accumulator);
            _accumulator = 0;
            _bitsHeld = 0;
        }
    }

    public void WriteSigned(long value, int count) => WriteBits((ulong)value & ((1UL << count) - 1), count);

    /// <summary>Unary: <paramref name="zeros"/> zero bits terminated by a one (RFC 9639 §5).</summary>
    public void WriteUnary(long zeros)
    {
        for (var i = 0L; i < zeros; i++)
        {
            WriteBits(0, 1);
        }

        WriteBits(1, 1);
    }

    public void AlignToByte()
    {
        while (_bitsHeld != 0)
        {
            WriteBits(0, 1);
        }
    }

    public void Flush()
    {
        AlignToByte();
        foreach (var b in _bytes)
        {
            stream.WriteByte(b);
        }

        _bytes.Clear();
    }

    public void Reset() => _bytes.Clear();
}

/// <summary>
/// FLAC encoder (RFC 9639), for the same 24-bit mono 48 kHz renders the WAV
/// writer produces. Plan §3.6 asks for "48 kHz / 24-bit WAV plus FLAC", and
/// §7.3 for own writers rather than an audio dependency.
///
/// Encodes the fixed-predictor subset: every subframe is CONSTANT, FIXED
/// (orders 0–4) or VERBATIM, with partitioned Rice-coded residuals. Fixed
/// predictors need no stored coefficients and get most of FLAC's compression
/// on this material; general LPC would add solving and coefficient overhead
/// for a few percent more. Escaped partitions are never emitted — VERBATIM
/// already covers the incompressible case.
///
/// <b>Verification.</b> A wrong FLAC file is worse than none, so this is
/// checked two ways: <c>FlacReader</c> decodes it back and the samples must
/// match bit for bit, and CI runs the reference <c>flac -t</c> over a
/// produced file, which independently validates every frame CRC and the
/// STREAMINFO MD5. The round-trip alone would only prove the encoder and
/// decoder share an opinion.
/// </summary>
public static class FlacWriter
{
    /// <summary>Nominal frame length in samples.</summary>
    private const int DefaultBlockSize = 4096;

    private const int BitsPerSample = 24;
    private const int MaxRicePartitionOrder = 5;

    /// <summary>
    /// Largest Rice parameter codable with 4-bit parameters (0b1111 escapes).
    /// 24-bit audio routinely needs more than this — the low bits of a
    /// rendered stem are rounding noise, so residuals stay large — which is
    /// why the 5-bit parameter method exists and why restricting to 4-bit
    /// sends most real blocks to VERBATIM.
    /// </summary>
    private const int MaxParameter4Bit = 14;

    /// <summary>Largest Rice parameter codable with 5-bit parameters (0b11111 escapes).</summary>
    private const int MaxParameter5Bit = 30;

    /// <summary>Write a mono stem as 24-bit FLAC. Returns the peak sample magnitude seen.</summary>
    public static double Write(string path, AudioStem stem, double fullScale = 1.0)
    {
        using var stream = File.Create(path);
        return Write(stream, stem, fullScale);
    }

    public static double Write(Stream stream, AudioStem stem, double fullScale = 1.0)
    {
        // Quantise exactly as the WAV writer does, so the two exports carry
        // identical audio and a comparison between them is meaningful.
        const int max24 = 8_388_607;
        var samples = new int[stem.Samples.Length];
        var peak = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var normalised = stem.Samples[i] / fullScale;
            peak = Math.Max(peak, Math.Abs(normalised));
            samples[i] = (int)Math.Round(Math.Clamp(normalised, -1.0, 1.0) * max24);
        }

        var rate = (int)Math.Round(stem.SampleRate);
        var blockSizes = PlanBlocks(samples.Length);

        stream.Write("fLaC"u8);
        WriteStreamInfo(stream, samples, rate, blockSizes);

        var frameNumber = 0L;
        var offset = 0;
        foreach (var block in blockSizes)
        {
            WriteFrame(stream, samples.AsSpan(offset, block), rate, frameNumber++);
            offset += block;
        }

        return peak;
    }

    /// <summary>
    /// Splits the stream into frames. All frames are the same size except
    /// possibly the last, which is what the fixed blocking strategy requires.
    ///
    /// The block size is nudged down when the tail would otherwise be shorter
    /// than 16 samples, because RFC 9639 §8.2 requires the STREAMINFO minimum
    /// block size to be at least 16 — and the tail IS the minimum.
    /// </summary>
    private static List<int> PlanBlocks(int total)
    {
        var blocks = new List<int>();
        if (total == 0)
        {
            return blocks;
        }

        if (total <= DefaultBlockSize)
        {
            blocks.Add(total);
            return blocks;
        }

        var size = DefaultBlockSize;
        while (size > 16)
        {
            var remainder = total % size;
            if (remainder == 0 || remainder >= 16)
            {
                break;
            }

            size--;
        }

        var offset = 0;
        while (offset < total)
        {
            var block = Math.Min(size, total - offset);
            blocks.Add(block);
            offset += block;
        }

        return blocks;
    }

    private static void WriteStreamInfo(Stream stream, int[] samples, int rate, List<int> blockSizes)
    {
        var writer = new BitWriter(stream);
        writer.WriteBits(1, 1);   // last metadata block
        writer.WriteBits(0, 7);   // STREAMINFO
        writer.WriteBits(34, 24); // length

        var min = blockSizes.Count > 0 ? blockSizes.Min() : 0;
        var max = blockSizes.Count > 0 ? blockSizes.Max() : 0;
        writer.WriteBits((ulong)min, 16);
        writer.WriteBits((ulong)max, 16);
        writer.WriteBits(0, 24);  // min frame size: unknown
        writer.WriteBits(0, 24);  // max frame size: unknown
        writer.WriteBits((ulong)rate, 20);
        writer.WriteBits(0, 3);   // channels − 1 (mono)
        writer.WriteBits(BitsPerSample - 1, 5);
        writer.WriteBits((ulong)samples.Length, 36);

        // MD5 of the unencoded audio: 24-bit little-endian, as stored in a
        // WAV data chunk. This is what `flac -t` checks, and it is the reason
        // a corrupt encode cannot pass silently.
        var raw = new byte[samples.Length * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            raw[i * 3] = (byte)(samples[i] & 0xFF);
            raw[(i * 3) + 1] = (byte)((samples[i] >> 8) & 0xFF);
            raw[(i * 3) + 2] = (byte)((samples[i] >> 16) & 0xFF);
        }

        foreach (var b in MD5.HashData(raw))
        {
            writer.WriteBits(b, 8);
        }

        writer.Flush();
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<int> block, int rate, long frameNumber)
    {
        var writer = new BitWriter(stream);

        // --- frame header ---
        writer.WriteBits(0b111111111111100, 15); // sync
        writer.WriteBits(0, 1);                  // fixed blocking strategy

        var (blockCode, uncommonBlock, uncommonBlockBits) = BlockSizeCode(block.Length);
        var (rateCode, uncommonRate, uncommonRateBits) = SampleRateCode(rate);
        writer.WriteBits(blockCode, 4);
        writer.WriteBits(rateCode, 4);
        writer.WriteBits(0b0000, 4);             // mono
        writer.WriteBits(0b110, 3);              // 24 bits per sample
        writer.WriteBits(0, 1);                  // reserved

        WriteCodedNumber(writer, (ulong)frameNumber);

        if (uncommonBlockBits > 0)
        {
            writer.WriteBits((ulong)uncommonBlock, uncommonBlockBits);
        }

        if (uncommonRateBits > 0)
        {
            writer.WriteBits((ulong)uncommonRate, uncommonRateBits);
        }

        writer.WriteBits(Crc8(writer.Bytes, 0, writer.ByteCount), 8);

        // --- subframe ---
        WriteSubframe(writer, block);

        // --- frame footer ---
        writer.AlignToByte();
        writer.WriteBits(Crc16(writer.Bytes, 0, writer.ByteCount), 16);
        writer.Flush();
    }

    /// <summary>
    /// Picks the cheapest representation for this block and writes it.
    /// </summary>
    private static void WriteSubframe(BitWriter writer, ReadOnlySpan<int> block)
    {
        // Constant: every sample identical.
        var constant = true;
        for (var i = 1; i < block.Length && constant; i++)
        {
            constant = block[i] == block[0];
        }

        if (constant && block.Length > 0)
        {
            writer.WriteBits(0, 1);
            writer.WriteBits(0b000000, 6);
            writer.WriteBits(0, 1);
            writer.WriteSigned(block[0], BitsPerSample);
            return;
        }

        // Fixed predictors: pick the order whose residuals are smallest, by
        // the usual sum-of-magnitudes proxy.
        var bestOrder = -1;
        var bestCost = double.MaxValue;
        long[]? bestResidual = null;

        for (var order = 0; order <= 4 && order < block.Length; order++)
        {
            var residual = Residual(block, order);
            double cost = 0;
            foreach (var r in residual)
            {
                cost += Math.Abs((double)r);
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestOrder = order;
                bestResidual = residual;
            }
        }

        if (bestOrder >= 0 && bestResidual is not null
            && TryPlanRice(bestResidual, block.Length, bestOrder, out var plan)
            && plan.TotalBits + (bestOrder * BitsPerSample) < block.Length * BitsPerSample)
        {
            writer.WriteBits(0, 1);
            writer.WriteBits((ulong)(0b001000 | bestOrder), 6);
            writer.WriteBits(0, 1);
            for (var i = 0; i < bestOrder; i++)
            {
                writer.WriteSigned(block[i], BitsPerSample);
            }

            WriteRiceResidual(writer, bestResidual, block.Length, bestOrder, plan);
            return;
        }

        // Verbatim: nothing predicted better than storing it.
        writer.WriteBits(0, 1);
        writer.WriteBits(0b000001, 6);
        writer.WriteBits(0, 1);
        foreach (var sample in block)
        {
            writer.WriteSigned(sample, BitsPerSample);
        }
    }

    /// <summary>Fixed-predictor residuals for the given order (RFC 9639 Table 20).</summary>
    private static long[] Residual(ReadOnlySpan<int> block, int order)
    {
        var residual = new long[block.Length - order];
        for (var n = order; n < block.Length; n++)
        {
            long prediction = order switch
            {
                0 => 0,
                1 => block[n - 1],
                2 => (2L * block[n - 1]) - block[n - 2],
                3 => (3L * block[n - 1]) - (3L * block[n - 2]) + block[n - 3],
                _ => (4L * block[n - 1]) - (6L * block[n - 2]) + (4L * block[n - 3]) - block[n - 4],
            };

            residual[n - order] = block[n] - prediction;
        }

        return residual;
    }

    /// <summary>
    /// A chosen residual coding: partition order, coding method (0 = 4-bit
    /// Rice parameters, 1 = 5-bit), the per-partition parameters, and the
    /// resulting size in bits.
    /// </summary>
    private readonly record struct RicePlan(int PartitionOrder, int Method, int[] Parameters, long TotalBits);

    /// <summary>
    /// Chooses the partition order and per-partition Rice parameters that
    /// minimise the coded size. Returns false if any residual is too large to
    /// code without an escape partition, which sends the block to VERBATIM.
    /// </summary>
    private static bool TryPlanRice(long[] residual, int blockSize, int order, out RicePlan plan)
    {
        plan = default;
        var best = long.MaxValue;

        for (var partitionOrder = 0; partitionOrder <= MaxRicePartitionOrder; partitionOrder++)
        {
            var partitions = 1 << partitionOrder;
            if (blockSize % partitions != 0 || blockSize >> partitionOrder <= order)
            {
                continue;
            }

            var parameters = new int[partitions];
            long payload = 0;
            var feasible = true;
            var widestParameter = 0;

            for (var p = 0; p < partitions && feasible; p++)
            {
                var (from, count) = PartitionRange(blockSize, partitionOrder, order, p);
                var bestBits = long.MaxValue;
                var bestParameter = 0;

                for (var k = 0; k <= MaxParameter5Bit; k++)
                {
                    long bits = 0;
                    for (var i = from; i < from + count; i++)
                    {
                        bits += (long)(Fold(residual[i]) >> k) + 1 + k;
                        if (bits >= bestBits)
                        {
                            break;
                        }
                    }

                    if (bits < bestBits)
                    {
                        bestBits = bits;
                        bestParameter = k;
                    }
                }

                // A quotient this long means an escaped partition would be
                // needed; the verbatim fallback is simpler and never worse.
                if (bestBits == long.MaxValue || bestBits > count * 64L)
                {
                    feasible = false;
                    break;
                }

                parameters[p] = bestParameter;
                widestParameter = Math.Max(widestParameter, bestParameter);
                payload += bestBits;
            }

            if (!feasible)
            {
                continue;
            }

            // 4-bit parameters where they suffice, 5-bit otherwise.
            var method = widestParameter <= MaxParameter4Bit ? 0 : 1;
            var total = 2 + 4 + ((method == 0 ? 4L : 5L) * partitions) + payload;

            if (total < best)
            {
                best = total;
                plan = new RicePlan(partitionOrder, method, parameters, total);
            }
        }

        return best != long.MaxValue;
    }

    private static (int From, int Count) PartitionRange(int blockSize, int partitionOrder, int order, int partition)
    {
        var per = blockSize >> partitionOrder;
        return partition == 0 ? (0, per - order) : ((per * partition) - order, per);
    }

    private static void WriteRiceResidual(
        BitWriter writer, long[] residual, int blockSize, int order, RicePlan plan)
    {
        var parameterBits = plan.Method == 0 ? 4 : 5;
        writer.WriteBits((ulong)plan.Method, 2);
        writer.WriteBits((ulong)plan.PartitionOrder, 4);

        var partitions = 1 << plan.PartitionOrder;
        for (var p = 0; p < partitions; p++)
        {
            var k = plan.Parameters[p];
            writer.WriteBits((ulong)k, parameterBits);

            var (from, count) = PartitionRange(blockSize, plan.PartitionOrder, order, p);
            for (var i = from; i < from + count; i++)
            {
                var folded = Fold(residual[i]);
                writer.WriteUnary((long)(folded >> k));
                if (k > 0)
                {
                    writer.WriteBits(folded & ((1UL << k) - 1), k);
                }
            }
        }
    }

    /// <summary>
    /// Zigzag folding (RFC 9639 §9.2.7.2): positive values double, negative
    /// values are multiplied by −2 and have 1 subtracted.
    /// </summary>
    private static ulong Fold(long value) => value >= 0 ? (ulong)(value * 2) : (ulong)((value * -2) - 1);

    private static (ulong Code, int Uncommon, int Bits) BlockSizeCode(int blockSize) => blockSize switch
    {
        192 => (0b0001UL, 0, 0),
        576 => (0b0010UL, 0, 0),
        1152 => (0b0011UL, 0, 0),
        2304 => (0b0100UL, 0, 0),
        4608 => (0b0101UL, 0, 0),
        256 => (0b1000UL, 0, 0),
        512 => (0b1001UL, 0, 0),
        1024 => (0b1010UL, 0, 0),
        2048 => (0b1011UL, 0, 0),
        4096 => (0b1100UL, 0, 0),
        8192 => (0b1101UL, 0, 0),
        16384 => (0b1110UL, 0, 0),
        32768 => (0b1111UL, 0, 0),
        // Uncommon: 8-bit if it fits, else 16-bit, storing size − 1.
        _ => blockSize - 1 <= 0xFF ? (0b0110UL, blockSize - 1, 8) : (0b0111UL, blockSize - 1, 16),
    };

    private static (ulong Code, int Value, int Bits) SampleRateCode(int rate) => rate switch
    {
        88_200 => (0b0001UL, 0, 0),
        176_400 => (0b0010UL, 0, 0),
        192_000 => (0b0011UL, 0, 0),
        8_000 => (0b0100UL, 0, 0),
        16_000 => (0b0101UL, 0, 0),
        22_050 => (0b0110UL, 0, 0),
        24_000 => (0b0111UL, 0, 0),
        32_000 => (0b1000UL, 0, 0),
        44_100 => (0b1001UL, 0, 0),
        48_000 => (0b1010UL, 0, 0),
        96_000 => (0b1011UL, 0, 0),
        _ when rate % 1000 == 0 && rate / 1000 <= 0xFF => (0b1100UL, rate / 1000, 8),
        _ when rate <= 0xFFFF => (0b1101UL, rate, 16),
        _ when rate % 10 == 0 && rate / 10 <= 0xFFFF => (0b1110UL, rate / 10, 16),
        _ => throw new NotSupportedException($"Sample rate {rate} Hz cannot be coded in a FLAC frame header."),
    };

    /// <summary>UTF-8-like coded number (RFC 9639 §9.1.5).</summary>
    private static void WriteCodedNumber(BitWriter writer, ulong value)
    {
        if (value < 0x80)
        {
            writer.WriteBits(value, 8);
            return;
        }

        // Find how many continuation bytes are needed.
        var bytes = value switch
        {
            < 0x800 => 2,
            < 0x10000 => 3,
            < 0x200000 => 4,
            < 0x4000000 => 5,
            < 0x80000000 => 6,
            _ => 7,
        };

        // Lead byte: `bytes` ones, a zero, then the top (7 − bytes) value bits.
        var prefix = (0xFFUL << (8 - bytes)) & 0xFF;
        writer.WriteBits(prefix | (value >> (6 * (bytes - 1))), 8);

        for (var i = bytes - 2; i >= 0; i--)
        {
            writer.WriteBits(0x80UL | ((value >> (6 * i)) & 0x3F), 8);
        }
    }

    /// <summary>CRC-8, polynomial x⁸+x²+x+1, initial value 0 (RFC 9639 §9.1.8).</summary>
    internal static byte Crc8(IReadOnlyList<byte> data, int from, int count)
    {
        byte crc = 0;
        for (var i = from; i < from + count; i++)
        {
            crc ^= data[i];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }

        return crc;
    }

    /// <summary>CRC-16, polynomial x¹⁶+x¹⁵+x²+1, initial value 0 (RFC 9639 §9.3).</summary>
    internal static ushort Crc16(IReadOnlyList<byte> data, int from, int count)
    {
        ushort crc = 0;
        for (var i = from; i < from + count; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            }
        }

        return crc;
    }
}
