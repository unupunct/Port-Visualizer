using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PortVisualizer.Core.Graph;

namespace PortVisualizer.App.Controls;

/// <summary>
/// Custom-rendered interactive network graph: zoom (wheel), pan (drag background), drag nodes, hover tooltips,
/// click to select. Only redraws while the layout is moving or the line animation is running.
/// </summary>
public sealed class GraphCanvas : FrameworkElement
{
    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(nameof(Graph), typeof(NetworkGraph), typeof(GraphCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(nameof(Animate), typeof(bool), typeof(GraphCanvas), new PropertyMetadata(true));

    public NetworkGraph? Graph { get => (NetworkGraph?)GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public bool Animate { get => (bool)GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    public event Action<GraphNode?>? NodeSelected;

    private static readonly Color CComputer = Color.FromRgb(0x00, 0xE5, 0xA0), CProcess = Color.FromRgb(0x00, 0xD4, 0xFF),
        CRemote = Color.FromRgb(0x3B, 0x82, 0xF6), CListening = Color.FromRgb(0xA8, 0x55, 0xF7), CNew = Color.FromRgb(0xFA, 0xCC, 0x15),
        CClosed = Color.FromRgb(0x6B, 0x72, 0x80), CWarning = Color.FromRgb(0xFB, 0x92, 0x3C);

    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private readonly Dictionary<Color, RadialGradientBrush> _glows = new();
    private readonly Dictionary<(Color, int, bool), Pen> _pens = new();
    private readonly Dictionary<(string, int, bool), FormattedText> _texts = new();
    private readonly DashStyle _dash = new(new double[] { 2, 3 }, 0);
    private double _scale = 1, _offX, _offY, _phase;
    private GraphNode? _hover, _selected, _drag;
    private Point _lastMouse, _downPos;
    private bool _panning, _moved, _dirty = true, _fitted, _userView;
    private HashSet<string> _neighbors = new();
    private TimeSpan _lastTick;

    public GraphCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        SizeChanged += (_, _) => _dirty = true;
    }

    public void Wake() { Graph?.Wake(); _dirty = true; }

    public void FitToView()
    {
        var g = Graph;
        if (g is null || ActualWidth < 10) return;
        var nodes = g.Nodes.Values.ToList();
        double minX = nodes.Min(n => n.X - n.Radius), maxX = nodes.Max(n => n.X + n.Radius);
        double minY = nodes.Min(n => n.Y - n.Radius), maxY = nodes.Max(n => n.Y + n.Radius);
        var w = Math.Max(1, maxX - minX); var h = Math.Max(1, maxY - minY);
        _scale = Math.Clamp(Math.Min((ActualWidth - 120) / w, (ActualHeight - 120) / h), 0.1, 2.0);
        _offX = -(minX + maxX) / 2 * _scale; _offY = -(minY + maxY) / 2 * _scale;
        _fitted = true; _dirty = true;
    }

    public void FitAndFollow() { _userView = false; FitToView(); }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if ((now - _lastTick).TotalMilliseconds < 30) return;
        _lastTick = now;
        var g = Graph;
        if (g is null) return;

        var moving = false;
        if (g.Nodes.Count > 1)
        {
            moving = g.Step(g.Nodes.Count < 250 ? 2 : 1);
            if (!_userView && (moving || !_fitted)) FitToView();
        }
        var animating = Animate && g.Edges.Count is > 0 and < 900;
        if (animating) { _phase = (_phase - 0.12) % 5; _dash.Offset = _phase; }
        if (moving || animating || _dirty) { _dirty = false; InvalidateVisual(); }
    }

    // ---- coordinates ----
    private Point ToWorld(Point p) => new((p.X - ActualWidth / 2 - _offX) / _scale, (p.Y - ActualHeight / 2 - _offY) / _scale);

    private GraphNode? HitTest(Point screen)
    {
        var g = Graph; if (g is null) return null;
        var w = ToWorld(screen);
        GraphNode? best = null; var bestRank = -1;
        foreach (var n in g.Nodes.Values)
        {
            var dx = n.X - w.X; var dy = n.Y - w.Y; var r = n.Radius + 4 / _scale;
            if (dx * dx + dy * dy > r * r) continue;
            var rank = n.Kind switch { NodeKind.Computer => 3, NodeKind.Process => 2, _ => 1 };
            if (rank > bestRank) { best = n; bestRank = rank; }
        }
        return best;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        _userView = true;
        var before = ToWorld(p);
        _scale = Math.Clamp(_scale * Math.Pow(1.0015, e.Delta), 0.08, 5);
        _offX = p.X - ActualWidth / 2 - before.X * _scale;
        _offY = p.Y - ActualHeight / 2 - before.Y * _scale;
        _dirty = true;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _lastMouse = _downPos = e.GetPosition(this);
        _moved = false;
        if (e.ClickCount == 2) { FitToView(); return; }
        var hit = HitTest(_lastMouse);
        if (hit is not null && hit.Kind != NodeKind.Computer || hit is { Kind: NodeKind.Computer }) _drag = hit; else _panning = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is not null)
        {
            if ((p - _downPos).Length > 3) _moved = true;
            if (_moved)
            {
                var w = ToWorld(p);
                _drag.X = w.X; _drag.Y = w.Y; _drag.VX = _drag.VY = 0; _drag.Pinned = true;
                Graph?.Wake();
            }
        }
        else if (_panning)
        {
            if ((p - _downPos).Length > 3) _moved = true;
            _userView = true;
            _offX += p.X - _lastMouse.X; _offY += p.Y - _lastMouse.Y;
        }
        else
        {
            var h = HitTest(p);
            if (!ReferenceEquals(h, _hover)) { _hover = h; Cursor = h is null ? Cursors.Arrow : Cursors.Hand; }
        }
        _lastMouse = p;
        _dirty = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_moved) Select(_drag is not null ? _drag : null);
        _drag = null; _panning = false;
        ReleaseMouseCapture();
        _dirty = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; _dirty = true; }

