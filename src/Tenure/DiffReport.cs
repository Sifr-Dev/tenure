namespace Tenure;

public sealed record TypeDelta(string Name, long BeforeBytes, long AfterBytes, long BeforeCount, long AfterCount)
{
    public long ByteDelta => AfterBytes - BeforeBytes;
    public long CountDelta => AfterCount - BeforeCount;
}

public sealed record DiffReport(IReadOnlyList<TypeDelta> Regressions, long TotalByteDelta, long TotalCountDelta)
{
    public static DiffReport Compare(HeapSnapshot baseline, HeapSnapshot candidate, IReadOnlyCollection<string>? ignoredTypes = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var ignored = ignoredTypes is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(ignoredTypes, StringComparer.Ordinal);
        var before = baseline.Types.Where(t => !ignored.Contains(t.Name)).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var after = candidate.Types.Where(t => !ignored.Contains(t.Name)).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var deltas = after.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                before.TryGetValue(name, out var b);
                after.TryGetValue(name, out var a);
                return new TypeDelta(name, b?.RetainedBytes ?? 0, a?.RetainedBytes ?? 0, b?.ObjectCount ?? 0, a?.ObjectCount ?? 0);
            })
            .Where(d => d.ByteDelta > 0)
            .OrderByDescending(d => d.ByteDelta)
            .ThenByDescending(d => d.CountDelta)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
        return new(deltas, deltas.Sum(d => d.ByteDelta), deltas.Sum(d => d.CountDelta));
    }

    public bool Exceeds(long byteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteBudget);
        return TotalByteDelta > byteBudget;
    }
}
