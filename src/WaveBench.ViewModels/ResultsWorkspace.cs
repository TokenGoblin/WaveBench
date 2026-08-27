using System.Globalization;
using WaveBench.Analysis;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels;

/// <summary>Tabs of the Results workspace, in shell order (plan §8.4).</summary>
public enum ResultsTab
{
    Performance,
    Waves,
    Cylinders,
    Transient,
}

/// <summary>
/// One captured probe trace, already on a crank-angle grid.
/// </summary>
/// <param name="Name">Probe name.</param>
/// <param name="AnglesDeg">Cycle angle of each sample.</param>
/// <param name="PressurePa">Static pressure.</param>
/// <param name="Velocity">Signed axial velocity, m/s.</param>
public sealed record ProbeTrace(
    string Name,
    IReadOnlyList<double> AnglesDeg,
    IReadOnlyList<double> PressurePa,
    IReadOnlyList<double> Velocity);

/// <summary>
/// Everything one solved run makes available to the Results workspace.
///
/// Deliberately a plain container: the workspace turns it into plots, the
/// results store writes it to disk, and a report reads it. None of those
/// should need a live <see cref="EngineSimulator"/>, which holds a mesh and
/// cannot be serialised or held across a re-run.
/// </summary>
public sealed record RunResult
{
    public required string ModelName { get; init; }

    /// <summary>Sweep points in rpm order; one entry for a single point.</summary>
    public required IReadOnlyList<OperatingPointResult> Points { get; init; }

    /// <summary>The speed the traces and wave fields were captured at.</summary>
    public double CaptureRpm { get; init; }

    public IReadOnlyList<ProbeTrace> Probes { get; init; } = [];

    public IReadOnlyList<DuctFieldCapture> Fields { get; init; } = [];

    /// <summary>Valve events to overlay, as (cycle angle, label).</summary>
    public IReadOnlyList<(double AngleDeg, string Label)> ValveEvents { get; init; } = [];

    /// <summary>Reference state the wave decomposition is taken against.</summary>
    public double ReferencePressurePa { get; init; } = 101_325.0;

    public double ReferenceSoundSpeed { get; init; } = 343.0;

    public double Gamma { get; init; } = 1.4;

    public bool HasSweep => Points.Count > 1;
}

/// <summary>
/// The Results workspace (plan Phase 19, §8.4): performance plots, the x–t
/// wave diagram, per-cylinder charts and transient traces.
///
/// Contains no UI-framework types. Every figure is produced as a
/// <see cref="PlotModel"/>, which is what makes "every plot exports to PNG and
/// SVG" a property of the design rather than a feature bolted on per chart —
/// and what lets the whole workspace be tested without a window.
/// </summary>
public sealed class ResultsWorkspace(RunResult run, UserPreferences? preferences = null)
{
    private readonly RunResult _run = run ?? throw new ArgumentNullException(nameof(run));

    public UserPreferences Preferences { get; } = preferences ?? new UserPreferences();

    public RunResult Run => _run;

    public ResultsTab SelectedTab { get; set; } = ResultsTab.Performance;

    public static IReadOnlyList<(ResultsTab Tab, string Title)> Tabs { get; } =
    [
        (ResultsTab.Performance, "Performance"),
        (ResultsTab.Waves, "Waves"),
        (ResultsTab.Cylinders, "Cylinders"),
        (ResultsTab.Transient, "Transient"),
    ];

    /// <summary>Which pipe the wave diagram is showing.</summary>
    public int SelectedField { get; set; }

    /// <summary>Scrub position on the wave diagram, cycle degrees.</summary>
    public double ScrubAngleDeg { get; set; }

    // ---- Performance ------------------------------------------------------

