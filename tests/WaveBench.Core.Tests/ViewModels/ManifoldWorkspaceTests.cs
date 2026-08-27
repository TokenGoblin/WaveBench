using FluentAssertions;
using WaveBench.Model;
using WaveBench.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// Phase 18 (§8.4) manifold canvas: palette, selection, drag with snap,
/// auto-layout, copy/paste, the live geometry summary, and inline design
/// warnings with citations.
/// </summary>
public class ManifoldWorkspaceTests(ITestOutputHelper output)
{
    private static ManifoldWorkspace Workspace(out ProjectSession session)
    {
        session = ModelTemplates.Open(ModelTemplates.Find("fsae-600")!);
        return new ManifoldWorkspace(session, new UserPreferences { Mode = UiMode.Advanced });
    }

    [Fact]
    public void Gate_the_palette_offers_every_component_and_every_configuration()
    {
        ManifoldWorkspace.Components.Should().HaveCount(5, "pipe, junction, plenum, port and open end (plan §2.7)");
        ManifoldWorkspace.Components.Should().OnlyContain(c => c.Kind != null);
        ManifoldWorkspace.Components.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Description));

        ManifoldWorkspace.Configurations.Should().HaveCount(CollectorLibrary.Configurations.Count);
        ManifoldWorkspace.Configurations.Should().Contain(c => c.Id == "4-2-1");
        ManifoldWorkspace.Configurations.Should().OnlyContain(c => c.Kind == null);
    }

    [Fact]
    public void Gate_a_configuration_is_one_action_and_seeds_itself_from_the_model()
    {
        var workspace = Workspace(out var session);
        session.Document.ExhaustManifold.Should().BeNull("a model starts with the simple runner");

        workspace.ApplyConfiguration("4-2-1");

        var manifold = session.Document.ExhaustManifold;
        manifold.Should().NotBeNull();
        manifold!.Configuration.Should().Be("4-2-1");
        manifold.Nodes.Count(n => n.Kind == ManifoldNodeKind.Port).Should().Be(4, "the template is a four-cylinder");
        manifold.Validate().Should().NotContain(i => i.Severity == ModelIssueSeverity.Error);

        // Seeded from the model, not from a blank guess.
        var primary = manifold.Nodes.First(n => n.Id == "pri1");
        primary.LengthMm.Should().Be(session.Document.ExhaustRunner.LengthMm);
        primary.DiameterMm.Should().Be(session.Document.ExhaustRunner.DiameterMm);

        output.WriteLine($"one action produced {manifold.Nodes.Count} nodes and {manifold.Connections.Count} connections");
    }

    [Fact]
    public void Gate_canvas_edits_are_undoable()
    {
        // Plan §8.11: "Undo/redo across the whole model tree, including canvas
        // edits". The graph is edited as a VALUE for exactly this reason — a
        // graph mutated in place leaves undo two references to one object and
        // nothing to restore.
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");
        var before = session.Document.Save();

        var id = workspace.Add(ManifoldNodeKind.Pipe, 3, 3);
        session.Document.ExhaustManifold!.Node(id).Should().NotBeNull();

        session.Undo().Should().BeTrue();
        session.Document.ExhaustManifold!.Node(id).Should().BeNull("undo must remove the placed component");
        session.Document.Save().Should().Be(before);

        session.Redo().Should().BeTrue();
        session.Document.ExhaustManifold!.Node(id).Should().NotBeNull("and redo must put it back");
    }

    [Fact]
    public void Gate_an_edit_never_mutates_the_document_until_it_is_committed()
    {
        // The draft-and-commit pattern: a rejected connection must leave no
        // trace at all.
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");
        var before = session.Document.Save();

        workspace.Connect("pri1", "pri1").Should().BeFalse("a node cannot connect to itself");
        workspace.Connect("pri1", "nonexistent").Should().BeFalse();
        workspace.Disconnect("pri1", "tail").Should().BeFalse("they were never connected");

        session.Document.Save().Should().Be(before, "three refused operations must change nothing");
    }

    [Fact]
    public void Gate_dragging_snaps_to_the_grid()
    {
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");
        workspace.GridSize = 0.5;

        workspace.Select("pri1");
        workspace.MoveSelection(1.3, -0.7);

        var node = session.Document.ExhaustManifold!.Node("pri1")!;
        (node.X / 0.5).Should().BeApproximately(Math.Round(node.X / 0.5), 1e-9, "X must land on the grid");
        (node.Y / 0.5).Should().BeApproximately(Math.Round(node.Y / 0.5), 1e-9, "Y must land on the grid");

        // And snapping off means exactly where it was put.
        workspace.SnapToGrid = false;
        workspace.MoveSelection(0.13, 0.0);
        session.Document.ExhaustManifold!.Node("pri1")!.X.Should().BeApproximately(node.X + 0.13, 1e-9);
    }

    [Fact]
    public void Multi_select_moves_everything_together_and_leaves_the_rest_alone()
    {
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");

        workspace.Select("pri1");
        workspace.Select("pri2", additive: true);
        workspace.Selection.Should().HaveCount(2);

        var untouched = session.Document.ExhaustManifold!.Node("pri3")!.X;
        workspace.MoveSelection(2.0, 0.0);

        var manifold = session.Document.ExhaustManifold!;
        manifold.Node("pri1")!.X.Should().Be(4.0, "moved from 2 by 2");
        manifold.Node("pri2")!.X.Should().Be(4.0);
        manifold.Node("pri3")!.X.Should().Be(untouched, "not selected, not moved");
    }

    [Fact]
    public void Rubber_band_selection_takes_what_is_inside_it()
    {
        var workspace = Workspace(out _);
        workspace.ApplyConfiguration("4-1");

        // The primaries sit at x = 2; the ports at x = 0.
        workspace.SelectInside(-0.5, -0.5, 1.0, 10.0);
        workspace.Selection.Should().OnlyContain(id => id.StartsWith("cyl"), "only the port column is inside");
        workspace.Selection.Should().HaveCount(4);
    }

    [Fact]
    public void Gate_copy_and_paste_duplicates_a_subgraph_with_fresh_identity()
    {
        // Plan §8.4 asks for "copy/paste a whole bank".
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");

        workspace.Select("cyl1");
        workspace.Select("pri1", additive: true);
        workspace.Copy().Should().Be(2);

        var pasted = workspace.Paste();
        pasted.Should().HaveCount(2);

        var manifold = session.Document.ExhaustManifold!;
        manifold.Nodes.Select(n => n.Id).Should().OnlyHaveUniqueItems("pasted nodes need fresh ids");

        // The connection between the two copied nodes came with them.
        var newPort = pasted.Select(manifold.Node).First(n => n!.Kind == ManifoldNodeKind.Port)!;
        var newPipe = pasted.Select(manifold.Node).First(n => n!.Kind == ManifoldNodeKind.Pipe)!;
        manifold.Downstream(newPort.Id).Should().Contain(newPipe.Id);

        // But a pasted port cannot claim a cylinder that already has one.
        newPort.Cylinder.Should().Be(5, "cylinders 1–4 are taken");
        manifold.Nodes.Where(n => n.Kind == ManifoldNodeKind.Port)
            .Select(n => n.Cylinder).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Copying_half_a_connection_does_not_paste_a_dangling_one()
    {
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");

        // Only the pipe, not the port it connects to.
        workspace.Select("pri1");
        workspace.Copy().Should().Be(1);
        var pasted = workspace.Paste();

        var manifold = session.Document.ExhaustManifold!;
        manifold.Upstream(pasted[0]).Should().BeEmpty();
        manifold.Downstream(pasted[0]).Should().BeEmpty("a copied node with no copied partner arrives unconnected");
    }

    [Fact]
    public void Gate_auto_layout_orders_the_graph_by_flow()
    {
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");

        // Scramble it.
        workspace.SelectAll();
        workspace.MoveSelection(0, 0);
        foreach (var node in session.Document.ExhaustManifold!.Nodes)
        {
            node.X = 0;
            node.Y = 0;
        }

        workspace.AutoLayout();

        var manifold = session.Document.ExhaustManifold!;
        var ports = manifold.Nodes.Where(n => n.Kind == ManifoldNodeKind.Port).ToList();
        var outlet = manifold.Nodes.First(n => n.Kind == ManifoldNodeKind.Atmosphere);

        ports.Should().OnlyContain(p => p.X == 0.0, "cylinder ports are the first column");
        outlet.X.Should().BeGreaterThan(manifold.Node("merge")!.X, "the exit is downstream of the merge");
        manifold.Node("merge")!.X.Should().BeGreaterThan(manifold.Node("pri1")!.X);

        // Nothing stacked on top of anything else.
        manifold.Nodes.Select(n => (n.X, n.Y)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Gate_the_geometry_summary_is_live()
    {
        var workspace = Workspace(out var session);
        workspace.Summary().Readouts.Should().Contain(r => r.Value.Contains("Single runner"),
            "before a collector exists the summary says so");

        workspace.ApplyConfiguration("4-1");
        var summary = workspace.Summary();

        foreach (var r in summary.Readouts)
        {
            output.WriteLine($"{r.Label,-28} {r.Value}   {r.Note}");
        }

        summary.Readouts.Should().Contain(r => r.Label == "Configuration");
        summary.Readouts.Should().Contain(r => r.Label == "Path length per cylinder");
        summary.Readouts.Should().Contain(r => r.Label == "Primary length");

        // Change a primary and the summary must follow.
        workspace.EditNode("pri1", n => n.LengthMm += 250).Should().BeTrue();
        var spread = workspace.Summary().Readouts.First(r => r.Label == "Path length per cylinder");
        spread.Note.Should().Contain("spread");
        spread.Warning.Should().NotBeNull("250 mm of mismatch is worth flagging");
    }

    [Fact]
    public void Gate_a_steep_diffuser_produces_the_warning_the_plan_writes_out()
    {
        // Plan §8.4's own example: "Diffuser half-angle 11°: separation likely
        // (SAE 2006-01-3654). Suggested ≤ 7°."
        var workspace = Workspace(out _);
        workspace.ApplyConfiguration("4-1");

        // 40 → 80 mm over 100 mm is a half-angle of about 11°.
        workspace.EditNode("collector", n =>
        {
            n.LengthMm = 100;
            n.DiameterMm = 40;
            n.OutletDiameterMm = 80;
        });

        var warning = workspace.Warnings().FirstOrDefault(w => w.Message.Contains("half-angle"));
        warning.Should().NotBeNull();
        output.WriteLine($"{warning!.Message} {warning.Suggestion} ({warning.Citation})");

        warning.Message.Should().Contain("separation likely");
        warning.Suggestion.Should().Contain("7");
        warning.Citation.Should().Contain("2006-01-3654");
        warning.CrossLink.Should().NotBeNullOrEmpty("plan §8.3 wants warnings to link to the workspace that shows it");
    }

    [Fact]
    public void Gate_every_design_warning_carries_a_citation_or_an_actionable_suggestion()
    {
        // A warning without a source is this tool's opinion. Structural
        // errors from the model are exempt: "the manifold will not solve" is
        // not a matter of authority.
        var workspace = Workspace(out _);
        workspace.ApplyConfiguration("4-1");
        workspace.EditNode("merge", n => n.BranchAngleDeg = 90);
        workspace.EditNode("collector", n => { n.LengthMm = 30; n.DiameterMm = 60; });

        var warnings = workspace.Warnings();
        warnings.Should().NotBeEmpty();

        foreach (var w in warnings)
        {
            output.WriteLine($"[{w.NodeId ?? "-"}] {w.Message}  → {w.Suggestion}  ({w.Citation})");
            (w.Citation is not null || w.Suggestion is not null)
                .Should().BeTrue($"'{w.Message}' must say why or what to do");
        }

        warnings.Should().Contain(w => w.Message.Contains("Branch angle") && w.Citation!.Contains("Idelchik"));
        warnings.Should().Contain(w => w.Message.Contains("L/D"));
    }

    [Fact]
    public void A_multi_way_merge_warns_that_the_loss_model_does_not_cover_it()
    {
        var workspace = Workspace(out _);
        workspace.ApplyConfiguration("4-1");

        workspace.Warnings().Should().Contain(
            w => w.Message.Contains("5-leg") && w.Suggestion!.Contains("constant-pressure"),
            "the same honesty the solver reports, shown where the user is editing");
    }

    [Fact]
    public void A_sane_4_2_1_produces_no_geometry_warnings()
    {
        // The counterweight: if everything warns, nothing does.
        var workspace = Workspace(out _);
        workspace.ApplyConfiguration("4-2-1");

        var warnings = workspace.Warnings();
        foreach (var w in warnings)
        {
            output.WriteLine($"unexpected: [{w.NodeId}] {w.Message}");
        }

        warnings.Should().BeEmpty("a library 4-2-1 built from the model's own runner is a reasonable header");
    }

    [Fact]
    public void Deleting_a_component_takes_its_connections_with_it()
    {
        var workspace = Workspace(out var session);
        workspace.ApplyConfiguration("4-1");

        workspace.Select("pri1");
        workspace.DeleteSelected().Should().Be(1);

        var manifold = session.Document.ExhaustManifold!;
        manifold.Node("pri1").Should().BeNull();
        manifold.Connections.Should().NotContain(c => c.From == "pri1" || c.To == "pri1",
            "a connection to a deleted node is a dangling reference");
        workspace.Selection.Should().BeEmpty();
    }
}
