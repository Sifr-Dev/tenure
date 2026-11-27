using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tenure;

public sealed record HeapType(string Name, long RetainedBytes, long ObjectCount)
{
    public const int MaximumNameLength = 4096;

    public static HeapType Create(string name, long retainedBytes, long objectCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        if (name.Length > MaximumNameLength) throw new InvalidDataException("A type name exceeds the 4096-byte limit.");
        return new(name, retainedBytes, objectCount);
    }
}

/// <summary>Shallow bytes exclusively dominated by roots of a single GC-root kind.</summary>
public sealed record RootKindTotal(int Kind, long RetainedBytes, long ObjectCount)
{
    public static RootKindTotal Create(int kind, long retainedBytes, long objectCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(kind);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        return new(kind, retainedBytes, objectCount);
    }
}

/// <summary>
/// One observed ownership chain from the dominating root down to a representative retained object.
/// The first hop names the GC-root kind and every later hop is the type name of an object, so two
/// captures of the same workload can be compared even though heap addresses differ. Depth counts
/// edges from the root's object to the representative: a representative that is itself the root
/// object has depth 0. The chain is evidence, not a proof: when several roots of the same kind
/// retain objects of the same type, one chain is recorded for the whole group, so changing the
/// type alone does not prove every retained object of that type is held by the shown chain.
/// </summary>
public sealed record OwnershipEvidence(IReadOnlyList<string> Path, long Depth, bool Truncated)
{
    /// <summary>
    /// Value equality over the hop sequence. The default record comparison would compare the list
    /// by reference, which silently makes two equal chains from two captures unequal.
    /// </summary>
    public bool Equals(OwnershipEvidence? other) =>
        other is not null && Depth == other.Depth && Truncated == other.Truncated && Path.SequenceEqual(other.Path);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var hop in Path) hash.Add(hop, StringComparer.Ordinal);
        hash.Add(Depth);
        hash.Add(Truncated);
        return hash.ToHashCode();
    }

    /// <summary>Largest recorded edge count. Deeper chains keep a prefix and set <see cref="Truncated"/>.</summary>
    public const int MaximumHops = 256;

    public static OwnershipEvidence Create(IEnumerable<string> path, long depth, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(path);
        var hops = path.ToArray();
        if (hops.Length < 2) throw new InvalidDataException("Ownership evidence requires a root hop and at least one object hop.");
        if (hops.Length > MaximumHops + 2) throw new InvalidDataException("Ownership evidence exceeds the 256-hop limit.");
        foreach (var hop in hops)
        {
            if (string.IsNullOrWhiteSpace(hop)) throw new InvalidDataException("An ownership evidence hop is empty.");
            if (hop.Length > HeapType.MaximumNameLength) throw new InvalidDataException("An ownership evidence hop exceeds the 4096-byte limit.");
        }
        if (!RootKinds.IsKnownName(hops[0]))
            throw new InvalidDataException($"Unknown ownership evidence root kind: {hops[0]}");
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        var edges = hops.Length - 2;
        if (!truncated && depth != edges)
            throw new InvalidDataException("An untruncated ownership evidence chain must have depth equal to its edge count.");
        if (truncated && depth <= edges)
            throw new InvalidDataException("A truncated ownership evidence chain must be deeper than its retained prefix.");
        return new(hops, depth, truncated);
    }
}

/// <summary>
/// Shallow bytes that roots of one kind exclusively dominate, split by the type of the retained
/// object. The groups of a kind sum exactly to its <see cref="RootKindTotal"/>.
/// </summary>
public sealed record OwnershipGroup(
    int RootKind,
    string TypeName,
    long RetainedBytes,
    long ObjectCount,
    int RootCount,
    OwnershipEvidence Evidence)
{
    public static OwnershipGroup Create(int rootKind, string typeName, long retainedBytes, long objectCount, int rootCount, OwnershipEvidence evidence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rootKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        if (typeName.Length > HeapType.MaximumNameLength) throw new InvalidDataException("An ownership type name exceeds the 4096-byte limit.");
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentOutOfRangeException.ThrowIfNegative(rootCount);
        ArgumentNullException.ThrowIfNull(evidence);
        if (objectCount == 0) throw new InvalidDataException("An ownership group must retain at least one object.");
        if (rootCount == 0 || rootCount > objectCount)
            throw new InvalidDataException("An ownership group's root count must be between 1 and its object count.");
        return new(rootKind, typeName, retainedBytes, objectCount, rootCount, evidence);
    }
}

