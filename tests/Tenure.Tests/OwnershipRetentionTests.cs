using System.Text;
using Xunit;

namespace Tenure.Tests;

public sealed class OwnershipRetentionTests
{
    private static HeapGraphBuilder RootWithChildren()
    {
        // Handle root (0x10) owns 0x11 (type 7) which owns 0x12 and 0x13 (type 8).
        // Stack root (0x20) owns 0x21 (type 8).
        var builder = new HeapGraphBuilder();
        builder.AddType(7, "App.Root");
        builder.AddType(8, "System.Byte[]");
        builder.AddNode(new(0x10, 7, 32, 1));
        builder.AddNode(new(0x11, 7, 64, 2));
        builder.AddNode(new(0x12, 8, 128, 0));
        builder.AddNode(new(0x13, 8, 256, 0));
        builder.AddNode(new(0x20, 7, 16, 1));
        builder.AddNode(new(0x21, 8, 512, 0));
        builder.AddReference(0x11, 0);
        builder.AddReference(0x12, 0);
        builder.AddReference(0x13, 0);
        builder.AddReference(0x21, 0);
        builder.AddRoot(0x10, RootKinds.Handle, 0, 100);
        builder.AddRoot(0x20, RootKinds.Stack, 0, 200);
        builder.CompleteGraph();
        return builder;
    }

    [Fact]
    public void OwnershipGroupsSumToRootKindTotals()
    {
        var graph = new RetentionGraph(RootWithChildren());
        var byKind = graph.ComputeRootKindRetention();
        var ownership = graph.ComputeTypeOwnership();

        Assert.Equal(byKind.Sum(k => k.RetainedBytes), ownership.Sum(o => o.RetainedBytes));
        Assert.Equal(byKind.Sum(k => k.ObjectCount), ownership.Sum(o => o.ObjectCount));
        foreach (var kind in byKind)
        {
            Assert.Equal(kind.RetainedBytes, ownership.Where(o => o.RootKind == kind.RootKind).Sum(o => o.RetainedBytes));
            Assert.Equal(kind.ObjectCount, ownership.Where(o => o.RootKind == kind.RootKind).Sum(o => o.ObjectCount));
        }
    }

    [Fact]
    public void OwnershipGroupsSplitRetentionByRetainedType()
    {
        var ownership = new RetentionGraph(RootWithChildren()).ComputeTypeOwnership();

        var handleRoot = Assert.Single(ownership, o => o.RootKind == RootKinds.Handle && o.TypeName == "App.Root");
        Assert.Equal(96, handleRoot.RetainedBytes);
        Assert.Equal(2, handleRoot.ObjectCount);

        var handleArrays = Assert.Single(ownership, o => o.RootKind == RootKinds.Handle && o.TypeName == "System.Byte[]");
        Assert.Equal(384, handleArrays.RetainedBytes);
        Assert.Equal(2, handleArrays.ObjectCount);

        var stackArrays = Assert.Single(ownership, o => o.RootKind == RootKinds.Stack && o.TypeName == "System.Byte[]");
        Assert.Equal(512, stackArrays.RetainedBytes);
        Assert.Equal(1, stackArrays.ObjectCount);
        Assert.Equal(1, handleRoot.RootCount);
    }

    [Fact]
    public void EvidenceStartsAtTheRootKindAndEndsAtTheRepresentativeType()
    {
        var ownership = new RetentionGraph(RootWithChildren()).ComputeTypeOwnership();
        var stackArrays = Assert.Single(ownership, o => o.RootKind == RootKinds.Stack && o.TypeName == "System.Byte[]");

        Assert.Equal(["Stack", "App.Root", "System.Byte[]"], stackArrays.Evidence.Path);
        Assert.Equal(1, stackArrays.Evidence.Depth);
        Assert.False(stackArrays.Evidence.Truncated);

        var handleRoot = Assert.Single(ownership, o => o.RootKind == RootKinds.Handle && o.TypeName == "App.Root");
        Assert.Equal(["Handle", "App.Root"], handleRoot.Evidence.Path);
        Assert.Equal(0, handleRoot.Evidence.Depth);
    }

    [Fact]
    public void OwnershipIsDeterministicAcrossRuns()
    {
        static IReadOnlyList<RetentionGraph.TypeOwnership> Build() => new RetentionGraph(RootWithChildren()).ComputeTypeOwnership();
        var first = Build();
        var second = Build();
        Assert.Equal(first, second);
        Assert.Equal(
            first.OrderByDescending(o => o.RetainedBytes).ThenBy(o => o.RootKind).ThenBy(o => o.TypeName, StringComparer.Ordinal).Select(o => o.RetainedBytes),
            first.Select(o => o.RetainedBytes));
    }

