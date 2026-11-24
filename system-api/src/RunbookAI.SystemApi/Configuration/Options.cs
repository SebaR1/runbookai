using System.ComponentModel.DataAnnotations;

namespace RunbookAI.SystemApi.Configuration;

// Options pattern: each class binds one configuration section. Defaults point to
// localhost so `dotnet run` works without configuration; Compose (Phase 7) overrides
// them with environment variables like Rag__BaseUrl=http://rag-service:8000.

public sealed class MongoOptions
{
    public const string Section = "Mongo";

    [Required] public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    [Required] public string Database { get; set; } = "runbookai";

    // If empty, they are taken from MONGO_ROOT_USER / MONGO_ROOT_PASSWORD (.env),
    // the same variables Compose uses to create the root user.
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class BlobOptions
{
    public const string Section = "Blob";

    // Azurite outside Docker. The account and key behind this shortcut are public,
    // so it is not a secret. Inside Compose it must be the full connection string
    // with azurite:10000.
    public string? ConnectionString { get; set; } = "UseDevelopmentStorage=true";

    // Real Azure Blob: leave ConnectionString empty and set the account URI.
    // Authentication goes through DefaultAzureCredential (Managed Identity on AKS).
    public Uri? ServiceUri { get; set; }

    [Required] public string ContainerName { get; set; } = "documents";
}

public sealed class RagOptions
{
    public const string Section = "Rag";

    [Required] public Uri BaseUrl { get; set; } = new("http://localhost:8000");

    // Ingesting a large PDF means many embeddings: the default 100 s of HttpClient
    // is not enough.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    // Limit for a whole streamed answer of /ask (rewrite + retrieval + generation).
    // The streaming client has no HttpClient.Timeout: with ResponseHeadersRead it
    // would only cover until the headers.
    public TimeSpan AskTimeout { get; set; } = TimeSpan.FromMinutes(3);
}

public sealed class ConversationsOptions
{
    public const string Section = "Conversations";

    // Messages of history sent to the RAG service (whole turns, so even numbers).
    // The System API cuts it because it owns the conversation; the RAG service keeps
    // its own history_max_messages as a safety net.
    [Range(0, 50)] public int HistoryMaxMessages { get; set; } = 8;

    [Range(1, 20_000)] public int MaxMessageLength { get; set; } = 4000;
}

public sealed class DocumentsOptions
{
    public const string Section = "Documents";

    [Range(1, 100 * 1024 * 1024)] public long MaxFileSizeBytes { get; set; } = 20 * 1024 * 1024;
}
