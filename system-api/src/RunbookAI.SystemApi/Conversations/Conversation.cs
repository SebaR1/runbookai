namespace RunbookAI.SystemApi.Conversations;

// Mongo `conversations` collection and API response. Same BSON conventions as
// Document (camelCase, standard UUID, enums as lowercase strings).
public sealed class Conversation
{
    public Guid Id { get; init; }

    // Users are out of scope: always null for now. With Entra ID it will hold the
    // user's object id, as a reference only.
    public string? UserId { get; init; }

    // Null until the first message if it was not given on creation; then it is
    // taken from that question (ConversationRules.TitleFrom).
    public string? Title { get; set; }

    // Fixed for the whole conversation: every question searches this namespace.
    public required string Namespace { get; init; }

    public DateTime CreatedAt { get; init; }

    // Last activity, to list the most recent conversations first.
    public DateTime UpdatedAt { get; set; }
}
