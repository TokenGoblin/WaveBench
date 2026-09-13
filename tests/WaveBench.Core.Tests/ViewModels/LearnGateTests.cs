using FluentAssertions;
using WaveBench.Model;
using WaveBench.Optimize;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// The Phase 24 gate, stated by the plan as: <i>every user-editable field has
/// "why" text and a typical range; "Show me" works on every numeric parameter
/// participating in the solve; every design warning links to the field or plot
/// causing it.</i>
///
/// All three clauses are coverage claims, and coverage rots. Each is therefore
/// tested by WALKING the catalogue rather than by checking a sample: a field
/// added next year without help text fails here, which is the only way a
/// "every field has…" promise survives contact with a growing model.
/// </summary>
public class LearnGateTests(ITestOutputHelper output)
{
    // ---- Clause 1: why text and a typical range on every field ------------

    [Fact]
    public void Gate_every_editable_field_has_why_text()
    {
        var missing = FieldLocator.All.Where(f => string.IsNullOrWhiteSpace(f.Help)).ToList();

        output.WriteLine($"{FieldLocator.All.Count} editable fields, {missing.Count} without why-text");
        foreach (var field in missing)
        {
            output.WriteLine($"  ! {field.Path}");
        }

        missing.Should().BeEmpty("plan §8.9: \"'Why' on every field\"");
    }

    /// <summary>
    /// A sentence, not a restatement of the label. "Bore" as the help for Bore
    /// satisfies a null check and teaches nothing, so the gate asks for
    /// something long enough to carry a reason.
    /// </summary>
    [Fact]
    public void Why_text_says_something_the_label_does_not()
    {
        foreach (var field in FieldLocator.All)
        {
            field.Help!.Length.Should().BeGreaterThan(field.Label.Length + 20, $"{field.Path} needs a reason, not a restatement");
            field.Help.Should().NotBe(field.Label);
        }
    }

    [Fact]
    public void Gate_every_numeric_field_has_a_typical_range()
    {
        var numeric = FieldLocator.All.Where(f => f.Kind is FieldKind.Number or FieldKind.Integer).ToList();
        var missing = numeric.Where(f => f.Typical is null).ToList();

        output.WriteLine($"{numeric.Count} numeric fields, {missing.Count} without a typical range");
        foreach (var field in missing)
        {
            output.WriteLine($"  ! {field.Path}");
        }

        missing.Should().BeEmpty("plan §8.9: \"one sentence, a typical range\"");
    }

    /// <summary>
    /// The typical range is where answers LAND; the plausibility bounds are
    /// what the field will accept. Confusing the two would make the typical
    /// range either useless (equal to the bounds) or a lie (outside them).
    /// </summary>
    [Fact]
    public void A_typical_range_sits_inside_the_plausible_bounds_and_is_narrower()
    {
        foreach (var field in FieldLocator.All.Where(f => f.Typical is not null))
        {
            var typical = field.Typical!;

            typical.Minimum.Should().BeLessThan(typical.Maximum, field.Path);

            if (field.Minimum is { } min)
            {
                typical.Minimum.Should().BeGreaterThanOrEqualTo(min, $"{field.Path}'s typical range must be reachable");
            }

            if (field.Maximum is { } max)
            {
                typical.Maximum.Should().BeLessThanOrEqualTo(max, $"{field.Path}'s typical range must be reachable");
            }

            if (field.Minimum is { } lo && field.Maximum is { } hi && hi > lo)
            {
                var share = (typical.Maximum - typical.Minimum) / (hi - lo);
                share.Should().BeLessThan(1.0,
                    $"{field.Path}: a typical range as wide as the bounds tells the user nothing");
            }
        }
    }

