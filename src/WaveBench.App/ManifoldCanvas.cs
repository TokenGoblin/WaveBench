using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WaveBench.Model;
using WaveBench.ViewModels;

namespace WaveBench.App;

/// <summary>
/// The node-graph surface for the Manifold canvas (plan Phase 18, §8.4).
///
/// This class draws and forwards gestures. Every decision about what a
/// gesture MEANS — what snaps, what a selection is, when an edit becomes an
/// undo step — lives in <see cref="ManifoldWorkspace"/>, which is why the
/// Phase 18 behaviour is testable without a window and why the 60 fps gate
/// can be measured on the work that actually repeats per frame.
/// </summary>
public sealed class ManifoldSurface : Canvas
{
    /// <summary>Pixels per grid unit. Node positions in the model are grid units.</summary>
    /// <remarks>
    /// Chosen against <see cref="ManifoldWorkspace.AutoLayout"/>, which puts
    /// columns 2.0 units apart: the box must stay narrower than 2 × Scale or
    /// an auto-laid-out graph overlaps itself.
    /// </remarks>
    private const double Scale = 54.0;

    private const double BoxWidth = 104.0;
    private const double BoxHeight = 44.0;

    /// <summary>Below this, a click is a click; above it, it was a drag.</summary>
    private const double DragThreshold = 4.0;

    private readonly ManifoldWorkspace _workspace;
    private readonly Action _refresh;
    private readonly Dictionary<string, FrameworkElement> _boxes = new(StringComparer.Ordinal);

    private Point _pressAt;
    private bool _dragging;
    private string? _dragNode;
    private Rectangle? _band;
    private string? _connectFrom;

    /// <summary>
    /// Set when a press changed the selection. A refresh rebuilds the whole
    /// Design view — including this surface — so it can only happen on
    /// release: refreshing on press would destroy the element mid-drag, mouse
    /// capture and all, and the drag would never complete.
    /// </summary>
    private bool _selectionChanged;

    public ManifoldSurface(ManifoldWorkspace workspace, Action refresh)
    {
        _workspace = workspace;
        _refresh = refresh;

        Background = (Brush)Application.Current.Resources["Brush.Canvas"];

        // Zoom as a layout transform, so every coordinate below — drawing,
        // hit-testing, drag deltas — stays in unscaled canvas space. WPF
        // reports GetPosition(this) in the element's own space, so nothing
        // here has to know about the zoom at all.
        LayoutTransform = new ScaleTransform(workspace.Zoom, workspace.Zoom);

        Focusable = true;
        ClipToBounds = true;
        MinHeight = 360;

        MouseLeftButtonDown += OnPress;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnRelease;
        KeyDown += OnKey;

        // Focusable, so Tab reaches the canvas and Del / Ctrl+C work without a
        // mouse. Deliberately NOT focused programmatically: every committed
        // edit rebuilds this surface, and grabbing focus on each rebuild would
        // yank the caret out of the inspector between two fields.
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    /// <summary>
    /// Set while a shift-drag is in progress: the view shows the pending link
    /// so "click two things" is not an invisible mode.
    /// </summary>
    public string? ConnectFrom => _connectFrom;

    // ---- Drawing -------------------------------------------------------------

    public void Redraw()
    {
        Children.Clear();
        _boxes.Clear();

        var spec = _workspace.Manifold;
        if (spec is null || spec.Nodes.Count == 0)
        {
            DrawEmptyState();
            return;
        }

        // Size the surface to its content so the scroll viewer knows how far
        // it can go, with a margin for dragging something out past the edge.
        Width = Math.Max(600, spec.Nodes.Max(n => n.X) * Scale + BoxWidth + 80);
        Height = Math.Max(360, spec.Nodes.Max(n => n.Y) * Scale + BoxHeight + 80);

        DrawGrid();

        // Connections first, so nodes sit on top of their own lines.
        foreach (var connection in spec.Connections)
        {
            var from = spec.Node(connection.From);
            var to = spec.Node(connection.To);
            if (from is null || to is null)
            {
                continue;
            }

            DrawConnection(from, to);
        }

        var flagged = _workspace.Warnings()
            .Where(w => w.NodeId is not null)
            .Select(w => w.NodeId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in spec.Nodes)
        {
            DrawNode(node, flagged.Contains(node.Id));
        }
    }

    private void DrawEmptyState()
    {
        Width = double.NaN;
        Height = double.NaN;

        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock
        {
            Text = "No manifold on this model.",
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Each cylinder currently has its own runner straight to atmosphere — which is a valid "
                 + "model, not a missing one. Pick a configuration from the palette to build a collector, "
                 + "or place components one at a time.",
            Foreground = (Brush)Application.Current.Resources["Brush.TextSecondary"],
            TextWrapping = TextWrapping.Wrap,

            // A Canvas measures its children with infinite width, so the wrap
            // point has to be stated. Narrower than the column, or the last
            // few words fall off the right edge of the viewport.
            MaxWidth = 360,
        });

        Children.Add(panel);
    }

