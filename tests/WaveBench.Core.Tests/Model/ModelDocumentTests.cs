using FluentAssertions;
using WaveBench.Analysis;
using WaveBench.Core.Solver;
using WaveBench.Model;
using Xunit;

namespace WaveBench.Core.Tests.Model;

public class ModelDocumentTests
{
    private static EngineModelDocument Sample() => new()
    {
        Name = "test",
        Engine = new EngineSpec { BoreMm = 86, StrokeMm = 62, RodLengthMm = 107, CompressionRatio = 11 },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 31, Count = 2, MaxLiftMm = 10, OpenDeg = 340, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 26, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 380 },
        IntakeRunner = new DuctSpec { LengthMm = 600, DiameterMm = 38 },
        ExhaustRunner = new DuctSpec { LengthMm = 200, DiameterMm = 35 },
        Combustion = new CombustionSpec { Fuel = "RON95" },
    };

    [Fact]
    public void Round_trips_through_json_byte_identically()
    {
        var original = Sample();
        var json = original.Save();
        var reloaded = EngineModelDocument.Load(json);
        reloaded.Save().Should().Be(json, "serialisation must be stable (git-diffable, deterministic)");
        reloaded.Should().Be(original);
    }

    [Fact]
    public void Omitted_optional_fields_keep_their_documented_defaults()
    {
        var json = """
            {
              "name": "minimal",
              "engine": { "boreMm": 86, "strokeMm": 62, "rodLengthMm": 107, "compressionRatio": 11 },
              "intakeValves": { "headDiameterMm": 31, "maxLiftMm": 10, "openDeg": 340, "closeDeg": 580 },
              "exhaustValves": { "headDiameterMm": 26, "maxLiftMm": 10, "openDeg": 140, "closeDeg": 380 },
              "intakeRunner": { "lengthMm": 600, "diameterMm": 38 },
              "exhaustRunner": { "lengthMm": 200, "diameterMm": 35 },
              "combustion": { }
            }
            """;
        var document = EngineModelDocument.Load(json);
        document.Solver.Limiter.Should().Be("VanLeer");
        document.Solver.Cfl.Should().Be(0.8);
        document.Combustion!.HeatTransfer.Should().Be("Woschni");
        document.Combustion.Efficiency.Should().Be(0.98);
        document.Ambient.PressureKPa.Should().Be(101.325);
        document.IntakeValves.Count.Should().Be(1);
    }

    [Fact]
    public void Validation_catches_errors_and_warns_on_implausible_inputs()
    {
        var bad = Sample() with
        {
            Engine = new EngineSpec { BoreMm = 86, StrokeMm = 62, RodLengthMm = 40, CompressionRatio = 30 },
        };
        var issues = bad.Validate();
        issues.Should().Contain(i => i.Severity == ModelIssueSeverity.Error && i.Path == "engine.compressionRatio");
        issues.Should().Contain(i => i.Severity == ModelIssueSeverity.Error && i.Path == "engine.rodLengthMm");

        var implausible = Sample() with
        {
            IntakeValves = new ValveTrainSpec { HeadDiameterMm = 31, MaxLiftMm = 16, OpenDeg = 340, CloseDeg = 580 },
        };
        implausible.Validate().Should().Contain(i =>
            i.Severity == ModelIssueSeverity.Warning && i.Path == "intakeValves.maxLiftMm");

        Sample().Validate().Should().NotContain(i => i.Severity == ModelIssueSeverity.Error);
    }

    [Fact]
    public void Builder_refuses_models_with_errors()
    {
        var bad = Sample() with
        {
            IntakeRunner = new DuctSpec { LengthMm = -1, DiameterMm = 38 },
        };
        var act = () => EngineBuilder.Build(bad, 5000.0);
        act.Should().Throw<InvalidOperationException>().WithMessage("*intakeRunner*");
    }

    [Fact]
    public void Built_engine_runs_and_produces_metrics()
    {
        var document = Sample() with
        {
            Solver = new SolverSpec { MinCycles = 2, MaxCycles = 3, CellSizeMm = 10.0 },
        };
        var result = OperatingPointRunner.Run(document, 5000.0);
        result.VolumetricEfficiency.Should().BeInRange(0.4, 1.4);
        result.TorqueNm.Should().BeGreaterThan(0.0);
        result.BsfcGPerKwh.Should().BeGreaterThan(50.0);
    }

    [Fact]
    public void Results_store_round_trips_points_and_captures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wavebench-test-{Guid.NewGuid():N}.db");
        try
        {
            var point = new OperatingPointResult
            {
                Rpm = 5000,
                VolumetricEfficiency = 1.05,
                ImepPa = 12e5,
                BmepPa = 10.5e5,
                TorqueNm = 30.1,
                PowerW = 15_500,
                BsfcGPerKwh = 265.0,
                PeakPressurePa = 75e5,
                KnockIntegral = 0.4,
                CyclesToConvergence = 9,
            };

            long pointId;
            using (var store = new ResultsStore(path))
            {
                var runId = store.BeginRun("test", "0.1", "{}");
                pointId = store.AddPoint(runId, point);
                store.AddCapture(pointId, "tailpipe", 48_000.0, [1.0, -2.5, 3.25]);

                var points = store.ReadPoints(runId);
                points.Should().ContainSingle().Which.Should().Be(point);

                var capture = store.ReadCapture(pointId, "tailpipe");
                capture.Should().Equal(1.0f, -2.5f, 3.25f);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