    [Fact]
    public void SharedObjectsAreNotAttributedToOwnership()
    {
        // 0x3 is reachable directly from both roots, so neither root exclusively dominates it.
        var builder = new HeapGraphBuilder();
        builder.AddType(7, "Shared");
        builder.AddNode(new(1, 7, 64, 1));
        builder.AddNode(new(2, 7, 128, 1));
        builder.AddNode(new(3, 7, 512, 0));
        builder.AddReference(3, 0);
        builder.AddReference(3, 0);
        builder.AddRoot(1, RootKinds.Stack, 0, 1);
        builder.AddRoot(2, RootKinds.Handle, 0, 2);
        builder.CompleteGraph();

        var ownership = new RetentionGraph(builder).ComputeTypeOwnership();
        Assert.DoesNotContain(ownership, o => o.RetainedBytes == 512);
        Assert.Equal(192, ownership.Sum(o => o.RetainedBytes));
        Assert.Equal(192, new RetentionGraph(builder).ComputeRootKindRetention().Sum(k => k.RetainedBytes));
    }

    [Fact]
    public void EvidenceLongerThanTheHopLimitIsTruncatedButKeepsItsDepth()
    {
        // A chain of distinct types, so the deepest type has no shallower representative and its
        // recorded chain must be truncated while still reporting the true depth.
        var builder = new HeapGraphBuilder();
        var count = OwnershipEvidence.MaximumHops + 10;
        for (var i = 0; i < count; i++) builder.AddType((ulong)(i + 1), $"App.Link{i}");
        for (var i = 0; i < count; i++)
            builder.AddNode(new((ulong)(i + 1), (ulong)(i + 1), 8, i == count - 1 ? 0 : 1));
        for (var i = 1; i < count; i++) builder.AddReference((ulong)(i + 1), 0);
        builder.AddRoot(1, RootKinds.Stack, 0, 1);
        builder.CompleteGraph();

        var deepest = new RetentionGraph(builder).ComputeTypeOwnership()
            .Single(o => o.TypeName == $"App.Link{count - 1}");
        Assert.True(deepest.Evidence.Truncated);
        Assert.Equal(OwnershipEvidence.MaximumHops + 2, deepest.Evidence.Path.Count);
        Assert.Equal(count - 1, deepest.Evidence.Depth);
    }
}

public sealed class OwnershipDiffTests
{
    private static OwnershipEvidence Evidence(params string[] path) =>
        OwnershipEvidence.Create(path, path.Length - 2, truncated: false);

    private static OwnershipGroup Group(int kind, string type, long bytes, long objects, int roots, params string[] path) =>
        OwnershipGroup.Create(kind, type, bytes, objects, roots, Evidence("Handle", "App.Holder", "System.Byte[]"));

    private static HeapSnapshot Version3(params OwnershipGroup[] groups)
    {
        var kinds = groups.GroupBy(g => g.RootKind)
            .Select(g => RootKindTotal.Create(g.Key, g.Sum(x => x.RetainedBytes), g.Sum(x => x.ObjectCount)))
            .ToArray();
        return HeapSnapshot.Create([HeapType.Create("System.Byte[]", 4096, 8)], kinds, groups);
    }

    [Fact]
    public async Task VersionThreeRoundTripsOwnershipWithEvidence()
    {
        var snapshot = Version3(Group(RootKinds.Handle, "System.Byte[]", 4096, 8, 2));
        await using var stream = new MemoryStream();
        await snapshot.WriteAsync(stream, TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\": 3", text);
        Assert.Contains("\"ownershipRetention\"", text);
        Assert.Contains("\"rootKindRetention\"", text);
        Assert.Contains("\"evidence\"", text);

        stream.Position = 0;
        var decoded = await HeapSnapshot.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.True(decoded.HasOwnership);
        Assert.Equal(snapshot.Ownership, decoded.Ownership);
        Assert.Equal(snapshot.RootKindTotals, decoded.RootKindTotals);
    }

    [Fact]
    public void VersionTwoSnapshotsStayReadableAndRefuseOwnershipComparison()
    {
        var v2 = HeapSnapshot.Create([HeapType.Create("A", 10, 1)], [RootKindTotal.Create(RootKinds.Handle, 10, 1)]);
        var v3 = Version3(Group(RootKinds.Handle, "System.Byte[]", 4096, 8, 2));

        Assert.False(v2.HasOwnership);
        Assert.Throws<InvalidDataException>(() => DiffReport.CompareOwnership(v2, v3));
        Assert.Throws<InvalidDataException>(() => DiffReport.CompareOwnership(v3, v2));
        var report = DiffReport.Compare(v2, v3);
        Assert.True(report.RootKindComparable);
        Assert.False(report.OwnershipComparable);
        Assert.Empty(report.OwnershipRegressions);
        Assert.True(DiffReport.CanCompareRootKinds(v2, v3));
    }

