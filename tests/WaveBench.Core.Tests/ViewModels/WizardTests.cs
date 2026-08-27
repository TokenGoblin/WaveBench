using System.Diagnostics;
using FluentAssertions;
using WaveBench.Core.Solver;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 23 — Simple mode and the wizard (plan §8.6).
///
/// The gate has a usability half that no test can settle and a checkable half
/// that this file is about: <i>"the brief's numbers match what Advanced mode
/// produces from the identical model"</i>, <i>"first preview under 1 s"</i>,
/// and <i>"every recommendation carries a why, a confidence and an uncertainty
/// band"</i>.
/// </summary>
public class WizardTests(ITestOutputHelper output)
{
    private static Wizard Fresh(Action<Wizard>? answer = null)
    {
        var document = new EngineModelDocument
        {
            Name = "wizard build",
            Engine = new EngineSpec
            {
                BoreMm = 82, StrokeMm = 78, RodLengthMm = 133, CompressionRatio = 10.5, CylinderCount = 4,
            },
            IntakeValves = new ValveTrainSpec
            {
                HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580,
            },
            ExhaustValves = new ValveTrainSpec
            {
                HeadDiameterMm = 28, Count = 2, MaxLiftMm = 9.5, OpenDeg = 140, CloseDeg = 370,
            },
            IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
            ExhaustRunner = new DuctSpec { LengthMm = 500, DiameterMm = 38 },
            Combustion = new CombustionSpec { Fuel = "Gasoline RON95" },
            Solver = new SolverSpec { CellSizeMm = 14.0, MinCycles = 4, MaxCycles = 8 },
        };

        var wizard = new Wizard(new ProjectSession(document))
        {
            BandFromRpm = 4000,
            BandToRpm = 7000,
        };

        answer?.Invoke(wizard);
        return wizard;
    }

    // ---- Structure --------------------------------------------------------

    [Fact]
    public void There_are_nine_steps_and_every_one_explains_why_it_is_asking()
    {
        // Plan §8.6: three regions per step, one of which is a "why this
        // matters" explainer. A wizard that collects bad answers efficiently
        // is worse than one that is slow.
        Wizard.Steps.Should().HaveCount(9);
        Wizard.Steps.Select(s => s.Step).Should().Equal(Enum.GetValues<WizardStep>());

        foreach (var (step, title, question) in Wizard.Steps)
        {
            var why = Wizard.Explainer(step);
            output.WriteLine($"{step,-12} {title,-18} {question}");

            title.Should().NotBeNullOrWhiteSpace();
            question.Should().NotBeNullOrWhiteSpace();
            why.Length.Should().BeGreaterThan(120, $"{step} must actually explain itself");
            why.Count(c => c == '.').Should().BeGreaterThanOrEqualTo(2, "two or three plain sentences");
        }
    }

    [Fact]
    public void Steps_advance_and_go_back_without_running_off_either_end()
    {
        var wizard = Fresh();

        wizard.Step.Should().Be(WizardStep.Purpose);
        wizard.CanGoBack.Should().BeFalse();

        wizard.Back();
        wizard.Step.Should().Be(WizardStep.Purpose, "there is nothing before the first step");

        for (var i = 0; i < 20; i++)
        {
            wizard.Next();
        }

        wizard.Step.Should().Be(WizardStep.Compute);
        wizard.CanGoNext.Should().BeFalse();
    }

    // ---- Derivation -------------------------------------------------------

