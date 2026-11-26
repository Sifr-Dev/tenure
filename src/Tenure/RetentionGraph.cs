namespace Tenure;

public sealed class RetentionGraph
{
    public sealed record TypeReachability(string Name, long ShallowBytes, long ObjectCount);
    public sealed record RootRetention(int RootKind, ulong RootId, ulong RootAddress, long RetainedBytes, long ObjectCount);
    public sealed record RootKindRetention(int RootKind, long RetainedBytes, long ObjectCount);
    public sealed record RootPath(ulong Address, int RootKind, ulong RootId, ulong RootAddress, IReadOnlyList<ulong> Path);

    private const ulong VirtualRoot = 0;
    private readonly IReadOnlyDictionary<ulong, HeapGraphBuilder.Node> _nodes;
    private readonly IReadOnlyDictionary<ulong, string> _types;
    private readonly Dictionary<ulong, List<HeapGraphBuilder.Edge>> _outgoing = new();
    private readonly Dictionary<ulong, List<ulong>> _incoming = new();
    private readonly Dictionary<ulong, int> _postorder = new();
    private readonly Dictionary<ulong, int> _depth = new();
    private readonly Dictionary<ulong, ulong> _parent = new();
    private readonly Dictionary<ulong, ulong?> _idom = new();
    private readonly Dictionary<ulong, HeapGraphBuilder.RootEdge> _roots = new();
    private readonly HashSet<ulong> _reachable = new();
    private readonly Dictionary<ulong, ulong> _dominators = new();

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
            if (!_incoming.TryGetValue(edge.Target, out var incoming))
            {
                incoming = new List<ulong>();
                _incoming[edge.Target] = incoming;
            }
            incoming.Add(edge.Source);
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

    public IReadOnlyList<RootRetention> ComputeExclusiveRetention()
    {
        TraverseRoots();
        ComputeDominators();
        var children = DominatorChildren();
        var results = new List<RootRetention>();
        foreach (var root in _roots.Values.OrderBy(r => r.RootedNodeAddress))
        {
            if (!_reachable.Contains(root.RootedNodeAddress)) continue;
            var dominated = DominatedBy(root.RootedNodeAddress, children);
            results.Add(new RootRetention(root.Kind, root.RootId, root.RootedNodeAddress, ShallowBytes(dominated), dominated.Count));
        }
        return results;
    }

    /// <summary>
    /// Partitions reachable objects by the <paramref name="kind"/> of the root that exclusively
    /// retains them. Roots occupy disjoint dominator subtrees, so every object is counted at most
    /// once and objects not dominated by any root are excluded. The resulting bytes are therefore
    /// a lower bound on root-reachable bytes whenever a root shares an object.
    /// </summary>
    public IReadOnlyList<RootKindRetention> ComputeRootKindRetention()
    {
        TraverseRoots();
        ComputeDominators();
        var children = DominatorChildren();
        var totals = new Dictionary<int, (long Bytes, long Count)>();
        foreach (var root in _roots.Values.OrderBy(r => r.RootedNodeAddress))
        {
            if (!_reachable.Contains(root.RootedNodeAddress)) continue;
            var dominated = DominatedBy(root.RootedNodeAddress, children);
            totals.TryGetValue(root.Kind, out var current);
            current.Bytes += ShallowBytes(dominated);
            current.Count += dominated.Count;
            totals[root.Kind] = current;
        }
        return totals.Select(x => new RootKindRetention(x.Key, x.Value.Bytes, x.Value.Count))
            .OrderByDescending(x => x.RetainedBytes).ThenBy(x => x.RootKind).ToArray();
    }

    private Dictionary<ulong, List<ulong>> DominatorChildren()
    {
        var children = new Dictionary<ulong, List<ulong>>();
        foreach (var address in _reachable)
        {
            if (_roots.ContainsKey(address)) continue;
            var parent = _dominators[address];
            if (!children.TryGetValue(parent, out var list))
            {
                list = new List<ulong>();
                children[parent] = list;
            }
            list.Add(address);
        }
        return children;
    }

    private HashSet<ulong> DominatedBy(ulong rootAddress, Dictionary<ulong, List<ulong>> children)
    {
        var dominated = new HashSet<ulong>();
        var queue = new Queue<ulong>();
        queue.Enqueue(rootAddress);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dominated.Add(current)) continue;
            if (!children.TryGetValue(current, out var list)) continue;
            foreach (var child in list) queue.Enqueue(child);
        }
        return dominated;
    }

    private long ShallowBytes(IEnumerable<ulong> addresses)
    {
        long bytes = 0;
        foreach (var address in addresses) bytes += (long)_nodes[address].ShallowSize;
        return bytes;
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

    private void ComputeDominators()
    {
        _dominators.Clear();
        _dominators[VirtualRoot] = VirtualRoot;
        foreach (var address in _reachable) _dominators[address] = VirtualRoot;

        _postorder.Clear();
        _postorder[VirtualRoot] = 0;
        var nextOrder = 1;
        var visited = new HashSet<ulong>();
        void Visit(ulong address)
        {
            if (!visited.Add(address)) return;
            if (_outgoing.TryGetValue(address, out var edges))
            {
                foreach (var edge in edges.OrderBy(e => e.Target).ThenBy(e => e.Field))
                    if (_reachable.Contains(edge.Target)) Visit(edge.Target);
            }
            _postorder[address] = nextOrder++;
        }
        foreach (var root in _roots.Keys) Visit(root);

        var order = _reachable.OrderBy(address => _postorder[address]).ToArray();
        var changed = true;
        var rounds = 0;
        while (changed && rounds++ <= _reachable.Count)
        {
            changed = false;
            foreach (var address in order)
            {
                if (_roots.ContainsKey(address)) continue;
                if (!_incoming.TryGetValue(address, out var parents)) continue;
                var candidates = parents.Where(parent => _reachable.Contains(parent)).OrderBy(x => x).ToArray();
                if (candidates.Length == 0) continue;
                var next = candidates[0];
                foreach (var candidate in candidates.Skip(1)) next = Intersect(next, candidate);
                if (_dominators[address] != next)
                {
                    _dominators[address] = next;
                    changed = true;
                }
            }
        }
        if (changed) throw new InvalidDataException("Dominator computation did not converge.");
    }

    private ulong Intersect(ulong left, ulong right)
    {
        while (left != right)
        {
            while (_postorder[left] > _postorder[right]) left = _dominators[left];
            while (_postorder[right] > _postorder[left]) right = _dominators[right];
        }
        return left;
    }

    private int Depth(ulong address) => address == VirtualRoot ? -1 : _depth[address];

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