public sealed record HeapSnapshot(
    IReadOnlyList<HeapType> Types,
    IReadOnlyList<RootKindTotal> RootKindTotals,
    IReadOnlyList<OwnershipGroup> Ownership)
{
    /// <summary>
    /// Version 1 stores shallow per-type aggregates only. Version 2 adds <c>rootKindRetention</c>.
    /// Version 3 adds <c>ownershipRetention</c>: the same exclusive retention split by retained
    /// type, with an observed ownership chain as change evidence. Every earlier field and meaning
    /// is unchanged, so version-1 and version-2 readers keep working on their own fields.
    /// </summary>
    public const int CurrentVersion = 3;
    public const int MinimumSupportedVersion = 1;
    public const int MaximumTypes = 1_000_000;
    public const int MaximumOwnershipGroups = 250_000;

    public bool HasRootKindRetention => RootKindTotals.Count != 0;

    public bool HasOwnership => Ownership.Count != 0;

    public static HeapSnapshot Create(
        IEnumerable<HeapType> types,
        IEnumerable<RootKindTotal>? rootKindTotals = null,
        IEnumerable<OwnershipGroup>? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        var ordered = types.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        if (ordered.Length > MaximumTypes) throw new InvalidDataException("The snapshot has too many types.");
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i - 1].Name == ordered[i].Name)
                throw new InvalidDataException($"Duplicate type: {ordered[i].Name}");
        var kinds = (rootKindTotals ?? []).OrderBy(k => k.Kind).ToArray();
        for (var i = 1; i < kinds.Length; i++)
            if (kinds[i - 1].Kind == kinds[i].Kind)
                throw new InvalidDataException($"Duplicate root kind: {kinds[i].Kind}");
        var groups = (ownership ?? [])
            .OrderBy(o => o.RootKind)
            .ThenBy(o => o.TypeName, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length > MaximumOwnershipGroups) throw new InvalidDataException("The snapshot has too many ownership groups.");
        for (var i = 1; i < groups.Length; i++)
            if (groups[i - 1].RootKind == groups[i].RootKind && groups[i - 1].TypeName == groups[i].TypeName)
                throw new InvalidDataException($"Duplicate ownership group: kind {groups[i].RootKind}, type {groups[i].TypeName}");
        ValidateOwnershipConsistency(kinds, groups);
        return new(ordered, kinds, groups);
    }

    /// <summary>
    /// Ownership groups are a strict refinement of root-kind retention: each kind's groups must sum
    /// to the kind total, and a kind total with retention must have at least one group. A snapshot
    /// that disagrees with itself is rejected instead of being repaired.
    /// </summary>
    private static void ValidateOwnershipConsistency(RootKindTotal[] kinds, OwnershipGroup[] groups)
    {
        if (groups.Length == 0) return;
        if (kinds.Length == 0)
            throw new InvalidDataException("Ownership retention requires root-kind retention.");
        var sums = groups.GroupBy(g => g.RootKind)
            .ToDictionary(
                g => g.Key,
                g => (Bytes: g.Sum(x => x.RetainedBytes), Count: g.Sum(x => x.ObjectCount)));
        foreach (var kind in kinds)
        {
            if (!sums.TryGetValue(kind.Kind, out var sum))
            {
                if (kind.RetainedBytes != 0 || kind.ObjectCount != 0)
                    throw new InvalidDataException($"Root kind {kind.Kind} declares retention without ownership groups.");
                continue;
            }
            if (sum.Bytes != kind.RetainedBytes || sum.Count != kind.ObjectCount)
                throw new InvalidDataException($"Ownership groups for root kind {kind.Kind} do not sum to its root-kind retention.");
        }
        foreach (var kind in sums.Keys)
            if (!kinds.Any(k => k.Kind == kind))
                throw new InvalidDataException($"Ownership group for root kind {kind} has no root-kind retention.");
    }

    public async Task WriteAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var document = new SnapshotDocument
        {
            Version = HasOwnership ? CurrentVersion : HasRootKindRetention ? 2 : MinimumSupportedVersion,
            Types = Types.Select(t => new HeapTypeDocument
            {
                Name = t.Name,
                RetainedBytes = t.RetainedBytes,
                ObjectCount = t.ObjectCount,
            }).ToArray(),
            RootKindRetention = HasRootKindRetention
                ? RootKindTotals.Select(k => new RootKindDocument
                {
                    Kind = k.Kind,
                    RetainedBytes = k.RetainedBytes,
                    ObjectCount = k.ObjectCount,
                }).ToArray()
                : null,
            OwnershipRetention = HasOwnership
                ? Ownership.Select(o => new OwnershipDocument
                {
                    Kind = o.RootKind,
                    Type = o.TypeName,
                    RetainedBytes = o.RetainedBytes,
                    ObjectCount = o.ObjectCount,
                    RootCount = o.RootCount,
                    Evidence = new OwnershipEvidenceDocument
                    {
                        Path = o.Evidence.Path.ToArray(),
                        Depth = o.Evidence.Depth,
                        Truncated = o.Evidence.Truncated,
                    },
                }).ToArray()
                : null,
        };
        await JsonSerializer.SerializeAsync(destination, document, SnapshotJsonContext.Default.SnapshotDocument, cancellationToken);
    }

    public static async Task<HeapSnapshot> ReadAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var document = await JsonSerializer.DeserializeAsync(source, SnapshotJsonContext.Default.SnapshotDocument, cancellationToken)
            ?? throw new InvalidDataException("The snapshot is empty.");
        if (document.Version is < MinimumSupportedVersion or > CurrentVersion)
            throw new InvalidDataException($"Unsupported snapshot version {document.Version}.");
        if (document.Version >= 2 && document.RootKindRetention is null)
            throw new InvalidDataException("A version-2 or later snapshot must declare rootKindRetention.");
        if (document.Version == 1 && document.RootKindRetention is not null)
            throw new InvalidDataException("A version-1 snapshot must not declare rootKindRetention.");
        if (document.Version >= 3 && document.OwnershipRetention is null)
            throw new InvalidDataException("A version-3 snapshot must declare ownershipRetention.");
        if (document.Version < 3 && document.OwnershipRetention is not null)
            throw new InvalidDataException($"A version-{document.Version} snapshot must not declare ownershipRetention.");
        return Create(
            document.Types.Select(t => t.ToModel()),
            document.RootKindRetention?.Select(k => k.ToModel()),
            document.OwnershipRetention?.Select(o => o.ToModel()));
    }
}

