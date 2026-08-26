using System.IO.Compression;
using System.Globalization;
using System.Xml.Linq;
using FluentAssertions;
using WaveBench.Acoustics.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Conformance of <see cref="ZwickerLoudness"/> against the normative
/// ISO 532-1:2017 Annex B validation data.
///
/// <b>This test is licence-gated and does not run by default.</b> ISO
/// publishes the reference program and the Annex B test signals free of
/// charge at <c>standards.iso.org/iso/532/-1/ed-1/en</c>, but that package
/// is ISO-copyrighted and may not be redistributed, so it cannot live in
/// this repository. To run the check, download and extract the package and
/// point <c>WAVEBENCH_ISO532_DIR</c> at the directory containing the
/// <c>Annex B.2</c> folder. Without it these tests report what they would
/// have done and return.
///
/// The result measured against the package is recorded in
/// docs/acoustics.md §4, which is the declaration of conformance ISO 532-1
/// §5.1 asks an implementer to publish.
///
/// ISO 532-1 §5.1 permits an implementation if, for the Annex B stationary
/// signals, total loudness is within ±5% or ±0.1 sone and every specific
/// loudness value is within ±5% or ±0.1 sone/Bark of the test
/// implementation. Both bounds are checked here.
/// </summary>
public class Iso532ConformanceTests(ITestOutputHelper output)
{
    private const string DirectoryVariable = "WAVEBENCH_ISO532_DIR";

    /// <summary>Row 8 of the reference sheet holds z = 0.1 Bark.</summary>
    private const int FirstPatternRow = 8;

