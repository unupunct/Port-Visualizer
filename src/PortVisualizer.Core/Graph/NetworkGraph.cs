using PortVisualizer.Core.Models;
using PortVisualizer.Core.Settings;

namespace PortVisualizer.Core.Graph;

public enum NodeKind { Computer, Process, Remote, Listening }
public enum NodeStatus { Normal, New, Closed, Warning }

public sealed class GraphNode
{
    public GraphNode(string id, NodeKind kind, string label) { Id = id; Kind = kind; Label = label; }
    public string Id { get; }
    public NodeKind Kind { get; }
    public string Label { get; set; }
    public double X, Y, VX, VY;
    public double TargetX, TargetY;
    public bool Pinned;
    public NodeStatus Status { get; set; }
    public int ConnectionCount { get; set; }
    public double Radius { get; set; } = 10;
    public string? ParentId { get; set; }
    /// <summary>Process for process/listening nodes; null otherwise.</summary>
    public ProcessInfo? Process { get; set; }
    public string? RemoteAddress { get; set; }
    public List<TrackedConnection> Connections { get; } = new();
}

public sealed class GraphEdge
{
    public GraphEdge(string key, GraphNode from, GraphNode to) { Key = key; From = from; To = to; }
    public string Key { get; }
    public GraphNode From { get; }
    public GraphNode To { get; }
    public int Count { get; set; }
    public NodeStatus Status { get; set; }
}

public sealed record GraphOptions(
    bool GroupRemoteByIp = true,
    bool ShowListening = true,
    bool ShowClosed = true,
    TimeSpan? NewHighlight = null,
    IReadOnlySet<string>? WarningRemoteIps = null,
    int MaxRemoteNodes = 4000);

/// <summary>
/// Incrementally maintained Computer -> Process -> Remote graph. Existing nodes keep their position and
/// identity across updates; only added/removed nodes and changed counts are touched.
/// </summary>
public sealed class NetworkGraph
{
    public const string ComputerId = "pc";
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly Dictionary<string, GraphEdge> _edges = new();
    private readonly Random _rng = new(7);

    public NetworkGraph()
    {
        _nodes[ComputerId] = new GraphNode(ComputerId, NodeKind.Computer, Environment.MachineName) { Pinned = true, Radius = 28 };
    }

    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;
    public IReadOnlyCollection<GraphEdge> Edges => _edges.Values;
    public GraphNode Computer => _nodes[ComputerId];
    public GraphLayoutMode Mode { get; private set; } = GraphLayoutMode.ForceDirected;
    public double Energy { get; private set; } = double.MaxValue;
    public int HiddenRemoteCount { get; private set; }

