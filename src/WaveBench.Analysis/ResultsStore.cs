using Microsoft.Data.Sqlite;
using WaveBench.Core.Solver;

namespace WaveBench.Analysis;

/// <summary>
/// SQLite results store (plan Phase 7 / §7.3): queryable, single file, no
/// server. Schema: runs (model snapshot + metadata) → points (per operating
/// point) → captures (optional high-resolution probe traces, float32 blobs
/// with a documented basis — the §3.4 acoustic capture lands on this table).
/// </summary>
public sealed class ResultsStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public ResultsStore(string path)
    {
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                model_name TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                model_json TEXT NOT NULL,
                note TEXT
            );
            CREATE TABLE IF NOT EXISTS points (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL REFERENCES runs(id),
                rpm REAL NOT NULL,
                ve REAL NOT NULL,
                imep_pa REAL NOT NULL,
                bmep_pa REAL NOT NULL,
                torque_nm REAL NOT NULL,
                power_w REAL NOT NULL,
                bsfc_g_per_kwh REAL,
                peak_pressure_pa REAL NOT NULL,
                knock_integral REAL NOT NULL,
                cycles INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS captures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                point_id INTEGER NOT NULL REFERENCES points(id),
                probe TEXT NOT NULL,
                sample_rate_hz REAL NOT NULL,
                samples BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public long BeginRun(string modelName, string schemaVersion, string modelJson, string? note = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO runs (created_utc, model_name, schema_version, model_json, note) " +
            "VALUES ($created, $name, $schema, $json, $note); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$name", modelName);
        command.Parameters.AddWithValue("$schema", schemaVersion);
        command.Parameters.AddWithValue("$json", modelJson);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    public long AddPoint(long runId, OperatingPointResult point)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO points (run_id, rpm, ve, imep_pa, bmep_pa, torque_nm, power_w,
                                bsfc_g_per_kwh, peak_pressure_pa, knock_integral, cycles)
            VALUES ($run, $rpm, $ve, $imep, $bmep, $torque, $power, $bsfc, $peak, $knock, $cycles);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$rpm", point.Rpm);
        command.Parameters.AddWithValue("$ve", point.VolumetricEfficiency);
        command.Parameters.AddWithValue("$imep", point.ImepPa);
        command.Parameters.AddWithValue("$bmep", point.BmepPa);
        command.Parameters.AddWithValue("$torque", point.TorqueNm);
        command.Parameters.AddWithValue("$power", point.PowerW);
        command.Parameters.AddWithValue("$bsfc", double.IsNaN(point.BsfcGPerKwh) ? DBNull.Value : point.BsfcGPerKwh);
        command.Parameters.AddWithValue("$peak", point.PeakPressurePa);
        command.Parameters.AddWithValue("$knock", point.KnockIntegral);
        command.Parameters.AddWithValue("$cycles", point.CyclesToConvergence);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Store a probe trace as float32 samples (plan §3.4 storage format).</summary>
    public void AddCapture(long pointId, string probe, double sampleRateHz, ReadOnlySpan<double> samples)
    {
        var blob = new byte[samples.Length * sizeof(float)];
        for (var i = 0; i < samples.Length; i++)
        {
            BitConverter.TryWriteBytes(blob.AsSpan(i * sizeof(float)), (float)samples[i]);
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO captures (point_id, probe, sample_rate_hz, samples) VALUES ($point, $probe, $rate, $blob);";
        command.Parameters.AddWithValue("$point", pointId);
        command.Parameters.AddWithValue("$probe", probe);
        command.Parameters.AddWithValue("$rate", sampleRateHz);
        command.Parameters.AddWithValue("$blob", blob);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<OperatingPointResult> ReadPoints(long runId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT rpm, ve, imep_pa, bmep_pa, torque_nm, power_w, bsfc_g_per_kwh, " +
                              "peak_pressure_pa, knock_integral, cycles FROM points WHERE run_id = $run ORDER BY rpm;";
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        var points = new List<OperatingPointResult>();
        while (reader.Read())
        {
            points.Add(new OperatingPointResult
            {
                Rpm = reader.GetDouble(0),
                VolumetricEfficiency = reader.GetDouble(1),
                ImepPa = reader.GetDouble(2),
                BmepPa = reader.GetDouble(3),
                TorqueNm = reader.GetDouble(4),
                PowerW = reader.GetDouble(5),
                BsfcGPerKwh = reader.IsDBNull(6) ? double.NaN : reader.GetDouble(6),
                PeakPressurePa = reader.GetDouble(7),
                KnockIntegral = reader.GetDouble(8),
                CyclesToConvergence = reader.GetInt32(9),
            });
        }

        return points;
    }

    public float[] ReadCapture(long pointId, string probe)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT samples FROM captures WHERE point_id = $point AND probe = $probe;";
        command.Parameters.AddWithValue("$point", pointId);
        command.Parameters.AddWithValue("$probe", probe);
        var blob = (byte[]?)command.ExecuteScalar() ?? throw new KeyNotFoundException($"No capture '{probe}'.");
        var samples = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, samples, 0, blob.Length);
        return samples;
    }

    public void Dispose() => _connection.Dispose();
}
