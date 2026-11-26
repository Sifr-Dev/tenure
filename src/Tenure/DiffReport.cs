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

/// <summary>
/// Growth of exclusive dominator retention for one (root kind, retained type) pair, with the
/// candidate's observed ownership chain. Only growth is reported (the same rule as the type and
/// root-kind reports), so the candidate always contains the group that grew and its chain is real
/// evidence from that capture rather than a chain borrowed from the baseline.
/// </summary>
public sealed record OwnershipDelta(
    int Kind,
    string TypeName,
    long BeforeBytes,
    long AfterBytes,
    long BeforeCount,
    long AfterCount,
    long BeforeRootCount,
    long AfterRootCount,
    OwnershipEvidence Evidence)
{
    public long ByteDelta => AfterBytes - BeforeBytes;
    public long CountDelta => AfterCount - BeforeCount;

    /// <summary>True when the baseline had no objects of this type under this root kind at all.</summary>
    public bool IsNew => BeforeCount == 0;
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

    /// <summary>
    /// Positive exclusive-retention growth split by (root kind, retained type), with the observed
    /// ownership chain of the changed group. Empty when <see cref="OwnershipComparable"/> is false.
    /// </summary>
    public IReadOnlyList<OwnershipDelta> OwnershipRegressions { get; init; } = [];

    public long TotalOwnershipByteDelta => OwnershipRegressions.Sum(d => d.ByteDelta);

    public long TotalOwnershipCountDelta => OwnershipRegressions.Sum(d => d.CountDelta);

    /// <summary>False unless both snapshots were captured with ownership evidence (version 3).</summary>
    public bool OwnershipComparable { get; init; } = true;

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
        var ownershipComparable = CanCompareOwnership(baseline, candidate);
        var ownership = ownershipComparable ? CompareOwnership(baseline, candidate, ignoredTypes) : [];
        return new(deltas, deltas.Sum(d => d.ByteDelta), deltas.Sum(d => d.CountDelta), kinds, kinds.Sum(d => d.ByteDelta), kinds.Sum(d => d.CountDelta))
        {
            RootKindComparable = comparable,
            OwnershipRegressions = ownership,
            OwnershipComparable = ownershipComparable,
        };
    }

    public static bool CanCompareRootKinds(HeapSnapshot baseline, HeapSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        return baseline.HasRootKindRetention && candidate.HasRootKindRetention;
    }

    /// <summary>Ownership evidence needs both snapshots to carry version-3 groups.</summary>
    public static bool CanCompareOwnership(HeapSnapshot baseline, HeapSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        return baseline.HasOwnership && candidate.HasOwnership;
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
                "Root-kind comparison requires snapshots captured with root analysis (snapshot version 2 or later).");
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

    /// <summary>
    /// Compares exclusive retention per (root kind, retained type) and attaches the observed
    /// ownership chain of each changed group. Both snapshots must carry version-3 ownership
    /// evidence; a version-2 snapshot has the kind totals but cannot say which type is responsible,
    /// so the comparison refuses instead of reporting a misleading zero.
    /// </summary>
    public static IReadOnlyList<OwnershipDelta> CompareOwnership(
        HeapSnapshot baseline,
        HeapSnapshot candidate,
        IReadOnlyCollection<string>? ignoredTypes = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!CanCompareOwnership(baseline, candidate))
            throw new InvalidDataException(
                "Ownership comparison requires snapshots captured with ownership paths (snapshot version 3).");
        var ignored = ignoredTypes is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(ignoredTypes, StringComparer.Ordinal);
        var before = baseline.Ownership
            .Where(o => !ignored.Contains(o.TypeName))
            .ToDictionary(o => (o.RootKind, o.TypeName));
        var after = candidate.Ownership
            .Where(o => !ignored.Contains(o.TypeName))
            .ToDictionary(o => (o.RootKind, o.TypeName));
        return after.Keys.Concat(before.Keys).Distinct()
            // Growth only, decided before the candidate is dereferenced: a group that shrank or
            // disappeared has no candidate evidence, and inventing one would be a lie.
            .Where(key => (after.TryGetValue(key, out var a) ? a.RetainedBytes : 0) - (before.TryGetValue(key, out var b) ? b.RetainedBytes : 0) > 0)
            .Select(key =>
            {
                before.TryGetValue(key, out var b);
                var a = after[key];
                return new OwnershipDelta(
                    key.RootKind,
                    key.TypeName,
                    b?.RetainedBytes ?? 0,
                    a.RetainedBytes,
                    b?.ObjectCount ?? 0,
                    a.ObjectCount,
                    b?.RootCount ?? 0,
                    a.RootCount,
                    a.Evidence);
            })
            .OrderByDescending(d => d.ByteDelta)
            .ThenBy(d => d.Kind)
            .ThenBy(d => d.TypeName, StringComparer.Ordinal)
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

    public bool ExceedsOwnership(long byteBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteBudget);
        return TotalOwnershipByteDelta > byteBudget;
    }
}