    /// <summary>
    /// Torque and power against speed. Two quantities, two scales: sharing one
    /// axis prints the power curve against numbers a reader takes for N·m.
    /// </summary>
    public PlotModel TorqueAndPower()
    {
        var rpm = _run.Points.Select(p => p.Rpm).ToList();
        var torque = _run.Points.Select(p => p.TorqueNm).ToList();
        var power = _run.Points.Select(p => p.PowerW / 1000.0).ToList();

        return new PlotModel
        {
            Title = "Torque and power",
            Subtitle = _run.ModelName,
            XAxis = new PlotAxis("Engine speed", Floor(rpm, 500), Ceil(rpm, 500), "rpm"),
            YAxis = new PlotAxis("Torque", 0, Ceil(torque, 10), "N·m"),
            RightAxis = new PlotAxis("Power", 0, Ceil(power, 5), "kW"),
            Series =
            [
                new PlotSeries("Torque", rpm, torque, "Brush.Accent"),
                new PlotSeries("Power", rpm, power, "Brush.Info", PlotSeriesKind.Dashed, RightAxis: true),
            ],
            Markers = PeakMarkers(rpm, torque, power),
        };
    }

    /// <summary>Volumetric efficiency against speed — the breathing curve.</summary>
    public PlotModel VolumetricEfficiency()
    {
        var rpm = _run.Points.Select(p => p.Rpm).ToList();
        var ve = _run.Points.Select(p => p.VolumetricEfficiency).ToList();

        return new PlotModel
        {
            Title = "Volumetric efficiency",
            Subtitle = _run.ModelName,
            XAxis = new PlotAxis("Engine speed", Floor(rpm, 500), Ceil(rpm, 500), "rpm"),
            YAxis = new PlotAxis("VE", 0, Math.Max(1.2, Ceil(ve, 0.1)), ""),
            Series = [new PlotSeries("VE", rpm, ve, "Brush.Success")],
            Notes = ["Above 1.0 the intake is filling the cylinder by wave action, not just by piston displacement."],
        };
    }

    /// <summary>BMEP and BSFC — what the engine makes and what it costs.</summary>
    public PlotModel BmepAndBsfc()
    {
        var rpm = _run.Points.Select(p => p.Rpm).ToList();
        var bmep = _run.Points.Select(p => p.BmepPa / 1e5).ToList();
        var bsfc = _run.Points.Select(p => p.BsfcGPerKwh).ToList();
        var finite = bsfc.Where(double.IsFinite).DefaultIfEmpty(0).ToList();

        return new PlotModel
        {
            Title = "BMEP and BSFC",
            Subtitle = _run.ModelName,
            XAxis = new PlotAxis("Engine speed", Floor(rpm, 500), Ceil(rpm, 500), "rpm"),
            YAxis = new PlotAxis("BMEP", 0, Ceil(bmep, 2), "bar"),
            RightAxis = new PlotAxis("BSFC", Floor(finite, 20), Ceil(finite, 20), "g/kWh"),
            Series =
            [
                new PlotSeries("BMEP", rpm, bmep, "Brush.Accent"),
                new PlotSeries("BSFC", rpm, bsfc, "Brush.Warning", PlotSeriesKind.Dashed, RightAxis: true),
            ],
            Notes = _run.Points.Any(p => !double.IsFinite(p.BsfcGPerKwh))
                ? ["BSFC is undefined at motored points and is omitted there."]
                : [],
        };
    }

    // ---- Cylinders --------------------------------------------------------

    /// <summary>
    /// Per-cylinder VE as bars, with the spread stated (plan §8.4).
    ///
    /// The mean is what a single number would report and it is the number that
    /// hides the problem: two cylinders at 1.05 and two at 0.85 average to the
    /// same 0.95 as four even ones, and only one of those is a manifold worth
    /// building.
    /// </summary>
    public PlotModel PerCylinderVolumetricEfficiency()
    {
        var point = CapturePoint();
        var values = point.PerCylinderVolumetricEfficiency;
        var index = Enumerable.Range(1, values.Length).Select(i => (double)i).ToList();
        var mean = values.Length > 0 ? values.Average() : 0.0;

        return new PlotModel
        {
            Title = "Volumetric efficiency by cylinder",
            Subtitle = $"{_run.ModelName} at {point.Rpm:F0} rpm",
            XAxis = new PlotAxis("Cylinder", 0.5, values.Length + 0.5, "", index),
            YAxis = new PlotAxis("VE", 0, Math.Max(1.2, Ceil(values, 0.1)), ""),
            Series = [new PlotSeries("VE", index, values, "Brush.Accent", PlotSeriesKind.Bar)],
            Notes =
            [
                values.Length < 2
                    ? "Single cylinder: nothing to compare."
                    : $"Mean {mean:F3}, spread {Percent(point.VolumetricEfficiencySpread)} "
                      + $"({values.Min():F3}–{values.Max():F3}).",
            ],
        };
    }

