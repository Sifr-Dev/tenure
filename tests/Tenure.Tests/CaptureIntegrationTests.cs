using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Tenure.Tests;

public sealed class CaptureIntegrationTests
{
    /// <summary>
    /// Some tests exercise live EventPipe capture. They are skipped when the sample cannot be built
    /// so an ordinary offline unit-test run stays green, and they are the only place that asserts on
    /// real captured graphs.
    /// </summary>
    private static async Task<Process?> StartWorkloadAsync(string workspace, CancellationToken cancellationToken)
    {
        if (Environment.OSVersion.Platform != PlatformID.Unix) return null;
        var project = Path.Combine(workspace, "samples/AllocationWorkload/AllocationWorkload.csproj");
        if (!File.Exists(project)) return null;
        var executable = Path.Combine(workspace, "samples/AllocationWorkload/bin/Debug/net10.0/AllocationWorkload");
        if (!File.Exists(executable))
        {
            using var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{project}\" --nologo -v q")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (build is null) return null;
            await build.WaitForExitAsync(cancellationToken);
            if (build.ExitCode != 0 || !File.Exists(executable)) return null;
        }
        return Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
    }

    private static string Workspace =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private static async Task<HeapSnapshot> CaptureSnapshotAsync(Process workload, string path, bool rootAnalysis, CancellationToken cancellationToken)
    {
        using (var write = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await HeapCapture.CaptureAsync(workload.Id, write, TimeSpan.FromSeconds(2), cancellationToken, rootAnalysis: rootAnalysis);
            await write.FlushAsync(cancellationToken);
        }
        await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await HeapSnapshot.ReadAsync(read, cancellationToken);
    }

    [Fact]
    public async Task RealEventPipeCaptureProducesAUsableSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = Path.Combine(Path.GetTempPath(), $"tenure-test-{Guid.NewGuid():N}.json");
        var child = await StartWorkloadAsync(Workspace, cancellationToken);
        if (child is null) return;
        try
        {
            await child.StandardOutput.ReadLineAsync(cancellationToken);
            await using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await HeapCapture.CaptureAsync(child.Id, stream, TimeSpan.FromSeconds(2), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                Assert.True(stream.Length > 0);
            }
            using var document = JsonDocument.Parse(File.ReadAllText(output));
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("rootKindRetention", out _));
            var types = document.RootElement.GetProperty("types").EnumerateArray().ToArray();
            Assert.NotEmpty(types);
            Assert.Contains(types, t => t.GetProperty("name").GetString() == "System.Byte[]" && t.GetProperty("objectCount").GetInt64() > 0);
            var names = types.Select(t => t.GetProperty("name").GetString()!).ToArray();
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
        }
        finally
        {
            await StopAsync(child, cancellationToken);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public async Task RealCaptureWithRootAnalysisIsStableAndWritesVersionTwoRetention()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = Path.Combine(Path.GetTempPath(), $"tenure-root-a-{Guid.NewGuid():N}.json");
        var second = Path.Combine(Path.GetTempPath(), $"tenure-root-b-{Guid.NewGuid():N}.json");
        var child = await StartWorkloadAsync(Workspace, cancellationToken);
        if (child is null) return;
        try
        {
            await child.StandardOutput.ReadLineAsync(cancellationToken);

            var baseline = await CaptureSnapshotAsync(child, first, rootAnalysis: true, cancellationToken);
            using (var document = JsonDocument.Parse(File.ReadAllText(first)))
            {
                Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
                Assert.True(document.RootElement.GetProperty("rootKindRetention").GetArrayLength() > 0);
            }
            Assert.True(baseline.HasRootKindRetention);
            Assert.Contains(baseline.RootKindTotals, k => k.Kind == RootKinds.Stack && k.ObjectCount > 0);
            Assert.All(baseline.RootKindTotals, k => Assert.True(k.RetainedBytes >= 0));
            Assert.Equal(baseline.RootKindTotals.Select(k => k.Kind), baseline.RootKindTotals.Select(k => k.Kind).OrderBy(k => k));

            // The fixture allocates a second batch of arrays after its first delay, so a later
            // capture of the same live process must report strictly more retention. This is the
            // behaviour the gate exists for; it is deliberately not asserted as equal, because
            // capturing a running process twice is not a no-op.
            var candidate = await CaptureSnapshotAsync(child, second, rootAnalysis: true, cancellationToken);
            Assert.True(candidate.RootKindTotals.Sum(k => k.RetainedBytes) > baseline.RootKindTotals.Sum(k => k.RetainedBytes));
            var report = DiffReport.Compare(baseline, candidate);
            Assert.True(report.RootKindComparable);
            Assert.True(report.TotalRootKindByteDelta > 0);
            Assert.True(report.ExceedsRootKind(report.TotalRootKindByteDelta - 1));
            Assert.False(report.ExceedsRootKind(report.TotalRootKindByteDelta));
        }
        finally
        {
            await StopAsync(child, cancellationToken);
            foreach (var path in new[] { first, second })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RealCaptureWithOwnershipPathsWritesVersionThreeEvidenceAndDiffsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = Path.Combine(Path.GetTempPath(), $"tenure-own-a-{Guid.NewGuid():N}.json");
        var second = Path.Combine(Path.GetTempPath(), $"tenure-own-b-{Guid.NewGuid():N}.json");
        var child = await StartWorkloadAsync(Workspace, cancellationToken);
        if (child is null) return;
        try
        {
            await child.StandardOutput.ReadLineAsync(cancellationToken);

            var baseline = await CaptureSnapshotWithOwnershipAsync(child, first, cancellationToken);
            using (var document = JsonDocument.Parse(File.ReadAllText(first)))
            {
                var root = document.RootElement;
                Assert.Equal(3, root.GetProperty("version").GetInt32());
                // v1 and v2 fields stay present with their old meaning so earlier readers keep working.
                Assert.True(root.GetProperty("types").GetArrayLength() > 0);
                Assert.True(root.GetProperty("rootKindRetention").GetArrayLength() > 0);
                var ownership = root.GetProperty("ownershipRetention");
                Assert.True(ownership.GetArrayLength() > 0);
                foreach (var group in ownership.EnumerateArray())
                {
                    var path = group.GetProperty("evidence").GetProperty("path").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    Assert.True(RootKinds.IsKnownName(path[0]));
                    Assert.True(path.Length >= 2);
                    Assert.Equal(path.Length - 2, group.GetProperty("evidence").GetProperty("depth").GetInt64());
                    Assert.False(group.GetProperty("evidence").GetProperty("truncated").GetBoolean());
                    Assert.True(group.GetProperty("rootCount").GetInt32() >= 1);
                    Assert.True(group.GetProperty("rootCount").GetInt32() <= group.GetProperty("objectCount").GetInt64());
                }
            }

            // Ownership must be a strict refinement of root-kind retention, not a second measurement.
            Assert.Equal(baseline.RootKindTotals.Sum(k => k.RetainedBytes), baseline.Ownership.Sum(o => o.RetainedBytes));
            Assert.Equal(baseline.RootKindTotals.Sum(k => k.ObjectCount), baseline.Ownership.Sum(o => o.ObjectCount));
            Assert.All(baseline.Ownership, o => Assert.Contains(baseline.RootKindTotals, k => k.Kind == o.RootKind));
            Assert.Contains(baseline.Ownership, o => o.TypeName == "System.Byte[]" || o.TypeName.Contains("Byte", StringComparison.Ordinal));

            var candidate = await CaptureSnapshotWithOwnershipAsync(child, second, cancellationToken);
            Assert.True(candidate.HasOwnership);
            var report = DiffReport.Compare(baseline, candidate);
            Assert.True(report.OwnershipComparable);
            Assert.True(report.TotalOwnershipByteDelta >= 0);
            // Every reported ownership delta must name a group that still exists in the candidate,
            // so the recorded chain is evidence from that capture rather than a baseline leftover.
            foreach (var delta in report.OwnershipRegressions)
            {
                var group = Assert.Single(candidate.Ownership, o => o.RootKind == delta.Kind && o.TypeName == delta.TypeName);
                Assert.Equal(group.Evidence, delta.Evidence);
                Assert.True(delta.ByteDelta > 0);
                Assert.True(delta.AfterCount > 0);
            }
            Assert.Equal(report.OwnershipRegressions.Sum(d => d.ByteDelta), report.TotalOwnershipByteDelta);

            // A version-2 snapshot of the same process keeps its kind totals but cannot be
            // ownership-compared, and the report says so instead of reporting a false zero.
            var kindsOnly = HeapSnapshot.Create(baseline.Types, baseline.RootKindTotals);
            Assert.False(DiffReport.Compare(kindsOnly, candidate).OwnershipComparable);
        }
        finally
        {
            await StopAsync(child, cancellationToken);
            foreach (var path in new[] { first, second })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<HeapSnapshot> CaptureSnapshotWithOwnershipAsync(Process workload, string path, CancellationToken cancellationToken)
    {
        using (var write = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await HeapCapture.CaptureAsync(workload.Id, write, TimeSpan.FromSeconds(2), cancellationToken, ownershipPaths: true);
            await write.FlushAsync(cancellationToken);
        }
        await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await HeapSnapshot.ReadAsync(read, cancellationToken);
    }

    private static async Task StopAsync(Process child, CancellationToken cancellationToken)
    {
        if (!child.HasExited) child.Kill(true);
        await child.WaitForExitAsync(cancellationToken);
        child.Dispose();
    }
}