    /// <summary>
    /// Plan §8.10: <i>"Implausible input detection with a warning, NEVER a
    /// hard block"</i>. An unusual value is still accepted and still written.
    /// </summary>
    [Fact]
    public void An_unusual_value_is_warned_about_and_still_accepted()
    {
        var session = new ProjectSession(TestEngine());
        var editor = new FieldEditor(session, new UserPreferences());
        var field = DesignCatalogue.Find("Engine.CompressionRatio")!;

        // 17.5:1 is a diesel, not a petrol engine — inside the bounds, well
        // outside the usual range.
        var outcome = editor.Apply(field, "17.5");
        var warning = editor.Unusual(field);

        output.WriteLine($"accepted: {outcome.Accepted}");
        output.WriteLine($"typical:  {editor.DescribeTypical(field)}");
        output.WriteLine($"warning:  {warning}");

        outcome.Accepted.Should().BeTrue("§8.10 warns, it does not block");
        session.Document.Engine.CompressionRatio.Should().Be(17.5, "the edit must actually have been written");
        warning.Should().NotBeNullOrWhiteSpace().And.Subject.Should().Contain("above");

        // ...and an ordinary value says nothing at all.
        editor.Apply(field, "11").Accepted.Should().BeTrue();
        editor.Unusual(field).Should().BeNull("11:1 is an ordinary compression ratio and needs no comment");
    }

    /// <summary>
    /// Found by looking at the screen: wall roughness read <i>"0 mm is below
    /// the usual 0–0.3 mm (nearest usual value 0 mm)"</i> — arithmetically
    /// correct, because the range started at 0.0015, and self-evidently absurd
    /// to read, because the field displays to two decimals. A warning the user
    /// cannot see in the number is not a warning.
    /// </summary>
    [Fact]
    public void A_warning_never_contradicts_the_numbers_it_prints()
    {
        var session = new ProjectSession(TestEngine());
        var editor = new FieldEditor(session, new UserPreferences());

        foreach (var field in FieldLocator.All.Where(f => f.Typical is not null))
        {
            foreach (var probe in new[] { field.Typical!.Minimum, field.Typical.Maximum, field.Minimum ?? 0 })
            {
                if (editor.Unusual(field, probe) is not { } message)
                {
                    continue;
                }

                // The value and the bound it is said to be outside must not
                // print the same, or the sentence argues with itself.
                var shown = editor.Format(field, probe);
                var nearest = editor.Format(field, field.Typical.NearestEnd(probe)!.Value);

                output.WriteLine($"{field.Path}: {message}");
                shown.Should().NotBe(nearest, $"{field.Path} warns about {shown} by comparing it to {nearest}");
            }
        }
    }

    [Fact]
    public void A_typical_range_is_described_in_the_users_own_units()
    {
        var session = new ProjectSession(TestEngine());
        var field = DesignCatalogue.Find("Engine.BoreMm")!;

        var metric = new FieldEditor(session, new UserPreferences { Units = UnitSystem.Metric });
        var imperial = new FieldEditor(session, new UserPreferences { Units = UnitSystem.Imperial });

        output.WriteLine("metric:   " + metric.DescribeTypical(field));
        output.WriteLine("imperial: " + imperial.DescribeTypical(field));

        metric.DescribeTypical(field).Should().Contain("65").And.Contain("mm");
        imperial.DescribeTypical(field).Should().Contain("in").And.NotContain("65 ");
    }

    // ---- Clause 2: "Show me" on every numeric parameter in the solve ------

    [Fact]
    public void Gate_show_me_is_offered_on_every_numeric_field_or_says_why_not()
    {
        var numeric = FieldLocator.All.Where(f => f.Kind is FieldKind.Number or FieldKind.Integer).ToList();
        var unexplained = numeric
            .Where(f => !ShowMe.Supports(f))
            .Where(f => !ShowMe.NotInTheSteadySolve.TryGetValue(f.Path, out var why) || string.IsNullOrWhiteSpace(why))
            .ToList();

        output.WriteLine($"{numeric.Count} numeric fields");
        output.WriteLine($"{numeric.Count(ShowMe.Supports)} sweepable");
        foreach (var (path, why) in ShowMe.NotInTheSteadySolve)
        {
            output.WriteLine($"  excluded: {path} — {why}");
        }

        unexplained.Should().BeEmpty(
            "a numeric field that \"Show me\" refuses must say why, or the gate has a silent hole in it");

        // The exclusions have to name real fields, or they are a list of
        // typos that quietly excuses nothing.
        foreach (var path in ShowMe.NotInTheSteadySolve.Keys)
        {
            FieldLocator.Find(path).Should().NotBeNull($"'{path}' is excluded from Show me but is not a field");
        }
    }