    /// <summary>Applies a new set of connections. Returns true if nodes or edges were added or removed.</summary>
    public bool Update(IReadOnlyList<TrackedConnection> active, IReadOnlyList<TrackedConnection> closed, GraphOptions opt, DateTime nowUtc)
    {
        var highlight = opt.NewHighlight ?? TimeSpan.FromSeconds(10);
        var wantNodes = new Dictionary<string, GraphNode>();
        var wantEdges = new Dictionary<string, (string From, string To, int Count, NodeStatus Status)>();
        var seen = new Dictionary<string, (int Open, int Closed, int New)>();
        HiddenRemoteCount = 0;

        void Touch(string nodeId, TrackedConnection t)
        {
            seen.TryGetValue(nodeId, out var s);
            if (t.IsClosed) s.Closed++; else s.Open++;
            if (!t.IsClosed && t.DetectedAfterBaseline && nowUtc - t.FirstObservedUtc < highlight) s.New++;
            seen[nodeId] = s;
        }

        var remoteCount = 0;
        foreach (var t in opt.ShowClosed ? active.Concat(closed) : active)
        {
            var c = t.Connection;
            var pid = "p:" + c.Process.Key;
            string? childId = null;
            NodeKind childKind = NodeKind.Remote;
            string childLabel = "";

            if (c.IsListening || c.IsUdp)
            {
                if (!opt.ShowListening) continue;
                childId = $"l:{c.Process.Key}:{c.Protocol}";
                childKind = NodeKind.Listening;
                childLabel = c.Protocol.ToString().ToUpperInvariant();
            }
            else if (c.HasRemote)
            {
                childId = opt.GroupRemoteByIp ? "r:" + c.RemoteAddress : $"r:{c.RemoteAddress}:{c.RemotePort}";
                childLabel = opt.GroupRemoteByIp ? c.RemoteAddress! : c.RemoteEndpoint!;
                if (!wantNodes.ContainsKey(childId) && !_nodes.ContainsKey(childId) && ++remoteCount > opt.MaxRemoteNodes)
                { HiddenRemoteCount++; continue; }
            }

            if (!wantNodes.TryGetValue(pid, out var pn))
            {
                if (!_nodes.TryGetValue(pid, out pn))
                    pn = new GraphNode(pid, NodeKind.Process, c.Process.Name) { ParentId = ComputerId };
                pn.Process = c.Process;
                pn.Label = c.Process.Name;
                wantNodes[pid] = pn;
                pn.Connections.Clear();
            }
            pn.Connections.Add(t);
            Touch(pid, t);
            AddEdge(wantEdges, ComputerId, pid, t, nowUtc, highlight);

            if (childId is null) continue;
            if (!wantNodes.TryGetValue(childId, out var cn))
            {
                if (!_nodes.TryGetValue(childId, out cn))
                    cn = new GraphNode(childId, childKind, childLabel) { ParentId = childKind == NodeKind.Listening ? pid : pid };
                cn.Label = childLabel;
                cn.Process = c.Process;
                cn.RemoteAddress = c.RemoteAddress;
                wantNodes[childId] = cn;
                cn.Connections.Clear();
            }
            cn.Connections.Add(t);
            Touch(childId, t);
            AddEdge(wantEdges, pid, childId, t, nowUtc, highlight);
        }

        // Status & counts
        foreach (var (id, node) in wantNodes)
        {
            var s = seen[id];
            node.ConnectionCount = s.Open + s.Closed;
            if (node.Kind == NodeKind.Listening && node.Connections.Count > 0)
            {
                var first = node.Connections[0].Connection;
                var proto = first.Protocol == Protocol.Tcp ? "TCP listen" : "UDP";
                node.Label = node.Connections.Count == 1 ? $"{first.Protocol.ToString().ToUpperInvariant()} :{first.LocalPort}" : $"{proto} x{node.Connections.Count}";
            }
            node.Status = s.Open == 0 ? NodeStatus.Closed : s.New > 0 ? NodeStatus.New : NodeStatus.Normal;
            if (node.Kind == NodeKind.Remote && node.RemoteAddress is { } ra && opt.WarningRemoteIps?.Contains(ra) == true)
                node.Status = NodeStatus.Warning;
            node.Radius = node.Kind switch
            {
                NodeKind.Process => 13 + Math.Min(10, Math.Log2(1 + node.ConnectionCount) * 2),
                NodeKind.Listening => 9,
                _ => 8 + Math.Min(5, Math.Log2(1 + node.ConnectionCount)),
            };
        }
        Computer.ConnectionCount = active.Count;

        var changed = false;
        foreach (var id in _nodes.Keys.ToList())
        {
            if (id == ComputerId) continue;
            if (!wantNodes.ContainsKey(id)) { _nodes.Remove(id); changed = true; }
        }
        foreach (var (id, node) in wantNodes)
        {
            if (_nodes.ContainsKey(id)) continue;
            Spawn(node, wantNodes);
            _nodes[id] = node;
            changed = true;
        }
        foreach (var k in _edges.Keys.ToList())
            if (!wantEdges.ContainsKey(k)) { _edges.Remove(k); changed = true; }
        foreach (var (k, e) in wantEdges)
        {
            if (!_edges.TryGetValue(k, out var edge))
            {
                edge = new GraphEdge(k, _nodes[e.From], _nodes[e.To]);
                _edges[k] = edge;
                changed = true;
            }
            edge.Count = e.Count;
            edge.Status = e.Status;
        }

        if (changed) Energy = double.MaxValue;
        return changed;
    }

    private static void AddEdge(Dictionary<string, (string From, string To, int Count, NodeStatus Status)> d, string from, string to,
        TrackedConnection t, DateTime now, TimeSpan highlight)
    {
        var key = from + ">" + to;
        d.TryGetValue(key, out var e);
        var st = t.IsClosed ? NodeStatus.Closed : t.DetectedAfterBaseline && now - t.FirstObservedUtc < highlight ? NodeStatus.New : NodeStatus.Normal;
        // an edge is "normal" if any connection on it is established and old.
        var merged = e.Count == 0 ? st : (e.Status == NodeStatus.Normal || st == NodeStatus.Normal) ? NodeStatus.Normal
                   : (e.Status == NodeStatus.New || st == NodeStatus.New) ? NodeStatus.New : NodeStatus.Closed;
        d[key] = (from, to, e.Count + 1, merged);
    }

