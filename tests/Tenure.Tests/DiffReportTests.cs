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
        var report = new DiffReport([], 100, 1, [], 0, 0);
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

    [Fact]
    public async Task VersionTwoSnapshotsCarryRootKindRetentionAndRoundTrip()
    {
        var snapshot = HeapSnapshot.Create(
            [HeapType.Create("App.Customer", 160, 3)],
            [RootKindTotal.Create(RootKinds.Handle, 128, 2), RootKindTotal.Create(RootKinds.Stack, 32, 1)]);
        await using var stream = new MemoryStream();
        await snapshot.WriteAsync(stream, TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\": 2", text);
        Assert.Contains("\"rootKindRetention\"", text);
        Assert.Contains("\"kind\": 2", text);
        Assert.Contains("\"kind\": 0", text);

        stream.Position = 0;
        var decoded = await HeapSnapshot.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(snapshot.Types, decoded.Types);
        Assert.Equal(snapshot.RootKindTotals, decoded.RootKindTotals);
        Assert.True(decoded.HasRootKindRetention);
    }

    [Fact]
    public async Task VersionOneSnapshotsRemainReadableAfterTheVersionTwoUpgrade()
    {
        const string legacy = """
            {
              "version": 1,
              "types": [
                { "name": "App.Customer", "retainedBytes": 160, "objectCount": 3 }
              ]
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(legacy));
        var snapshot = await HeapSnapshot.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(160, snapshot.Types.Single().RetainedBytes);
        Assert.False(snapshot.HasRootKindRetention);
    }

    [Fact]
    public void RootKindComparisonRequiresTwoRootAnalyzedSnapshots()
    {
        var shallow = Snapshot(HeapType.Create("A", 10, 1));
        var analyzed = HeapSnapshot.Create([HeapType.Create("A", 10, 1)], [RootKindTotal.Create(RootKinds.Handle, 10, 1)]);
        Assert.Throws<InvalidDataException>(() => DiffReport.CompareRootKinds(shallow, analyzed));
        Assert.Throws<InvalidDataException>(() => DiffReport.CompareRootKinds(analyzed, shallow));
        Assert.False(analyzed.HasRootKindRetention == false);
        Assert.False(shallow.HasRootKindRetention);
    }

    [Fact]
    public void RootKindDeltasReportOnlyPositiveIncreasesInStableOrder()
    {
        var baseline = HeapSnapshot.Create(
            [],
            [RootKindTotal.Create(RootKinds.Stack, 100, 2), RootKindTotal.Create(RootKinds.Handle, 500, 4), RootKindTotal.Create(RootKinds.Finalizer, 64, 1)]);
        var candidate = HeapSnapshot.Create(
            [],
            [RootKindTotal.Create(RootKinds.Stack, 140, 3), RootKindTotal.Create(RootKinds.Handle, 200, 1), RootKindTotal.Create(RootKinds.Finalizer, 64, 1)]);
        var report = DiffReport.Compare(baseline, candidate);

        var delta = Assert.Single(report.RootKindRegressions);
        Assert.Equal(RootKinds.Stack, delta.Kind);
        Assert.Equal(40, delta.ByteDelta);
        Assert.Equal(1, delta.CountDelta);
        Assert.Equal(40, report.TotalRootKindByteDelta);
        Assert.Equal(1, report.TotalRootKindCountDelta);
    }

    [Fact]
    public void RootKindBudgetIsStrictlyGreaterThanTheConfiguredLimit()
    {
        var baseline = HeapSnapshot.Create([], [RootKindTotal.Create(RootKinds.Handle, 100, 1)]);
        var candidate = HeapSnapshot.Create([], [RootKindTotal.Create(RootKinds.Handle, 150, 1)]);
        var report = DiffReport.Compare(baseline, candidate);
        Assert.False(report.ExceedsRootKind(50));
        Assert.True(report.ExceedsRootKind(49));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.ExceedsRootKind(-1));
    }

    [Theory]
    [InlineData("{\"version\":2,\"types\":[]}")]
    [InlineData("{\"version\":2,\"types\":[],\"rootKindRetention\":[{\"kind\":1,\"retainedBytes\":1,\"objectCount\":1},{\"kind\":1,\"retainedBytes\":2,\"objectCount\":1}]}")]
    [InlineData("{\"version\":1,\"types\":[],\"rootKindRetention\":[{\"kind\":1,\"retainedBytes\":1,\"objectCount\":1}]}")]
    [InlineData("{\"version\":2,\"types\":[],\"rootKindRetention\":[{\"kind\":-1,\"retainedBytes\":1,\"objectCount\":1}]}")]
    public async Task InconsistentOrInvalidRootKindDeclarationsAreRejected(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAnyAsync<Exception>(() => HeapSnapshot.ReadAsync(stream, TestContext.Current.CancellationToken));
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
