namespace Tenure;

public sealed record TypeDelta(string Name, long BeforeBytes, long AfterBytes, long BeforeCount, long AfterCount)
{
    public long ByteDelta => AfterBytes - BeforeBytes;
    public long CountDelta => AfterCount - BeforeCount;
}

public sealed record RootKindDelta(int Kind, long BeforeBytes, long AfterBytes, long BeforeCount, long AfterCount)
{
    public long ByteDelta => AfterBytes - BeforeBytes;
    public long CountDelta => AfterCount - BeforeCount;
}

public sealed record DiffReport(
    IReadOnlyList<TypeDelta> Regressions,
    long TotalByteDelta,
    long TotalCountDelta,
    IReadOnlyList<RootKindDelta> RootKindRegressions,
    long TotalRootKindByteDelta,
    long TotalRootKindCountDelta)
{
    /// <summary>
    /// False when either snapshot predates root-kind retention (version 1). The type comparison is
    /// still valid; callers that gate on <c>--by-root-kind</c> must refuse rather than pass silently.
    /// </summary>
    public bool RootKindComparable { get; init; } = true;

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
        var comparable = CanCompareRootKinds(baseline, candidate);
        var kinds = comparable ? CompareRootKinds(baseline, candidate) : [];
        return new(deltas, deltas.Sum(d => d.ByteDelta), deltas.Sum(d => d.CountDelta), kinds, kinds.Sum(d => d.ByteDelta), kinds.Sum(d => d.CountDelta))
        {
            RootKindComparable = comparable,
        };
    }

    public static bool CanCompareRootKinds(HeapSnapshot baseline, HeapSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        return baseline.HasRootKindRetention && candidate.HasRootKindRetention;
    }

    /// <summary>
    /// Compares exclusive retention per GC-root kind. Both snapshots must carry root-kind totals;
    /// a version-1 snapshot cannot be compared this way because it lacks dominator retention.
    /// </summary>
    public static IReadOnlyList<RootKindDelta> CompareRootKinds(HeapSnapshot baseline, HeapSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!CanCompareRootKinds(baseline, candidate))
            throw new InvalidDataException(
                "Root-kind comparison requires snapshots captured with root analysis (snapshot version 2).");
        var before = baseline.RootKindTotals.ToDictionary(k => k.Kind);
        var after = candidate.RootKindTotals.ToDictionary(k => k.Kind);
        return after.Keys.Concat(before.Keys).Distinct()
            .Select(kind =>
            {
                before.TryGetValue(kind, out var b);
                after.TryGetValue(kind, out var a);
                return new RootKindDelta(kind, b?.RetainedBytes ?? 0, a?.RetainedBytes ?? 0, b?.ObjectCount ?? 0, a?.ObjectCount ?? 0);
            })
            .Where(d => d.ByteDelta > 0)
            .OrderByDescending(d => d.ByteDelta)
            .ThenByDescending(d => d.CountDelta)
            .ThenBy(d => d.Kind)
            .ToArray();
    }

    public bool Exceeds(long byteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteBudget);
        return TotalByteDelta > byteBudget;
    }

    public bool ExceedsRootKind(long byteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteBudget);
        return TotalRootKindByteDelta > byteBudget;
    }
}