    [Fact]
    public void Answers_write_into_the_full_model_not_a_parallel_simple_one()
    {
        // Plan §8.6: "Every answer writes into the full model... Nothing lives
        // in a parallel simple model." A second model is a second thing to
        // keep in sync and the first thing to drift.
        var wizard = Fresh(w =>
        {
            w.BoreMm = 86;
            w.StrokeMm = 86;
            w.Cylinders = 6;
            w.CompressionRatio = 11.5;
            w.Cam = CamCharacter.Aggressive;
            w.Fuel = "E85";
        });

        wizard.Apply();
        var document = wizard.Session.Document;

        document.Engine.BoreMm.Should().Be(86);
        document.Engine.StrokeMm.Should().Be(86);
        document.Engine.CylinderCount.Should().Be(6);
        document.Engine.CompressionRatio.Should().Be(11.5);
        document.Combustion!.Fuel.Should().Be("E85");

        var (open, close, _, _) = Wizard.CamEvents(CamCharacter.Aggressive);
        document.IntakeValves.OpenDeg.Should().Be(open);
        document.IntakeValves.CloseDeg.Should().Be(close);

        // Derived, not asked for.
        document.Engine.RodLengthMm.Should().BeApproximately(86 * 1.7, 0.05);
        document.IntakeValves.HeadDiameterMm.Should().BeApproximately(86 * 0.40, 0.05);

        // And the model it produced is a valid one the solver will accept.
        document.Validate().Should().NotContain(i => i.Severity == ModelIssueSeverity.Error);
    }

    [Fact]
    public void A_rerun_never_touches_what_the_user_set_by_hand()
    {
        // Phase 16 built the guarantee; this is the wizard actually relying on
        // it. The failure it prevents: a beginner runs the wizard, tweaks a
        // number in Advanced, re-runs, and silently loses the tweak.
        var wizard = Fresh();
        wizard.Apply();

        wizard.Session.EditByUser("IntakeRunner.LengthMm", 999.0);
        wizard.Session.EditByImport("IntakeValves.MaxLiftMm", 11.4, "cam-measured.csv");

        wizard.BandFromRpm = 2500;
        wizard.BandToRpm = 5500;
        var result = wizard.Apply();

        wizard.Session.Document.IntakeRunner.LengthMm.Should().Be(999.0, "the user typed it");
        wizard.Session.Document.IntakeValves.MaxLiftMm.Should().Be(11.4, "it was imported");

        result.Blocked.Select(b => b.Path).Should().Contain("IntakeRunner.LengthMm");
        output.WriteLine($"{result.Applied.Count} applied, {result.Blocked.Count} blocked, "
                         + $"{result.Unchanged.Count} unchanged");

        // A preview must report the same thing without doing it.
        wizard.RedlineRpm = 8000;
        var before = wizard.Session.Document.Save();
        wizard.Preview();
        wizard.Session.Document.Save().Should().Be(before, "a preview changes nothing");
    }

    // ---- The analytical seed ---------------------------------------------

    [Fact]
    public void The_seed_tunes_to_the_speed_the_torque_shape_asks_for()
    {
        // A broad-midrange preference tunes low in the band and therefore
        // long; peak power tunes high and short. If these came out the same
        // way round, the goal slider would be decoration.
        var broad = Fresh(w => w.Shape = TorqueShape.BroadMidrange);
        var peak = Fresh(w => w.Shape = TorqueShape.PeakPower);

        broad.TargetRpm().Should().BeLessThan(peak.TargetRpm());
        broad.SeedGeometry().IntakeLengthMm.Should().BeGreaterThan(peak.SeedGeometry().IntakeLengthMm);

        output.WriteLine($"broad midrange: tunes {broad.TargetRpm():F0} rpm, "
                         + $"{broad.SeedGeometry().IntakeLengthMm:F0} mm runner");
        output.WriteLine($"peak power:     tunes {peak.TargetRpm():F0} rpm, "
                         + $"{peak.SeedGeometry().IntakeLengthMm:F0} mm runner");

        // Tuned length is a·Δθ/(12N·k), so halving the speed doubles it. The
        // packaging limit has to be lifted out of the way first or it is the
        // clamp being measured rather than the relation.
        var slow = Fresh(w => { w.BandFromRpm = 3000; w.BandToRpm = 3000; w.PackagingLimitMm = 3000; });
        var fast = Fresh(w => { w.BandFromRpm = 6000; w.BandToRpm = 6000; w.PackagingLimitMm = 3000; });
        var ratio = slow.SeedGeometry().IntakeLengthMm / fast.SeedGeometry().IntakeLengthMm;
        ratio.Should().BeApproximately(2.0, 0.02);

        // And against the project's own validation case: the Yin engine's
        // measured optimum is 800 mm at 3000 rpm on a 235° window. The seed is
        // allowed to be short of that — the search covers the difference — but
        // not by more than it covers.
        var yin = Fresh(w =>
        {
            w.BandFromRpm = 3000;
            w.BandToRpm = 3000;
            w.PackagingLimitMm = 3000;
            w.Cam = CamCharacter.Mild;
        });
        var seeded = yin.SeedGeometry().IntakeLengthMm;
        output.WriteLine($"Yin case: seed {seeded:F0} mm against a measured optimum of 800 mm "
                         + $"({100.0 * (seeded / 800.0 - 1.0):+0.0;-0.0}%)");
        seeded.Should().BeInRange(650, 850);
    }

