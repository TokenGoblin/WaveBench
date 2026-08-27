using FluentAssertions;
using WaveBench.Analysis;
using WaveBench.Model;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 18 §2.8/§4.6.2: the manifold topology, the collector library, and
/// the pulse-interference diagram the plan calls a required artefact.
/// </summary>
public class ManifoldTopologyTests(ITestOutputHelper output)
{
    private static readonly CollectorGeometry Geometry = new();

    /// <summary>Exhaust at ~900 K: a ≈ 600 m/s, not the 343 of ambient air.</summary>
    private const double ExhaustSoundSpeed = 600.0;

    [Fact]
    public void Gate_every_configuration_the_plan_names_can_be_built_and_is_valid()
    {
        // Plan §2.8 lists the shapes the editor "must express trivially".
        // Building each one and validating the graph is the structural half
        // of that requirement.
        foreach (var (id, name, _) in CollectorLibrary.Configurations)
        {
            var spec = CollectorLibrary.Build(id, Geometry);
            var errors = spec.Validate().Where(i => i.Severity == ModelIssueSeverity.Error).ToList();

            foreach (var error in errors)
            {
                output.WriteLine($"  {id}: {error.Path}: {error.Message}");
            }

            errors.Should().BeEmpty($"'{name}' must produce a solvable graph");
            spec.Nodes.Should().NotBeEmpty();
            spec.Configuration.Should().NotBeNullOrWhiteSpace();

            output.WriteLine($"{id,-14} {spec.Nodes.Count,3} nodes, {spec.Connections.Count,3} connections  ({name})");
        }
    }

    [Fact]
    public void Gate_each_configuration_is_one_action_rather_than_a_pile_of_placements()
    {
        // The gate says "buildable in under two minutes by a new user". This
        // is the measurable PROXY for that, not a usability result: each
        // shape is one palette action, and the count below is how many manual
        // node placements and connections that action replaced. A user
        // hand-wiring a 4-2-1 would be making 30+ interactions; here they make
        // one and then edit numbers.
        var total = 0;
        foreach (var (id, _, _) in CollectorLibrary.Configurations)
        {
            var operations = CollectorLibrary.OperationCount(id, Geometry);
            total += operations;
            output.WriteLine($"{id,-14} one palette action replaces {operations,3} manual operations");
            operations.Should().BeGreaterThan(2, "a configuration worth naming has real structure behind it");
        }

        output.WriteLine($"\n{CollectorLibrary.Configurations.Count} configurations, {total} operations saved in total");
        CollectorLibrary.Configurations.Should().HaveCountGreaterThanOrEqualTo(9,
            "plan §2.8 names 4-1, 4-2-1, tri-Y, 180° crossover, log, individual runners, X-pipe, H-pipe and twin-scroll");
    }

    [Fact]
    public void Gate_a_4_1_collector_puts_every_cylinder_the_same_distance_from_the_merge()
    {
        // Equal-length primaries are the defining property. If the builder
        // got a path wrong this is where it shows.
        var spec = CollectorLibrary.Build("4-1", Geometry);

        var lengths = Enumerable.Range(1, 4)
            .Select(c => spec.PathLengthMm($"cyl{c}", "merge"))
            .ToList();

        lengths.Should().OnlyContain(l => l.HasValue, "every cylinder must reach the collector");
        lengths.Select(l => l!.Value).Distinct().Should().ContainSingle(
            "a 4-1 header is equal-length by construction");
        lengths[0]!.Value.Should().Be(Geometry.PrimaryLengthMm);
    }

    [Fact]
    public void Gate_pulse_arrivals_use_the_real_sound_speed_not_a_nominal_one()
    {
        // The plan is explicit: transit is L/a with the ACTUAL computed local
        // sound speed. Hot exhaust carries a pulse nearly twice as fast as
        // ambient air, so a nominal-speed diagram is wrong by that factor.
        var spec = CollectorLibrary.Build("4-1", Geometry);
        var order = CollectorLibrary.DefaultFiringOrder(4);

        var hot = PulseInterference.Arrivals(spec, "merge", order, 130.0, ExhaustSoundSpeed, 6000.0);
        var ambient = PulseInterference.Arrivals(spec, "merge", order, 130.0, 343.0, 6000.0);

        var hotTransit = hot[0].TransitDeg;
        var ambientTransit = ambient[0].TransitDeg;

        output.WriteLine($"450 mm primary at 6000 rpm: {hotTransit:F1}° at 600 m/s, {ambientTransit:F1}° at 343 m/s");
        (ambientTransit / hotTransit).Should().BeApproximately(600.0 / 343.0, 1e-9,
            "transit scales inversely with sound speed");
        hotTransit.Should().BeApproximately(0.450 / 600.0 * 6.0 * 6000.0, 1e-9);
    }