public sealed class SnapshotDocument
{
    public int Version { get; init; } = HeapSnapshot.CurrentVersion;
    public required HeapTypeDocument[] Types { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RootKindDocument[]? RootKindRetention { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OwnershipDocument[]? OwnershipRetention { get; init; }
}

public sealed class HeapTypeDocument
{
    public required string Name { get; init; }
    public required long RetainedBytes { get; init; }
    public required long ObjectCount { get; init; }
}

public sealed class RootKindDocument
{
    public required int Kind { get; init; }
    public required long RetainedBytes { get; init; }
    public required long ObjectCount { get; init; }
}

public sealed class OwnershipDocument
{
    public required int Kind { get; init; }
    public required string Type { get; init; }
    public required long RetainedBytes { get; init; }
    public required long ObjectCount { get; init; }
    public required int RootCount { get; init; }
    public required OwnershipEvidenceDocument Evidence { get; init; }
}

public sealed class OwnershipEvidenceDocument
{
    public required string[] Path { get; init; }
    public required long Depth { get; init; }
    public required bool Truncated { get; init; }
}

public static class HeapSnapshotDocumentExtensions
{
    public static HeapType ToModel(this HeapTypeDocument document) =>
        HeapType.Create(document.Name, document.RetainedBytes, document.ObjectCount);

    public static RootKindTotal ToModel(this RootKindDocument document) =>
        RootKindTotal.Create(document.Kind, document.RetainedBytes, document.ObjectCount);

    public static OwnershipGroup ToModel(this OwnershipDocument document)
    {
        ArgumentNullException.ThrowIfNull(document.Evidence);
        return OwnershipGroup.Create(
            document.Kind,
            document.Type,
            document.RetainedBytes,
            document.ObjectCount,
            document.RootCount,
            OwnershipEvidence.Create(document.Evidence.Path, document.Evidence.Depth, document.Evidence.Truncated));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SnapshotDocument))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;
