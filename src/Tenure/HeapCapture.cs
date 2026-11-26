using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Tenure;

public static class HeapCapture
{
    /// <summary>
    /// Captures a heap snapshot from a live process. <paramref name="ownershipPaths"/> implies
    /// <paramref name="rootAnalysis"/>: per-type ownership evidence is a refinement of the root-kind
    /// retention that root analysis computes, so asking for the refinement always records both.
    /// </summary>
    public static async Task CaptureAsync(
        int processId,
        Stream output,
        TimeSpan duration,
        CancellationToken cancellationToken = default,
        bool rootAnalysis = false,
        bool ownershipPaths = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(duration), "Capture duration must be between 1 ms and 5 minutes.");
        if (ownershipPaths) rootAnalysis = true;

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
        IReadOnlyList<RootKindTotal> rootKinds = [];
        IReadOnlyList<OwnershipGroup> ownership = [];
        if (rootAnalysis)
        {
            var graph = new RetentionGraph(builder);
            rootKinds = graph.ComputeRootKindRetention().Select(k => RootKindTotal.Create(k.RootKind, k.RetainedBytes, k.ObjectCount)).ToArray();
            Console.Error.WriteLine("Root-reachable shallow bytes (not exclusive dominator retention):");
            foreach (var type in graph.ComputeReachability().Take(20))
                Console.Error.WriteLine($"{type.Name} | {type.ShallowBytes} | {type.ObjectCount}");
            Console.Error.WriteLine("Exclusive dominator retention by root kind:");
            foreach (var kind in rootKinds.OrderByDescending(x => x.RetainedBytes))
                Console.Error.WriteLine($"{RootKinds.NameOf(kind.Kind)} | {kind.RetainedBytes} bytes | {kind.ObjectCount} objects");
            Console.Error.WriteLine("Exclusive dominator retention by root:");
            foreach (var root in graph.ComputeExclusiveRetention().OrderByDescending(x => x.RetainedBytes).Take(20))
                Console.Error.WriteLine($"kind={root.RootKind} id=0x{root.RootId:x} address=0x{root.RootAddress:x} | {root.RetainedBytes} bytes | {root.ObjectCount} objects");
            if (ownershipPaths)
            {
                var observed = graph.ComputeTypeOwnership();
                ownership = observed.Select(o => OwnershipGroup.Create(o.RootKind, o.TypeName, o.RetainedBytes, o.ObjectCount, o.RootCount, o.Evidence)).ToArray();
                Console.Error.WriteLine("Exclusive dominator retention by root kind and retained type (ownership evidence):");
                foreach (var group in observed.Take(20))
                    Console.Error.WriteLine($"{RootKinds.NameOf(group.RootKind)} | {group.TypeName} | {group.RetainedBytes} bytes | {group.ObjectCount} objects | {group.RootCount} roots | via {string.Join(" -> ", group.Evidence.Path)}");
            }
            var exemplars = builder.Nodes.Keys.OrderBy(x => x).Select(graph.FindRootPath).Where(p => p is not null).Take(5);
            foreach (var path in exemplars)
                Console.Error.WriteLine($"root path to 0x{path!.Address:x}: kind={path.RootKind} id=0x{path.RootId:x} via {string.Join(" -> ", path.Path.Select(x => $"0x{x:x}"))}");
        }
        var snapshot = builder.ToSnapshot(rootKinds, ownership);
        await snapshot.WriteAsync(output, cancellationToken);
    }
}
