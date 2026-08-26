using System.Security.Cryptography;

namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// MSB-first bit reader for the FLAC bitstream.
/// </summary>
internal sealed class BitReader(byte[] data)
{
    private int _bytePosition;
    private int _bitPosition;

    public int BytePosition => _bytePosition;

    public bool IsByteAligned => _bitPosition == 0;

    public ulong ReadBits(int count)
    {
        ulong value = 0;
        for (var i = 0; i < count; i++)
        {
            // Corrupt input must be reported, not run off the end of the
            // buffer: this decoder reads files a user supplies.
            if (_bytePosition >= data.Length)
            {
                throw new InvalidDataException("Truncated FLAC stream: ran out of data mid-value.");
            }

            var bit = (uint)(data[_bytePosition] >> (7 - _bitPosition)) & 1u;
            value = (value << 1) | bit;
            if (++_bitPosition != 8)
            {
                continue;
            }

            _bitPosition = 0;
            _bytePosition++;
        }

        return value;
    }

    /// <summary>Two's-complement signed read.</summary>
    public long ReadSigned(int count)
    {
        var raw = ReadBits(count);
        var sign = 1UL << (count - 1);
        return (raw & sign) != 0 ? (long)raw - (long)(sign << 1) : (long)raw;
    }

    /// <summary>Counts zero bits up to and including the terminating one.</summary>
    public long ReadUnary()
    {
        long zeros = 0;
        while (ReadBits(1) == 0)
        {
            zeros++;
        }

        return zeros;
    }

    public void AlignToByte()
    {
        if (_bitPosition == 0)
        {
            return;
        }

        _bitPosition = 0;
        _bytePosition++;
    }
}

/// <summary>
/// FLAC decoder for the subset <see cref="FlacWriter"/> produces: mono,
/// CONSTANT / VERBATIM / FIXED subframes with partitioned Rice residuals,
/// including escaped partitions.
///
/// It exists to make the encoder's round-trip test meaningful, and it
/// validates both CRCs and the STREAMINFO MD5 while decoding, so a
/// bitstream that is self-consistent but wrong still fails. That is not a
/// substitute for a reference decoder — an encoder and decoder written from
/// the same reading of a spec can share a misreading — which is why CI also
/// runs <c>flac -t</c> against a produced file.
/// </summary>
public static class FlacReader
{
    public sealed record FlacStream(int[] Samples, int SampleRate, int BitsPerSample, int Channels)
    {
        public AudioStem ToStem(string name, double fullScale = 1.0)
        {
            var scale = fullScale / ((1 << (BitsPerSample - 1)) - 1);
            var samples = new float[Samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)(Samples[i] * scale);
            }