    [Fact]
    public void Gate_transit_grows_with_engine_speed_and_with_primary_length()
    {
        var order = CollectorLibrary.DefaultFiringOrder(4);
        var spec = CollectorLibrary.Build("4-1", Geometry);

        var slow = PulseInterference.Arrivals(spec, "merge", order, 130.0, ExhaustSoundSpeed, 3000.0)[0].TransitDeg;
        var fast = PulseInterference.Arrivals(spec, "merge", order, 130.0, ExhaustSoundSpeed, 6000.0)[0].TransitDeg;
        fast.Should().BeApproximately(slow * 2.0, 1e-9, "twice the speed is twice the crank angle for the same journey");

        var longer = CollectorLibrary.Build("4-1", Geometry with { PrimaryLengthMm = 900 });
        var longTransit = PulseInterference.Arrivals(longer, "merge", order, 130.0, ExhaustSoundSpeed, 6000.0)[0].TransitDeg;
        longTransit.Should().BeApproximately(fast * 2.0, 1e-9);
    }

    [Fact]
    public void Gate_an_equal_length_4_1_arrives_evenly_and_a_bad_pairing_does_not()
    {
        // The diagram's whole purpose: show that a sensible layout spreads
        // its pulses and a poor one bunches them.
        var order = CollectorLibrary.DefaultFiringOrder(4);
        var good = CollectorLibrary.Build("4-1", Geometry);
        var goodArrivals = PulseInterference.Arrivals(good, "merge", order, 130.0, ExhaustSoundSpeed, 6000.0);

        PulseInterference.Collisions(goodArrivals).Should().BeEmpty(
            "an equal-length 4-1 spreads its four pulses 180° apart");
        var evenness = PulseInterference.Evenness(goodArrivals);
        output.WriteLine($"equal-length 4-1: evenness {evenness:F3}");
        evenness.Should().BeApproximately(1.0, 1e-6);

        // Now break it deliberately. Cylinders fire 180° apart, so delaying
        // one pulse by exactly one firing interval walks it onto the next
        // cylinder's. That delay is computed from the physics rather than
        // guessed: at 6000 rpm the crank turns 36000°/s, so 180° takes 5 ms,
        // and at 600 m/s the pulse covers 3.0 m in that time — not the 0.3 m
        // a factor-of-ten slip would suggest.
        const double rpm = 6000.0;
        var oneIntervalMm = 180.0 / (6.0 * rpm) * ExhaustSoundSpeed * 1000.0;
        oneIntervalMm.Should().BeApproximately(3000.0, 1e-6);

        var uneven = CollectorLibrary.Build("4-1", Geometry);
        uneven.Node("pri1")!.LengthMm = Geometry.PrimaryLengthMm + oneIntervalMm;

        var badArrivals = PulseInterference.Arrivals(uneven, "merge", order, 130.0, ExhaustSoundSpeed, rpm);
        var collisions = PulseInterference.Collisions(badArrivals);

        foreach (var c in collisions)
        {
            output.WriteLine($"collision: cylinders {c.First} and {c.Second}, {c.SeparationDeg:F1}° apart, severity {c.Severity:F2}");
        }

        collisions.Should().NotBeEmpty("delaying one primary by a full firing interval lands its pulse on a neighbour's");
        PulseInterference.Evenness(badArrivals).Should().BeLessThan(evenness);
    }

    [Fact]
    public void Gate_the_correct_twin_scroll_pairing_separates_and_the_wrong_one_collides()
    {
        // Plan §4.6.2 states this is "a validation test, not just a display".
        // Correct pairing must show near-zero overlap; incorrect pairing must
        // show a large one.
        var order = CollectorLibrary.DefaultFiringOrder(4);
        order.Should().Equal(1, 3, 4, 2);

        var correct = CollectorLibrary.TwinScrollPairings[0];
        correct.Groups.Should().BeEquivalentTo(new[] { new[] { 1, 4 }, new[] { 2, 3 } },
            "1 and 4 are 360° apart in 1-3-4-2, as are 2 and 3");

        // EVO 130° / EVC 380° — a 250° exhaust window, which is what a
        // scroll-mate's blowdown must stay out of.
        const double evo = 130.0, evc = 380.0;

        var good = CollectorLibrary.TwinScroll(Geometry, correct);
        var goodIndex = PulseInterference.ScrollSeparation(good, order, evo, evc, ExhaustSoundSpeed, 6000.0);

        // The classic mistake: pair cylinders adjacent in the firing order.
        var wrong = new CylinderPairing("I4 paired 1-3 / 4-2 (wrong)", [[1, 3], [4, 2]],
            "Adjacent in the firing order, so only 180° apart.");
        var bad = CollectorLibrary.TwinScroll(Geometry, wrong);
        var badIndex = PulseInterference.ScrollSeparation(bad, order, evo, evc, ExhaustSoundSpeed, 6000.0);

        foreach (var (scroll, index) in goodIndex)
        {
            output.WriteLine($"correct pairing, {scroll}: separation index {index:F3}");
        }

        foreach (var (scroll, index) in badIndex)
        {
            output.WriteLine($"wrong pairing,   {scroll}: separation index {index:F3}");
        }

        goodIndex.Should().OnlyContain(s => s.Index < 0.05, "cylinders 360° apart never share a scroll's blowdown window");
        badIndex.Max(s => s.Index).Should().BeGreaterThan(0.3, "adjacent firings collide in the scroll they share");
    }

