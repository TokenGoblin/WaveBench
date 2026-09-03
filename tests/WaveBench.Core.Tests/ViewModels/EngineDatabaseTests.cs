using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// A curated library of real engines that seeds a session from known facts
/// (bore, stroke, compression ratio, cylinder count — whatever a source like
/// Wikipedia actually publishes), with everything else filled the same way
/// the wizard fills any gap. Not part of the 26-phase build contract; held to
/// the same provenance discipline as <c>WaveBench.Boost.TurboDatabase</c>.
/// </summary>
public class EngineDatabaseTests(ITestOutputHelper output)
{
    private static EngineEntry Valid(Action<EngineEntryBuilder>? edit = null)
    {
        var builder = new EngineEntryBuilder();
        edit?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>Mutable scratch for building test entries without an eleven-argument record initialiser.</summary>
    private sealed class EngineEntryBuilder
    {
        public string Name = "Test Four";
        public double BoreMm = 82.0;
        public double StrokeMm = 78.0;
        public double? CompressionRatio = 10.0;
        public int CylinderCount = 4;
        public double? DisplacementCc = Math.PI / 4.0 * 82.0 * 82.0 * 78.0 * 4 / 1000.0;
        public double? PeakPowerRpm = 6500.0;
        public EngineAspiration Aspiration = EngineAspiration.NaturallyAspirated;
        public string Source = "Wikipedia, \"Test Four\", https://en.wikipedia.org/wiki/Test_Four";
        public string Licence = "CC BY-SA 4.0 (Wikipedia), bare fact only.";

        public EngineEntry Build() => new()
        {
            Name = Name,
            Manufacturer = "Test",
            BoreMm = BoreMm,
            StrokeMm = StrokeMm,
            CompressionRatio = CompressionRatio,
            CylinderCount = CylinderCount,
            DisplacementCc = DisplacementCc,
            PeakPowerRpm = PeakPowerRpm,
            Aspiration = Aspiration,
            Source = Source,
            Licence = Licence,
        };
    }

    // ---- Validate -----------------------------------------------------

    [Fact]
    public void A_well_formed_entry_validates_without_throwing()
    {
        var act = () => Valid().Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void An_entry_with_no_source_is_rejected()
    {
        var entry = Valid(b => b.Source = "");
        var act = entry.Validate;
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void An_entry_with_no_licence_is_rejected()
    {
        var entry = Valid(b => b.Licence = "  ");
        var act = entry.Validate;
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Bore_and_stroke_must_be_positive()
    {
        Valid(b => b.BoreMm = 0).Invoking(e => e.Validate()).Should().Throw<InvalidDataException>();
        Valid(b => b.StrokeMm = -1).Invoking(e => e.Validate()).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_compression_ratio_outside_the_models_representable_range_is_rejected()
    {
        Valid(b => b.CompressionRatio = 1.0).Invoking(e => e.Validate()).Should().Throw<InvalidDataException>();
        Valid(b => b.CompressionRatio = 30.0).Invoking(e => e.Validate()).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_null_compression_ratio_is_allowed_when_the_source_does_not_publish_one()
    {
        // The 2JZ-GTE entry is exactly this case (see EngineLibrary's remarks).
        Valid(b => b.CompressionRatio = null).Invoking(e => e.Validate()).Should().NotThrow();
    }

    [Fact]
    public void A_published_displacement_far_from_bore_times_stroke_times_count_is_rejected()
    {
        // Real bore/stroke (82x78x4) computes ~1653cc; claiming 2000cc published
        // is a >20% gap, which should read as a mistranscription, not a quirk.
        var entry = Valid(b => b.DisplacementCc = 2000.0);
        var act = entry.Validate;
        act.Should().Throw<InvalidDataException>().WithMessage("*mistranscribed*");
    }

    [Fact]
    public void A_published_displacement_within_rounding_of_the_computed_figure_is_accepted()
    {
        // Real engines round bore/stroke to 0.1mm and displacement to the
        // nearest cc, so a whisker of disagreement must not fail the gate.
        var entry = Valid(b => b.DisplacementCc = Math.Round((double)b.DisplacementCc! * 1.005));
        entry.Invoking(e => e.Validate()).Should().NotThrow();
    }

    // ---- EngineDatabase -------------------------------------------------

    [Fact]
    public void Adding_two_engines_with_the_same_name_is_rejected()
    {
        var database = new EngineDatabase();
        database.Add(Valid());

        var act = () => database.Add(Valid());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        var database = new EngineDatabase();
        database.Add(Valid(b => b.Name = "BMW S54B32"));

        database.Find("bmw s54b32").Should().NotBeNull();
        database.Find("nonexistent").Should().BeNull();
    }

    [Fact]
    public void A_database_round_trips_through_json_including_nullable_fields()
    {
        var database = new EngineDatabase();
        database.Add(Valid(b => { b.Name = "Round Trip"; b.CompressionRatio = null; b.PeakPowerRpm = null; }));

        var json = database.Save();
        output.WriteLine(json);
        var reloaded = EngineDatabase.Load(json);

        var entry = reloaded.Find("Round Trip");
        entry.Should().NotBeNull();
        entry!.CompressionRatio.Should().BeNull();
        entry.PeakPowerRpm.Should().BeNull();
        entry.BoreMm.Should().Be(82.0);
    }

    // ---- Seed -------------------------------------------------------------

    [Fact]
    public void Seeding_a_known_engine_produces_a_document_with_no_validation_errors()
    {
        var entry = Valid();
        var session = entry.Seed();

        var issues = session.Document.Validate();
        var errors = issues.Where(i => i.Severity == ModelIssueSeverity.Error).ToList();
        foreach (var issue in issues)
        {
            output.WriteLine($"{issue.Severity} {issue.Path}: {issue.Message}");
        }

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Cited_facts_are_stamped_imported_and_protected_while_everything_else_stays_derived()
    {
        var entry = Valid();
        var session = entry.Seed();

        session.Provenance.OriginOf("Engine.BoreMm").Should().Be(Provenance.Imported);
        session.Provenance.OriginOf("Engine.StrokeMm").Should().Be(Provenance.Imported);
        session.Provenance.OriginOf("Engine.CylinderCount").Should().Be(Provenance.Imported);
        session.Provenance.OriginOf("Engine.CompressionRatio").Should().Be(Provenance.Imported);
        session.Provenance.IsProtected("Engine.BoreMm").Should().BeTrue();

        // Nothing Wikipedia doesn't publish is presented as if it were cited.
        session.Provenance.OriginOf("IntakeRunner.LengthMm").Should().Be(Provenance.Wizard);
        session.Provenance.OriginOf("Engine.RodLengthMm").Should().Be(Provenance.Wizard);

        session.Document.Engine.BoreMm.Should().Be(entry.BoreMm);
        session.Document.Engine.StrokeMm.Should().Be(entry.StrokeMm);
    }

    [Fact]
    public void A_missing_compression_ratio_falls_back_without_being_stamped_as_cited()
    {
        var entry = Valid(b => b.CompressionRatio = null);
        var session = entry.Seed();

        session.Provenance.OriginOf("Engine.CompressionRatio").Should().Be(Provenance.Wizard);
        session.Document.Engine.CompressionRatio.Should().BeGreaterThan(1.0); // the fallback applied, not zero/unset
    }

    [Fact]
    public void A_missing_peak_power_rpm_still_produces_a_runnable_seed()
    {
        var entry = Valid(b => b.PeakPowerRpm = null);
        var act = () => entry.Seed();
        act.Should().NotThrow();
    }

    // ---- The curated library itself ----------------------------------

    [Fact]
    public void Every_curated_entry_validates_and_cites_a_real_wikipedia_source()
    {
        EngineLibrary.Curated.Should().HaveCountGreaterOrEqualTo(3);

        foreach (var entry in EngineLibrary.Curated)
        {
            output.WriteLine($"{entry.Name}: {entry.Source}");
            entry.Invoking(e => e.Validate()).Should().NotThrow($"{entry.Name} should be internally consistent");
            entry.Source.Should().Contain("en.wikipedia.org", $"{entry.Name}'s source should be a real, checkable URL");
        }
    }

    [Fact]
    public void Every_curated_entry_seeds_a_document_with_no_validation_errors()
    {
        foreach (var entry in EngineLibrary.Curated)
        {
            var session = entry.Seed();
            var errors = session.Document.Validate().Where(i => i.Severity == ModelIssueSeverity.Error).ToList();
            foreach (var error in errors)
            {
                output.WriteLine($"{entry.Name}: {error.Path}: {error.Message}");
            }

            errors.Should().BeEmpty($"{entry.Name} should seed a runnable model");
        }
    }

    [Fact]
    public void EngineLibrary_Default_builds_a_database_with_every_curated_entry_findable_by_name()
    {
        var database = EngineLibrary.Default();
        foreach (var entry in EngineLibrary.Curated)
        {
            database.Find(entry.Name).Should().NotBeNull();
        }
    }
}
