using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Tenure;

public static class HeapCapture
{
    public static async Task CaptureAsync(int processId, Stream output, TimeSpan duration, CancellationToken cancellationToken = default)
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
        var snapshot = builder.ToSnapshot();
        await snapshot.WriteAsync(output, cancellationToken);
    }
}
