using System.Text.Json;
using Tenure;

return await CommandLine.RunAsync(args);

internal static class CommandLine
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("Usage: tenure diff <baseline.json> <candidate.json> [--fail-over <bytes>] [--allow <type>]");
            Console.WriteLine("Compare retained bytes and object counts by type. Exit 1 when the budget is exceeded.");
            return 0;
        }

        if (args[0] != "diff" || args.Length < 3)
        {
            Console.Error.WriteLine("Expected: tenure diff <baseline.json> <candidate.json> [--fail-over <bytes>] [--allow <type>]");
            return 2;
        }

        long budget = long.MaxValue;
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fail-over" when i + 1 < args.Length && long.TryParse(args[++i], out var value) && value >= 0:
                    budget = value;
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
            if (report.Exceeds(budget))
            {
                Console.Error.WriteLine($"Retention budget exceeded: {report.TotalByteDelta} > {budget} bytes.");
                return 1;
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"tenure: {ex.Message}");
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
