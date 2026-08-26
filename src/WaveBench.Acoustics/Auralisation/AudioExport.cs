using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WaveBench.Acoustics.Auralisation;

/// <summary>
/// Provenance sidecar for a render (plan §3.6): every clip must trace back
/// to the design that produced it.
/// </summary>
public sealed record RenderMetadata
{
    public required string ModelName { get; init; }

    /// <summary>SHA-256 of the model document — the exact input, not a label.</summary>
    public required string ModelHash { get; init; }

    public required string RpmProfile { get; init; }

    public required string ListenerPreset { get; init; }

    public required ulong Seed { get; init; }

    /// <summary>Measured −3 dB scheme bandwidth (docs/numerics.md §5); audio above this is NOT physical.</summary>
    public required double ResolvedBandwidthHz { get; init; }

    public double SampleRate { get; init; } = 48_000.0;

    public int BitsPerSample { get; init; } = 24;

    public double IntegratedLufs { get; init; }

    public string? Notes { get; init; }

    public static string HashOf(string modelJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelJson))).ToLowerInvariant()[..16];

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"modelName\": {Quote(ModelName)},");
        sb.AppendLine($"  \"modelHash\": {Quote(ModelHash)},");
        sb.AppendLine($"  \"rpmProfile\": {Quote(RpmProfile)},");
        sb.AppendLine($"  \"listenerPreset\": {Quote(ListenerPreset)},");
        sb.AppendLine($"  \"seed\": {Seed},");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  \"resolvedBandwidthHz\": {ResolvedBandwidthHz:F1},"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  \"sampleRate\": {SampleRate:F0},"));
        sb.AppendLine($"  \"bitsPerSample\": {BitsPerSample},");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  \"integratedLufs\": {IntegratedLufs:F2},"));
        sb.AppendLine($"  \"notes\": {Quote(Notes ?? "Audio above resolvedBandwidthHz is not physically resolved (plan §5.5).")}");
        sb.Append('}');
        return sb.ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>
/// WAV writer (plan §7.3: own writers, no audio dependency for export).
/// 24-bit PCM at 48 kHz is the documented render format.
/// </summary>
public static class WavWriter
{
    /// <summary>Write a mono stem as 24-bit PCM. Returns the peak sample magnitude seen.</summary>
    public static double Write(string path, AudioStem stem, double fullScale = 1.0)
    {
        using var stream = File.Create(path);
        return Write(stream, stem, fullScale);
    }

    public static double Write(Stream stream, AudioStem stem, double fullScale = 1.0)
    {
        const int bitsPerSample = 24;
        const int channels = 1;
        var rate = (int)Math.Round(stem.SampleRate);
        var bytesPerSample = bitsPerSample / 8;
        var dataBytes = stem.Samples.Length * bytesPerSample * channels;

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                                       // PCM chunk size
        writer.Write((short)1);                                 // PCM
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bytesPerSample);         // byte rate
        writer.Write((short)(channels * bytesPerSample));       // block align
        writer.Write((short)bitsPerSample);

        writer.Write("data"u8);
        writer.Write(dataBytes);

        var peak = 0.0;
        const int max24 = 8_388_607;
        foreach (var sample in stem.Samples)
        {
            var normalised = sample / fullScale;
            peak = Math.Max(peak, Math.Abs(normalised));
            var clamped = Math.Clamp(normalised, -1.0, 1.0);
            var value = (int)Math.Round(clamped * max24);
            writer.Write((byte)(value & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)((value >> 16) & 0xFF));
        }

        return peak;
    }

    /// <summary>Read back a 24-bit mono WAV — used by the round-trip tests.</summary>
    public static AudioStem Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        var rate = 48_000;
        var bits = 24;
        float[]? samples = null;

        while (stream.Position < stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                reader.ReadInt16();
                reader.ReadInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                for (var i = 16; i < size; i++)
                {
                    reader.ReadByte();
                }
            }
            else if (id == "data")
            {
                var bytesPerSample = bits / 8;
                var count = size / bytesPerSample;
                samples = new float[count];
                for (var i = 0; i < count; i++)
                {
                    int value = reader.ReadByte() | (reader.ReadByte() << 8) | (reader.ReadByte() << 16);
                    if ((value & 0x800000) != 0)
                    {
                        value |= unchecked((int)0xFF000000); // sign-extend 24 → 32
                    }

                    samples[i] = value / 8_388_607f;
                }
            }
            else
            {
                stream.Seek(size, SeekOrigin.Current);
            }
        }

        return new AudioStem(Path.GetFileNameWithoutExtension(path), samples ?? [], rate);
    }
}

/// <summary>
/// Writes a complete render: the mix, each stem, and the metadata sidecar
/// (plan §3.6). Stems are normalised together by ONE gain so their relative
/// balance survives export.
/// </summary>
public static class RenderExport
{
    public sealed record Result(string MixPath, IReadOnlyList<string> StemPaths, string MetadataPath, double PeakBeforeClip);

    public static Result Write(
        string directory, string baseName, AudioStem mix, IReadOnlyList<AudioStem> stems, RenderMetadata metadata)
    {
        Directory.CreateDirectory(directory);

        // One shared full-scale so stems keep their relative levels; 0.9 of
        // the mix peak leaves headroom without altering balance.
        var peak = mix.Samples.Length == 0 ? 1.0 : mix.Samples.Max(s => Math.Abs((double)s));
        var fullScale = Math.Max(peak / 0.9, 1e-12);

        var mixPath = Path.Combine(directory, $"{baseName}.wav");
        WavWriter.Write(mixPath, mix, fullScale);

        var stemPaths = new List<string>();
        foreach (var stem in stems)
        {
            var path = Path.Combine(directory, $"{baseName}.{stem.Name}.wav");
            WavWriter.Write(path, stem, fullScale);
            stemPaths.Add(path);
        }

        var metadataPath = Path.Combine(directory, $"{baseName}.json");
        File.WriteAllText(metadataPath, metadata.ToJson());

        return new Result(mixPath, stemPaths, metadataPath, peak / fullScale);
    }
}
