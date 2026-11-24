namespace RunbookAI.SystemApi.Documents;

// Mongo `documents` collection and API response. Element names (camelCase), enum
// values (lowercase strings) and the Guid representation (standard UUID) come from
// the conventions in Infrastructure/MongoSetup.cs, not from attributes per field.
public sealed class Document
{
    public Guid Id { get; init; }
    public required string Title { get; set; }
    public required string OriginalFileName { get; set; }
    public required string BlobName { get; set; }
    public required string Namespace { get; init; }
    public long SizeBytes { get; set; }
    public required string Hash { get; set; }
    public int Version { get; set; }
    public DocumentStatus Status { get; set; }
    public string? ErrorDetail { get; set; }
    public int? ChunkCount { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? IndexedAt { get; set; }
}

public enum DocumentStatus
{
    Pending,
    Indexing,
    Indexed,
    Error,
    Deleted,
}

public static class DocumentStatuses
{
    // Every status except Deleted. The partial unique index on {namespace, hash}
    // only applies to these, so a deleted document does not block re-uploading it.
    public static readonly DocumentStatus[] Active =
        [DocumentStatus.Pending, DocumentStatus.Indexing, DocumentStatus.Indexed, DocumentStatus.Error];

    // States from which a new indexing run can start (reindex or new version).
    public static readonly DocumentStatus[] Settled = [DocumentStatus.Indexed, DocumentStatus.Error];
}
