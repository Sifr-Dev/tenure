using System.Text.Json;
using Tenure;

return await CommandLine.RunAsync(args);

internal static class CommandLine
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("Usage: tenure diff <baseline.json> <candidate.json> [--fail-over <bytes>] [--allow <type>] [--by-root-kind [--fail-over-root-kind <bytes>]]");
            Console.WriteLine("       tenure capture --pid <process-id> --output <snapshot.json> [--duration <milliseconds>] [--root-analysis]");
            Console.WriteLine("Compare retained bytes and object counts by type. Exit 1 when the budget is exceeded.");
            return 0;
        }

        if (args[0] == "capture")
            return await RunCapture(args);

        if (args[0] != "diff" || args.Length < 3)
        {
            Console.Error.WriteLine("Expected: tenure diff <baseline.json> <candidate.json> [--fail-over <bytes>] [--allow <type>] [--by-root-kind [--fail-over-root-kind <bytes>]]");
            return 2;
        }

        long budget = long.MaxValue;
        long rootKindBudget = long.MaxValue;
        var byRootKind = false;
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fail-over" when i + 1 < args.Length && long.TryParse(args[++i], out var value) && value >= 0:
                    budget = value;
                    break;
                case "--by-root-kind":
                    byRootKind = true;
                    break;
                case "--fail-over-root-kind" when i + 1 < args.Length && long.TryParse(args[++i], out var value) && value >= 0:
                    byRootKind = true;
                    rootKindBudget = value;
                    break;
                case "--allow" when i + 1 < args.Length:
                    ignored.Add(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"Invalid argument: {args[i]}");
                    return 2;
            }
        }

        try
        {
            var baseline = await ReadSnapshot(args[1]);
            var candidate = await ReadSnapshot(args[2]);
            var report = DiffReport.Compare(baseline, candidate, ignored);
            Console.WriteLine("Type | Before bytes | After bytes | Delta bytes | Delta objects");
            foreach (var delta in report.Regressions)
                Console.WriteLine($"{delta.Name} | {delta.BeforeBytes} | {delta.AfterBytes} | +{delta.ByteDelta} | {delta.CountDelta:+#;0;-#;0}");
            Console.WriteLine($"Retained-byte increase: {report.TotalByteDelta}; object-count increase: {report.TotalCountDelta}");
            var rootKindExceeded = false;
            if (byRootKind)
            {
                if (!report.RootKindComparable)
                    throw new InvalidDataException("--by-root-kind requires snapshots captured with --root-analysis (snapshot version 2).");
                Console.WriteLine("Root kind | Before bytes | After bytes | Delta bytes | Delta objects");
                foreach (var delta in report.RootKindRegressions)
                    Console.WriteLine($"{RootKinds.NameOf(delta.Kind)} | {delta.BeforeBytes} | {delta.AfterBytes} | +{delta.ByteDelta} | {delta.CountDelta:+#;0;-#;0}");
                Console.WriteLine($"Exclusive root-kind retention increase: {report.TotalRootKindByteDelta}; object-count increase: {report.TotalRootKindCountDelta}");
                rootKindExceeded = report.ExceedsRootKind(rootKindBudget);
                if (rootKindExceeded)
                    Console.Error.WriteLine($"Root-kind retention budget exceeded: {report.TotalRootKindByteDelta} > {rootKindBudget} bytes.");
            }
            if (report.Exceeds(budget))
            {
                Console.Error.WriteLine($"Retention budget exceeded: {report.TotalByteDelta} > {budget} bytes.");
                return 1;
            }
            return rootKindExceeded ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"tenure: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunCapture(string[] args)
    {
        var output = "";
        var duration = TimeSpan.FromSeconds(1);
        int? pid = null;
        var rootAnalysis = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root-analysis":
                    rootAnalysis = true;
                    break;
                case "--pid" when i + 1 < args.Length && int.TryParse(args[++i], out var value):
                    pid = value;
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--duration" when i + 1 < args.Length && int.TryParse(args[++i], out var value) && value > 0:
                    duration = TimeSpan.FromMilliseconds(value);
                    break;
                default:
                    Console.Error.WriteLine($"Invalid argument: {args[i]}");
                    return 2;
            }
        }
        if (pid is null || string.IsNullOrEmpty(output))
        {
            Console.Error.WriteLine("Expected: tenure capture --pid <process-id> --output <snapshot.json> [--duration <milliseconds>] [--root-analysis]");
            return 2;
        }
        try
        {
            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await HeapCapture.CaptureAsync(pid.Value, stream, duration, rootAnalysis: rootAnalysis);
            Console.WriteLine($"Wrote heap snapshot to {output}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OperationCanceledException or TimeoutException or AggregateException or Microsoft.Diagnostics.NETCore.Client.ServerNotAvailableException)
        {
            Console.Error.WriteLine($"tenure: {ex.Message}");
            if (File.Exists(output)) File.Delete(output);
            return 2;
        }
    }

    private static async Task<HeapSnapshot> ReadSnapshot(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > 256 * 1024 * 1024)
            throw new InvalidDataException("The snapshot exceeds the 256 MiB input limit.");
        return await HeapSnapshot.ReadAsync(stream);
    }
}
