using MongoDB.Driver;

namespace RunbookAI.SystemApi.Documents;

// Single consumer: Ollama processes embeddings one at a time on the GPU, so more
// consumers would not index faster.
public sealed class IndexingWorker(
    IIndexingQueue queue,
    IServiceScopeFactory scopes,
    IMongoCollection<Document> documents,
    ILogger<IndexingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                // The worker is a singleton; the indexer and its typed HttpClient are
                // not. One scope per document.
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DocumentIndexer>().IndexAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // An unhandled exception in a BackgroundService stops the whole host.
                log.LogError(ex, "Indexing loop failed for document {DocumentId}", id);
            }
        }
    }

    // The queue lives in memory: whatever was pending when the process stopped is
    // lost from it, but not from Mongo. "indexing" at startup means the previous
    // process died mid-way. Valid with a single instance; with several replicas this
    // needs a real queue (Phase 8).
    private async Task RequeueUnfinishedAsync(CancellationToken ct)
    {
        await documents.UpdateManyAsync(
            d => d.Status == DocumentStatus.Indexing,
            Builders<Document>.Update.Set(d => d.Status, DocumentStatus.Pending),
            cancellationToken: ct);
        var pending = await documents.Find(d => d.Status == DocumentStatus.Pending)
            .SortBy(d => d.CreatedAt)
            .Project(d => d.Id)
            .ToListAsync(ct);
        foreach (var id in pending) await queue.EnqueueAsync(id, ct);
        if (pending.Count > 0) log.LogInformation("Re-enqueued {Count} unfinished documents", pending.Count);
    }
}