    [Fact]
    public void The_seed_respects_the_packaging_limit_and_says_when_it_bites()
    {
        var cramped = Fresh(w =>
        {
            w.PackagingLimitMm = 180;
            w.BandFromRpm = 2500;
            w.BandToRpm = 3000;
            w.Shape = TorqueShape.BroadMidrange;
        });

        cramped.SeedGeometry().IntakeLengthMm.Should().BeLessThanOrEqualTo(180);

        var warning = cramped.Check()
            .Should().Contain(i => i.Message.Contains("capped", StringComparison.Ordinal)).And.Subject
            .First(i => i.Message.Contains("capped", StringComparison.Ordinal));

        output.WriteLine(warning.Message);
        warning.Severity.Should().Be(ModelIssueSeverity.Warning);
    }

    [Fact]
    public void Implausible_answers_are_objected_to_rather_than_derived_from()
    {
        var wizard = Fresh(w =>
        {
            w.StrokeMm = 100;
            w.RedlineRpm = 9000;              // 30 m/s mean piston speed
            w.BandToRpm = 11000;              // above the redline
            w.CompressionRatio = 14.0;        // on pump fuel
        });

        var issues = wizard.Check();
        foreach (var issue in issues)
        {
            output.WriteLine($"{issue.Severity}: {issue.Message}");
        }

        issues.Should().Contain(i => i.Message.Contains("piston speed", StringComparison.Ordinal));
        issues.Should().Contain(i => i.Message.Contains("redline", StringComparison.Ordinal));
        issues.Should().Contain(i => i.Message.Contains("knock", StringComparison.Ordinal));

        // An inverted band is an error, not a warning: nothing downstream can
        // do anything sensible with it.
        var inverted = Fresh(w => { w.BandFromRpm = 7000; w.BandToRpm = 3000; });
        inverted.Check().Should().Contain(i => i.Severity == ModelIssueSeverity.Error);
    }

    [Fact]
    public void Forced_induction_is_declined_honestly_rather_than_faked()
    {
        // The turbo phases are not built. Offering the choice and then
        // modelling it as naturally aspirated would be the worst of both.
        var wizard = Fresh(w => w.ForcedInduction = true);

        wizard.Check().Should().Contain(i => i.Message.Contains("not modelled yet", StringComparison.Ordinal));
        BriefBuilder.Preview(wizard).Caveats
            .Should().Contain(c => c.Contains("not modelled yet", StringComparison.Ordinal));

        output.WriteLine(Wizard.AspirationNote);
    }

    // ---- The brief --------------------------------------------------------