    private static string? PackageDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(DirectoryVariable);
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
        }
    }

    [Fact]
    public void Gate_annex_b2_third_octave_test_signal_matches_the_reference_implementation()
    {
        var root = PackageDirectory;
        if (root is null)
        {
            output.WriteLine(
                $"SKIPPED: set {DirectoryVariable} to an extracted copy of the free ISO 532-1 package " +
                "(standards.iso.org/iso/532/-1/ed-1/en) to run the normative conformance check. " +
                "The measured result is recorded in docs/acoustics.md §4.");
            return;
        }

        var annex = Path.Combine(root, "Annex B.2");
        var levels = ReadThirdOctaveLevels(Path.Combine(annex, "Test signal 1.txt"));
        levels.Should().HaveCount(28);

        var cells = ReadSheetCells(Path.Combine(
            annex, "Results and test (stationary loudness based on third octave levels).xlsx"));

        var expectedSone = cells["B8"];
        var expectedPhon = cells["B9"];

        var result = ZwickerLoudness.FromThirdOctaveBands(levels);
        output.WriteLine($"Annex B.2: calculated {result.Sone:F4} sone / {result.Phon:F4} phon, " +
                         $"reference {expectedSone:F4} sone / {expectedPhon:F4} phon " +
                         $"({100.0 * (result.Sone - expectedSone) / expectedSone:+0.00;-0.00}%)");

        WithinTolerance(result.Sone, expectedSone, 0.1).Should().BeTrue(
            $"ISO 532-1 §5.1 allows ±5% or ±0.1 sone; got {result.Sone:F4} against {expectedSone:F4}");

        // The phon figure is a pure function of the sone figure, so it is a
        // check on the conversion rather than on the method.
        result.Phon.Should().BeApproximately(expectedPhon, 0.5);
    }

    [Fact]
    public void Gate_annex_b2_specific_loudness_pattern_matches_at_every_bark()
    {
        var root = PackageDirectory;
        if (root is null)
        {
            output.WriteLine($"SKIPPED: set {DirectoryVariable} — see docs/acoustics.md §4.");
            return;
        }

        var annex = Path.Combine(root, "Annex B.2");
        var levels = ReadThirdOctaveLevels(Path.Combine(annex, "Test signal 1.txt"));
        var cells = ReadSheetCells(Path.Combine(
            annex, "Results and test (stationary loudness based on third octave levels).xlsx"));

        var pattern = ZwickerLoudness.FromThirdOctaveBands(levels).SpecificLoudness;
        pattern.Should().HaveCount(ZwickerLoudness.SpecificLoudnessBins);

        // The scalar total can be right while the pattern is wrong — two
        // errors of opposite sign integrate away. Checking every Bark is what
        // makes this a conformance test rather than a spot check.
        var worst = 0.0;
        var worstBark = 0.0;
        var failures = new List<string>();

        for (var i = 0; i < pattern.Length; i++)
        {
            var reference = cells[$"G{FirstPatternRow + i}"];
            var bark = (i + 1) * 0.1;
            var deviation = reference > 0 ? Math.Abs(pattern[i] - reference) / reference : 0.0;

            if (deviation > worst)
            {
                worst = deviation;
                worstBark = bark;
            }

            if (!WithinTolerance(pattern[i], reference, 0.1))
            {
                failures.Add($"{bark:F1} Bark: {pattern[i]:F4} vs {reference:F4} sone/Bark");
            }
        }

        output.WriteLine($"Annex B.2 pattern: worst deviation {100.0 * worst:F2}% at {worstBark:F1} Bark, " +
                         $"{failures.Count} of {pattern.Length} points outside tolerance");
        foreach (var failure in failures.Take(12))
        {
            output.WriteLine("  " + failure);
        }

        failures.Should().BeEmpty("every specific loudness value must be within ±5% or ±0.1 sone/Bark");
    }

    [Theory]
    [InlineData("Test signal 2 (250 Hz 80 dB).wav", 14.6545)]
    [InlineData("Test signal 3 (1 kHz 60 dB).wav", 4.0192)]
    [InlineData("Test signal 4 (4 kHz 40 dB).wav", 1.5494)]
    [InlineData("Test signal 5 (pinknoise 60 dB).wav", 10.4978)]
    public void Gate_annex_b3_signal_path_matches_the_reference_implementation(string file, double expectedSone)
    {
        // The band-level path is exact (Annex B.2). This exercises the other
        // half — the one-third-octave analyser — which is where engine audio
        // actually enters. It is an FFT power-response approximation of the
        // standard's 6th-order Chebyshev bank rather than that bank itself,
        // so this measures how much that substitution costs.
        var root = PackageDirectory;
        if (root is null)
        {
            output.WriteLine($"SKIPPED: set {DirectoryVariable} — see docs/acoustics.md §4.");
            return;
        }

        var (samples, sampleRate) = ReadWav(Path.Combine(root, "Annex B.3", file));
        var result = ZwickerLoudness.FromSignal(samples, sampleRate);

        output.WriteLine($"{file}: calculated {result.Sone:F4} sone, reference {expectedSone:F4} " +
                         $"({100.0 * (result.Sone - expectedSone) / expectedSone:+0.00;-0.00}%), " +
                         $"{samples.Length} samples at {sampleRate:F0} Hz");

        WithinTolerance(result.Sone, expectedSone, 0.1).Should().BeTrue(
            $"ISO 532-1 §5.1 allows ±5% or ±0.1 sone; got {result.Sone:F4} against {expectedSone:F4}");
    }

    /// <summary>
    /// Sound pressure of a full-scale sample, Pa. ISO 532-1 §4 allows 16-bit
    /// integer signals provided a calibration is supplied, and the package's
    /// own signals use the usual convention that a full-scale 1 kHz sine is
    /// 100 dB SPL — that is 2 Pa RMS, so full scale is 2·√2 Pa. Every file in
    /// the package agrees on this to 0.9%, calibration signal included.
    /// Treating full scale as 1 Pa instead reads them ~9 dB quiet, which
    /// costs about half the loudness.
    /// </summary>
    private static readonly double FullScalePascals = 2.0 * Math.Sqrt(2.0);

    /// <summary>
    /// Minimal RIFF/WAVE reader for the package's signals: 16-bit PCM or
    /// 32-bit float, returning sound pressure in Pa. Float data is already
    /// unnormalised pressure per §4; integer data is calibrated by
    /// <see cref="FullScalePascals"/>.
    /// </summary>
    private static (double[] Samples, double SampleRate) ReadWav(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a RIFF file.");
        }

        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a WAVE file.");
        }

        int format = 0, channels = 0, bits = 0;
        double sampleRate = 0;

        while (reader.BaseStream.Position < reader.BaseStream.Length - 8)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var next = reader.BaseStream.Position + size + (size % 2);

            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
            }
            else if (id == "data")
            {
                var frames = (int)(size / (uint)(channels * (bits / 8)));
                var samples = new double[frames];
                for (var i = 0; i < frames; i++)
                {
                    // Mono in practice; average any extra channels rather than
                    // silently reading only the first.
                    double sum = 0;
                    for (var c = 0; c < channels; c++)
                    {
                        sum += (format, bits) switch
                        {
                            (3, 32) => reader.ReadSingle(),
                            (1, 16) => reader.ReadInt16() / 32768.0 * FullScalePascals,
                            (1, 32) => reader.ReadInt32() / 2147483648.0 * FullScalePascals,
                            _ => throw new NotSupportedException($"WAVE format {format}, {bits} bit."),
                        };
                    }

                    samples[i] = sum / channels;
                }

                return (samples, sampleRate);
            }

            reader.BaseStream.Position = next;
        }

        throw new InvalidDataException($"No data chunk in {Path.GetFileName(path)}.");
    }

    /// <summary>ISO 532-1 §5.1: within ±5% OR within the absolute floor.</summary>
    private static bool WithinTolerance(double actual, double expected, double absolute) =>
        Math.Abs(actual - expected) <= Math.Max(absolute, 0.05 * Math.Abs(expected));

    /// <summary>
    /// Reads the package's level file: "# " comments, one band per line, the
    /// value being whatever follows the colon.
    /// </summary>
    private static double[] ReadThirdOctaveLevels(string path)
    {
        var levels = new List<double>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon >= 0)
            {
                levels.Add(double.Parse(trimmed[(colon + 1)..].Trim(), CultureInfo.InvariantCulture));
            }
        }

        return [.. levels];
    }

    /// <summary>
    /// Numeric cells of the workbook's first sheet, keyed by reference
    /// ("B8"). An xlsx is a zip of XML, so no spreadsheet dependency is
    /// needed — and adding one to read licence-gated data would be worse.
    /// </summary>
    private static Dictionary<string, double> ReadSheetCells(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("xl/worksheets/sheet1.xml")
                    ?? throw new InvalidOperationException($"No first worksheet in {Path.GetFileName(path)}.");

        using var stream = entry.Open();
        var ns = XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var cells = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var cell in XDocument.Load(stream).Descendants(ns + "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            var value = cell.Element(ns + "v")?.Value;

            // Skip shared strings and blanks; only numbers matter here.
            if (reference is not null && value is not null && cell.Attribute("t")?.Value != "s"
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                cells[reference] = number;
            }
        }

        return cells;
    }
}