    private void Spawn(GraphNode n, Dictionary<string, GraphNode> wanted)
    {
        GraphNode? parent = null;
        if (n.ParentId is not null && !_nodes.TryGetValue(n.ParentId, out parent)) wanted.TryGetValue(n.ParentId, out parent);
        var a = _rng.NextDouble() * Math.PI * 2;
        var r = n.Kind == NodeKind.Process ? 120 + _rng.NextDouble() * 60 : 50 + _rng.NextDouble() * 40;
        n.X = (parent?.X ?? 0) + Math.Cos(a) * r;
        n.Y = (parent?.Y ?? 0) + Math.Sin(a) * r;
    }

    public void Wake() => Energy = double.MaxValue;

    public void SetMode(GraphLayoutMode mode)
    {
        Mode = mode;
        Energy = double.MaxValue;
    }

    /// <summary>Reset to a clean layout: clears pins, re-seeds positions and restarts the simulation.</summary>
    public void ResetLayout()
    {
        foreach (var n in _nodes.Values)
        {
            n.VX = n.VY = 0;
            if (n.Kind != NodeKind.Computer) n.Pinned = false;
        }
        Computer.X = Computer.Y = 0;
        foreach (var n in _nodes.Values.Where(n => n.Kind == NodeKind.Process))
        { var a = _rng.NextDouble() * Math.PI * 2; n.X = Math.Cos(a) * 140; n.Y = Math.Sin(a) * 140; }
        foreach (var n in _nodes.Values.Where(n => n.Kind is NodeKind.Remote or NodeKind.Listening))
        {
            _nodes.TryGetValue(n.ParentId ?? "", out var p);
            var a = _rng.NextDouble() * Math.PI * 2;
            n.X = (p?.X ?? 0) + Math.Cos(a) * 70; n.Y = (p?.Y ?? 0) + Math.Sin(a) * 70;
        }
        Energy = double.MaxValue;
    }

    /// <summary>Advances the layout. Returns false once the layout has settled.</summary>
    public bool Step(int iterations = 1)
    {
        if (Energy < 0.02 && Mode == GraphLayoutMode.ForceDirected) return false;
        if (Mode == GraphLayoutMode.ForceDirected)
        {
            for (var i = 0; i < iterations; i++) Energy = ForceStep();
            return Energy >= 0.02;
        }
        ComputeTargets();
        double moved = 0;
        foreach (var n in _nodes.Values)
        {
            if (n.Pinned) continue;
            var dx = n.TargetX - n.X; var dy = n.TargetY - n.Y;
            n.X += dx * 0.18; n.Y += dy * 0.18;
            moved += Math.Abs(dx) + Math.Abs(dy);
        }
        Energy = moved / Math.Max(1, _nodes.Count);
        return Energy >= 0.05;
    }

    /// <summary>Runs the force simulation synchronously until it settles (used for tests and "Reset layout").</summary>
    public void Settle(int maxIterations = 400)
    {
        for (var i = 0; i < maxIterations && Step(); i++) { }
    }

    private double ForceStep()
    {
        const double repulse = 2600, spring = 0.035, gravity = 0.004, damping = 0.82, cell = 150;
        var nodes = _nodes.Values.ToArray();
        var grid = new Dictionary<(int, int), List<GraphNode>>();
        foreach (var n in nodes)
        {
            var key = ((int)Math.Floor(n.X / cell), (int)Math.Floor(n.Y / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<GraphNode>(4);
            l.Add(n);
        }
        foreach (var n in nodes)
        {
            var cx = (int)Math.Floor(n.X / cell); var cy = (int)Math.Floor(n.Y / cell);
            for (var gx = cx - 1; gx <= cx + 1; gx++)
                for (var gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var l)) continue;
                    foreach (var m in l)
                    {
                        if (ReferenceEquals(m, n)) continue;
                        var dx = n.X - m.X; var dy = n.Y - m.Y;
                        var d2 = dx * dx + dy * dy;
                        if (d2 < 0.01) { dx = _rng.NextDouble() - 0.5; dy = _rng.NextDouble() - 0.5; d2 = 0.5; }
                        var d = Math.Sqrt(d2);
                        var minD = n.Radius + m.Radius + 14;
                        var f = repulse / d2 + (d < minD ? (minD - d) * 0.5 : 0);
                        n.VX += dx / d * f; n.VY += dy / d * f;
                    }
                }
            n.VX -= n.X * gravity; n.VY -= n.Y * gravity;
        }
        foreach (var e in _edges.Values)
        {
            var dx = e.To.X - e.From.X; var dy = e.To.Y - e.From.Y;
            var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var rest = e.From.Kind == NodeKind.Computer ? 190 : 85;
            var f = (d - rest) * spring;
            var fx = dx / d * f; var fy = dy / d * f;
            e.From.VX += fx; e.From.VY += fy; e.To.VX -= fx; e.To.VY -= fy;
        }
        double energy = 0;
        foreach (var n in nodes)
        {
            if (n.Pinned) { n.VX = n.VY = 0; continue; }
            n.VX = Math.Clamp(n.VX * damping, -30, 30); n.VY = Math.Clamp(n.VY * damping, -30, 30);
            n.X += n.VX; n.Y += n.VY;
            energy += Math.Abs(n.VX) + Math.Abs(n.VY);
        }
        return energy / Math.Max(1, nodes.Length);
    }

