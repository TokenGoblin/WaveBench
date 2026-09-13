using WaveBench.Core.Solver;
using WaveBench.Model;

namespace WaveBench.ViewModels.Reporting;

/// <summary>
/// The Report workspace (plan §8.4): <i>"One-click PDF/HTML"</i>.
///
/// One click, but not one instant — a report with mesh-sensitivity evidence in
/// it costs three extra solves, and the plan wants that evidence published by
/// default rather than on request (§5.3). So the state of a generation lives
/// here and the screen stays usable while it runs, the same way a "Show me"
/// sweep does.
/// </summary>
public sealed class ReportWorkspace(ProjectSession session, UserPreferences? preferences = null)
{
    private CancellationTokenSource? _cancellation;

    public UserPreferences Preferences { get; } = preferences ?? new UserPreferences();

    /// <summary>The completed sweep the report will describe, if there is one.</summary>
    public RunResult? Run { get; set; }

    /// <summary>The acoustic case, if the Sound workspace has been set up.</summary>
    public SoundWorkspace? Sound { get; set; }

    /// <summary>
    /// Whether to run the mesh-sensitivity study. On by default: a report
    /// without it cannot say whether its numbers are a property of the engine
    /// or of the grid.
    /// </summary>
    public bool IncludeMeshStudy { get; set; } = true;

    public bool IsGenerating { get; private set; }

    /// <summary>The most recent report, for the preview and for writing out.</summary>
    public ReportDocument? Latest { get; private set; }

    /// <summary>Where the last generation wrote to, in order.</summary>
    public IReadOnlyList<string> WrittenFiles { get; private set; } = [];

    public string? Error { get; private set; }

    /// <summary>Raised when the state changes, so the view can redraw.</summary>
    public Action? Changed { get; set; }

    /// <summary>
    /// What the report will be able to say, before it is generated. A screen
    /// that offers a button and nothing else leaves the user to discover after
    /// a minute of solving that half the sections were going to be empty.
    /// </summary>
    public IReadOnlyList<DerivedReadout> Readiness()
    {
        var readouts = new List<DerivedReadout>
        {
            new("Performance", Run is { Points.Count: > 0 } run ? $"{run.Points.Count} operating points" : "no run",
                Run is null ? null : "Torque, power, VE, BMEP and BSFC, with the figures.",
                Run is null ? "Run a sweep first, or the report carries no results at all." : null),

            new("Mesh sensitivity", IncludeMeshStudy ? "will be run" : "skipped",
                "Three extra solves at 0.5×, 1× and 2× cell size.",
                IncludeMeshStudy ? null : "Without it the report cannot distinguish a result from a grid artefact."),

            new("Acoustics", Sound is null ? "not attached" : "attached",
                "Order structure and transmission loss.",
                Sound is null ? "Open the Sound workspace to include a sound decision." : null),

            new("Boost matching", session.Document.ForcedInduction.IsForced ? "included" : "not applicable",
                session.Document.ForcedInduction.IsForced
                    ? "Compressor map with this engine's operating line, margins and the A/R trade."
                    : "This model is naturally aspirated."),
        };

        // Named for what it counts. "Caveats: 5" beside a preview reading "10
        // caveats" is two true numbers that look like a contradiction: this
        // one is what is wrong with THIS MODEL, and the report adds the
        // standing limits of the tool itself on top.
        var caveats = new Guardrails(session, Preferences).All();
        readouts.Add(new("Caveats from this model", $"{caveats.Count}",
            "Each with what would remove it. The report adds the tool's own standing limits — the acoustic "
            + "model's, and every validation case still open — so its total is higher.",
            caveats.Any(c => c.Weight == CaveatWeight.Dominant)
                ? "One of them is the dominant error source in this model."
                : null));

        return readouts;
    }

    /// <summary>Build the report without writing anything — used by the preview and by tests.</summary>
    public ReportDocument Build(MeshSensitivityResult? mesh = null)
    {
        var builder = new ReportBuilder(session, Preferences)
        {
            Run = Run,
            Sound = Sound,
            MeshStudy = mesh,
        };

        return builder.Build();
    }

    /// <summary>
    /// Generate and write both formats into <paramref name="directory"/>.
    /// Returns the task so a test can await it; the view redraws on
    /// <see cref="Changed"/>.
    /// </summary>
    public Task GenerateAsync(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        IsGenerating = true;
        Error = null;
        Changed?.Invoke();

        var document = session.Document;
        var run = Run;
        var includeMesh = IncludeMeshStudy;

        return Task.Run(() =>
        {
            try
            {
                MeshSensitivityResult? mesh = null;
                if (includeMesh && run is { Points.Count: > 0 } points)
                {
                    var at = points.Points[points.Points.Count / 2].Rpm;
                    mesh = OperatingPointRunner.MeshSensitivity(document, at);
                }

                cancellation.Token.ThrowIfCancellationRequested();

                var report = Build(mesh);
                Directory.CreateDirectory(directory);

                var html = Path.Combine(directory, "report.html");
                var pdf = Path.Combine(directory, "report.pdf");

                File.WriteAllText(html, HtmlReportWriter.Write(report));
                File.WriteAllBytes(pdf, PdfReportWriter.Write(report));

                Latest = report;
                WrittenFiles = [html, pdf];
            }
            catch (OperationCanceledException)
            {
                // Cancelling is not a failure; nothing was promised.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or ArithmeticException or InvalidOperationException)
            {
                Error = e.Message;
            }
            finally
            {
                IsGenerating = false;
                Changed?.Invoke();
            }
        }, cancellation.Token);
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        IsGenerating = false;
    }
}