            return new AudioStem(name, samples, SampleRate);
        }
    }

    public static FlacStream Read(string path) => Decode(File.ReadAllBytes(path));

    public static FlacStream Decode(byte[] data)
    {
        if (data.Length < 4 || data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C')
        {
            throw new InvalidDataException("Not a FLAC stream: missing the fLaC marker.");
        }

        var reader = new BitReader(data);
        reader.ReadBits(32); // the marker

        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        long totalSamples = 0;
        byte[] expectedMd5 = [];

        // Metadata blocks.
        bool last;
        do
        {
            last = reader.ReadBits(1) == 1;
            var type = (int)reader.ReadBits(7);
            var length = (int)reader.ReadBits(24);

            if (type == 0)
            {
                reader.ReadBits(16); // min block size
                reader.ReadBits(16); // max block size
                reader.ReadBits(24); // min frame size
                reader.ReadBits(24); // max frame size
                sampleRate = (int)reader.ReadBits(20);
                channels = (int)reader.ReadBits(3) + 1;
                bitsPerSample = (int)reader.ReadBits(5) + 1;
                totalSamples = (long)reader.ReadBits(36);
                expectedMd5 = new byte[16];
                for (var i = 0; i < 16; i++)
                {
                    expectedMd5[i] = (byte)reader.ReadBits(8);
                }
            }
            else
            {
                for (var i = 0; i < length; i++)
                {
                    reader.ReadBits(8);
                }
            }
        }
        while (!last);

        if (channels != 1)
        {
            throw new NotSupportedException($"Only mono is supported here; this stream has {channels} channels.");
        }

        if (totalSamples < 0 || bitsPerSample % 8 != 0)
        {
            throw new InvalidDataException($"Unsupported STREAMINFO: {totalSamples} samples at {bitsPerSample} bits.");
        }

        var output = new List<int>((int)Math.Min(totalSamples, 1 << 20));
        while (output.Count < totalSamples)
        {
            var before = output.Count;
            output.AddRange(DecodeFrame(reader, data, bitsPerSample));
            if (output.Count == before)
            {
                throw new InvalidDataException("Frame decoded no samples; the stream is corrupt.");
            }
        }

        var samples = output.ToArray();
        VerifyMd5(samples, bitsPerSample, expectedMd5);
        return new FlacStream(samples, sampleRate, bitsPerSample, channels);
    }

    private static int[] DecodeFrame(BitReader reader, byte[] data, int streamBits)
    {
        var frameStart = reader.BytePosition;

        var sync = reader.ReadBits(15);
        if (sync != 0b111111111111100)
        {
            throw new InvalidDataException($"Bad frame sync at byte {frameStart}.");
        }

        reader.ReadBits(1); // blocking strategy
        var blockCode = (int)reader.ReadBits(4);
        var rateCode = (int)reader.ReadBits(4);
        var channelCode = (int)reader.ReadBits(4);
        var depthCode = (int)reader.ReadBits(3);
        if (reader.ReadBits(1) != 0)
        {
            throw new InvalidDataException("Reserved frame-header bit is not zero.");
        }

        if (channelCode != 0)
        {
            throw new NotSupportedException("Only mono frames are supported here.");
        }

        ReadCodedNumber(reader);

        var blockSize = blockCode switch
        {
            0b0001 => 192,
            >= 0b0010 and <= 0b0101 => 576 << (blockCode - 2),
            0b0110 => (int)reader.ReadBits(8) + 1,
            0b0111 => (int)reader.ReadBits(16) + 1,
            >= 0b1000 and <= 0b1111 => 256 << (blockCode - 8),
            _ => throw new InvalidDataException("Forbidden block-size code 0."),
        };

        if (rateCode is 0b1100)
        {
            reader.ReadBits(8);
        }
        else if (rateCode is 0b1101 or 0b1110)
        {
            reader.ReadBits(16);
        }

        var bitsPerSample = depthCode switch
        {
            0b000 => streamBits,
            0b001 => 8,
            0b010 => 12,
            0b100 => 16,
            0b101 => 20,
            0b110 => 24,
            0b111 => 32,
            _ => throw new InvalidDataException($"Reserved bit-depth code {depthCode}."),
        };

        var headerBytes = reader.BytePosition - frameStart;
        var expectedCrc8 = FlacWriter.Crc8(data, frameStart, headerBytes);
        var actualCrc8 = (byte)reader.ReadBits(8);
        if (expectedCrc8 != actualCrc8)
        {
            throw new InvalidDataException(
                $"Frame header CRC-8 mismatch at byte {frameStart}: expected {expectedCrc8:X2}, got {actualCrc8:X2}.");
        }

        var samples = DecodeSubframe(reader, blockSize, bitsPerSample);

        reader.AlignToByte();
        var frameBytes = reader.BytePosition - frameStart;
        var expectedCrc16 = FlacWriter.Crc16(data, frameStart, frameBytes);
        var actualCrc16 = (ushort)reader.ReadBits(16);
        if (expectedCrc16 != actualCrc16)
        {
            throw new InvalidDataException(
                $"Frame CRC-16 mismatch at byte {frameStart}: expected {expectedCrc16:X4}, got {actualCrc16:X4}.");
        }

        return samples;
    }

    private static int[] DecodeSubframe(BitReader reader, int blockSize, int bitsPerSample)
    {
        if (reader.ReadBits(1) != 0)
        {
            throw new InvalidDataException("Subframe padding bit is not zero.");
        }

        var type = (int)reader.ReadBits(6);
        var wastedFlag = reader.ReadBits(1) == 1;
        var wasted = 0;
        if (wastedFlag)
        {
            wasted = (int)reader.ReadUnary() + 1;
            bitsPerSample -= wasted;
        }

        var samples = new int[blockSize];

        if (type == 0b000000)
        {
            var value = (int)reader.ReadSigned(bitsPerSample);
            Array.Fill(samples, value);
        }
        else if (type == 0b000001)
        {
            for (var i = 0; i < blockSize; i++)
            {
                samples[i] = (int)reader.ReadSigned(bitsPerSample);
            }
        }
        else if (type is >= 0b001000 and <= 0b001100)
        {
            var order = type - 0b001000;
            for (var i = 0; i < order; i++)
            {
                samples[i] = (int)reader.ReadSigned(bitsPerSample);
            }

            var residual = DecodeResidual(reader, blockSize, order);
            for (var n = order; n < blockSize; n++)
            {
                long prediction = order switch
                {
                    0 => 0,
                    1 => samples[n - 1],
                    2 => (2L * samples[n - 1]) - samples[n - 2],
                    3 => (3L * samples[n - 1]) - (3L * samples[n - 2]) + samples[n - 3],
                    _ => (4L * samples[n - 1]) - (6L * samples[n - 2]) + (4L * samples[n - 3]) - samples[n - 4],
                };

                samples[n] = (int)(prediction + residual[n - order]);
            }
        }
        else
        {
            throw new NotSupportedException($"Subframe type {type} (LPC) is not decoded here.");
        }

        if (wasted > 0)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] <<= wasted;
            }
        }

        return samples;
    }

    private static long[] DecodeResidual(BitReader reader, int blockSize, int order)
    {
        var method = (int)reader.ReadBits(2);
        if (method > 1)
        {
            throw new InvalidDataException($"Reserved residual coding method {method}.");
        }

        var parameterBits = method == 0 ? 4 : 5;
        var escape = method == 0 ? 0b1111 : 0b11111;
        var partitionOrder = (int)reader.ReadBits(4);
        var partitions = 1 << partitionOrder;

        var residual = new long[blockSize - order];
        var index = 0;

        for (var p = 0; p < partitions; p++)
        {
            var count = (blockSize >> partitionOrder) - (p == 0 ? order : 0);
            if (count < 0 || index + count > residual.Length)
            {
                throw new InvalidDataException(
                    $"Residual partitioning does not fit the block: order {partitionOrder}, block {blockSize}.");
            }

            var parameter = (int)reader.ReadBits(parameterBits);

            if (parameter == escape)
            {
                var raw = (int)reader.ReadBits(5);
                for (var i = 0; i < count; i++)
                {
                    residual[index++] = raw == 0 ? 0 : reader.ReadSigned(raw);
                }

                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var quotient = reader.ReadUnary();
                var folded = ((ulong)quotient << parameter) | (parameter > 0 ? reader.ReadBits(parameter) : 0UL);
                residual[index++] = (folded & 1) == 0 ? (long)(folded >> 1) : -(long)(folded >> 1) - 1;
            }
        }

        return residual;
    }

    private static void ReadCodedNumber(BitReader reader)
    {
        var lead = (int)reader.ReadBits(8);
        if ((lead & 0x80) == 0)
        {
            return;
        }

        var continuation = 0;
        for (var mask = 0x40; (lead & mask) != 0 && mask > 0; mask >>= 1)
        {
            continuation++;
        }

        for (var i = 0; i < continuation; i++)
        {
            reader.ReadBits(8);
        }
    }

    private static void VerifyMd5(int[] samples, int bitsPerSample, byte[] expected)
    {
        if (expected.Length != 16 || expected.All(b => b == 0))
        {
            return; // all-zero means "unknown", which is legal
        }

        var bytesPerSample = bitsPerSample / 8;
        var raw = new byte[samples.Length * bytesPerSample];
        for (var i = 0; i < samples.Length; i++)
        {
            for (var b = 0; b < bytesPerSample; b++)
            {
                raw[(i * bytesPerSample) + b] = (byte)((samples[i] >> (8 * b)) & 0xFF);
            }
        }

        if (!MD5.HashData(raw).SequenceEqual(expected))
        {
            throw new InvalidDataException(
                "STREAMINFO MD5 does not match the decoded audio — the stream is corrupt.");
        }
    }
}
