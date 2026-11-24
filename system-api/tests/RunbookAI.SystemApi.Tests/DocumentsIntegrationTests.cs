using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs.Models;
using MongoDB.Driver;
using RunbookAI.SystemApi.Documents;

namespace RunbookAI.SystemApi.Tests;

// Run only the unit tests: dotnet test -- --filter-not-trait "Category=Integration"
[Trait("Category", "Integration")]
public class DocumentsIntegrationTests(SystemApiFixture fx) : IClassFixture<SystemApiFixture>
{
    private static readonly string SampleRunbook = SystemApiFixture.SampleRunbook;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireStorage() => Assert.SkipWhen(fx.StorageUnavailable is not null, fx.StorageUnavailable ?? "");

    private void RequireAll()
    {
        RequireStorage();
        Assert.SkipWhen(fx.RagUnavailable is not null, fx.RagUnavailable ?? "");
    }

    // ---- With the RAG service: the "Lista cuando" of Phase 4 ----

    [Fact]
    public async Task Upload_is_indexed_and_delete_cleans_blob_mongo_and_chroma()
    {
        RequireAll();
        var ns = NewNamespace();

        var upload = await UploadAsync(File.ReadAllBytes(SampleRunbook), "runbook-pagos.md", ns);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        var created = await ReadAsync<Document>(upload);
        Assert.Equal(DocumentStatus.Pending, created.Status);
        Assert.Equal($"/documents/{created.Id}", upload.Headers.Location?.OriginalString);

        var indexed = await fx.WaitForSettledAsync(created.Id);
        Assert.Equal(DocumentStatus.Indexed, indexed.Status);
        Assert.True(indexed.ChunkCount > 1);
        Assert.NotNull(indexed.IndexedAt);
        Assert.True((await fx.Blobs.GetBlobClient(indexed.BlobName).ExistsAsync(Ct)).Value);

        var delete = await fx.Client.DeleteAsync($"/documents/{created.Id}", Ct);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(DocumentStatus.Deleted, (await ReadAsync<Document>(delete)).Status);

        Assert.Empty(await BlobsOfAsync(created.Id));
        var record = await fx.Documents.Find(d => d.Id == created.Id).SingleAsync(Ct);
        Assert.Equal(DocumentStatus.Deleted, record.Status);
        Assert.Equal(0, await RemainingChunksAsync(created.Id));
    }

    [Fact]
    public async Task New_version_keeps_the_id_and_delete_removes_every_version()
    {
        RequireAll();
        var ns = NewNamespace();
        var v1 = await ReadAsync<Document>(await UploadAsync(Text("# Runbook\n\nReiniciar con `kubectl rollout restart`."), "r.md", ns));
        await fx.WaitForSettledAsync(v1.Id);

        var same = await ReplaceAsync(v1.Id, Text("# Runbook\n\nReiniciar con `kubectl rollout restart`."), "r.md");
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);

        var replaced = await ReplaceAsync(v1.Id, Text("# Runbook v2\n\nReiniciar con `kubectl delete pod`."), "r.md");
        Assert.Equal(HttpStatusCode.Accepted, replaced.StatusCode);
        var v2 = await ReadAsync<Document>(replaced);
        Assert.Equal(v1.Id, v2.Id);
        Assert.Equal(2, v2.Version);
        Assert.EndsWith("/v2.md", v2.BlobName);
        Assert.Equal(DocumentStatus.Indexed, (await fx.WaitForSettledAsync(v1.Id)).Status);
        Assert.Equal(2, (await BlobsOfAsync(v1.Id)).Count);

