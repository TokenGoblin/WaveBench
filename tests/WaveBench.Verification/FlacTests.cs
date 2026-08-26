using FluentAssertions;
using WaveBench.Acoustics.Auralisation;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 10 §3.6 verification of FLAC export. The deferral note said a wrong
/// FLAC file is worse than none, so the bar here is that the bytes decode
/// back to exactly the samples that went in, with both frame CRCs and the
/// STREAMINFO MD5 validating — plus, in CI, the reference <c>flac -t</c>
/// over a produced file, because an encoder and decoder written from one
/// reading of a spec can share a misreading.
/// </summary>
public class FlacTests(ITestOutputHelper output)
{
    private const double SampleRate = 48_000.0;
    private const int Max24 = 8_388_607;

    private static AudioStem Stem(string name, Func<int, double> shape, int count) =>
        new(name, Enumerable.Range(0, count).Select(i => (float)shape(i)).ToArray(), SampleRate);

    /// <summary>The 24-bit values the writers quantise to, for exact comparison.</summary>
    private static int[] Quantise(AudioStem stem) =>
        stem.Samples.Select(s => (int)Math.Round(Math.Clamp((double)s, -1.0, 1.0) * Max24)).ToArray();

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"wavebench-flac-{Guid.NewGuid():N}.flac");

    private void RoundTrip(AudioStem stem, string description)
    {
        var path = TempPath();
        try
        {
            FlacWriter.Write(path, stem);
            var decoded = FlacReader.Read(path);

            var expected = Quantise(stem);
            decoded.SampleRate.Should().Be(48_000);
            decoded.BitsPerSample.Should().Be(24);
            decoded.Channels.Should().Be(1);
            decoded.Samples.Should().HaveCount(expected.Length);
            decoded.Samples.Should().Equal(expected, $"{description} must survive the round trip exactly");

            var size = new FileInfo(path).Length;
            var wavBytes = (expected.Length * 3L) + 44;
            output.WriteLine($"{description}: {expected.Length} samples, {size} bytes " +
                             $"({100.0 * size / wavBytes:F1}% of the equivalent WAV)");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Gate_a_synthesised_engine_note_round_trips_exactly()
    {
        // Harmonic stack with a decaying spectrum — the shape a real render
        // has, and the case the fixed predictors are chosen for.
        RoundTrip(
            Stem("engine", i =>
            {
                double sum = 0;
                for (var h = 1; h <= 12; h++)
                {
                    sum += Math.Sin(2.0 * Math.PI * 90.0 * h * i / SampleRate) / h;
                }

                return 0.35 * sum;
            }, 40_000),
            "engine harmonic stack");
    }

    [Fact]
    public void Gate_noise_round_trips_exactly()
    {
        // Incompressible input: exercises the large-residual path and the
        // verbatim fallback rather than the predictors.
        var random = new Random(1234);
        RoundTrip(Stem("noise", _ => (random.NextDouble() * 2.0) - 1.0, 20_000), "white noise");
    }

    [Fact]
    public void Gate_silence_and_constant_blocks_round_trip_exactly()
    {
        RoundTrip(Stem("silence", _ => 0.0, 12_000), "digital silence");
        RoundTrip(Stem("dc", _ => 0.25, 9_000), "constant offset");
    }

    [Fact]
    public void Gate_full_scale_and_extremes_round_trip_exactly()
    {
        // Alternating full scale is the worst case for the fixed predictors:
        // residuals grow with order, so the encoder must fall back rather
        // than overflow.
        RoundTrip(Stem("square", i => i % 2 == 0 ? 1.0 : -1.0, 8_000), "alternating full scale");
        RoundTrip(Stem("ramp", i => (2.0 * (i % 4096) / 4096.0) - 1.0, 8_192), "sawtooth");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8192)]
    [InlineData(12_290)]
    public void Lengths_around_the_block_boundary_round_trip_exactly(int count)
    {
        // 4097 samples would leave a 1-sample final frame, and RFC 9639 §8.2
        // requires the minimum block size to be at least 16 — the block plan
        // has to notice that.
        RoundTrip(Stem($"n{count}", i => 0.4 * Math.Sin(2.0 * Math.PI * 440.0 * i / SampleRate), count),
            $"{count} samples");
    }

    [Fact]
    public void Gate_the_stream_is_structurally_a_flac_file()
    {
        var path = TempPath();
        try
        {
            FlacWriter.Write(path, Stem("x", i => 0.3 * Math.Sin(i * 0.01), 5_000));
            var bytes = File.ReadAllBytes(path);

            bytes[..4].Should().Equal("fLaC"u8.ToArray(), "the stream marker");

            // First metadata block must be STREAMINFO, last-block flag set,
            // length 34.
            (bytes[4] & 0x7F).Should().Be(0, "the first metadata block is STREAMINFO");
            ((bytes[4] & 0x80) != 0).Should().BeTrue("it is also the last metadata block here");
            var length = (bytes[5] << 16) | (bytes[6] << 8) | bytes[7];
            length.Should().Be(34, "STREAMINFO is always 34 bytes");

            // The first frame must start with the sync code.
            var frame = 8 + 34;
            bytes[frame].Should().Be(0xFF);
            (bytes[frame + 1] & 0xFE).Should().Be(0xF8, "sync is 0b111111111111100 then the blocking strategy");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Gate_corruption_is_detected_rather_than_decoded()
    {
        // The whole point of the CRCs and the MD5: a damaged stream must fail
        // loudly, not return plausible audio.
        var path = TempPath();
        try
        {
            FlacWriter.Write(path, Stem("x", i => 0.3 * Math.Sin(i * 0.02), 9_000));
            var bytes = File.ReadAllBytes(path);

            // Flip a bit well inside the first frame's payload.
            var target = 8 + 34 + 24;
            bytes[target] ^= 0x08;

            var act = () => FlacReader.Decode(bytes);
            act.Should().Throw<InvalidDataException>("a corrupt frame must not decode silently");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Flac_and_wav_exports_carry_identical_audio()
    {
        // The two writers must quantise the same way, or a user comparing
        // them hears a difference that is not in the model.
        var stem = Stem("both", i => 0.42 * Math.Sin(2.0 * Math.PI * 220.0 * i / SampleRate), 16_000);
        var flacPath = TempPath();
        var wavPath = Path.ChangeExtension(flacPath, ".wav");
        try
        {
            FlacWriter.Write(flacPath, stem);
            WavWriter.Write(wavPath, stem);

            var fromFlac = FlacReader.Read(flacPath).Samples;
            var fromWav = WavWriter.Read(wavPath).Samples
                .Select(s => (int)Math.Round((double)s * Max24))
                .ToArray();

            fromFlac.Should().Equal(fromWav, "lossless means lossless");
        }
        finally
        {
            File.Delete(flacPath);
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void Compression_actually_compresses_a_tonal_render()
    {
        // If FLAC came out no smaller than WAV there would be no reason to
        // offer it, and the fixed predictors would not be working.
        var stem = Stem("tonal", i =>
        {
            double sum = 0;
            for (var h = 1; h <= 8; h++)
            {
                sum += Math.Sin(2.0 * Math.PI * 120.0 * h * i / SampleRate) / (h * h);
            }

            return 0.5 * sum;
        }, 48_000);

        var path = TempPath();
        try
        {
            FlacWriter.Write(path, stem);
            var flacBytes = new FileInfo(path).Length;
            var wavBytes = (stem.Samples.Length * 3L) + 44;

            output.WriteLine($"tonal render: WAV {wavBytes} bytes, FLAC {flacBytes} bytes " +
                             $"({100.0 * flacBytes / wavBytes:F1}%)");
            flacBytes.Should().BeLessThan((long)(wavBytes * 0.75),
                "fixed predictors should get well under three quarters on tonal material");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
