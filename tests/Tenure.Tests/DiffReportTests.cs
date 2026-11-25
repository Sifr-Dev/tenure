using System.Text;
using Xunit;

namespace Tenure.Tests;

public sealed class DiffReportTests
{
    private static HeapSnapshot Snapshot(params HeapType[] types) => HeapSnapshot.Create(types);

    [Fact]
    public void ReportsPositiveDeltasInStableOrder()
    {
        var baseline = Snapshot(HeapType.Create("App.Customer", 100, 2), HeapType.Create("System.Byte[]", 64, 1));
        var candidate = Snapshot(HeapType.Create("App.Order", 25, 1), HeapType.Create("App.Customer", 160, 3), HeapType.Create("System.Byte[]", 64, 1));
        var report = DiffReport.Compare(baseline, candidate);

        Assert.Equal(85, report.TotalByteDelta);
        Assert.Equal(2, report.TotalCountDelta);
        Assert.Equal(new[] { "App.Customer", "App.Order" }, report.Regressions.Select(d => d.Name));
        Assert.Equal(60, report.Regressions[0].ByteDelta);
        Assert.Equal(25, report.Regressions[1].ByteDelta);
    }

    [Fact]
    public void RemovalsAndAllowlistedTypesDoNotCauseFalseRegressions()
    {
        var report = DiffReport.Compare(
            Snapshot(HeapType.Create("App.Customer", 100, 2), HeapType.Create("System.Byte[]", 64, 1)),
            Snapshot(HeapType.Create("App.Customer", 80, 1)),
            ["System.Byte[]"]);
        Assert.Empty(report.Regressions);
        Assert.Equal(0, report.TotalByteDelta);
        Assert.False(report.Exceeds(0));
    }

    [Fact]
    public void BudgetIsStrictlyGreaterThanTheConfiguredLimit()
    {
        var report = new DiffReport([], 100, 1);
        Assert.False(report.Exceeds(100));
        Assert.True(report.Exceeds(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Exceeds(-1));
    }

    [Fact]
    public async Task SnapshotRoundTripIsCanonicalAndVersioned()
    {
        var snapshot = Snapshot(HeapType.Create("Z.Type", 20, 1), HeapType.Create("A.Type", 10, 2));
        await using var stream = new MemoryStream();
        await snapshot.WriteAsync(stream, TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\": 1", text);
        Assert.True(text.IndexOf("A.Type", StringComparison.Ordinal) < text.IndexOf("Z.Type", StringComparison.Ordinal));

        stream.Position = 0;
        var decoded = await HeapSnapshot.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(snapshot.Types, decoded.Types);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":99,\"types\":[]}")]
    public async Task MalformedSnapshotsAreRejected(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAnyAsync<Exception>(() => HeapSnapshot.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void InvalidTypesAndDuplicatesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => HeapType.Create(" ", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeapType.Create("A", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeapType.Create("A", 1, -1));
        Assert.Throws<InvalidDataException>(() => Snapshot(HeapType.Create("A", 1, 1), HeapType.Create("A", 2, 1)));
    }
}
