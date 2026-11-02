using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Kampf.Application.Models;
using Kampf.Domain.ValueObjects;

namespace Kampf.Controls;

public sealed class GraphNodeEventArgs : EventArgs
{
    public GraphNodeEventArgs(GraphNode node) => Node = node;

    public GraphNode Node { get; }
}

/// <summary>
/// Obsidian-style force-directed graph drawn directly on the Avalonia
/// render surface: node size = connectivity, colour = primary tag, edges
/// coloured by why they exist.
/// </summary>
public sealed class GraphControl : Control
{
    public static readonly StyledProperty<GraphModel?> GraphSourceProperty =
        AvaloniaProperty.Register<GraphControl, GraphModel?>(nameof(GraphSource));

    public static readonly StyledProperty<string?> SelectedNodeIdProperty =
        AvaloniaProperty.Register<GraphControl, string?>(nameof(SelectedNodeId));

    private static readonly IBrush NoteFill = new SolidColorBrush(Color.Parse("#6B84E8"));
    private static readonly IBrush DocumentFill = new SolidColorBrush(Color.Parse("#35A08B"));
    private static readonly IBrush SelectedRing = new SolidColorBrush(Color.Parse("#E0A146"));

    private static readonly Color[] TagPalette =
    {
        Color.Parse("#6C8CFF"), Color.Parse("#54C6A3"), Color.Parse("#E0825B"),
        Color.Parse("#C58BEA"), Color.Parse("#E0B25B"), Color.Parse("#5BB7E0"),
        Color.Parse("#E06B8B"), Color.Parse("#93C45B"),
    };

    private sealed class SimNode
    {
        public required GraphNode Source { get; init; }
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Radius;
        public FormattedText Label = null!;
        public IBrush Fill = NoteFill;
    }

    private readonly List<SimNode> _nodes = new();
    private readonly Dictionary<string, SimNode> _byId = new(StringComparer.Ordinal);
    private readonly List<(SimNode A, SimNode B)> _edges = new();
    private readonly List<EdgeKind> _edgeKinds = new();
    private readonly Dictionary<EdgeKind, Pen> _edgePens = new();
    private readonly DispatcherTimer _timer;

    private ThemeVariant? _penTheme;
    private ThemeVariant? _labelTheme;
    private double _alpha = 1;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private Point _lastPointer;
    private SimNode? _dragging;
    private bool _panning;
    private bool _simulationActive;

    public GraphControl()
    {
        ClipToBounds = true;
        Focusable = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();

        ActualThemeVariantChanged += (_, _) =>
        {
            _penTheme = null;
            _labelTheme = null;
            InvalidateVisual();
        };
    }

    public GraphModel? GraphSource
    {
        get => GetValue(GraphSourceProperty);
        set => SetValue(GraphSourceProperty, value);
    }

    public string? SelectedNodeId
    {
        get => GetValue(SelectedNodeIdProperty);
        set => SetValue(SelectedNodeIdProperty, value);
    }

    public event EventHandler<GraphNodeEventArgs>? NodeActivated;
    public event EventHandler<GraphNodeEventArgs>? NodeSelected;

