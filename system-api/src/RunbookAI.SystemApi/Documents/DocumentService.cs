using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RunbookAI.SystemApi.Configuration;
using RunbookAI.SystemApi.Rag;

namespace RunbookAI.SystemApi.Documents;

public enum DocumentError
{
    NotFound,
    Duplicate,
    InvalidState,
    InvalidNamespace,
    EmptyFile,
    TooLarge,
    UnsupportedType,
    RagFailed,
}

// Result of a use case: the document, or why it could not be done. The endpoints
// translate it into HTTP; the service knows nothing about status codes.
public sealed record DocumentOutcome(Document? Document, DocumentError? Error = null, string? Detail = null, Guid? ExistingDocumentId = null)
{
    public static DocumentOutcome Ok(Document document) => new(document);

    public static DocumentOutcome Fail(DocumentError error, string detail, Guid? existingDocumentId = null) =>
        new(null, error, detail, existingDocumentId);
}

// Use cases on the document registry. Talks to Mongo, Blob and the RAG service
// directly: no repositories or interfaces in between, because the tests run against
// the real services and there is no second provider yet.
public sealed class DocumentService(
    IMongoCollection<Document> documents,
    BlobContainerClient blobs,
    IIndexingQueue queue,
    IRagClient rag,
    IOptions<DocumentsOptions> options,
    TimeProvider time,
    ILogger<DocumentService> log)
{
    public const string DefaultNamespace = "default";

    private static readonly FilterDefinitionBuilder<Document> Filter = Builders<Document>.Filter;
    private static readonly UpdateDefinitionBuilder<Document> Update = Builders<Document>.Update;
    private static readonly FindOneAndUpdateOptions<Document> ReturnAfter = new() { ReturnDocument = ReturnDocument.After };

    public async Task<IReadOnlyList<Document>> ListAsync(string? ns, bool includeDeleted, CancellationToken ct)
    {
        var filter = Filter.Empty;
        if (!string.IsNullOrWhiteSpace(ns)) filter &= Filter.Eq(d => d.Namespace, ns.Trim());
        if (!includeDeleted) filter &= Filter.Ne(d => d.Status, DocumentStatus.Deleted);
        return await documents.Find(filter).SortByDescending(d => d.CreatedAt).ToListAsync(ct);
    }

    public async Task<Document?> GetAsync(Guid id, CancellationToken ct) =>
        await documents.Find(d => d.Id == id).FirstOrDefaultAsync(ct);

    public async Task<DocumentOutcome> UploadAsync(IFormFile file, string? ns, string? title, CancellationToken ct)
    {
        ns = string.IsNullOrWhiteSpace(ns) ? DefaultNamespace : ns.Trim();
        if (!DocumentRules.IsValidNamespace(ns))
            return DocumentOutcome.Fail(DocumentError.InvalidNamespace,
                "El namespace solo admite letras, números, '-' y '_' (hasta 64 caracteres).");
        if (Validate(file) is { } invalid) return invalid;

        var content = await ReadAsync(file, ct);
        var hash = DocumentRules.Sha256(content);
        if (await FindActiveByHashAsync(ns, hash, ct) is { } existing)
            return Duplicate(existing.Id);

        // UUID v7: time ordered, so new ids land at the end of the _id index
        // instead of at random pages (better locality than v4).
        var id = Guid.CreateVersion7();
        var fileName = Path.GetFileName(file.FileName);
        var now = time.GetUtcNow().UtcDateTime;
        var document = new Document
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? DocumentRules.DefaultTitle(fileName) : title.Trim(),
            OriginalFileName = fileName,
            BlobName = DocumentRules.BlobName(id, 1, fileName),
            Namespace = ns,
            SizeBytes = content.Length,
            Hash = hash,
            Version = 1,
            Status = DocumentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Blob first, record second: a record never points to a missing blob. If the
        // insert fails, the blob is removed.
        await UploadBlobAsync(document.BlobName, content, fileName, ct);
        try
        {
            await documents.InsertOneAsync(document, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent upload of the same file won the race; the unique index stopped this one.
            await blobs.DeleteBlobIfExistsAsync(document.BlobName, cancellationToken: CancellationToken.None);
            return Duplicate((await FindActiveByHashAsync(ns, hash, ct))?.Id);
        }

        await queue.EnqueueAsync(id, ct);
        log.LogInformation("Document {DocumentId} uploaded to namespace {Namespace} ({SizeBytes} bytes)", id, ns, content.Length);
        return DocumentOutcome.Ok(document);
    }

    // New version of an existing document. The id does not change, so anything that
    // references it (citations, messages) stays valid.
    public async Task<DocumentOutcome> ReplaceContentAsync(Guid id, IFormFile file, string? title, CancellationToken ct)
    {
        var current = await GetAsync(id, ct);
        if (current is null || current.Status == DocumentStatus.Deleted) return NotFound();
        if (!DocumentStatuses.Settled.Contains(current.Status)) return Busy(current.Status);
        if (Validate(file) is { } invalid) return invalid;

        var content = await ReadAsync(file, ct);
        var hash = DocumentRules.Sha256(content);
        if (hash == current.Hash)
            return DocumentOutcome.Fail(DocumentError.Duplicate, "El archivo es idéntico a la versión actual.", id);
        if (await FindActiveByHashAsync(current.Namespace, hash, ct) is { } other)
            return Duplicate(other.Id);

        var fileName = Path.GetFileName(file.FileName);
        var version = current.Version + 1;
        var blobName = DocumentRules.BlobName(id, version, fileName);
        await UploadBlobAsync(blobName, content, fileName, ct);

        var update = Update
            .Set(d => d.OriginalFileName, fileName)
            .Set(d => d.BlobName, blobName)
            .Set(d => d.SizeBytes, content.Length)
            .Set(d => d.Hash, hash)
            .Set(d => d.Version, version)
            .Set(d => d.Status, DocumentStatus.Pending)
            .Set(d => d.ErrorDetail, null)
            .Set(d => d.UpdatedAt, time.GetUtcNow().UtcDateTime);
        if (!string.IsNullOrWhiteSpace(title)) update = update.Set(d => d.Title, title.Trim());

        Document? updated;
        try
        {
            // Optimistic concurrency: only if nobody changed the document since we read it.
            updated = await documents.FindOneAndUpdateAsync(
                d => d.Id == id && d.Version == current.Version && d.Status == current.Status, update, ReturnAfter, ct);
        }
        catch (MongoCommandException ex) when (ex.Code == 11000)
        {
            await blobs.DeleteBlobIfExistsAsync(blobName, cancellationToken: CancellationToken.None);
            return Duplicate((await FindActiveByHashAsync(current.Namespace, hash, ct))?.Id);
        }
        if (updated is null)
        {
            await blobs.DeleteBlobIfExistsAsync(blobName, cancellationToken: CancellationToken.None);
            return Changed();
        }

        await queue.EnqueueAsync(id, ct);
        log.LogInformation("Document {DocumentId} replaced with version {Version}", id, version);
        return DocumentOutcome.Ok(updated);
    }

    public async Task<DocumentOutcome> ReindexAsync(Guid id, CancellationToken ct)
    {
        var updated = await documents.FindOneAndUpdateAsync(
            Filter.Eq(d => d.Id, id) & Filter.In(d => d.Status, DocumentStatuses.Settled),
            Update.Set(d => d.Status, DocumentStatus.Pending)
                .Set(d => d.ErrorDetail, null)
                .Set(d => d.UpdatedAt, time.GetUtcNow().UtcDateTime),
            ReturnAfter, ct);
        if (updated is null)
        {
            var current = await GetAsync(id, ct);
            return current is null || current.Status == DocumentStatus.Deleted ? NotFound() : Busy(current.Status);
        }

        await queue.EnqueueAsync(id, ct);
        return DocumentOutcome.Ok(updated);
    }

    // Order: chunks (RAG) -> blobs -> mark deleted. Marking last means "deleted" is
    // only written once everything is clean. Every step is idempotent, so if one fails
    // the record is left as it was and repeating the DELETE is safe.
    public async Task<DocumentOutcome> DeleteAsync(Guid id, CancellationToken ct)
    {
        var current = await GetAsync(id, ct);
        if (current is null) return NotFound();
        if (current.Status == DocumentStatus.Deleted) return DocumentOutcome.Ok(current);
        if (current.Status == DocumentStatus.Indexing) return Busy(current.Status);

        try
        {
            var result = await rag.DeleteDocumentAsync(id.ToString(), ct);
            log.LogInformation("Deleted {ChunkCount} chunks of document {DocumentId}", result.DeletedChunkCount, id);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not delete the chunks of document {DocumentId}", id);
            return DocumentOutcome.Fail(DocumentError.RagFailed,
                "No se pudieron borrar los fragmentos en el RAG service. No se borró nada; reintentá el borrado.");
        }

        await foreach (var blob in blobs.GetBlobsAsync(BlobTraits.None, BlobStates.None, DocumentRules.BlobPrefix(id), ct))
        {
            await blobs.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
        }

        var deleted = await documents.FindOneAndUpdateAsync(
            d => d.Id == id && d.Status == current.Status,
            Update.Set(d => d.Status, DocumentStatus.Deleted).Set(d => d.UpdatedAt, time.GetUtcNow().UtcDateTime),
            ReturnAfter, ct);
        // If the worker picked it up in the meantime, the chunks may come back:
        // a new DELETE (status indexed by then) cleans everything again.
        return deleted is null ? Changed() : DocumentOutcome.Ok(deleted);
    }

    private DocumentOutcome? Validate(IFormFile file)
    {
        if (!DocumentRules.IsSupported(file.FileName))
            return DocumentOutcome.Fail(DocumentError.UnsupportedType,
                $"Formato no soportado. Se aceptan: {string.Join(", ", DocumentRules.SupportedExtensions)}.");
        if (file.Length == 0)
            return DocumentOutcome.Fail(DocumentError.EmptyFile, "El archivo está vacío.");
        var max = options.Value.MaxFileSizeBytes;
        if (file.Length > max)
            return DocumentOutcome.Fail(DocumentError.TooLarge, $"El archivo supera el máximo de {max / (1024 * 1024)} MB.");
        return null;
    }

    // Bounded by MaxFileSizeBytes, so reading it into memory is fine and lets us hash
    // and upload the same bytes.
    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private async Task UploadBlobAsync(string blobName, byte[] content, string fileName, CancellationToken ct) =>
        await blobs.GetBlobClient(blobName).UploadAsync(
            new BinaryData(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = DocumentRules.ContentTypeFor(fileName) } },
            ct);

    private Task<Document?> FindActiveByHashAsync(string ns, string hash, CancellationToken ct) =>
        documents.Find(Filter.Eq(d => d.Namespace, ns) & Filter.Eq(d => d.Hash, hash)
                       & Filter.In(d => d.Status, DocumentStatuses.Active))
            .FirstOrDefaultAsync(ct)!;

    private static DocumentOutcome NotFound() =>
        DocumentOutcome.Fail(DocumentError.NotFound, "El documento no existe.");

    private static DocumentOutcome Duplicate(Guid? existingId) =>
        DocumentOutcome.Fail(DocumentError.Duplicate, "Ya existe un documento con el mismo contenido en este namespace.", existingId);

    private static DocumentOutcome Busy(DocumentStatus status) =>
        DocumentOutcome.Fail(DocumentError.InvalidState,
            $"El documento está en estado '{status.ToString().ToLowerInvariant()}'. Esperá a que termine de indexarse.");

    private static DocumentOutcome Changed() =>
        DocumentOutcome.Fail(DocumentError.InvalidState, "El documento cambió durante la operación. Reintentá.");
}