    private void ComputeTargets()
    {
        var procs = _nodes.Values.Where(n => n.Kind == NodeKind.Process).OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var childrenOf = _nodes.Values.Where(n => n.Kind is NodeKind.Remote or NodeKind.Listening)
            .GroupBy(n => n.ParentId ?? "").ToDictionary(g => g.Key, g => g.OrderBy(n => n.Label, StringComparer.Ordinal).ToList());
        Computer.TargetX = Computer.TargetY = 0;
        Computer.X = Computer.Y = 0;

        switch (Mode)
        {
            case GraphLayoutMode.Hierarchical:
            {
                const double gap = 78, rowGap = 70, perRow = 28;
                var pIdx = 0;
                var procY = -120.0;
                var count = procs.Count;
                foreach (var p in procs)
                {
                    var row = (int)(pIdx / perRow); var col = pIdx % (int)perRow;
                    var inRow = Math.Min(perRow, count - row * perRow);
                    p.TargetX = (col - (inRow - 1) / 2.0) * gap * 1.6;
                    p.TargetY = procY + row * 40;
                    pIdx++;
                }
                var kids = procs.SelectMany(p => childrenOf.GetValueOrDefault(p.Id) ?? new()).ToList();
                var k = 0;
                foreach (var n in kids)
                {
                    var row = (int)(k / (perRow * 1.6)); var colsInRow = (int)(perRow * 1.6);
                    var col = k % colsInRow;
                    var inRow = Math.Min(colsInRow, kids.Count - row * colsInRow);
                    n.TargetX = (col - (inRow - 1) / 2.0) * gap * 0.9;
                    n.TargetY = 110 + row * rowGap;
                    k++;
                }
                Computer.TargetY = -220; Computer.Y = -220;
                break;
            }
            case GraphLayoutMode.Circular:
            {
                var r1 = Math.Max(170, procs.Count * 34 / (2 * Math.PI));
                var all = procs.SelectMany(p => childrenOf.GetValueOrDefault(p.Id) ?? new()).ToList();
                var r2 = r1 + Math.Max(150, all.Count * 24 / (2 * Math.PI));
                for (var i = 0; i < procs.Count; i++)
                {
                    var a = 2 * Math.PI * i / Math.Max(1, procs.Count) - Math.PI / 2;
                    procs[i].TargetX = Math.Cos(a) * r1; procs[i].TargetY = Math.Sin(a) * r1;
                }
                for (var i = 0; i < all.Count; i++)
                {
                    var a = 2 * Math.PI * i / Math.Max(1, all.Count) - Math.PI / 2;
                    all[i].TargetX = Math.Cos(a) * r2; all[i].TargetY = Math.Sin(a) * r2;
                }
                break;
            }
            default: // GroupedByProcess
            {
                var maxKids = procs.Select(p => childrenOf.GetValueOrDefault(p.Id)?.Count ?? 0).DefaultIfEmpty(0).Max();
                var clusterR = Math.Max(60, Math.Sqrt(maxKids) * 34);
                var ring = Math.Max(260, procs.Count * (clusterR * 2 + 30) / (2 * Math.PI));
                for (var i = 0; i < procs.Count; i++)
                {
                    var a = 2 * Math.PI * i / Math.Max(1, procs.Count) - Math.PI / 2;
                    var p = procs[i];
                    p.TargetX = Math.Cos(a) * ring; p.TargetY = Math.Sin(a) * ring;
                    var kids = childrenOf.GetValueOrDefault(p.Id) ?? new();
                    for (var j = 0; j < kids.Count; j++)
                    {
                        var ka = 2 * Math.PI * j / Math.Max(1, kids.Count);
                        var kr = 40 + Math.Sqrt(kids.Count) * 22 * (1 + j % 2 * 0.0);
                        kids[j].TargetX = p.TargetX + Math.Cos(ka) * kr; kids[j].TargetY = p.TargetY + Math.Sin(ka) * kr;
                    }
                }
                break;
            }
        }
    }
}
