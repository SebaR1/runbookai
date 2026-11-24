using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RunbookAI.SystemApi.Configuration;
using RunbookAI.SystemApi.Documents;
using RunbookAI.SystemApi.Rag;

namespace RunbookAI.SystemApi.Conversations;

public enum ConversationError
{
    NotFound,
    InvalidNamespace,
    InvalidTitle,
    EmptyMessage,
    MessageTooLong,
}

public sealed record ConversationOutcome(Conversation? Conversation, ConversationError? Error = null, string? Detail = null)
{
    public static ConversationOutcome Ok(Conversation conversation) => new(conversation);
    public static ConversationOutcome Fail(ConversationError error, string detail) => new(null, error, detail);
}

// Conversations and messages in Mongo. Like DocumentService, it talks to the driver
// directly: no repositories, the tests run against the real Mongo.
public sealed class ConversationService(
    IMongoCollection<Conversation> conversations,
    IMongoCollection<Message> messages,
    IOptions<ConversationsOptions> options,
    TimeProvider time)
{
    private static readonly FilterDefinitionBuilder<Conversation> Filter = Builders<Conversation>.Filter;
    private static readonly UpdateDefinitionBuilder<Conversation> Update = Builders<Conversation>.Update;

    public async Task<ConversationOutcome> CreateAsync(string? ns, string? title, CancellationToken ct)
    {
        ns = string.IsNullOrWhiteSpace(ns) ? DocumentService.DefaultNamespace : ns.Trim();
        if (!DocumentRules.IsValidNamespace(ns))
            return ConversationOutcome.Fail(ConversationError.InvalidNamespace,
                "El namespace solo admite letras, números, '-' y '_' (hasta 64 caracteres).");
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (title is { Length: > 200 })
            return ConversationOutcome.Fail(ConversationError.InvalidTitle, "El título admite hasta 200 caracteres.");

        var now = time.GetUtcNow().UtcDateTime;
        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            UserId = null,
            Title = title,
            Namespace = ns,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await conversations.InsertOneAsync(conversation, cancellationToken: ct);
        return ConversationOutcome.Ok(conversation);
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(string? ns, CancellationToken ct)
    {
        var filter = string.IsNullOrWhiteSpace(ns) ? Filter.Empty : Filter.Eq(c => c.Namespace, ns.Trim());
        return await conversations.Find(filter).SortByDescending(c => c.UpdatedAt).ToListAsync(ct);
    }

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken ct) =>
        await conversations.Find(c => c.Id == id).FirstOrDefaultAsync(ct);

    // Chronological. _id (UUID v7, time ordered) breaks ties in the same millisecond.
    public async Task<IReadOnlyList<Message>> ListMessagesAsync(Guid conversationId, CancellationToken ct) =>
        await messages.Find(m => m.ConversationId == conversationId)
            .SortBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);

    // Validates a new message before the stream starts, so the errors are a normal
    // 400 and not an in-band SSE event.
    public ConversationError? ValidateMessage(string? content, out string detail)
    {
        detail = "";
        if (string.IsNullOrWhiteSpace(content))
        {
            detail = "El mensaje está vacío.";
            return ConversationError.EmptyMessage;
        }
        if (content.Length > options.Value.MaxMessageLength)
        {
            detail = $"El mensaje admite hasta {options.Value.MaxMessageLength} caracteres.";
            return ConversationError.MessageTooLong;
        }
        return null;
    }

    // The last messages, newest first through the {conversationId, createdAt} index
    // (walked backwards), then put back in order. It reads more than the limit so
    // failed turns, which are skipped, do not shrink the history.
    public async Task<IReadOnlyList<RagHistoryMessage>> LoadHistoryAsync(Guid conversationId, CancellationToken ct)
    {
        var max = options.Value.HistoryMaxMessages;
        if (max <= 0) return [];
        var recent = await messages.Find(m => m.ConversationId == conversationId)
            .SortByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Limit(max * 2)
            .ToListAsync(ct);
        recent.Reverse();
        return ConversationRules.BuildHistory(recent, max);
    }

    public async Task<Message> AddUserMessageAsync(Conversation conversation, string content, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = content.Trim(),
            CreatedAt = now,
        };
        await messages.InsertOneAsync(message, cancellationToken: ct);

        await conversations.UpdateOneAsync(c => c.Id == conversation.Id, Update.Set(c => c.UpdatedAt, now), cancellationToken: ct);
        if (conversation.Title is null)
        {
            // Only if it is still untitled: a concurrent first message does not overwrite it.
            await conversations.UpdateOneAsync(
                Filter.Eq(c => c.Id, conversation.Id) & Filter.Eq(c => c.Title, null),
                Update.Set(c => c.Title, ConversationRules.TitleFrom(message.Content)),
                cancellationToken: ct);
        }
        return message;
    }

    public async Task<Message> AddAssistantMessageAsync(
        Guid id, Guid conversationId, string content, MessageStatus status, string? errorDetail,
        IReadOnlyList<MessageSource>? sources, string? model, int? inputTokens, int? outputTokens, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var message = new Message
        {
            Id = id,
            ConversationId = conversationId,
            Role = MessageRole.Assistant,
            Content = content,
            CreatedAt = now,
            Status = status,
            ErrorDetail = errorDetail,
            Sources = sources,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
        };
        await messages.InsertOneAsync(message, cancellationToken: ct);
        await conversations.UpdateOneAsync(c => c.Id == conversationId, Update.Set(c => c.UpdatedAt, now), cancellationToken: ct);
        return message;
    }
}
