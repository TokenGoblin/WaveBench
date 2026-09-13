using FluentAssertions;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Writes a real report to disk so it can be LOOKED AT.
///
/// The gate tests assert that the strings are present. They cannot tell
/// whether the document is readable — whether a figure overprints its notes,
/// whether a table is clipped, whether the page breaks land somewhere absurd.
/// Three of the Phase 22 defects and six of the Phase 24 defects were
/// arithmetically self-consistent and visibly wrong, so the report gets the
/// same treatment as the screens did.
///
/// The output directory is opt-in through an environment variable, so CI does
/// not write files nobody reads and the path never appears in the repository.
/// </summary>
public class ReportSampleTests(ITestOutputHelper output)
{
    [Fact]
    public void Write_a_sample_report_for_inspection()
    {
        var directory = Environment.GetEnvironmentVariable("WAVEBENCH_REPORT_OUT");
        if (string.IsNullOrWhiteSpace(directory))
        {
            output.WriteLine("WAVEBENCH_REPORT_OUT is not set; nothing written.");
            return;
        }

        Directory.CreateDirectory(directory);

        var document = new EngineModelDocument
        {
            Name = "FSAE 600, turbocharged",
            Engine = new EngineSpec
            {
                BoreMm = 67, StrokeMm = 42.5, RodLengthMm = 90, CompressionRatio = 11.5, CylinderCount = 4,
            },
            IntakeValves = new ValveTrainSpec { HeadDiameterMm = 23, Count = 2, MaxLiftMm = 8, OpenDeg = 350, CloseDeg = 570 },
            ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 20, Count = 2, MaxLiftMm = 8, OpenDeg = 150, CloseDeg = 370 },
            IntakeRunner = new DuctSpec { LengthMm = 250, DiameterMm = 34 },
            ExhaustRunner = new DuctSpec { LengthMm = 450, DiameterMm = 32 },
            Combustion = new CombustionSpec { Fuel = "RON95", Lambda = 0.85 },
            Solver = new SolverSpec { CellSizeMm = 14.0, MinCycles = 3, MaxCycles = 8 },
            ForcedInduction = new ForcedInductionSpec
            {
                Aspiration = AspirationKinds.Turbocharged,
                TurboName = TurboLibrary.Names[1],
                TargetBoostKPa = 120,
                RestrictorFitted = true,
                RestrictorThroatMm = 20,
            },
        };

        var session = new ProjectSession(document);
        session.EditByUser("Combustion.Lambda", 0.85);
        session.EditByImport("IntakeValves.MaxLiftMm", 8.0, "cam-measured.csv");
        session.EditByOptimiser("IntakeRunner.LengthMm", 250.0, "opt-2026-09-01");

        var speeds = new double[] { 4000, 5000, 6000, 7000, 8000, 9000, 10000 };

        var builder = new ReportBuilder(session)
        {
            Run = new RunResult
            {
                ModelName = document.Name,
                Points = OperatingPointRunner.Sweep(session.Document, speeds),
            },
            MeshStudy = OperatingPointRunner.MeshSensitivity(session.Document, 8000),
            Sound = new SoundWorkspace(
                WaveBench.Acoustics.SoundCases.M50Factory(),
                WaveBench.Acoustics.SoundCases.M50EqualLength(),
                new UserPreferences()),
        };

        builder.Audio.Add(new ReportAudio(
            "Full-throttle sweep, 4000–10000 rpm", "sweep-4000-10000.flac",
            "48 kHz 24-bit, exterior listener at 2 m, seed 1"));

        var report = builder.Build();
        var html = Path.Combine(directory, "report.html");
        var pdf = Path.Combine(directory, "report.pdf");

        File.WriteAllText(html, HtmlReportWriter.Write(report));
        File.WriteAllBytes(pdf, PdfReportWriter.Write(report));

        output.WriteLine($"{report.Sections.Count} sections, {report.Figures.Count} figures, "
                         + $"{report.Caveats.Count} caveats, {report.Claims.Count} sourced claims");
        output.WriteLine($"html {new FileInfo(html).Length:N0} bytes");
        output.WriteLine($"pdf  {new FileInfo(pdf).Length:N0} bytes");

        new FileInfo(pdf).Length.Should().BeGreaterThan(20_000);
        new FileInfo(html).Length.Should().BeGreaterThan(40_000);
    }
}