    /// <summary>
    /// The feature has to actually solve. A "Show me" that returned a plausible
    /// shape without running the model would be the most convincing wrong
    /// answer in the application, so this asserts the response MOVES — and in
    /// the direction the physics requires.
    /// </summary>
    [Fact]
    public void Gate_show_me_runs_a_real_sweep_of_one_parameter()
    {
        var document = TestEngine();
        var showMe = new ShowMe(document, new UserPreferences())
        {
            Speeds = [4000, 6000, 8000],
            Steps = 4,
        };

        var study = showMe.Run("IntakeRunner.LengthMm");

        output.WriteLine($"{study.Label}: {study.Samples.Count} values in {study.Elapsed.TotalSeconds:F1} s");
        output.WriteLine("   value    peak Nm   at rpm    area");
        foreach (var sample in study.Samples)
        {
            output.WriteLine(sample.Failed
                ? $"  {sample.Display,6}   FAILED — {sample.Failure}"
                : $"  {sample.Display,6}  {sample.PeakTorqueNm,8:F2}  {sample.RpmAtPeakTorque,7:F0}  {sample.AreaUnderTorque,8:N0}");
        }

        output.WriteLine("");
        output.WriteLine(study.Narration);

        study.Solved.Should().HaveCountGreaterThanOrEqualTo(3, "a sweep with two points is not a curve");
        study.Flat.Should().BeFalse("runner length changes what the engine does — that is the whole lesson");

        // Every value tried is a value the field would have accepted.
        var field = DesignCatalogue.Find("IntakeRunner.LengthMm")!;
        study.Samples.Should().OnlyContain(s => s.Value >= field.Minimum && s.Value <= field.Maximum);

        // Plan §8.11: no information conveyed by colour alone. Every overlaid
        // curve gets its own line style, which is what fixes the step count at
        // four — there are only four styles a curve can take.
        study.Curves.Series.Select(s => s.Kind).Should().OnlyHaveUniqueItems(
            "overlaid curves must be distinguishable without colour");
        study.Response.Series.Select(s => s.Kind).Should().OnlyHaveUniqueItems();
        study.AllPlots().SelectMany(p => p.Series).Should()
            .OnlyContain(s => !string.IsNullOrWhiteSpace(s.StyleDescription));

        // The figures must be drawable: an axis that does not contain its own
        // series is the defect that got past three reviews in Phase 22.
        foreach (var plot in study.AllPlots())
        {
            foreach (var series in plot.Series)
            {
                var axis = series.RightAxis ? plot.RightAxis! : plot.YAxis;
                series.Y.Where(double.IsFinite).Should().OnlyContain(
                    y => y >= axis.Min - 1e-9 && y <= axis.Max + 1e-9,
                    $"{plot.Title}/{series.Name} must fit inside its axis");
                series.X.Where(double.IsFinite).Should().OnlyContain(
                    x => x >= plot.XAxis.Min - 1e-9 && x <= plot.XAxis.Max + 1e-9);
            }
        }
    }

    /// <summary>
    /// The teaching claim itself, at the shipped defaults: a longer intake
    /// runner tunes LOWER. If "Show me" cannot show that, it is a chart
    /// generator rather than a teaching feature — and the timing is printed
    /// because §8.9 promises "a ten-second experiment", which is a claim about
    /// cost as much as about content.
    /// </summary>
    [Fact]
    public void Show_me_reproduces_the_tuned_length_trade_at_the_shipped_defaults()
    {
        var study = new ShowMe(TestEngine(), new UserPreferences()).Run("IntakeRunner.LengthMm");

        output.WriteLine($"{study.Samples.Count} values × {5} speeds in {study.Elapsed.TotalSeconds:F1} s");
        output.WriteLine("   value    peak Nm   at rpm   low-end   top-end   low/top");
        foreach (var sample in study.Solved)
        {
            output.WriteLine(
                $"  {sample.Display,6}  {sample.PeakTorqueNm,8:F2}  {sample.RpmAtPeakTorque,7:F0}  "
                + $"{sample.LowEndTorqueNm,8:F2}  {sample.TopEndTorqueNm,8:F2}  "
                + $"{sample.LowEndTorqueNm / sample.TopEndTorqueNm,8:F3}");
        }

        output.WriteLine("");
        output.WriteLine(study.Narration);

        var shortest = study.Solved[0];
        var longest = study.Solved[^1];

        // Stated as a RATIO, because the tuned-length trade is about where the
        // torque is, not how much of it there is. Peak torque and rpm-at-peak
        // are both poorly conditioned on a five-point speed grid — rpm-at-peak
        // can only take five values — so neither is what this asserts.
        (longest.LowEndTorqueNm / longest.TopEndTorqueNm).Should()
            .BeGreaterThan(shortest.LowEndTorqueNm / shortest.TopEndTorqueNm,
                "a longer intake runner resonates at a lower frequency, so it moves torque toward the "
                + "bottom of the range — this is the whole point of the figure");
    }