    [Fact]
    public void Gate_a_4_2_1_pairs_cylinders_that_are_far_apart_in_the_firing_order()
    {
        // Pairing adjacent firings into the same secondary is the classic
        // header mistake — their blowdowns collide before the final merge.
        var pairs = CollectorLibrary.FiringPairs(4);
        pairs.Should().BeEquivalentTo(new[] { new[] { 1, 4 }, new[] { 3, 2 } });

        var spec = CollectorLibrary.Build("4-2-1", Geometry);
        var order = CollectorLibrary.DefaultFiringOrder(4);

        // At the first merges, the two cylinders sharing one must not collide.
        foreach (var merge in new[] { "merge1", "merge2" })
        {
            var arrivals = PulseInterference.Arrivals(spec, merge, order, 130.0, ExhaustSoundSpeed, 6000.0);
            var collisions = PulseInterference.Collisions(arrivals);
            output.WriteLine($"{merge}: {arrivals.Count} arrivals, {collisions.Count} collisions");
            collisions.Should().BeEmpty($"the pair feeding {merge} is chosen to be 360° apart");
        }
    }

    [Fact]
    public void A_tri_y_differs_from_a_4_2_1_only_in_its_secondary_lengths()
    {
        var four21 = CollectorLibrary.Build("4-2-1", Geometry);
        var triY = CollectorLibrary.Build("tri-y", Geometry);

        triY.Nodes.Count.Should().Be(four21.Nodes.Count);
        triY.Connections.Count.Should().Be(four21.Connections.Count);

        four21.Node("sec1")!.LengthMm.Should().Be(four21.Node("sec2")!.LengthMm, "a 4-2-1 is equal-length");
        triY.Node("sec1")!.LengthMm.Should().NotBe(triY.Node("sec2")!.LengthMm,
            "the unequal secondaries ARE the tri-Y — that asymmetry broadens the torque curve");
    }

    [Theory]
    [InlineData("no open end")]
    [InlineData("dangling pipe")]
    [InlineData("self loop")]
    [InlineData("cycle")]
    public void An_unsolvable_topology_is_rejected_with_a_reason(string breakage)
    {
        var spec = CollectorLibrary.Build("4-1", Geometry);

        switch (breakage)
        {
            case "no open end":
                spec.Nodes.RemoveAll(n => n.Kind == ManifoldNodeKind.Atmosphere);
                spec.Connections.RemoveAll(c => c.To == "out");
                break;
            case "dangling pipe":
                spec.Connections.RemoveAll(c => c.To == "tail");
                break;
            case "self loop":
                spec.Connections.Add(new ManifoldConnection("tail", "tail"));
                break;
            case "cycle":
                spec.Connections.Add(new ManifoldConnection("tail", "collector"));
                break;
        }

        var errors = spec.Validate().Where(i => i.Severity == ModelIssueSeverity.Error).ToList();
        foreach (var e in errors)
        {
            output.WriteLine($"{breakage}: {e.Message}");
        }

        errors.Should().NotBeEmpty($"'{breakage}' is not a topology the solver can build");
    }

    [Fact]
    public void A_valid_manifold_reports_no_errors_and_a_disconnected_node_is_only_a_warning()
    {
        var spec = CollectorLibrary.Build("4-1", Geometry);
        spec.Validate().Should().BeEmpty();

        // A node dropped on the canvas but not wired up yet is a normal
        // intermediate state, not an error.
        spec.Nodes.Add(new ManifoldNode
        {
            Id = "scratch", Kind = ManifoldNodeKind.Pipe, LengthMm = 100, DiameterMm = 40,
        });

        var issues = spec.Validate();
        issues.Should().Contain(i => i.Severity == ModelIssueSeverity.Warning && i.Path.Contains("scratch"));
    }

    [Fact]
    public void Twin_scroll_keeps_the_two_scrolls_genuinely_separate()
    {
        // The defining property: no path from a scroll-A cylinder to scroll B.
        var spec = CollectorLibrary.TwinScroll(Geometry, CollectorLibrary.TwinScrollPairings[0]);

        spec.PathLengthMm("cyl1", "turbine1").Should().NotBeNull();
        spec.PathLengthMm("cyl1", "turbine2").Should().BeNull("a shared path would defeat the whole point");
        spec.PathLengthMm("cyl2", "turbine2").Should().NotBeNull();
        spec.PathLengthMm("cyl2", "turbine1").Should().BeNull();
    }

    [Fact]
    public void Firing_orders_are_the_conventional_ones()
    {
        CollectorLibrary.DefaultFiringOrder(4).Should().Equal(1, 3, 4, 2);
        CollectorLibrary.DefaultFiringOrder(6).Should().Equal(1, 5, 3, 6, 2, 4);
        CollectorLibrary.DefaultFiringOrder(1).Should().Equal(1);

        foreach (var count in new[] { 1, 2, 3, 4, 5, 6, 8 })
        {
            CollectorLibrary.DefaultFiringOrder(count)
                .Should().BeEquivalentTo(Enumerable.Range(1, count),
                    $"a {count}-cylinder firing order must contain each cylinder exactly once");
        }
    }
}
