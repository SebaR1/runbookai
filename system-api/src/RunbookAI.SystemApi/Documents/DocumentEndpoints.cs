using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace RunbookAI.SystemApi.Documents;

public static class DocumentEndpoints
{
    public static RouteGroupBuilder MapDocuments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/documents")
            .WithTags("Documents")
            // Form endpoints get antiforgery checks by default. They protect cookie
            // sessions from CSRF; this API uses no cookies (and auth will be bearer tokens).
            .DisableAntiforgery();

        group.MapGet("/", ListAsync).WithName("listDocuments");
        group.MapGet("/{id:guid}", GetAsync).WithName("getDocument");

        group.MapPost("/", UploadAsync).WithName("uploadDocument")
            .WithSummary("Upload a document. Indexing runs in the background: poll GET /documents/{id}.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group.MapPut("/{id:guid}/content", ReplaceContentAsync).WithName("replaceDocumentContent")
            .WithSummary("Upload a new version of the document (same id, version + 1) and reindex it.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/{id:guid}/reindex", ReindexAsync).WithName("reindexDocument")
            .WithSummary("Index again a document in status indexed or error.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteAsync).WithName("deleteDocument")
            .WithSummary("Delete the chunks (RAG service), the original files (Blob) and mark the record as deleted.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return group;
    }

    private static async Task<Ok<IReadOnlyList<Document>>> ListAsync(
        DocumentService service, CancellationToken ct, string? @namespace = null, bool includeDeleted = false) =>
        TypedResults.Ok(await service.ListAsync(@namespace, includeDeleted, ct));

    private static async Task<Results<Ok<Document>, NotFound>> GetAsync(Guid id, DocumentService service, CancellationToken ct) =>
        await service.GetAsync(id, ct) is { } document ? TypedResults.Ok(document) : TypedResults.NotFound();

    private static async Task<Results<Accepted<Document>, ProblemHttpResult>> UploadAsync(
        IFormFile file, [FromForm] string? @namespace, [FromForm] string? title,
        DocumentService service, CancellationToken ct)
    {
        var outcome = await service.UploadAsync(file, @namespace, title, ct);
        return outcome.Document is { } document ? TypedResults.Accepted($"/documents/{document.Id}", document) : Problem(outcome);
    }

    private static async Task<Results<Accepted<Document>, ProblemHttpResult>> ReplaceContentAsync(
        Guid id, IFormFile file, [FromForm] string? title, DocumentService service, CancellationToken ct)
    {
        var outcome = await service.ReplaceContentAsync(id, file, title, ct);
        return outcome.Document is { } document ? TypedResults.Accepted($"/documents/{document.Id}", document) : Problem(outcome);
    }

    private static async Task<Results<Accepted<Document>, ProblemHttpResult>> ReindexAsync(
        Guid id, DocumentService service, CancellationToken ct)
    {
        var outcome = await service.ReindexAsync(id, ct);
        return outcome.Document is { } document ? TypedResults.Accepted($"/documents/{document.Id}", document) : Problem(outcome);
    }

    private static async Task<Results<Ok<Document>, ProblemHttpResult>> DeleteAsync(
        Guid id, DocumentService service, CancellationToken ct)
    {
        var outcome = await service.DeleteAsync(id, ct);
        return outcome.Document is { } document ? TypedResults.Ok(document) : Problem(outcome);
    }

    // ProblemDetails (RFC 9457). title/detail are shown to the user, so they are in Spanish.
    private static ProblemHttpResult Problem(DocumentOutcome outcome)
    {
        var (status, title) = outcome.Error switch
        {
            DocumentError.NotFound => (StatusCodes.Status404NotFound, "Documento no encontrado"),
            DocumentError.Duplicate => (StatusCodes.Status409Conflict, "Documento duplicado"),
            DocumentError.InvalidState => (StatusCodes.Status409Conflict, "Operación no permitida en el estado actual"),
            DocumentError.InvalidNamespace => (StatusCodes.Status400BadRequest, "Namespace inválido"),
            DocumentError.EmptyFile => (StatusCodes.Status400BadRequest, "Archivo vacío"),
            DocumentError.TooLarge => (StatusCodes.Status413PayloadTooLarge, "Archivo demasiado grande"),
            DocumentError.UnsupportedType => (StatusCodes.Status415UnsupportedMediaType, "Formato no soportado"),
            DocumentError.RagFailed => (StatusCodes.Status502BadGateway, "Falló el RAG service"),
            _ => (StatusCodes.Status500InternalServerError, "Error"),
        };
        var extensions = outcome.ExistingDocumentId is { } existing
            ? new Dictionary<string, object?> { ["existingDocumentId"] = existing }
            : null;
        return TypedResults.Problem(outcome.Detail, statusCode: status, title: title, extensions: extensions);
    }
}
