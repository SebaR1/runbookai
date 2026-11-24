using System.Threading.Channels;

namespace RunbookAI.SystemApi.Documents;

// Seam for Phase 8: today an in-process Channel; later Azure Queue Storage or
// Service Bus. The worker only cares about receiving document ids.
//
// The in-memory queue loses its content if the process dies. That is covered by
// IndexingWorker re-enqueueing pending/indexing documents on startup.
public interface IIndexingQueue
{
    ValueTask EnqueueAsync(Guid documentId, CancellationToken ct = default);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}

public sealed class ChannelIndexingQueue : IIndexingQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(Guid documentId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(documentId, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