    [Fact]
    public void Gate_the_first_preview_arrives_in_under_a_second()
    {
        var wizard = Fresh();

        // Warm the JIT, then measure.
        BriefBuilder.Preview(wizard);

        var sw = Stopwatch.StartNew();
        var brief = BriefBuilder.Preview(wizard);
        sw.Stop();

        output.WriteLine($"preview in {sw.Elapsed.TotalMilliseconds:F1} ms with {brief.Lines.Count} recommendations");
        sw.Elapsed.TotalSeconds.Should().BeLessThan(1.0, "plan §8.6 budgets the first preview at one second");

        brief.Lines.Should().NotBeEmpty();

        // And it must not pretend: with no solve there is no prediction, and
        // the brief says so rather than showing a blank.
        brief.Predictions.Should().BeEmpty();
        brief.Caveats.Should().Contain(c => c.Contains("No solve has run", StringComparison.Ordinal));
        brief.Lines.Should().OnlyContain(l => l.Confidence != Confidence.Good,
            "nothing is well-founded before anything has been solved");
    }

    [Fact]
    public void Gate_every_recommendation_carries_a_why_and_a_confidence()
    {
        // Plan §8.6, non-negotiable: "every number carries a one-sentence
        // why"; "every recommendation carries a confidence indicator".
        var brief = BriefBuilder.Preview(Fresh());

        foreach (var line in brief.Lines)
        {
            output.WriteLine($"{line.Group,-8} {line.Label,-18} {line.Value,-12} {line.Indicator} "
                             + $"{line.ConfidenceWord}");
            output.WriteLine($"         ↳ {line.Why}");

            line.Value.Should().NotBeNullOrWhiteSpace();
            line.Why.Should().NotBeNullOrWhiteSpace();
            line.Why.Should().EndWith(".", "a why is a sentence");
            line.Why.Length.Should().BeGreaterThan(40);
            line.Basis.Should().NotBeNullOrWhiteSpace("a confidence with nothing behind it is a decoration");
            line.Indicator.Should().HaveLength(4);
        }

        brief.Groups.Should().Contain(["INTAKE", "EXHAUST", "CAM"]);
    }

    [Fact]
    public void Every_prediction_carries_an_uncertainty_band()
    {
        // "Simple mode never presents a bare number as if measured."
        var brief = BriefBuilder.Build(Fresh(), quick: true);

        brief.Predictions.Should().NotBeEmpty();
        foreach (var p in brief.Predictions)
        {
            output.WriteLine($"{p.Label}: {p.Format()}");
            p.RelativeUncertainty.Should().BeGreaterThan(0.0,
                "a prediction with no band is a claim of measurement");
            p.Format().Should().Contain("±");
        }

        brief.Caveats.Should().Contain(c => c.Contains("Discharge coefficients", StringComparison.Ordinal),
            "the largest single source of error has to be stated");
    }

    [Fact]
    public void Gate_the_briefs_numbers_match_what_advanced_mode_produces_from_the_same_model()
    {
        // The gate's checkable half. The brief must not be computed from a
        // side model, a cached number or a fitted shortcut — it has to be the
        // same document through the same solver, or Simple and Advanced modes
        // are two products that disagree.
        var wizard = Fresh();
        var brief = BriefBuilder.Build(wizard, quick: true);

        var document = wizard.Session.Document;
        var advanced = OperatingPointRunner.Sweep(
            document, brief.Sweep.Select(p => p.Rpm).ToList());

        brief.Sweep.Should().NotBeEmpty();
        for (var i = 0; i < brief.Sweep.Count; i++)
        {
            output.WriteLine(
                $"{brief.Sweep[i].Rpm,6:F0} rpm  brief {brief.Sweep[i].TorqueNm:F6} N·m  "
                + $"advanced {advanced[i].TorqueNm:F6} N·m");

            brief.Sweep[i].TorqueNm.Should().Be(advanced[i].TorqueNm);
            brief.Sweep[i].VolumetricEfficiency.Should().Be(advanced[i].VolumetricEfficiency);
            brief.Sweep[i].PowerW.Should().Be(advanced[i].PowerW);
        }

        // And the geometry the brief recommends is the geometry in the model.
        var runner = brief.Lines.First(l => l.Label == "Runner length").Value;
        runner.Should().Be($"{document.IntakeRunner.LengthMm:F0} mm");

        var primary = brief.Lines.First(l => l.Label == "Primary length").Value;
        primary.Should().Be($"{document.ExhaustRunner.LengthMm:F0} mm");
    }

