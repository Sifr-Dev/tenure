using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tenure;

public sealed record HeapType(string Name, long RetainedBytes, long ObjectCount)
{
    public static HeapType Create(string name, long retainedBytes, long objectCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        if (name.Length > 4096) throw new InvalidDataException("A type name exceeds the 4096-byte limit.");
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

public sealed record HeapSnapshot(IReadOnlyList<HeapType> Types, IReadOnlyList<RootKindTotal> RootKindTotals)
{
    /// <summary>
    /// Version 1 stores shallow per-type aggregates only. Version 2 adds <c>rootKindRetention</c>
    /// and leaves every version-1 field and meaning unchanged, so version-1 consumers keep working
    /// and version-1 snapshots stay readable.
    /// </summary>
    public const int CurrentVersion = 2;
    public const int MinimumSupportedVersion = 1;
    public const int MaximumTypes = 1_000_000;

    public bool HasRootKindRetention => RootKindTotals.Count != 0;

    public static HeapSnapshot Create(IEnumerable<HeapType> types, IEnumerable<RootKindTotal>? rootKindTotals = null)
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
        return new(ordered, kinds);
    }

    public async Task WriteAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var document = new SnapshotDocument
        {
            Version = HasRootKindRetention ? CurrentVersion : MinimumSupportedVersion,
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
            throw new InvalidDataException("A version-2 snapshot must declare rootKindRetention.");
        if (document.Version == 1 && document.RootKindRetention is not null)
            throw new InvalidDataException("A version-1 snapshot must not declare rootKindRetention.");
        return Create(
            document.Types.Select(t => t.ToModel()),
            document.RootKindRetention?.Select(k => k.ToModel()));
    }
}

public sealed class SnapshotDocument
{
    public int Version { get; init; } = HeapSnapshot.CurrentVersion;
    public required HeapTypeDocument[] Types { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RootKindDocument[]? RootKindRetention { get; init; }
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

public static class HeapTypeDocumentExtensions
{
    public static HeapType ToModel(this HeapTypeDocument document) =>
        HeapType.Create(document.Name, document.RetainedBytes, document.ObjectCount);

    public static RootKindTotal ToModel(this RootKindDocument document) =>
        RootKindTotal.Create(document.Kind, document.RetainedBytes, document.ObjectCount);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SnapshotDocument))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;