    /// <summary>Knock margin and EGT per cylinder, the two that decide whether it survives.</summary>
    public PlotModel PerCylinderKnockAndEgt()
    {
        var point = CapturePoint();
        var knock = point.PerCylinderKnockIntegral;
        var egt = point.PerCylinderExhaustTemperatureK;
        var index = Enumerable.Range(1, Math.Max(knock.Length, egt.Length)).Select(i => (double)i).ToList();

        var notes = new List<string>();
        if (knock.Length > 0)
        {
            var worst = knock.Max();
            notes.Add(worst >= 1.0
                ? $"Cylinder {Array.IndexOf(knock, worst) + 1} reaches the knock integral at {worst:F2} — "
                  + "autoignition before the flame arrives (Livengood–Wu)."
                : $"Worst knock integral {worst:F2}; 1.0 is onset.");
        }

        if (egt.Any(double.IsFinite))
        {
            var finite = egt.Where(double.IsFinite).ToList();
            notes.Add($"EGT {finite.Min():F0}–{finite.Max():F0} K, mass-weighted at the port.");
        }

        return new PlotModel
        {
            Title = "Knock margin and EGT by cylinder",
            Subtitle = $"{_run.ModelName} at {point.Rpm:F0} rpm",
            XAxis = new PlotAxis("Cylinder", 0.5, index.Count + 0.5, "", index),
            YAxis = new PlotAxis("Knock integral", 0, Math.Max(1.2, Ceil(knock, 0.2)), ""),
            RightAxis = new PlotAxis("EGT", 0, Math.Max(1200, Ceil(egt.Where(double.IsFinite).ToList(), 100)), "K"),
            Series =
            [
                new PlotSeries("Knock integral", index, knock, "Brush.Danger", PlotSeriesKind.Bar),
                new PlotSeries("EGT", index, egt, "Brush.Warning", PlotSeriesKind.Scatter, RightAxis: true),
            ],
            Markers = [new PlotMarker(0.5, "")],
            Notes = notes,
        };
    }

    // ---- Waves ------------------------------------------------------------

    public IReadOnlyList<string> FieldNames => _run.Fields.Select(f => f.Name).ToList();

    /// <summary>
    /// The x–t wave diagram: the selected pipe's field as a heat map over
    /// distance and crank angle, with valve events overlaid.
    /// </summary>
    public PlotModel WaveDiagram()
    {
        if (_run.Fields.Count == 0)
        {
            return Empty("x–t wave diagram", "No pipe was captured for this run.");
        }

        var field = _run.Fields[Math.Clamp(SelectedField, 0, _run.Fields.Count - 1)];
        var (min, max) = field.Range();

        var values = new float[field.FrameCount * field.CellCount];
        for (var f = 0; f < field.FrameCount; f++)
        {
            field.Frame(f).CopyTo(values.AsSpan(f * field.CellCount, field.CellCount));
        }

        var firstAngle = field.FrameAngles.Count > 0 ? field.FrameAngles[0] : 0.0;
        var lastAngle = field.FrameAngles.Count > 0 ? field.FrameAngles[^1] : 720.0;

        return new PlotModel
        {
            Title = $"x–t wave diagram — {field.Name}",
            Subtitle = $"{Quantity(field.Quantity)} at {_run.CaptureRpm:F0} rpm",
            XAxis = new PlotAxis("Distance from the port", 0, field.Length, "m"),
            YAxis = new PlotAxis("Crank angle", firstAngle, lastAngle, "°", CycleTicks(firstAngle, lastAngle)),
            HeatMap = new HeatMapLayer(values, field.CellCount, field.FrameCount, min, max, Quantity(field.Quantity)),
            Notes =
            [
                $"Colour spans {Format(min, field.Quantity)} to {Format(max, field.Quantity)} across the whole "
                + "capture, so a decaying wave visibly decays.",
                "A wave is the diagonal; its gradient is the local wave speed and a reflection is a change of slope.",
            ],
        };
    }