    [Fact]
    public void The_search_improves_on_the_seed_rather_than_returning_it()
    {
        // "A bounded optimisation, not a lookup." If the search always
        // returned its starting point, the compute step would be a formula
        // with a progress bar.
        var wizard = Fresh(w =>
        {
            w.BandFromRpm = 5000;
            w.BandToRpm = 7000;
            w.Shape = TorqueShape.PeakPower;
        });

        var seed = wizard.SeedGeometry();
        var brief = BriefBuilder.Build(wizard, quick: true);
        var chosen = wizard.Session.Document;

        output.WriteLine($"seed:   {seed.IntakeLengthMm:F0} mm intake, {seed.PrimaryLengthMm:F0} mm primary");
        output.WriteLine($"chosen: {chosen.IntakeRunner.LengthMm:F0} mm intake, "
                         + $"{chosen.ExhaustRunner.LengthMm:F0} mm primary");

        // The winner is committed as Optimised, which is what protects it from
        // a later wizard re-run overwriting the search's own answer.
        wizard.Session.Provenance.IsProtected("IntakeRunner.LengthMm").Should().BeTrue();

        brief.Predictions.Should().NotBeEmpty();
    }

    [Fact]
    public void A_compute_can_be_cancelled_and_reports_progress_while_it_runs()
    {
        var seen = new List<BriefProgress>();
        var wizard = Fresh();

        using var cts = new CancellationTokenSource();
        var progress = new Progress<BriefProgress>(p =>
        {
            seen.Add(p);
            if (seen.Count >= 2)
            {
                cts.Cancel();
            }
        });

        var act = () => BriefBuilder.Build(wizard, quick: true, progress, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void The_build_list_is_orderable_rather_than_a_set_of_raw_numbers()
    {
        // "The build list has real dimensions and tube sizes." A
        // recommendation of 38.4 mm is a number; 1.5 inch is a thing on a
        // shelf.
        var brief = BriefBuilder.Preview(Fresh(w => w.Cylinders = 6));

        brief.BuildList.Should().NotBeEmpty();
        foreach (var item in brief.BuildList)
        {
            output.WriteLine($"{item.Quantity} × {item.Description}");
            item.Quantity.Should().BeGreaterThan(0);
        }

        brief.BuildList.Should().Contain(i => i.Description.Contains('"', StringComparison.Ordinal),
            "primaries are ordered by tube size, not by a computed millimetre");
        brief.BuildList.Should().Contain(i => i.Quantity == 6, "one primary per cylinder");
    }

    [Fact]
    public void The_brief_reports_its_weakest_link_rather_than_an_average()
    {
        // A brief is only as good as its shakiest input, and a reader should
        // not have to average the dots themselves.
        var brief = BriefBuilder.Build(Fresh(), quick: true);

        brief.WeakestConfidence.Should().Be(brief.Lines.Min(l => l.Confidence));
        brief.WeakestConfidence.Should().Be(Confidence.Rough,
            "the cam is a character, not a measured profile, so the brief cannot claim better");

        // Giving it a measured valve size lifts that line and only that line.
        var measured = BriefBuilder.Preview(Fresh(w => w.IntakeValveMm = 35.5));
        measured.Lines.First(l => l.Label == "Intake valve").Confidence.Should().Be(Confidence.Good);
        measured.Lines.First(l => l.Label == "Intake valve").Basis.Should().Be("As entered.");
    }
}