    [Fact]
    public void OwnershipDeltasNameTheTypeRootKindAndCarryEvidence()
    {
        var baseline = Version3(
            Group(RootKinds.Handle, "System.Byte[]", 1024, 2, 1),
            Group(RootKinds.Stack, "App.Customer", 512, 1, 1));
        var candidate = Version3(
            Group(RootKinds.Handle, "System.Byte[]", 4096, 8, 2),
            Group(RootKinds.Stack, "App.Customer", 256, 1, 1));

        var report = DiffReport.Compare(baseline, candidate);

        var delta = Assert.Single(report.OwnershipRegressions);
        Assert.Equal(RootKinds.Handle, delta.Kind);
        Assert.Equal("System.Byte[]", delta.TypeName);
        Assert.Equal(3072, delta.ByteDelta);
        Assert.Equal(6, delta.CountDelta);
        Assert.Equal(1, delta.AfterRootCount - delta.BeforeRootCount);
        Assert.False(delta.IsNew);
        Assert.Equal(["Handle", "App.Holder", "System.Byte[]"], delta.Evidence.Path);
        Assert.Equal(3072, report.TotalOwnershipByteDelta);
        Assert.Equal(6, report.TotalOwnershipCountDelta);
        Assert.True(report.ExceedsOwnership(3071));
        Assert.False(report.ExceedsOwnership(3072));
    }

    [Fact]
    public void OnlyGrowthIsReportedSoEveryDeltaCarriesCandidateEvidence()
    {
        var baseline = Version3(Group(RootKinds.Stack, "App.Cache", 512, 1, 1));
        var candidate = Version3(
            Group(RootKinds.Stack, "App.Cache", 512, 1, 1),
            Group(RootKinds.Handle, "System.Byte[]", 2048, 4, 1));

        var report = DiffReport.Compare(baseline, candidate);

        var added = Assert.Single(report.OwnershipRegressions);
        Assert.True(added.IsNew);
        Assert.Equal(2048, added.ByteDelta);
        Assert.Equal("System.Byte[]", added.TypeName);
        Assert.Equal(["Handle", "App.Holder", "System.Byte[]"], added.Evidence.Path);

        // Reversing the comparison shrinks the Handle group instead of growing it. Shrinking is not
        // a regression and carries no candidate evidence, so it must not be reported at all.
        var removalReport = DiffReport.Compare(candidate, baseline);
        Assert.DoesNotContain(removalReport.OwnershipRegressions, d => d.TypeName == "System.Byte[]");
        Assert.Equal(0, removalReport.TotalOwnershipByteDelta);
    }

    [Fact]
    public void EvidenceSurvivesTypeRenameAsRemovalPlusAddition()
    {
        var baseline = Version3(Group(RootKinds.Handle, "App.Before", 1024, 2, 1));
        var candidate = Version3(Group(RootKinds.Handle, "App.After", 1024, 2, 1));

        var report = DiffReport.Compare(baseline, candidate);

        var added = Assert.Single(report.OwnershipRegressions);
        Assert.Equal("App.After", added.TypeName);
        Assert.Equal(1024, added.ByteDelta);
        Assert.True(added.IsNew);
        Assert.Equal(0, added.BeforeCount);
        Assert.Equal(2, added.AfterCount);
        Assert.Equal(2, added.CountDelta);
    }

    [Fact]
    public void OwnershipRequiresConsistentRootKindTotals()
    {
        Assert.Throws<InvalidDataException>(() => HeapSnapshot.Create(
            [HeapType.Create("A", 1, 1)],
            [RootKindTotal.Create(RootKinds.Handle, 100, 2)],
            [OwnershipGroup.Create(RootKinds.Handle, "A", 50, 1, 1, Evidence("Handle", "A"))]));
        Assert.Throws<InvalidDataException>(() => HeapSnapshot.Create(
            [HeapType.Create("A", 1, 1)],
            [],
            [OwnershipGroup.Create(RootKinds.Handle, "A", 50, 1, 1, Evidence("Handle", "A"))]));
        Assert.Throws<InvalidDataException>(() => HeapSnapshot.Create(
            [HeapType.Create("A", 1, 1)],
            [RootKindTotal.Create(RootKinds.Handle, 50, 1), RootKindTotal.Create(RootKinds.Stack, 100, 2)],
            [OwnershipGroup.Create(RootKinds.Handle, "A", 50, 1, 1, Evidence("Handle", "A"))]));
        Assert.Throws<InvalidDataException>(() => HeapSnapshot.Create(
            [HeapType.Create("A", 1, 1)],
            [RootKindTotal.Create(RootKinds.Handle, 50, 1)],
            [
                OwnershipGroup.Create(RootKinds.Handle, "A", 25, 1, 1, Evidence("Handle", "A")),
                OwnershipGroup.Create(RootKinds.Handle, "A", 25, 1, 1, Evidence("Handle", "A")),
            ]));
    }

