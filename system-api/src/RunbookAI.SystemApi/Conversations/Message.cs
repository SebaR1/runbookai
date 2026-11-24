using RunbookAI.SystemApi.Rag;

namespace RunbookAI.SystemApi.Conversations;

// Mongo `messages` collection (separate from conversations, indexed by
// {conversationId, createdAt}) and API response. The assistant-only fields are
// null in user messages and are not stored (IgnoreIfNull convention for this type).
public sealed class Message
{
    public Guid Id { get; init; }
    public Guid ConversationId { get; init; }
    public MessageRole Role { get; init; }
    public required string Content { get; init; }
    public DateTime CreatedAt { get; init; }

    // --- Assistant only ---
    public MessageStatus? Status { get; init; }
    public string? ErrorDetail { get; init; }
    public IReadOnlyList<MessageSource>? Sources { get; init; }
    public string? Model { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

public enum MessageRole
{
    User,
    Assistant,
}

// How an assistant turn ended. Only completed turns go into the history sent to
// the RAG service; the others are kept so the user sees what happened.
public enum MessageStatus
{
    Completed,
    Error,
    Cancelled,
}

// A snapshot of the source at answer time. The chunk text is not stored: Chroma
// is a derived index and the text changes when the document is reindexed. Index
// is the number the answer cites ([1]); title keeps the source readable even if the
// document is deleted later.
public sealed record MessageSource(
    int Index,
    Guid DocumentId,
    string ChunkId,
    string Title,
    int Page,
    string Section,
    double Distance)
{
    public static MessageSource From(Source source) => new(
        source.Index,
        Guid.TryParse(source.DocumentId, out var id) ? id : Guid.Empty,
        source.ChunkId,
        source.Title,
        source.Page,
        source.Section ?? "",
        source.Distance);
}