    /// <summary>
    /// The sweep has to contain the user's own design, or the first question
    /// they ask of the chart is one it cannot answer.
    /// </summary>
    [Fact]
    public void A_show_me_sweep_brackets_the_value_the_model_actually_holds()
    {
        var document = TestEngine() with { IntakeRunner = new DuctSpec { LengthMm = 1200, DiameterMm = 44 } };
        var showMe = new ShowMe(document, new UserPreferences());
        var field = DesignCatalogue.Find("IntakeRunner.LengthMm")!;

        var values = showMe.Values(field);
        output.WriteLine($"current 1200 mm, swept {string.Join(", ", values.Select(v => v.ToString("F0")))}");

        values.Max().Should().BeGreaterThanOrEqualTo(1200, "a sweep that excludes the current design is somebody else's engine");
        values.Min().Should().BeLessThanOrEqualTo(field.Typical!.Minimum);
    }

    [Fact]
    public void Show_me_refuses_a_field_the_steady_solve_does_not_read_and_says_why()
    {
        var showMe = new ShowMe(TestEngine(), new UserPreferences());

        var refusal = Assert.Throws<InvalidOperationException>(
            () => showMe.Run("PipeThermal.ArealHeatCapacityJPerM2K"));

        output.WriteLine(refusal.Message);
        refusal.Message.Should().Contain("converged", "the refusal has to teach, not just decline");
    }

    // ---- Clause 3: every warning links to what causes it ------------------

    [Fact]
    public void Gate_every_design_warning_links_to_a_field_or_a_plot()
    {
        var warnings = AllWarnings().ToList();
        warnings.Should().HaveCountGreaterThan(8, "the fixtures must actually provoke warnings, or this proves nothing");

        output.WriteLine($"{warnings.Count} warnings raised across the fixtures");

        foreach (var (source, warning) in warnings)
        {
            output.WriteLine($"  [{source}] {warning.Message}");
            foreach (var link in warning.Targets)
            {
                output.WriteLine($"        → {link.Kind}: {link.Describe()}");
            }

            warning.Targets.Should().NotBeEmpty(
                $"[{source}] \"{warning.Message}\" has nowhere to send the user");
        }
    }

