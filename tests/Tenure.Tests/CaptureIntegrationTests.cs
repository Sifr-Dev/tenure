using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Tenure.Tests;

public sealed class CaptureIntegrationTests
{
    [Fact]
    public async Task RealEventPipeCaptureProducesAUsableSnapshot()
    {
        if (Environment.OSVersion.Platform != PlatformID.Unix) return;
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var workload = Path.Combine(workspace, "samples/AllocationWorkload/AllocationWorkload.csproj");
        if (!File.Exists(workload)) return;
        var output = Path.Combine(Path.GetTempPath(), $"tenure-test-{Guid.NewGuid():N}.json");
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo("dotnet", $"run --project \"{workload}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            Assert.NotNull(child);
            await child.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
            await HeapCapture.CaptureAsync(child.Id, stream, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await stream.FlushAsync(TestContext.Current.CancellationToken);
            Assert.True(stream.Length > 0);
            using var document = JsonDocument.Parse(File.ReadAllText(output));
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Contains(document.RootElement.GetProperty("types").EnumerateArray(), t => t.GetProperty("name").GetString() == "System.Byte[]" && t.GetProperty("objectCount").GetInt64() > 0);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(true);
                await child.WaitForExitAsync(TestContext.Current.CancellationToken);
                child.Dispose();
            }
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