    /// <summary>
    /// One probe's pressure trace split into its rightward- and
    /// leftward-running components, with the returning wave annotated.
    ///
    /// This is the plot the plan describes when it asks the UI to say
    /// <i>"reflected expansion arrives 12° before EVC"</i>.
    /// </summary>
    public PlotModel WaveDecompositionPlot(string? probeName = null)
    {
        var probe = probeName is null
            ? _run.Probes.FirstOrDefault()
            : _run.Probes.FirstOrDefault(p => p.Name == probeName);

        if (probe is null || probe.AnglesDeg.Count == 0)
        {
            return Empty("Wave decomposition", "No probe was captured for this run.");
        }

        var components = new WaveComponents[probe.AnglesDeg.Count];
        for (var i = 0; i < components.Length; i++)
        {
            components[i] = WaveDecomposition.At(
                probe.PressurePa[i], probe.Velocity[i],
                _run.ReferencePressurePa, _run.ReferenceSoundSpeed, _run.Gamma);
        }

        var rightward = components.Select(c => c.RightwardPressurePa / 1000.0).ToList();
        var leftward = components.Select(c => c.LeftwardPressurePa / 1000.0).ToList();
        var total = probe.PressurePa.Select(p => p / 1000.0).ToList();

        var notes = new List<string>();
        var markers = new List<PlotMarker>(ValveMarkers());

        // Annotate the returning expansion against the exhaust valve closing,
        // which is the event it has to beat.
        var evc = _run.ValveEvents.FirstOrDefault(e => e.Label.Contains("EVC", StringComparison.OrdinalIgnoreCase));
        var arrival = WaveDecomposition.Strongest(components, probe.AnglesDeg, WaveSense.Leftward);
        if (arrival is not null)
        {
            markers.Add(new PlotMarker(arrival.AngleDeg, "return", "Brush.Success"));
            notes.Add(evc.Label is not null
                ? WaveDecomposition.Annotate(arrival, evc.AngleDeg, "EVC")
                : $"Strongest returning {arrival.Kind} at {arrival.AngleDeg:F0}° (X {arrival.AmplitudeRatio:F3}).");
        }

        notes.Add("Blair superposition decomposition; homentropic, so the split degrades across a strong "
                  + "temperature gradient.");

        var all = rightward.Concat(leftward).Concat(total).Where(double.IsFinite).ToList();

        return new PlotModel
        {
            Title = $"Wave decomposition — {probe.Name}",
            Subtitle = $"{_run.ModelName} at {_run.CaptureRpm:F0} rpm",
            XAxis = new PlotAxis("Crank angle", 0, 720, "°", [0, 180, 360, 540, 720]),
            YAxis = new PlotAxis("Pressure", Floor(all, 20), Ceil(all, 20), "kPa"),
            Series =
            [
                new PlotSeries("Measured", probe.AnglesDeg, total, "Brush.TextSecondary", PlotSeriesKind.Dotted),
                new PlotSeries("Rightward (outgoing)", probe.AnglesDeg, rightward, "Brush.Accent"),
                new PlotSeries("Leftward (returning)", probe.AnglesDeg, leftward, "Brush.Success",
                    PlotSeriesKind.Dashed),
            ],
            Markers = markers,
            Notes = notes,
        };
    }