        Assert.Equal(HttpStatusCode.OK, (await fx.Client.DeleteAsync($"/documents/{v1.Id}", Ct)).StatusCode);
        Assert.Empty(await BlobsOfAsync(v1.Id));
        Assert.Equal(0, await RemainingChunksAsync(v1.Id));
    }

    [Fact]
    public async Task Rag_rejection_ends_in_error_with_detail_and_can_be_reindexed()
    {
        RequireAll();
        var created = await ReadAsync<Document>(await UploadAsync(Text("   \n\n  "), "vacio.md", NewNamespace()));

        var failed = await fx.WaitForSettledAsync(created.Id);
        Assert.Equal(DocumentStatus.Error, failed.Status);
        Assert.Contains("422", failed.ErrorDetail);
        Assert.Contains("no extractable text", failed.ErrorDetail);

        var reindex = await fx.Client.PostAsync($"/documents/{created.Id}/reindex", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, reindex.StatusCode);
        Assert.Equal(DocumentStatus.Error, (await fx.WaitForSettledAsync(created.Id)).Status);
    }

    // ---- Mongo + Azurite only ----

    [Fact]
    public async Task Same_content_in_the_same_namespace_is_a_409_with_the_existing_id()
    {
        RequireStorage();
        var ns = NewNamespace();
        var content = Text($"# Único {Guid.NewGuid()}\n\nContenido.");

        var first = await ReadAsync<Document>(await UploadAsync(content, "a.md", ns));
        var second = await UploadAsync(content, "otro-nombre.md", ns);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ReadAsync<JsonElement>(second);
        Assert.Equal(first.Id, problem.GetProperty("existingDocumentId").GetGuid());

        var otherNamespace = await UploadAsync(content, "a.md", NewNamespace());
        Assert.Equal(HttpStatusCode.Accepted, otherNamespace.StatusCode);
    }

    [Fact]
    public async Task Unsupported_format_is_a_415_and_stores_nothing()
    {
        RequireStorage();
        var ns = NewNamespace();
        var response = await UploadAsync(Text("hola"), "informe.docx", ns);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, await fx.Documents.CountDocumentsAsync(d => d.Namespace == ns, cancellationToken: Ct));
    }

    [Fact]
    public async Task Unknown_document_is_a_404()
    {
        RequireStorage();
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Client.GetAsync($"/documents/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Client.DeleteAsync($"/documents/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Unique_index_ignores_deleted_documents()
    {
        RequireStorage();
        var ns = NewNamespace();
        Document Make() => new()
        {
            Id = Guid.CreateVersion7(), Title = "t", OriginalFileName = "t.md", BlobName = "t", Namespace = ns,
            Hash = "same-hash", Version = 1, Status = DocumentStatus.Indexed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var first = Make();
        await fx.Documents.InsertOneAsync(first, cancellationToken: Ct);

        var duplicate = await Assert.ThrowsAsync<MongoWriteException>(() => fx.Documents.InsertOneAsync(Make(), cancellationToken: Ct));
        Assert.Equal(ServerErrorCategory.DuplicateKey, duplicate.WriteError.Category);

        await fx.Documents.UpdateOneAsync(d => d.Id == first.Id,
            Builders<Document>.Update.Set(d => d.Status, DocumentStatus.Deleted), cancellationToken: Ct);
        await fx.Documents.InsertOneAsync(Make(), cancellationToken: Ct);
    }

    [Fact]
    public async Task List_filters_by_namespace_and_hides_deleted()
    {
        RequireStorage();
        var ns = NewNamespace();
        var kept = await ReadAsync<Document>(await UploadAsync(Text($"# A {Guid.NewGuid()}"), "a.md", ns));
        var gone = await ReadAsync<Document>(await UploadAsync(Text($"# B {Guid.NewGuid()}"), "b.md", ns));
        await fx.Documents.UpdateOneAsync(d => d.Id == gone.Id,
            Builders<Document>.Update.Set(d => d.Status, DocumentStatus.Deleted), cancellationToken: Ct);

        var visible = await fx.Client.GetFromJsonAsync<List<Document>>($"/documents?namespace={ns}", SystemApiFixture.Json, Ct);
        Assert.Equal([kept.Id], visible!.Select(d => d.Id));
        var all = await fx.Client.GetFromJsonAsync<List<Document>>($"/documents?namespace={ns}&includeDeleted=true", SystemApiFixture.Json, Ct);
        Assert.Equal(2, all!.Count);
    }

    // ---- helpers ----

    private static string NewNamespace() => SystemApiFixture.NewNamespace();

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    private Task<HttpResponseMessage> UploadAsync(byte[] content, string fileName, string ns) =>
        fx.Client.PostAsync("/documents", Form(content, fileName, ns), Ct);

    private Task<HttpResponseMessage> ReplaceAsync(Guid id, byte[] content, string fileName) =>
        fx.Client.PutAsync($"/documents/{id}/content", Form(content, fileName, null), Ct);

    private static MultipartFormDataContent Form(byte[] content, string fileName, string? ns)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(DocumentRules.ContentTypeFor(fileName));
        var form = new MultipartFormDataContent { { file, "file", fileName } };
        if (ns is not null) form.Add(new StringContent(ns), "namespace");
        return form;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(SystemApiFixture.Json, Ct))!;

    private async Task<List<string>> BlobsOfAsync(Guid id)
    {
        var names = new List<string>();
        await foreach (var blob in fx.Blobs.GetBlobsAsync(BlobTraits.None, BlobStates.None, DocumentRules.BlobPrefix(id), Ct))
            names.Add(blob.Name);
        return names;
    }

    // The RAG DELETE is idempotent and returns how many chunks it found: 0 means Chroma is clean.
    private async Task<int> RemainingChunksAsync(Guid id)
    {
        var response = await fx.Rag.DeleteAsync($"/documents/{id}", Ct);
        response.EnsureSuccessStatusCode();
        return (await ReadAsync<JsonElement>(response)).GetProperty("deletedChunkCount").GetInt32();
    }
}
