using Xunit;

namespace Tenure.Tests;

public sealed class RetentionGraphTests
{
    [Fact]
    public void ReachableObjectsAreCountedOnceAcrossSharedReferences()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(1, 7, 64, 1));
        builder.AddNode(new(2, 7, 64, 1));
        builder.AddNode(new(3, 8, 128, 0));
        builder.AddNode(new(4, 8, 256, 0));
        builder.AddReference(3, 1);
        builder.AddReference(3, 2);
        builder.AddRoot(1, 1, 0, 100);
        builder.AddRoot(2, 2, 0, 200);
        builder.CompleteGraph();

        var graph = new RetentionGraph(builder);
        var result = graph.ComputeReachability();

        Assert.Equal(128, result.Single(x => x.Name == "Unknown type 0x7").ShallowBytes);
        Assert.Equal(128, result.Single(x => x.Name == "Unknown type 0x8").ShallowBytes);
        Assert.Equal(1, result.Single(x => x.Name == "Unknown type 0x8").ObjectCount);
        Assert.Equal(2, result.Single(x => x.Name == "Unknown type 0x7").ObjectCount);
        Assert.Equal(new ulong[] { 1, 3 }, graph.FindRootPath(3)?.Path);
    }

    [Fact]
    public void ExclusiveRetentionExcludesSharedChildren()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(1, 7, 64, 1));
        builder.AddNode(new(2, 7, 128, 1));
        builder.AddNode(new(3, 8, 256, 0));
        builder.AddNode(new(4, 8, 512, 0));
        builder.AddReference(3, 1);
        builder.AddReference(3, 2);
        builder.AddRoot(1, 1, 0, 100);
        builder.AddRoot(2, 2, 0, 200);
        builder.CompleteGraph();

        var retention = new RetentionGraph(builder).ComputeExclusiveRetention();
        Assert.Equal(2, retention.Count);
        Assert.All(retention, result => Assert.Equal(1, result.ObjectCount));
        Assert.Contains(retention, result => result.RootAddress == 1 && result.RetainedBytes == 64);
        Assert.Contains(retention, result => result.RootAddress == 2 && result.RetainedBytes == 128);
        Assert.Equal(new ulong[] { 1, 3 }, new RetentionGraph(builder).FindRootPath(3)?.Path);
    }

    [Fact]
    public void UnrootedObjectsAreNotAttributedToRetention()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(1, 7, 64, 0));
        builder.AddNode(new(2, 7, 128, 0));
        builder.AddRoot(1, 1, 0, 100);
        builder.CompleteGraph();

        var result = new RetentionGraph(builder).ComputeReachability();
        Assert.Equal(64, result.Single().ShallowBytes);
        Assert.Equal(1, result.Single().ObjectCount);
    }

    [Fact]
    public void RootKindRetentionCountsSharedObjectsOnceAndPartitionsByKind()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(1, 7, 64, 1));
        builder.AddNode(new(2, 7, 128, 1));
        builder.AddNode(new(3, 8, 256, 0));
        builder.AddNode(new(4, 8, 512, 0));
        builder.AddReference(3, 1);
        builder.AddReference(3, 2);
        builder.AddRoot(1, RootKinds.Stack, 0, 100);
        builder.AddRoot(2, RootKinds.Handle, 0, 200);
        builder.CompleteGraph();

        var graph = new RetentionGraph(builder);
        var byKind = graph.ComputeRootKindRetention();

        Assert.Equal(new[] { RootKinds.Handle, RootKinds.Stack }, byKind.Select(k => k.RootKind));
        Assert.Equal(128, byKind[0].RetainedBytes);
        Assert.Equal(64, byKind[1].RetainedBytes);
        Assert.Equal(192, byKind.Sum(k => k.RetainedBytes));
        Assert.Equal(graph.ComputeExclusiveRetention().Sum(r => r.RetainedBytes), byKind.Sum(k => k.RetainedBytes));
        Assert.Equal(2, byKind.Sum(k => k.ObjectCount));
    }

    [Fact]
    public void RootKindRetentionExcludesObjectsNotExclusivelyDominated()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(1, 7, 64, 1));
        builder.AddNode(new(2, 7, 128, 1));
        builder.AddNode(new(3, 8, 256, 0));
        builder.AddNode(new(4, 8, 512, 0));
        builder.AddReference(3, 1);
        builder.AddReference(3, 2);
        builder.AddRoot(1, RootKinds.Stack, 0, 100);
        builder.AddRoot(2, RootKinds.Handle, 0, 200);
        builder.CompleteGraph();

        var byKind = new RetentionGraph(builder).ComputeRootKindRetention();

        Assert.DoesNotContain(byKind, k => k.RetainedBytes == 256 || k.RetainedBytes == 512);
        Assert.Equal(192, byKind.Sum(k => k.RetainedBytes));
    }

    [Fact]
    public void RootKindRetentionIsDeterministicAcrossRuns()
    {
        static IReadOnlyList<RetentionGraph.RootKindRetention> Build()
        {
            var builder = new HeapGraphBuilder();
            builder.AddNode(new(1, 7, 64, 2));
            builder.AddNode(new(2, 7, 64, 2));
            builder.AddNode(new(3, 8, 32, 0));
            builder.AddNode(new(4, 8, 16, 0));
            builder.AddNode(new(5, 8, 16, 0));
            builder.AddReference(3, 1);
            builder.AddReference(4, 2);
            builder.AddReference(3, 1);
            builder.AddReference(5, 2);
            builder.AddRoot(1, RootKinds.Handle, 0, 11);
            builder.AddRoot(2, RootKinds.Finalizer, 0, 22);
            builder.CompleteGraph();
            return new RetentionGraph(builder).ComputeRootKindRetention();
        }

        var first = Build();
        var second = Build();
        Assert.Equal(first, second);
        Assert.Equal(first.Select(k => k.RootKind), first.Select(k => k.RootKind).OrderBy(k => k));
    }
}
