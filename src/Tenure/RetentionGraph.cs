namespace Tenure;

public sealed class RetentionGraph
{
    public sealed record TypeReachability(string Name, long ShallowBytes, long ObjectCount);
    public sealed record RootRetention(int RootKind, ulong RootId, ulong RootAddress, long RetainedBytes, long ObjectCount);
    public sealed record RootKindRetention(int RootKind, long RetainedBytes, long ObjectCount);
    public sealed record RootPath(ulong Address, int RootKind, ulong RootId, ulong RootAddress, IReadOnlyList<ulong> Path);
    public sealed record TypeOwnership(
        int RootKind,
        string TypeName,
        long RetainedBytes,
        long ObjectCount,
        int RootCount,
        OwnershipEvidence Evidence);

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

    /// <summary>
    /// Refines exclusive dominator retention by the type of each retained object, so a report can
    /// say which objects a root kind is holding rather than only how many bytes. Each group carries
    /// the shortest observed ownership chain from a dominator root to a representative object of
    /// that type, expressed as type names because heap addresses are not stable between captures.
    /// Group bytes sum exactly to <see cref="ComputeRootKindRetention"/> for the same graph.
    /// </summary>
    public IReadOnlyList<TypeOwnership> ComputeTypeOwnership()
    {
        TraverseRoots();
        ComputeDominators();
        var children = DominatorChildren();
        var groups = new Dictionary<(int Kind, string Type), TypeBucket>();
        var rootOrder = _roots.Values.OrderBy(r => r.RootedNodeAddress).ToArray();
        foreach (var root in rootOrder)
        {
            if (!_reachable.Contains(root.RootedNodeAddress)) continue;
            var perType = new Dictionary<string, TypeBucket>(StringComparer.Ordinal);
            foreach (var address in DominatedBy(root.RootedNodeAddress, children).OrderBy(x => x))
            {
                var node = _nodes[address];
                var name = TypeNameOf(node);
                if (!perType.TryGetValue(name, out var bucket))
                {
                    bucket = new TypeBucket(roots: 1);
                    perType[name] = bucket;
                }
                bucket.Add(_depth[address], address, (long)node.ShallowSize);
            }
            foreach (var (name, bucket) in perType)
            {
                var key = (root.Kind, name);
                if (!groups.TryGetValue(key, out var aggregate))
                {
                    aggregate = new TypeBucket();
                    groups[key] = aggregate;
                }
                aggregate.Merge(bucket);
            }
        }
        return groups
            .Select(group => new TypeOwnership(
                group.Key.Kind,
                group.Key.Type,
                group.Value.Bytes,
                group.Value.Count,
                group.Value.Roots,
                group.Value.BuildEvidence(this)))
            .OrderByDescending(x => x.RetainedBytes)
            .ThenBy(x => x.RootKind)
            .ThenBy(x => x.TypeName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Bytes, count, and the candidate set for the shortest ownership chain of one (kind, type)
    /// group. Only objects at the group's minimum BFS depth are kept, so a deep heap does not cause
    /// a full parent walk per object and the retained set stays small.
    /// </summary>
    private sealed class TypeBucket
    {
        private readonly List<ulong> _candidates = new();

        public TypeBucket(int roots = 0) => Roots = roots;

        public long Bytes { get; private set; }
        public long Count { get; private set; }
        public int Roots { get; private set; }
        public int BestDepth { get; private set; } = int.MaxValue;

        public void Add(int depth, ulong address, long shallowBytes)
        {
            Bytes += shallowBytes;
            Count++;
            Consider(depth, address);
        }

        public void Merge(TypeBucket other)
        {
            Bytes += other.Bytes;
            Count += other.Count;
            Roots += other.Roots;
            foreach (var address in other._candidates) Consider(other.BestDepth, address);
        }

        private void Consider(int depth, ulong address)
        {
            if (depth < BestDepth)
            {
                BestDepth = depth;
                _candidates.Clear();
                _candidates.Add(address);
            }
            else if (depth == BestDepth)
            {
                _candidates.Add(address);
            }
        }

        /// <summary>
        /// Builds the chain for every candidate at the minimum depth and keeps the ordinally
        /// smallest. Comparing path content rather than addresses keeps the recorded evidence stable
        /// between two captures of the same workload.
        /// </summary>
        public OwnershipEvidence BuildEvidence(RetentionGraph graph)
        {
            OwnershipEvidence? best = null;
            foreach (var address in _candidates.OrderBy(x => x))
            {
                var candidate = graph.BuildEvidenceFrom(address);
                if (best is null || PreferEvidence(candidate, best)) best = candidate;
            }
            return best!;
        }
    }

    private static bool PreferEvidence(OwnershipEvidence candidate, OwnershipEvidence current)
    {
        if (candidate.Path.Count != current.Path.Count) return candidate.Path.Count < current.Path.Count;
        for (var i = 0; i < candidate.Path.Count; i++)
        {
            var comparison = string.CompareOrdinal(candidate.Path[i], current.Path[i]);
            if (comparison != 0) return comparison < 0;
        }
        return false;
    }

    private string TypeNameOf(HeapGraphBuilder.Node node) =>
        _types.TryGetValue(node.TypeId, out var type) ? type : $"Unknown type 0x{node.TypeId:x}";

    /// <summary>
    /// Walks the BFS parent chain from a representative object up to its dominating root. The chain
    /// is deterministic for a given graph because <see cref="TraverseRoots"/> visits roots and edges
    /// in a fixed order and keeps the first parent it assigns.
    /// </summary>
    private OwnershipEvidence BuildEvidenceFrom(ulong representative)
    {
        var reversed = new List<string>();
        var current = representative;
        var depth = 0L;
        while (true)
        {
            reversed.Add(TypeNameOf(_nodes[current]));
            if (_roots.TryGetValue(current, out var root))
            {
                reversed.Add(RootKinds.NameOf(root.Kind));
                break;
            }
            if (!_parent.TryGetValue(current, out var parent)) break;
            depth++;
            current = parent;
        }
        var path = new List<string>(reversed.Count);
        for (var i = reversed.Count - 1; i >= 0; i--) path.Add(reversed[i]);
        var maximumPathHops = OwnershipEvidence.MaximumHops + 2;
        if (path.Count > maximumPathHops)
            return OwnershipEvidence.Create(path.Take(maximumPathHops).ToArray(), depth, truncated: true);
        return OwnershipEvidence.Create(path, depth, truncated: false);
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