    private void DrawGrid()
    {
        var faint = (Brush)Application.Current.Resources["Brush.BorderSubtle"];
        var step = Scale * _workspace.GridSize;
        if (!_workspace.SnapToGrid || step < 8)
        {
            return;
        }

        for (var x = 0.0; x < Width; x += step)
        {
            Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = Height,
                Stroke = faint, StrokeThickness = 0.5, Opacity = 0.5,
            });
        }

        for (var y = 0.0; y < Height; y += step)
        {
            Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = Width, Y2 = y,
                Stroke = faint, StrokeThickness = 0.5, Opacity = 0.5,
            });
        }
    }

    private void DrawConnection(ManifoldNode from, ManifoldNode to)
    {
        var stroke = (Brush)Application.Current.Resources["Brush.BorderStrong"];
        var (x1, y1) = Centre(from);
        var (x2, y2) = Centre(to);

        Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = stroke, StrokeThickness = 2,
        });

        // A short arrow at the midpoint: flow direction is load-bearing on an
        // exhaust graph and an undirected line does not carry it.
        var mx = (x1 + x2) / 2.0;
        var my = (y1 + y2) / 2.0;
        var angle = Math.Atan2(y2 - y1, x2 - x1);
        const double head = 8.0;

        var arrow = new Polygon { Fill = stroke };
        arrow.Points.Add(new Point(mx + head * Math.Cos(angle), my + head * Math.Sin(angle)));
        arrow.Points.Add(new Point(
            mx + head * Math.Cos(angle + 2.5), my + head * Math.Sin(angle + 2.5)));
        arrow.Points.Add(new Point(
            mx + head * Math.Cos(angle - 2.5), my + head * Math.Sin(angle - 2.5)));
        Children.Add(arrow);
    }

    private void DrawNode(ManifoldNode node, bool flagged)
    {
        var selected = _workspace.Selection.Contains(node.Id);
        var accent = (Brush)Application.Current.Resources["Brush.Accent"];
        var border = (Brush)Application.Current.Resources["Brush.BorderStrong"];

        var stack = new StackPanel();

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = ManifoldWorkspace.Glyph(node.Kind),
            Foreground = KindBrush(node.Kind),
            Margin = new Thickness(0, 0, 6, 0),
        });
        title.Children.Add(new TextBlock
        {
            Text = node.Label.Length > 0 ? node.Label : node.Id,
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (flagged)
        {
            title.Children.Add(new TextBlock
            {
                Text = "  ⚠",
                Foreground = (Brush)Application.Current.Resources["Brush.Warning"],
                FontSize = 12,
            });
        }

        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = _workspace.Caption(node),
            Foreground = (Brush)Application.Current.Resources["Brush.TextSecondary"],
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var box = new Border
        {
            Child = stack,
            Width = BoxWidth,
            Height = BoxHeight,
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["Brush.Surface"],
            BorderBrush = selected ? accent : border,

            // Selection is a DASHED outline as well as an accent one, so it
            // survives a colour-blind reading and a greyscale print (§8.11).
            BorderThickness = new Thickness(selected ? 2 : 1),
            ToolTip = $"{node.Kind} · {node.Id}\n{_workspace.Caption(node)}"
                      + (_connectFrom is null ? "\n\nShift-drag to another component to connect." : ""),
            Tag = node.Id,
        };

        if (selected)
        {
            // A Border cannot dash its own edge and keep a solid fill behind
            // it, so the marching-ants ring is a sibling drawn just outside.
            var ring = new Rectangle
            {
                Width = BoxWidth + 6,
                Height = BoxHeight + 6,
                RadiusX = 8, RadiusY = 8,
                Stroke = accent,
                StrokeThickness = 1,
                StrokeDashArray = [3, 2],
                IsHitTestVisible = false,
            };
            SetLeft(ring, node.X * Scale - 3);
            SetTop(ring, node.Y * Scale - 3);
            Children.Add(ring);
        }

        if (_connectFrom == node.Id)
        {
            box.BorderBrush = (Brush)Application.Current.Resources["Brush.Info"];
            box.BorderThickness = new Thickness(2);
        }

        SetLeft(box, node.X * Scale);
        SetTop(box, node.Y * Scale);
        Children.Add(box);
        _boxes[node.Id] = box;
    }

    /// <summary>
    /// Kind colour. Never the only cue — every box also carries a glyph and
    /// its kind in the tooltip.
    /// </summary>
    private static Brush KindBrush(ManifoldNodeKind kind) =>
        (Brush)Application.Current.Resources[kind switch
        {
            ManifoldNodeKind.Port => "Brush.Info",
            ManifoldNodeKind.Pipe => "Brush.Accent",
            ManifoldNodeKind.Junction => "Brush.Success",
            ManifoldNodeKind.Plenum => "Brush.Warning",
            _ => "Brush.TextSecondary",
        }];

    private static (double X, double Y) Centre(ManifoldNode node) =>
        (node.X * Scale + BoxWidth / 2.0, node.Y * Scale + BoxHeight / 2.0);

    // ---- Gestures ------------------------------------------------------------

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _pressAt = e.GetPosition(this);
        _dragging = false;
        _selectionChanged = false;
        _dragNode = HitTest(_pressAt);

        if (_dragNode is null)
        {
            // Empty space: rubber band. Clearing the selection is deferred to
            // release, or a mis-click would drop a selection the user is about
            // to extend.
            _band = new Rectangle
            {
                Stroke = (Brush)Application.Current.Resources["Brush.Accent"],
                StrokeThickness = 1,
                StrokeDashArray = [3, 2],
                IsHitTestVisible = false,
            };
            SetLeft(_band, _pressAt.X);
            SetTop(_band, _pressAt.Y);
            Children.Add(_band);
            CaptureMouse();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _connectFrom = _dragNode;
            _dragNode = null;
            Redraw();
            CaptureMouse();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _workspace.Toggle(_dragNode);
            _dragNode = null;
            _selectionChanged = true;
            Redraw();
            CaptureMouse();
            return;
        }

        // Dragging a node that is already part of a multi-selection moves the
        // whole selection; dragging an unselected one selects it first.
        if (!_workspace.Selection.Contains(_dragNode))
        {
            _workspace.Select(_dragNode);
            _selectionChanged = true;
            Redraw();
        }

        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var at = e.GetPosition(this);
        var dx = at.X - _pressAt.X;
        var dy = at.Y - _pressAt.Y;

        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < DragThreshold)
        {
            return;
        }

        _dragging = true;

        if (_band is not null)
        {
            SetLeft(_band, Math.Min(_pressAt.X, at.X));
            SetTop(_band, Math.Min(_pressAt.Y, at.Y));
            _band.Width = Math.Abs(dx);
            _band.Height = Math.Abs(dy);
            return;
        }

        if (_dragNode is null)
        {
            return;
        }

        // Move the VISUALS only. The model is written once on release: each
        // write is an undo step, and a drag that fills the undo stack with
        // sixty entries is worse than no undo at all.
        foreach (var id in _workspace.Selection)
        {
            if (_boxes.TryGetValue(id, out var box))
            {
                box.RenderTransform = new TranslateTransform(dx, dy);
            }
        }
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var at = e.GetPosition(this);
        var dx = at.X - _pressAt.X;
        var dy = at.Y - _pressAt.Y;

        if (_connectFrom is not null)
        {
            var target = HitTest(at);
            if (target is not null && target != _connectFrom)
            {
                _workspace.Connect(_connectFrom, target);
            }

            _connectFrom = null;
            _dragging = false;
            _refresh();
            return;
        }

        if (_band is not null)
        {
            Children.Remove(_band);
            _band = null;

            if (_dragging)
            {
                _workspace.SelectInside(
                    Math.Min(_pressAt.X, at.X) / Scale, Math.Min(_pressAt.Y, at.Y) / Scale,
                    Math.Max(_pressAt.X, at.X) / Scale, Math.Max(_pressAt.Y, at.Y) / Scale,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            }
            else
            {
                _workspace.ClearSelection();
            }

            _dragging = false;
            _refresh();
            return;
        }

        if (_dragNode is not null && _dragging)
        {
            _workspace.MoveSelection(dx / Scale, dy / Scale);
            _dragNode = null;
            _dragging = false;
            _refresh();
            return;
        }

        _dragNode = null;
        _dragging = false;

        // A plain click that changed the selection: now — not on press — is
        // when the inspector beside the canvas can safely be rebuilt.
        if (_selectionChanged)
        {
            _selectionChanged = false;
            _refresh();
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (e.Key)
        {
            case Key.Delete or Key.Back when _workspace.Selection.Count > 0:
                _workspace.DeleteSelected();
                break;

            case Key.A when control:
                _workspace.SelectAll();
                break;

            case Key.C when control:
                _workspace.Copy();
                break;

            case Key.V when control:
                _workspace.Paste();
                break;

            case Key.Escape:
                _connectFrom = null;
                _workspace.ClearSelection();
                break;

            default:
                return;
        }

        e.Handled = true;
        _refresh();
    }

    /// <summary>
    /// Topmost node under a point. Iterated in reverse so a box drawn later —
    /// and therefore on top — wins, which is what the user sees.
    /// </summary>
    private string? HitTest(Point at)
    {
        var spec = _workspace.Manifold;
        if (spec is null)
        {
            return null;
        }

        for (var i = spec.Nodes.Count - 1; i >= 0; i--)
        {
            var node = spec.Nodes[i];
            var left = node.X * Scale;
            var top = node.Y * Scale;
            if (at.X >= left && at.X <= left + BoxWidth && at.Y >= top && at.Y <= top + BoxHeight)
            {
                return node.Id;
            }
        }

        return null;
    }
}