    /// <summary>
    /// A link that does not resolve is worse than no link: it promises a
    /// destination and delivers a dead end.
    /// </summary>
    [Fact]
    public void Every_warning_link_resolves_to_something_that_exists()
    {
        var shell = new ShellViewModel(new ProjectSession(TestEngine()), new UserPreferences());

        foreach (var (source, warning) in AllWarnings())
        {
            foreach (var link in warning.Targets)
            {
                switch (link.Kind)
                {
                    case WarningTarget.Field:
                        FieldLocator.Find(link.Target).Should().NotBeNull(
                            $"[{source}] links to field '{link.Target}', which is in no catalogue");
                        link.Workspace.Should().NotBeNull($"[{source}] field link must know where the field lives");
                        break;

                    case WarningTarget.Plot:
                        link.Workspace.Should().NotBeNull($"[{source}] a plot link must name a workspace");
                        shell.Workspaces.First(w => w.Workspace == link.Workspace!.Value)
                            .SubTabs.Should().Contain(link.Target,
                                $"[{source}] links to '{link.Workspace} → {link.Target}', which is not a sub-tab there");
                        break;

                    default:
                        link.Target.Should().NotBeNullOrWhiteSpace();
                        break;
                }

                link.Describe().Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    /// <summary>
    /// A node link has to name a component that is on the canvas, or clicking
    /// it selects nothing.
    /// </summary>
    [Fact]
    public void A_node_link_names_a_component_that_is_actually_there()
    {
        var session = new ProjectSession(BadManifold());
        var workspace = new ManifoldWorkspace(session, new UserPreferences());

        var nodeLinks = workspace.Warnings()
            .SelectMany(w => w.Targets)
            .Where(l => l.Kind == WarningTarget.Node)
            .ToList();

        nodeLinks.Should().NotBeEmpty("the bad manifold has component-level problems");

        foreach (var link in nodeLinks)
        {
            output.WriteLine($"  node link → {link.Target}");
            workspace.Manifold!.Node(link.Target).Should().NotBeNull(
                $"'{link.Target}' is linked but not on the canvas");
        }
    }

    // ---- Fixtures ---------------------------------------------------------

    /// <summary>
    /// A 2-litre four. Built here rather than taken from the shipped sample so
    /// that a change to the sample cannot quietly change what this gate tests.
    /// </summary>
    private static EngineModelDocument TestEngine() => new()
    {
        Name = "learn gate engine",
        Engine = new EngineSpec
        {
            BoreMm = 86, StrokeMm = 86, RodLengthMm = 145, CompressionRatio = 10.5, CylinderCount = 4,
        },
        IntakeValves = new ValveTrainSpec { HeadDiameterMm = 33, Count = 2, MaxLiftMm = 10, OpenDeg = 350, CloseDeg = 580 },
        ExhaustValves = new ValveTrainSpec { HeadDiameterMm = 28, Count = 2, MaxLiftMm = 10, OpenDeg = 140, CloseDeg = 370 },
        IntakeRunner = new DuctSpec { LengthMm = 300, DiameterMm = 40 },
        ExhaustRunner = new DuctSpec { LengthMm = 400, DiameterMm = 38 },
        Combustion = new CombustionSpec { Fuel = "RON95", Lambda = 0.95 },
    };


    /// <summary>
    /// Every warning the application can raise, from documents built to
    /// provoke them. Sampling one model would test whichever warnings that
    /// model happens to trip.
    /// </summary>
    private static IEnumerable<(string Source, DesignWarning Warning)> AllWarnings()
    {
        var manifold = new ManifoldWorkspace(new ProjectSession(BadManifold()), new UserPreferences());
        foreach (var warning in manifold.Warnings())
        {
            yield return ("manifold", warning);
        }

        foreach (var (name, document) in BadBoostModels())
        {
            var session = new ProjectSession(document);
            var boost = new BoostWorkspace(session, new UserPreferences());
            foreach (var warning in boost.Warnings())
            {
                yield return ("boost/" + name, warning);
            }
        }
    }

    /// <summary>A manifold with a separated diffuser, a stub, a tee and a choked collector.</summary>
    private static EngineModelDocument BadManifold()
    {
        var document = TestEngine();
        var session = new ProjectSession(document);
        var workspace = new ManifoldWorkspace(session, new UserPreferences());

        workspace.ApplyConfiguration("4-2-1");

        var spec = workspace.Manifold!;
        foreach (var node in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Pipe).Take(2))
        {
            workspace.EditNode(node.Id, n =>
            {
                // A 100 mm cone from 40 to 80 mm is an 11° half-angle, the
                // plan's own worked example.
                n.LengthMm = 100;
                n.DiameterMm = 40;
                n.OutletDiameterMm = 80;
            });
        }

        foreach (var junction in spec.Nodes.Where(n => n.Kind == ManifoldNodeKind.Junction))
        {
            workspace.EditNode(junction.Id, n => n.BranchAngleDeg = 90);
        }

        return session.Document;
    }

    /// <summary>Boosted models, each broken in a different direction.</summary>
    private static IEnumerable<(string Name, EngineModelDocument Document)> BadBoostModels()
    {
        var boosted = TestEngine() with
        {
            ForcedInduction = new ForcedInductionSpec
            {
                Aspiration = AspirationKinds.Turbocharged,
                TurboName = TurboLibrary.Names[0],
                TargetBoostKPa = 150,
            },
        };

        // No turbo at all.
        yield return ("no-turbo", boosted with
        {
            ForcedInduction = boosted.ForcedInduction with { TurboName = "" },
        });

        // The largest turbo in the library on a small engine: surge.
        yield return ("oversized", boosted with
        {
            ForcedInduction = boosted.ForcedInduction with { TurboName = TurboLibrary.Names[^1] },
        });

        // The smallest, asked for a lot of boost: choke, back pressure, heat.
        yield return ("undersized", boosted with
        {
            ForcedInduction = boosted.ForcedInduction with
            {
                TurboName = TurboLibrary.Names[0],
                TargetBoostKPa = 250,
                TurbineAreaRatio = 0.35,
            },
        });
    }
}