    [Theory]
    [InlineData("Stack")]
    [InlineData("Handle")]
    public void EvidenceRejectsUnknownHopsAndImpossibleDepth(string root)
    {
        Assert.Throws<InvalidDataException>(() => OwnershipEvidence.Create([], 0, false));
        Assert.Throws<InvalidDataException>(() => OwnershipEvidence.Create([root], 0, false));
        Assert.Throws<InvalidDataException>(() => OwnershipEvidence.Create([root, " "], 0, false));
        Assert.Throws<InvalidDataException>(() => OwnershipEvidence.Create([root, "A"], 0, true));
        Assert.Throws<InvalidDataException>(() => OwnershipEvidence.Create(["Bogus", "A"], 0, false));
        Assert.Throws<InvalidDataException>(() => OwnershipEvidence.Create([root, "A"], 5, false));
        // A truncated chain keeps a prefix shorter than its real depth, which is legal.
        var truncated = OwnershipEvidence.Create([root, "A"], 1, true);
        Assert.True(truncated.Truncated);
        Assert.Equal(1, truncated.Depth);
        Assert.Throws<InvalidDataException>(() => OwnershipEvidence.Create(["Handle", new string('x', 5000)], 0, false));
    }

    [Fact]
    public void OwnershipGroupsRejectImpossibleRootCountsAndEmptyGroups()
    {
        var evidence = Evidence("Handle", "A");
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnershipGroup.Create(-1, "A", 1, 1, 1, evidence));
        Assert.Throws<ArgumentException>(() => OwnershipGroup.Create(1, " ", 1, 1, 1, evidence));
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnershipGroup.Create(1, "A", -1, 1, 1, evidence));
        Assert.Throws<InvalidDataException>(() => OwnershipGroup.Create(1, "A", 1, 0, 1, evidence));
        Assert.Throws<InvalidDataException>(() => OwnershipGroup.Create(1, "A", 1, 1, 0, evidence));
        Assert.Throws<InvalidDataException>(() => OwnershipGroup.Create(1, "A", 1, 1, 2, evidence));
    }

    [Fact]
    public async Task InconsistentVersionThreeDocumentsAreRejected()
    {
        const string noOwnership = """{"version":3,"types":[],"rootKindRetention":[]}""";
        const string ownershipWithoutKinds = """{"version":3,"types":[],"rootKindRetention":[],"ownershipRetention":[{"kind":1,"type":"A","retainedBytes":1,"objectCount":1,"rootCount":1,"evidence":{"path":["Handle","A"],"depth":0,"truncated":false}}]}""";
        const string mismatchedSum = """{"version":3,"types":[],"rootKindRetention":[{"kind":1,"retainedBytes":999,"objectCount":1}],"ownershipRetention":[{"kind":1,"type":"A","retainedBytes":1,"objectCount":1,"rootCount":1,"evidence":{"path":["Handle","A"],"depth":0,"truncated":false}}]}""";
        const string version2WithOwnership = """{"version":2,"types":[],"rootKindRetention":[],"ownershipRetention":[]}""";
        const string badDepth = """{"version":3,"types":[],"rootKindRetention":[{"kind":1,"retainedBytes":1,"objectCount":1}],"ownershipRetention":[{"kind":1,"type":"A","retainedBytes":1,"objectCount":1,"rootCount":1,"evidence":{"path":["Handle","A"],"depth":9,"truncated":false}}]}""";
        foreach (var json in new[] { noOwnership, ownershipWithoutKinds, mismatchedSum, version2WithOwnership, badDepth })
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await Assert.ThrowsAnyAsync<Exception>(() => HeapSnapshot.ReadAsync(stream, TestContext.Current.CancellationToken));
        }
    }
}

public sealed class ReportFormatTests
{
    [Fact]
    public void SignedRendersAnExplicitZeroInsteadOfADash()
    {
        Assert.Equal("+7", ReportFormat.Signed(7));
        Assert.Equal("-7", ReportFormat.Signed(-7));
        Assert.Equal("0", ReportFormat.Signed(0));
    }

    [Fact]
    public void RootDeltaNamesTheNoChangeCase()
    {
        Assert.Equal("unchanged", ReportFormat.RootDelta(0));
        Assert.Equal("+2", ReportFormat.RootDelta(2));
        Assert.Equal("-1", ReportFormat.RootDelta(-1));
    }
}