    /// <summary>A probe's raw pressure trace with the valve events overlaid.</summary>
    public PlotModel ProbeTracePlot(string? probeName = null)
    {
        var probe = probeName is null
            ? _run.Probes.FirstOrDefault()
            : _run.Probes.FirstOrDefault(p => p.Name == probeName);

        if (probe is null)
        {
            return Empty("Probe trace", "No probe was captured for this run.");
        }

        var kpa = probe.PressurePa.Select(p => p / 1000.0).ToList();

        return new PlotModel
        {
            Title = $"Pressure and velocity — {probe.Name}",
            Subtitle = $"{_run.ModelName} at {_run.CaptureRpm:F0} rpm",
            XAxis = new PlotAxis("Crank angle", 0, 720, "°", [0, 180, 360, 540, 720]),
            YAxis = new PlotAxis("Pressure", Floor(kpa, 20), Ceil(kpa, 20), "kPa"),
            RightAxis = new PlotAxis("Velocity", Floor(probe.Velocity, 50), Ceil(probe.Velocity, 50), "m/s"),
            Series =
            [
                new PlotSeries("Pressure", probe.AnglesDeg, kpa, "Brush.Accent"),
                new PlotSeries("Velocity", probe.AnglesDeg, probe.Velocity, "Brush.Info",
                    PlotSeriesKind.Dashed, RightAxis: true),
            ],
            Markers = ValveMarkers(),
        };
    }

    // ---- Everything, for export ------------------------------------------

    /// <summary>
    /// Every figure this run can produce. The export-all path and the report
    /// generator both walk this, so a plot added to a tab and forgotten here
    /// would be a plot missing from the report.
    /// </summary>
    public IReadOnlyList<PlotModel> AllPlots()
    {
        var plots = new List<PlotModel>();

        if (_run.HasSweep)
        {
            plots.Add(TorqueAndPower());
            plots.Add(VolumetricEfficiency());
            plots.Add(BmepAndBsfc());
        }

        if (CapturePoint().PerCylinderVolumetricEfficiency.Length > 0)
        {
            plots.Add(PerCylinderVolumetricEfficiency());
            plots.Add(PerCylinderKnockAndEgt());
        }

        if (_run.Fields.Count > 0)
        {
            var selected = SelectedField;
            for (var i = 0; i < _run.Fields.Count; i++)
            {
                SelectedField = i;
                plots.Add(WaveDiagram());
            }

            SelectedField = selected;
        }

        foreach (var probe in _run.Probes)
        {
            plots.Add(ProbeTracePlot(probe.Name));
            plots.Add(WaveDecompositionPlot(probe.Name));
        }

        return plots;
    }

    /// <summary>Headline numbers for the tiles above the plots.</summary>
    public IReadOnlyList<DerivedReadout> Headlines()
    {
        if (_run.Points.Count == 0)
        {
            return [];
        }

        var best = _run.Points.MaxBy(p => p.TorqueNm)!;
        var peak = _run.Points.MaxBy(p => p.PowerW)!;
        var breathing = _run.Points.MaxBy(p => p.VolumetricEfficiency)!;
        var economy = _run.Points.Where(p => double.IsFinite(p.BsfcGPerKwh)).MinBy(p => p.BsfcGPerKwh);

        var readouts = new List<DerivedReadout>
        {
            new("Peak torque", $"{best.TorqueNm:F1} N·m", $"at {best.Rpm:F0} rpm"),
            new("Peak power", $"{peak.PowerW / 1000.0:F1} kW", $"at {peak.Rpm:F0} rpm"),
            new("Peak VE", $"{breathing.VolumetricEfficiency:F3}", $"at {breathing.Rpm:F0} rpm"),
        };

        if (economy is not null)
        {
            readouts.Add(new("Best BSFC", $"{economy.BsfcGPerKwh:F0} g/kWh", $"at {economy.Rpm:F0} rpm"));
        }

        var worstKnock = _run.Points.MaxBy(p => p.KnockIntegral)!;
        readouts.Add(new("Knock integral", $"{worstKnock.KnockIntegral:F2}",
            $"worst, at {worstKnock.Rpm:F0} rpm",
            worstKnock.KnockIntegral >= 1.0
                ? "At or past onset: this model knocks at that speed (Livengood–Wu)."
                : null));

        var spread = CapturePoint().VolumetricEfficiencySpread;
        if (spread > 0)
        {
            readouts.Add(new("Cylinder VE spread", Percent(spread),
                $"at {CapturePoint().Rpm:F0} rpm",
                spread > 0.05 ? "Over 5%: the cylinders are not being fed alike." : null));
        }

        return readouts;
    }

    // ---- Internals --------------------------------------------------------

