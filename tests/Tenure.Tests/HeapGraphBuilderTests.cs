using Xunit;

namespace Tenure.Tests;

public sealed class HeapGraphBuilderTests
{
    [Fact]
    public void CompleteGraphAssociatesReferencesWithTheirSourceNodes()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(0x1000, 7, 64, 2));
        builder.AddNode(new(0x2000, 7, 128, 1));
        builder.AddReference(0x2000, 1);
        builder.AddReference(0x3000, 2);
        builder.AddReference(0x1000, 3);

        builder.CompleteGraph();

        Assert.Collection(builder.Edges,
            edge =>
            {
                Assert.Equal(0x1000UL, edge.Source);
                Assert.Equal(0x2000UL, edge.Target);
                Assert.Equal(1, edge.Field);
            },
            edge =>
            {
                Assert.Equal(0x1000UL, edge.Source);
                Assert.Equal(0x3000UL, edge.Target);
                Assert.Equal(2, edge.Field);
            },
            edge =>
            {
                Assert.Equal(0x2000UL, edge.Source);
                Assert.Equal(0x1000UL, edge.Target);
                Assert.Equal(3, edge.Field);
            });
    }

    [Fact]
    public void TruncatedReferencesFailInsteadOfInventingOwnership()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(0x1000, 7, 64, 1));
        Assert.Throws<InvalidDataException>(builder.CompleteGraph);
    }

    [Fact]
    public void SnapshotAggregatesShallowSizesByTypeName()
    {
        var builder = new HeapGraphBuilder();
        builder.AddNode(new(0x1000, 7, 64, 0));
        builder.AddNode(new(0x2000, 7, 128, 0));
        builder.AddNode(new(0x3000, 8, 32, 0));
        var snapshot = builder.ToSnapshot();
        var type = Assert.Single(snapshot.Types, t => t.Name == "Unknown type 0x7");
        Assert.Equal(192, type.RetainedBytes);
        Assert.Equal(2, type.ObjectCount);
    }
}
