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

public sealed record HeapSnapshot(IReadOnlyList<HeapType> Types)
{
    public const int CurrentVersion = 1;
    public const int MaximumTypes = 1_000_000;

    public static HeapSnapshot Create(IEnumerable<HeapType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var ordered = types.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        if (ordered.Length > MaximumTypes) throw new InvalidDataException("The snapshot has too many types.");
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i - 1].Name == ordered[i].Name)
                throw new InvalidDataException($"Duplicate type: {ordered[i].Name}");
        return new(ordered);
    }

    public async Task WriteAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var document = new SnapshotDocument
        {
            Types = Types.Select(t => new HeapTypeDocument
            {
                Name = t.Name,
                RetainedBytes = t.RetainedBytes,
                ObjectCount = t.ObjectCount,
            }).ToArray(),
        };
        await JsonSerializer.SerializeAsync(destination, document, SnapshotJsonContext.Default.SnapshotDocument, cancellationToken);
    }

    public static async Task<HeapSnapshot> ReadAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var document = await JsonSerializer.DeserializeAsync(source, SnapshotJsonContext.Default.SnapshotDocument, cancellationToken)
            ?? throw new InvalidDataException("The snapshot is empty.");
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported snapshot version {document.Version}.");
        return Create(document.Types.Select(t => t.ToModel()));
    }
}

public sealed class SnapshotDocument
{
    public int Version { get; init; } = HeapSnapshot.CurrentVersion;
    public required HeapTypeDocument[] Types { get; init; }
}

public sealed class HeapTypeDocument
{
    public required string Name { get; init; }
    public required long RetainedBytes { get; init; }
    public required long ObjectCount { get; init; }
}

public static class HeapTypeDocumentExtensions
{
    public static HeapType ToModel(this HeapTypeDocument document) =>
        HeapType.Create(document.Name, document.RetainedBytes, document.ObjectCount);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SnapshotDocument))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;
