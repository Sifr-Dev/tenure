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

    private static async Task StopAsync(Process child, CancellationToken cancellationToken)
    {
        if (!child.HasExited) child.Kill(true);
        await child.WaitForExitAsync(cancellationToken);
        child.Dispose();
    }
}
