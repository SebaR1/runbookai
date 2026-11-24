using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RunbookAI.SystemApi.Configuration;
using RunbookAI.SystemApi.Rag;

namespace RunbookAI.SystemApi.Documents;

// One indexing run: pending -> indexing -> indexed | error. Every transition is a
// conditional update (filter on the expected status and version), so a document
// that was deleted or replaced in the meantime is not overwritten.
public sealed class DocumentIndexer(
    IMongoCollection<Document> documents,
    BlobContainerClient blobs,
    IRagClient rag,
    IOptions<RagOptions> ragOptions,
    TimeProvider time,
    ILogger<DocumentIndexer> log)
{
    private static readonly UpdateDefinitionBuilder<Document> Update = Builders<Document>.Update;

    public async Task IndexAsync(Guid id, CancellationToken ct)
    {
        var document = await documents.FindOneAndUpdateAsync(
            d => d.Id == id && d.Status == DocumentStatus.Pending,
            Update.Set(d => d.Status, DocumentStatus.Indexing).Set(d => d.UpdatedAt, Now()),
            new FindOneAndUpdateOptions<Document> { ReturnDocument = ReturnDocument.After },
            ct);
        if (document is null)
        {
            log.LogInformation("Document {DocumentId} is no longer pending; skipped", id);
            return;
        }

        try
        {
            var blob = await blobs.GetBlobClient(document.BlobName).DownloadContentAsync(ct);
            using var stream = blob.Value.Content.ToStream();
            var result = await rag.IngestAsync(
                new FileParameter(stream, document.OriginalFileName, DocumentRules.ContentTypeFor(document.OriginalFileName)),
                document.Id.ToString(), document.Namespace, document.Title, ct);

            await FinishAsync(document, Update
                .Set(d => d.Status, DocumentStatus.Indexed)
                .Set(d => d.ChunkCount, result.ChunkCount)
                .Set(d => d.ErrorDetail, null)
                .Set(d => d.IndexedAt, Now()));
            log.LogInformation("Document {DocumentId} v{Version} indexed: {ChunkCount} chunks",
                document.Id, document.Version, result.ChunkCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: it stays "indexing" and the worker re-enqueues it on the next start.
            throw;
        }
        catch (Exception ex)
        {
            var detail = Describe(ex);
            log.LogWarning(ex, "Indexing of document {DocumentId} failed: {ErrorDetail}", document.Id, detail);
            await FinishAsync(document, Update.Set(d => d.Status, DocumentStatus.Error).Set(d => d.ErrorDetail, detail));
        }
    }

    private async Task FinishAsync(Document document, UpdateDefinition<Document> update)
    {
        // Not cancellable: once the RAG service answered, the result must be recorded.
        var result = await documents.UpdateOneAsync(
            d => d.Id == document.Id && d.Status == DocumentStatus.Indexing && d.Version == document.Version,
            update.Set(d => d.UpdatedAt, Now()));
        if (result.ModifiedCount == 0)
            log.LogWarning("Document {DocumentId} changed while indexing; result discarded", document.Id);
    }

    // errorDetail is shown to the user: Spanish, short, without stack traces.
    private string Describe(Exception ex) => ex switch
    {
        ApiException api => $"El RAG service rechazó el documento (HTTP {api.StatusCode}): {RagDetail(api.Response)}",
        TaskCanceledException => $"El RAG service no respondió en {ragOptions.Value.Timeout.TotalSeconds:0} s.",
        HttpRequestException http => $"No se pudo conectar con el RAG service: {http.Message}",
        Azure.RequestFailedException blob => $"No se pudo leer el archivo original del storage: {blob.ErrorCode}",
        _ => $"Error inesperado al indexar: {ex.Message}",
    };

    // FastAPI errors look like {"detail": "..."}; validation errors carry a list.
    private static string RagDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "sin detalle";
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("detail", out var detail))
                return detail.ValueKind == JsonValueKind.String ? detail.GetString()! : detail.GetRawText();
        }
        catch (JsonException)
        {
        }
        return body.Length <= 500 ? body : body[..500];
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}