    static GraphControl()
    {
        AffectsRender<GraphControl>(GraphSourceProperty, SelectedNodeIdProperty);
        GraphSourceProperty.Changed.AddClassHandler<GraphControl>((control, _) => control.Rebuild());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // ------------------------------------------------------------- simulation

    private void Rebuild()
    {
        _nodes.Clear();
        _byId.Clear();
        _edges.Clear();
        _edgeKinds.Clear();
        _penTheme = null;
        _labelTheme = null;

        var model = GraphSource;
        if (model is null || model.Nodes.Count == 0)
        {
            _timer.Stop();
            _simulationActive = false;
            InvalidateVisual();
            return;
        }

        var width = Math.Max(Bounds.Width, 640);
        var height = Math.Max(Bounds.Height, 420);
        var count = model.Nodes.Count;

        for (var i = 0; i < count; i++)
        {
            var source = model.Nodes[i];
            var angle = (i / (double)count) * Math.PI * 2;
            var radius = Math.Min(width, height) * 0.32;

            var node = new SimNode
            {
                Source = source,
                X = width / 2 + Math.Cos(angle) * radius,
                Y = height / 2 + Math.Sin(angle) * radius,
                Radius = 7 + Math.Min(source.Degree * 2.1, 17),
                Fill = BrushFor(source),
                Label = BuildLabel(source.Label, ThemePalette.Brush("GraphLabelBrush")),
            };

            _nodes.Add(node);
            _byId[source.Id] = node;
        }

        foreach (var edge in model.Edges)
        {
            if (!_byId.TryGetValue(edge.SourceId, out var a) || !_byId.TryGetValue(edge.TargetId, out var b))
                continue;

            _edges.Add((a, b));
            _edgeKinds.Add(edge.Kind);
        }

        _alpha = 1;
        _scale = 1;
        _offsetX = 0;
        _offsetY = 0;
        StartSimulation();
        InvalidateVisual();
    }

    private void StartSimulation()
    {
        _simulationActive = true;
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private void Tick()
    {
        if (!_simulationActive || _nodes.Count == 0)
        {
            _timer.Stop();
            return;
        }

        Step();
        InvalidateVisual();

        if (_alpha < 0.02 && _dragging is null)
        {
            _simulationActive = false;
            _timer.Stop();
        }
    }

    private void Step()
    {
        var width = Math.Max(Bounds.Width, 320);
        var height = Math.Max(Bounds.Height, 240);
        var n = _nodes.Count;

        if (n == 1)
        {
            _nodes[0].X = width / 2;
            _nodes[0].Y = height / 2;
            _alpha = 0;
            return;
        }

        var k = Math.Sqrt(width * height / (double)n);
        var kSq = k * k;
        var kInv = 1.0 / k;

        for (var i = 0; i < n; i++)
        {
            var a = _nodes[i];
            for (var j = i + 1; j < n; j++)
            {
                var b = _nodes[j];
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < 0.01)
                {
                    dx = 0.01 * (i + 1);
                    dy = 0.01 * (j + 1);
                    distance = Math.Sqrt(dx * dx + dy * dy);
                }

                var force = Math.Min(kSq / distance, 400) * _alpha;
                var fx = dx / distance * force;
                var fy = dy / distance * force;

                a.Vx += fx;
                a.Vy += fy;
                b.Vx -= fx;
                b.Vy -= fy;
            }
        }

        foreach (var (a, b) in _edges)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < 0.01)
                continue;

            var force = Math.Min(distance * distance * kInv, 300) * _alpha;
            var fx = dx / distance * force;
            var fy = dy / distance * force;

            a.Vx -= fx;
            a.Vy -= fy;
            b.Vx += fx;
            b.Vy += fy;
        }

        var centerX = width / 2;
        var centerY = height / 2;
        var margin = 24.0;

        foreach (var node in _nodes)
        {
            // Gentle gravity keeps disconnected notes from drifting away.
            node.Vx += (centerX - node.X) * 0.012 * _alpha;
            node.Vy += (centerY - node.Y) * 0.012 * _alpha;

            node.Vx *= 0.72;
            node.Vy *= 0.72;

            if (!ReferenceEquals(node, _dragging))
            {
                node.X += Math.Clamp(node.Vx, -24, 24);
                node.Y += Math.Clamp(node.Vy, -24, 24);
            }

            node.X = Math.Clamp(node.X, margin, Math.Max(width - margin, margin));
            node.Y = Math.Clamp(node.Y, margin, Math.Max(height - margin, margin));
        }

