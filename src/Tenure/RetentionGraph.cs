namespace Tenure;

public sealed class RetentionGraph
{
    public sealed record TypeReachability(string Name, long ShallowBytes, long ObjectCount);
    public sealed record RootPath(ulong Address, int RootKind, ulong RootId, ulong RootAddress, IReadOnlyList<ulong> Path);

    private readonly IReadOnlyDictionary<ulong, HeapGraphBuilder.Node> _nodes;
    private readonly IReadOnlyDictionary<ulong, string> _types;
    private readonly Dictionary<ulong, List<HeapGraphBuilder.Edge>> _outgoing = new();
    private readonly Dictionary<ulong, int> _depth = new();
    private readonly Dictionary<ulong, ulong> _parent = new();
    private readonly Dictionary<ulong, HeapGraphBuilder.RootEdge> _roots = new();
    private readonly HashSet<ulong> _reachable = new();

    public RetentionGraph(HeapGraphBuilder graph)
    {
        _nodes = graph.Nodes;
        _types = graph.Types;
        foreach (var edge in graph.Edges)
        {
            if (!_outgoing.TryGetValue(edge.Source, out var list))
            {
                list = new List<HeapGraphBuilder.Edge>();
                _outgoing[edge.Source] = list;
            }
            list.Add(edge);
        }
        foreach (var root in graph.Roots.OrderBy(r => r.RootedNodeAddress).ThenBy(r => r.Kind).ThenBy(r => r.RootId))
            _roots.TryAdd(root.RootedNodeAddress, root);
    }

    public IReadOnlyList<TypeReachability> ComputeReachability()
    {
        TraverseRoots();
        var totals = new Dictionary<string, (long Bytes, long Count)>(StringComparer.Ordinal);
        foreach (var address in _reachable)
        {
            var node = _nodes[address];
            var name = _types.TryGetValue(node.TypeId, out var type) ? type : $"Unknown type 0x{node.TypeId:x}";
            totals.TryGetValue(name, out var current);
            current.Bytes += (long)node.ShallowSize;
            current.Count++;
            totals[name] = current;
        }
        return totals.Select(x => new TypeReachability(x.Key, x.Value.Bytes, x.Value.Count))
            .OrderByDescending(x => x.ShallowBytes).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    public RootPath? FindRootPath(ulong address)
    {
        TraverseRoots();
        if (!_reachable.Contains(address)) return null;
        var path = new List<ulong> { address };
        var current = address;
        while (!_roots.ContainsKey(current))
        {
            if (!_parent.TryGetValue(current, out var parent)) return null;
            current = parent;
            path.Add(current);
        }
        path.Reverse();
        var root = _roots[current];
        return new RootPath(address, root.Kind, root.RootId, current, path);
    }

    private void TraverseRoots()
    {
        if (_depth.Count != 0 || _roots.Count == 0) return;
        var queue = new Queue<ulong>();
        foreach (var root in _roots.Keys)
        {
            if (!_nodes.ContainsKey(root)) continue;
            _depth[root] = 0;
            queue.Enqueue(root);
        }
        while (queue.Count > 0)
        {
            var address = queue.Dequeue();
            if (!_reachable.Add(address)) continue;
            if (!_outgoing.TryGetValue(address, out var edges)) continue;
            foreach (var edge in edges.OrderBy(e => e.Target).ThenBy(e => e.Field))
            {
                if (!_nodes.ContainsKey(edge.Target)) continue;
                if (_depth.TryAdd(edge.Target, _depth[address] + 1))
                {
                    _parent[edge.Target] = address;
                    queue.Enqueue(edge.Target);
                }
            }
        }
    }
}