    private OperatingPointResult CapturePoint() =>
        _run.Points.Count == 0
            ? new OperatingPointResult
            {
                Rpm = _run.CaptureRpm, VolumetricEfficiency = 0, ImepPa = 0, BmepPa = 0,
                TorqueNm = 0, PowerW = 0, BsfcGPerKwh = double.NaN, PeakPressurePa = 0,
                KnockIntegral = 0, CyclesToConvergence = 0,
            }
            : _run.Points.MinBy(p => Math.Abs(p.Rpm - _run.CaptureRpm))!;

    private IReadOnlyList<PlotMarker> ValveMarkers() =>
        _run.ValveEvents
            .Select(e => new PlotMarker(e.AngleDeg, e.Label, "Brush.TextSecondary"))
            .ToList();

    private IReadOnlyList<PlotMarker> PeakMarkers(
        IReadOnlyList<double> rpm, IReadOnlyList<double> torque, IReadOnlyList<double> power)
    {
        if (rpm.Count < 2)
        {
            return [];
        }

        var markers = new List<PlotMarker>();
        var bestTorque = torque.IndexOf(torque.Max());
        var bestPower = power.IndexOf(power.Max());
        markers.Add(new PlotMarker(rpm[bestTorque], "peak torque", "Brush.Accent"));
        if (bestPower != bestTorque)
        {
            markers.Add(new PlotMarker(rpm[bestPower], "peak power", "Brush.Info"));
        }

        return markers;
    }

    private static IReadOnlyList<double> CycleTicks(double from, double to)
    {
        var ticks = new List<double>();
        for (var t = Math.Ceiling(from / 180.0) * 180.0; t <= to; t += 180.0)
        {
            ticks.Add(t);
        }

        return ticks.Count > 0 ? ticks : [from, to];
    }

    private static string Quantity(FieldQuantity q) => q switch
    {
        FieldQuantity.Pressure => "Pressure",
        FieldQuantity.Velocity => "Velocity",
        FieldQuantity.Mach => "Mach number",
        FieldQuantity.Temperature => "Temperature",
        _ => q.ToString(),
    };

    private static string Format(float v, FieldQuantity q) => q switch
    {
        FieldQuantity.Pressure => (v / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " kPa",
        FieldQuantity.Velocity => v.ToString("F0", CultureInfo.InvariantCulture) + " m/s",
        FieldQuantity.Temperature => v.ToString("F0", CultureInfo.InvariantCulture) + " K",
        _ => v.ToString("F2", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// A fraction as a percentage, keeping enough digits to be worth reading.
    /// A 0.05% spread printed at one decimal is "0.0%", which says "the
    /// cylinders are identical" when the truth is "too small to show at this
    /// precision" — a different claim, and on a shared collector a wrong one.
    /// </summary>
    private static string Percent(double fraction)
    {
        var pct = fraction * 100.0;
        return pct switch
        {
            0 => "0%",
            < 0.1 => pct.ToString("F3", CultureInfo.InvariantCulture) + "%",
            < 1.0 => pct.ToString("F2", CultureInfo.InvariantCulture) + "%",
            _ => pct.ToString("F1", CultureInfo.InvariantCulture) + "%",
        };
    }

    private static PlotModel Empty(string title, string why) => new()
    {
        Title = title,
        XAxis = new PlotAxis("", 0, 1),
        YAxis = new PlotAxis("", 0, 1),
        Notes = [why],
    };

    private static double Ceil(IReadOnlyList<double> values, double step)
    {
        var max = values.Count == 0 ? 0.0 : values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        return Math.Ceiling(max / step) * step;
    }

    private static double Ceil(IReadOnlyList<float> values, double step) =>
        Ceil(values.Select(v => (double)v).ToList(), step);

    private static double Floor(IReadOnlyList<double> values, double step)
    {
        var min = values.Count == 0 ? 0.0 : values.Where(double.IsFinite).DefaultIfEmpty(0).Min();
        return Math.Floor(min / step) * step;
    }
}

internal static class ListExtensions
{
    public static int IndexOf(this IReadOnlyList<double> list, double value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(value))
            {
                return i;
            }
        }

        return -1;
    }
}