    private void Select(GraphNode? n)
    {
        _selected = n;
        _neighbors = new HashSet<string>();
        if (n is not null && Graph is { } g)
        {
            _neighbors.Add(n.Id);
            foreach (var edge in g.Edges)
            {
                if (edge.From == n) _neighbors.Add(edge.To.Id);
                else if (edge.To == n) _neighbors.Add(edge.From.Id);
            }
            // for a remote/listening node keep the whole path to the computer lit
            foreach (var edge in g.Edges) if (_neighbors.Contains(edge.To.Id) && edge.From.Kind == NodeKind.Computer) _neighbors.Add(edge.From.Id);
        }
        NodeSelected?.Invoke(n);
    }

    // ---- drawing ----
    private SolidColorBrush Brush(Color c)
    {
        if (!_brushes.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); _brushes[c] = b; }
        return b;
    }

    private RadialGradientBrush Glow(Color c)
    {
        if (!_glows.TryGetValue(c, out var b))
        {
            b = new RadialGradientBrush(Color.FromArgb(110, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            b.Freeze(); _glows[c] = b;
        }
        return b;
    }

    private Pen EdgePen(Color c, double thickness, bool dashed)
    {
        var level = (int)Math.Clamp(Math.Round(thickness * 2), 1, 14);
        if (!_pens.TryGetValue((c, level, dashed), out var p))
        {
            p = new Pen(Brush(c), level / 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (dashed) p.DashStyle = _dash;
            _pens[(c, level, dashed)] = p;
        }
        return p;
    }

    private static Color NodeColor(GraphNode n) => n.Status switch
    {
        NodeStatus.Warning => CWarning,
        NodeStatus.Closed => CClosed,
        NodeStatus.New when n.Kind != NodeKind.Computer => CNew,
        _ => n.Kind switch { NodeKind.Computer => CComputer, NodeKind.Process => CProcess, NodeKind.Listening => CListening, _ => CRemote },
    };

    private FormattedText Text(string s, double size, bool bold, Brush fg)
    {
        var key = (s, (int)(size * 10), bold);
        if (!_texts.TryGetValue(key, out var ft))
        {
            if (_texts.Count > 3000) _texts.Clear();
            ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = 220, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            _texts[key] = ft;
        }
        ft.SetForegroundBrush(fg);
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var g = Graph;
        if (g is null) return;
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var panel = TryFindResource("PanelBrush") as Brush ?? Brushes.Black;
        var border = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;

        if (g.Nodes.Count <= 1)
        {
            var ft = Text("Waiting for network activity...", 14, false, muted);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, ActualHeight / 2 - 40));
        }

        dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, ActualWidth / 2 + _offX, ActualHeight / 2 + _offY));
        var hasSel = _selected is not null;
        var glowEdges = g.Edges.Count < 500;

        // edges
        foreach (var e in g.Edges)
        {
            var lit = !hasSel || (_neighbors.Contains(e.From.Id) && _neighbors.Contains(e.To.Id) && (e.From == _selected || e.To == _selected || IsPathEdge(e)));
            var c = e.Status switch
            {
                NodeStatus.Closed => CClosed,
                NodeStatus.New => CNew,
                _ => e.From.Kind == NodeKind.Computer ? CComputer : e.To.Kind == NodeKind.Listening ? CListening : CProcess,
            };
            if (e.To.Status == NodeStatus.Warning && e.Status != NodeStatus.Closed) c = CWarning;
            var thick = Math.Min(7, 1 + Math.Log2(e.Count + 1) * 0.9);
            var a = new Point(e.From.X, e.From.Y); var b = new Point(e.To.X, e.To.Y);
            if (!lit) { dc.PushOpacity(0.13); dc.DrawLine(EdgePen(c, 1, false), a, b); dc.Pop(); continue; }
            if (glowEdges && e.Status != NodeStatus.Closed)
            {
                dc.PushOpacity(hasSel ? 0.34 : 0.2);
                dc.DrawLine(EdgePen(c, thick + 5, false), a, b);
                dc.Pop();
            }
            dc.PushOpacity(e.Status == NodeStatus.Closed ? 0.55 : 0.9);
            dc.DrawLine(EdgePen(c, thick, Animate && e.Status != NodeStatus.Closed && g.Edges.Count < 900), a, b);
            dc.Pop();
        }

        // nodes: leaves first, then processes, then computer
        foreach (var kind in new[] { NodeKind.Remote, NodeKind.Listening, NodeKind.Process, NodeKind.Computer })
        {
            foreach (var n in g.Nodes.Values)
            {
                if (n.Kind != kind) continue;
                var dim = hasSel && !_neighbors.Contains(n.Id);
                var col = NodeColor(n);
                var p = new Point(n.X, n.Y);
                if (dim) dc.PushOpacity(0.2);
                dc.DrawEllipse(Glow(col), null, p, n.Radius * 2.1, n.Radius * 2.1);
                var isSel = ReferenceEquals(n, _selected) || ReferenceEquals(n, _hover);
                dc.DrawEllipse(Brush(Color.FromArgb(255, (byte)(col.R * 0.22), (byte)(col.G * 0.22), (byte)(col.B * 0.22))),
                    new Pen(Brush(col), isSel ? 3.2 : 2), p, n.Radius, n.Radius);
                dc.DrawEllipse(Brush(col), null, p, n.Radius * (n.Kind == NodeKind.Computer ? 0.5 : 0.38), n.Radius * (n.Kind == NodeKind.Computer ? 0.5 : 0.38));
                if (n.Pinned && n.Kind != NodeKind.Computer) dc.DrawEllipse(Brushes.White, null, new Point(n.X + n.Radius * 0.75, n.Y - n.Radius * 0.75), 2.2, 2.2);

                var showLabel = n.Kind is NodeKind.Computer or NodeKind.Process || _scale >= 0.9 || isSel || (hasSel && !dim);
                if (showLabel && _scale > 0.35)
                {
                    var ft = Text(n.Label, n.Kind == NodeKind.Process || n.Kind == NodeKind.Computer ? 12 : 10.5, n.Kind != NodeKind.Remote, dim ? muted : text);
                    dc.DrawText(ft, new Point(n.X - ft.Width / 2, n.Y + n.Radius + 4));
                }
                if (dim) dc.Pop();
            }
        }
        dc.Pop();

        // hover tooltip (screen space)
        if (_hover is { } h && _drag is null && !_panning)
        {
            var lines = new List<string> { h.Kind switch { NodeKind.Computer => "Local computer: " + h.Label, NodeKind.Process => $"{h.Label}  (PID {h.Process?.Pid})", NodeKind.Listening => "Listening " + h.Label + $"  ({h.Process?.Name})", _ => "Remote " + h.Label } };
            lines.Add($"{h.ConnectionCount} connection{(h.ConnectionCount == 1 ? "" : "s")}" + (h.Status == NodeStatus.New ? "  |  newly detected" : h.Status == NodeStatus.Closed ? "  |  closed" : h.Status == NodeStatus.Warning ? "  |  matches an alert rule" : ""));
            if (h.Kind == NodeKind.Process && h.Process?.Path is { } path) lines.Add(path);
            var fts = lines.Select((s, i) => Text(s, i == 0 ? 12 : 11, i == 0, i == 0 ? text : muted)).ToList();
            var w = fts.Max(f => f.Width) + 20; var ht = fts.Sum(f => f.Height) + 14;
            var x = Math.Min(_lastMouse.X + 16, Math.Max(0, ActualWidth - w - 8)); var y = Math.Min(_lastMouse.Y + 16, Math.Max(0, ActualHeight - ht - 8));
            dc.DrawRoundedRectangle(panel, new Pen(border, 1), new Rect(x, y, w, ht), 6, 6);
            var ty = y + 7;
            foreach (var f in fts) { dc.DrawText(f, new Point(x + 10, ty)); ty += f.Height; }
        }
    }

    private bool IsPathEdge(GraphEdge e)
    {
        // Edge between two lit nodes that is not directly attached to the selection (e.g. computer -> process of a selected remote).
        return _selected is not null && (e.From.Kind == NodeKind.Computer || e.From.Kind == NodeKind.Process) && _neighbors.Contains(e.From.Id) && _neighbors.Contains(e.To.Id)
               && (_selected.Kind != NodeKind.Computer);
    }
}