        _alpha *= 0.985;
    }

    // ------------------------------------------------------------------ render

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(ThemePalette.Brush("GraphCanvasBrush"), new Rect(Bounds.Size));

        if (_nodes.Count == 0)
        {
            var empty = new FormattedText(
                "Import a paper or write a note to start the graph.",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                13,
                ThemePalette.Brush("GraphMutedBrush"));
            context.DrawText(empty, new Point((Bounds.Width - empty.Width) / 2, Bounds.Height / 2));
            return;
        }

        EnsureLabels();
        EnsureEdgePens();

        var transform = Matrix.CreateScale(_scale, _scale) *
                        Matrix.CreateTranslation(_offsetX, _offsetY);

        using (context.PushTransform(transform))
        {
            for (var i = 0; i < _edges.Count; i++)
            {
                var (a, b) = _edges[i];
                context.DrawLine(_edgePens[_edgeKinds[i]], new Point(a.X, a.Y), new Point(b.X, b.Y));
            }

            var showLabels = _scale > 0.45;

            foreach (var node in _nodes)
            {
                var center = new Point(node.X, node.Y);
                var isSelected = SelectedNodeId is not null && SelectedNodeId == node.Source.Id;

                context.DrawEllipse(node.Fill, null, center, node.Radius, node.Radius);

                if (node.Source.Kind == NodeKind.Document)
                {
                    var innerPen = new Pen(ThemePalette.Brush("GraphCanvasBrush"), 1.6);
                    context.DrawEllipse(null, innerPen, center, node.Radius - 3, node.Radius - 3);
                }

                if (isSelected)
                    context.DrawEllipse(null, new Pen(SelectedRing, 2), center, node.Radius + 3.5, node.Radius + 3.5);

                if (showLabels)
                    context.DrawText(node.Label, new Point(node.X + node.Radius + 6, node.Y - node.Label.Height / 2));
            }
        }
    }

    private void EnsureLabels()
    {
        if (_labelTheme == ActualThemeVariant)
            return;

        var brush = ThemePalette.Brush("GraphLabelBrush");

        foreach (var node in _nodes)
            node.Label = BuildLabel(node.Source.Label, brush);

        _labelTheme = ActualThemeVariant;
    }

    private void EnsureEdgePens()
    {
        if (_penTheme == ActualThemeVariant)
            return;

        _edgePens[EdgeKind.Manual] =
            new Pen(ThemePalette.Brush("GraphEdgeManualBrush"), 1.1);
        _edgePens[EdgeKind.Wiki] =
            new Pen(ThemePalette.Brush("GraphEdgeWikiBrush"), 1.1);
        _edgePens[EdgeKind.AcceptedSuggestion] =
            new Pen(ThemePalette.Brush("GraphEdgeAcceptedBrush"), 1.4);

        _penTheme = ActualThemeVariant;
    }

    private static FormattedText BuildLabel(string text, IBrush brush)
    {
        var trimmed = text.Length > 34 ? text[..33] + "\u2026" : text;
        return new FormattedText(
            trimmed,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            11.5,
            brush);
    }

    private static IBrush BrushFor(GraphNode node)
    {
        if (node.Kind == NodeKind.Document)
            return DocumentFill;

        if (string.IsNullOrEmpty(node.Tag))
            return NoteFill;

        var hash = 0;
        foreach (var ch in node.Tag)
            hash = unchecked(hash * 31 + ch);

        return new SolidColorBrush(TagPalette[Math.Abs(hash) % TagPalette.Length]);
    }

    // -------------------------------------------------------------- interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetPosition(this);
        var world = ToWorld(point);
        var hit = HitTest(world);

        e.Pointer.Capture(this);

        if (hit is not null)
        {
            _dragging = hit;
            _lastPointer = point;
            NodeSelected?.Invoke(this, new GraphNodeEventArgs(hit.Source));
            if (e.ClickCount >= 2)
                NodeActivated?.Invoke(this, new GraphNodeEventArgs(hit.Source));
            e.Handled = true;
        }
        else
        {
            _panning = true;
            _lastPointer = point;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_dragging is not null)
        {
            var world = ToWorld(point);
            _dragging.X = world.X;
            _dragging.Y = world.Y;
            _dragging.Vx = 0;
            _dragging.Vy = 0;
            _alpha = Math.Max(_alpha, 0.35);
            StartSimulation();
            InvalidateVisual();
        }
        else if (_panning)
        {
            _offsetX += point.X - _lastPointer.X;
            _offsetY += point.Y - _lastPointer.Y;
            InvalidateVisual();
        }

        _lastPointer = point;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = null;
        _panning = false;
        e.Pointer.Capture(null);

        if (_alpha > 0.02)
            StartSimulation();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.12 : 1 / 1.12;
        var newScale = Math.Clamp(_scale * factor, 0.25, 4);

        // Zoom about the cursor.
        var world = ToWorld(point);
        _scale = newScale;
        _offsetX = point.X - world.X * _scale;
        _offsetY = point.Y - world.Y * _scale;

        InvalidateVisual();
        e.Handled = true;
    }

    private Point ToWorld(Point screen) =>
        new((screen.X - _offsetX) / _scale, (screen.Y - _offsetY) / _scale);

    private SimNode? HitTest(Point world)
    {
        SimNode? best = null;
        var bestDistance = double.MaxValue;

        foreach (var node in _nodes)
        {
            var dx = node.X - world.X;
            var dy = node.Y - world.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance > node.Radius + 6 || distance >= bestDistance)
                continue;

            best = node;
            bestDistance = distance;
        }

        return best;
    }
}
