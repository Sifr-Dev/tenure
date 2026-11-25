using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Tenure;

public static class HeapCapture
{
    public static async Task CaptureAsync(int processId, Stream output, TimeSpan duration, CancellationToken cancellationToken = default, bool rootAnalysis = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(duration), "Capture duration must be between 1 ms and 5 minutes.");

        var client = new DiagnosticsClient(processId);
        var providers = new[]
        {
            new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Informational, (long)ClrTraceEventParser.Keywords.GCHeapSnapshot),
        };
        using var session = client.StartEventPipeSession(providers, requestRundown: false);
        using var eventSource = TraceLog.CreateFromEventPipeSession(session, TraceLog.EventPipeRundownConfiguration.None());
        var builder = new HeapGraphBuilder();
        builder.Attach(new ClrTraceEventParser(eventSource));
        var processing = Task.Run(() => eventSource.Process(), cancellationToken);
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            session.Stop();
        }
        await processing.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        builder.CompleteGraph();
        if (rootAnalysis)
        {
            var graph = new RetentionGraph(builder);
            Console.Error.WriteLine("Root-reachable shallow bytes (not exclusive dominator retention):");
            foreach (var type in graph.ComputeReachability().Take(20))
                Console.Error.WriteLine($"{type.Name} | {type.ShallowBytes} | {type.ObjectCount}");
            var exemplars = builder.Nodes.Keys.OrderBy(x => x).Select(graph.FindRootPath).Where(p => p is not null).Take(5);
            foreach (var path in exemplars)
                Console.Error.WriteLine($"root path to 0x{path!.Address:x}: kind={path.RootKind} id=0x{path.RootId:x} via {string.Join(" -> ", path.Path.Select(x => $"0x{x:x}"))}");
        }
        var snapshot = builder.ToSnapshot();
        await snapshot.WriteAsync(output, cancellationToken);
    }
}
