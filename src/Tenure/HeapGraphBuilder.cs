using Microsoft.Diagnostics.Tracing.Parsers;

namespace Tenure;

public sealed class HeapGraphBuilder
{
    public sealed record Node(ulong Address, ulong TypeId, ulong ShallowSize, long EdgeCount);
    public sealed record Edge(ulong Source, ulong Target, int Field);
    public sealed record RootEdge(ulong RootedNodeAddress, int Kind, int Flags, ulong RootId);

    private readonly Dictionary<ulong, Node> _nodes = new();
    private readonly Dictionary<ulong, string> _types = new();
    private readonly List<Edge> _edges = new();
    private readonly List<RootEdge> _roots = new();
    private readonly Queue<(ulong Target, int Field)> _references = new();
    private readonly int _maximumNodes;
    private readonly List<Node> _orderedNodes = new();
    private readonly int _maximumEdges;

    public HeapGraphBuilder(int maximumNodes = 500_000, int maximumEdges = 2_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEdges);
        _maximumNodes = maximumNodes;
        _maximumEdges = maximumEdges;
    }

    public void Attach(ClrTraceEventParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        parser.TypeBulkType += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                var value = data.Values(i);
                if (string.IsNullOrWhiteSpace(value.TypeName) || value.TypeName.Length > 4096)
                    throw new InvalidDataException("A type name is missing or too long.");
                _types[value.TypeID] = value.TypeName;
            }
        };
        parser.GCBulkNode += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                var value = data.Values(i);
                if (_nodes.Count >= _maximumNodes && !_nodes.ContainsKey(value.Address))
                    throw new InvalidDataException("The graph exceeds the configured node limit.");
                if (value.Size > long.MaxValue) throw new InvalidDataException("An object is too large.");
                _nodes[value.Address] = new(value.Address, value.TypeID, value.Size, value.EdgeCount);
                _orderedNodes.Add(_nodes[value.Address]);
            }
        };
        parser.GCBulkEdge += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                var value = data.Values(i);
                if (_edges.Count >= _maximumEdges) throw new InvalidDataException("The graph exceeds the configured edge limit.");
                _references.Enqueue((value.Target, value.ReferencingField));
            }
        };
        parser.GCBulkRootEdge += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                var value = data.Values(i);
                _roots.Add(new(value.RootedNodeAddress, (int)value.GCRootKind, (int)value.GCRootFlag, value.GCRootID));
            }
        };
    }

    public IReadOnlyDictionary<ulong, Node> Nodes => _nodes;
    public IReadOnlyDictionary<ulong, string> Types => _types;
    public IReadOnlyList<Edge> Edges => _edges;
    public IReadOnlyList<RootEdge> Roots => _roots;

    public void AddNode(Node node)
    {
        if (_nodes.Count >= _maximumNodes && !_nodes.ContainsKey(node.Address))
            throw new InvalidDataException("The graph exceeds the configured node limit.");
        _nodes[node.Address] = node;
        _orderedNodes.Add(node);
    }

    public void AddReference(ulong target, int field)
    {
        if (_references.Count >= _maximumEdges)
            throw new InvalidDataException("The graph exceeds the configured edge limit.");
        _references.Enqueue((target, field));
    }

    public void AddRoot(ulong address, int kind, int flags, ulong rootId)
    {
        _roots.Add(new(address, kind, flags, rootId));
    }

    public void CompleteGraph()
    {
        _edges.Clear();
        foreach (var node in _orderedNodes)
        {
            for (long i = 0; i < node.EdgeCount; i++)
            {
                if (!_references.TryDequeue(out var reference))
                    throw new InvalidDataException("The graph ends before all outgoing references are present.");
                _edges.Add(new(node.Address, reference.Target, reference.Field));
            }
        }
        if (_references.Count != 0)
            throw new InvalidDataException("The graph has references without a corresponding source node.");
    }

    public HeapSnapshot ToSnapshot(IEnumerable<RootKindTotal>? rootKindTotals = null)
    {
        var groups = new Dictionary<string, (long Bytes, long Count)>(StringComparer.Ordinal);
        foreach (var node in _nodes.Values)
        {
            var name = _types.TryGetValue(node.TypeId, out var typeName) ? typeName : $"Unknown type 0x{node.TypeId:x}";
            groups.TryGetValue(name, out var aggregate);
            aggregate.Bytes += (long)node.ShallowSize;
            aggregate.Count += 1;
            groups[name] = aggregate;
        }
        return HeapSnapshot.Create(groups.Select(g => HeapType.Create(g.Key, g.Value.Bytes, g.Value.Count)), rootKindTotals);
    }
}
