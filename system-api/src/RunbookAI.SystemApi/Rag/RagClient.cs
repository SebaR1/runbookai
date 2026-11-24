namespace RunbookAI.SystemApi.Rag;

// Extends the NSwag-generated client (obj/rag-service.openapiClient.cs).
public partial class RagClient
{
    // Keep the raw body of error responses in ApiException.Response, so the
    // FastAPI {"detail": "..."} reaches errorDetail. Without this, NSwag only keeps
    // it for undeclared status codes.
    partial void Initialize() => ReadResponseAsString = true;
}
