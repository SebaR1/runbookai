using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using RunbookAI.SystemApi.Conversations;
using RunbookAI.SystemApi.Infrastructure;

namespace RunbookAI.SystemApi.Tests;

public class ConversationRulesTests
{
    private static readonly Guid Conversation = Guid.CreateVersion7();

    private static Message User(string content) =>
        new() { Id = Guid.CreateVersion7(), ConversationId = Conversation, Role = MessageRole.User, Content = content };

    private static Message Assistant(string content, MessageStatus status = MessageStatus.Completed) =>
        new() { Id = Guid.CreateVersion7(), ConversationId = Conversation, Role = MessageRole.Assistant, Content = content, Status = status };

    [Fact]
    public void History_keeps_complete_turns_in_order()
    {
        var history = ConversationRules.BuildHistory([User("q1"), Assistant("a1"), User("q2"), Assistant("a2")], 8);

        Assert.Equal(["q1", "a1", "q2", "a2"], history.Select(m => m.Content));
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.User, MessageRole.Assistant], history.Select(m => m.Role));
    }

    [Fact]
    public void History_skips_failed_and_cancelled_turns_and_unanswered_questions()
    {
        var history = ConversationRules.BuildHistory(
        [
            User("q1"), Assistant("a1"),
            User("q2"), Assistant("partial", MessageStatus.Cancelled),
            User("q3"), Assistant("", MessageStatus.Error),
            User("q4"), // no answer saved (e.g. the process died)
            User("q5"), Assistant("a5"),
        ], 8);

        Assert.Equal(["q1", "a1", "q5", "a5"], history.Select(m => m.Content));
    }

    [Theory]
    [InlineData(4, new[] { "q2", "a2", "q3", "a3" })]
    [InlineData(3, new[] { "q3", "a3" })] // odd limit: whole turns only, never starts with an answer
    [InlineData(0, new string[0])]
    public void History_keeps_only_the_last_whole_turns(int max, string[] expected)
    {
        var history = ConversationRules.BuildHistory(
            [User("q1"), Assistant("a1"), User("q2"), Assistant("a2"), User("q3"), Assistant("a3")], max);

        Assert.Equal(expected, history.Select(m => m.Content));
    }

    [Fact]
    public void Title_is_the_question_or_a_cut_on_a_word_boundary()
    {
        Assert.Equal("¿Cómo reinicio payments-api?", ConversationRules.TitleFrom("  ¿Cómo reinicio\n payments-api?  "));

        var title = ConversationRules.TitleFrom(
            "¿Cuáles son los pasos exactos para restaurar orders-db a un punto en el tiempo sin tocar producción?");
        Assert.True(title.Length <= ConversationRules.TitleMaxLength);
        Assert.EndsWith("…", title);
        Assert.StartsWith("¿Cuáles son los pasos exactos para restaurar orders-db", title);
    }
}

public class MessageMappingTests
{
    public MessageMappingTests() => MongoSetup.RegisterConventions();

    [Fact]
    public void User_message_does_not_store_assistant_fields()
    {
        var bson = new Message
        {
            Id = Guid.CreateVersion7(), ConversationId = Guid.CreateVersion7(),
            Role = MessageRole.User, Content = "hola", CreatedAt = DateTime.UtcNow,
        }.ToBsonDocument();

        Assert.Equal(["_id", "conversationId", "role", "content", "createdAt"], bson.Names);
        Assert.Equal("user", bson["role"].AsString);
        Assert.Equal(BsonBinarySubType.UuidStandard, bson["conversationId"].AsBsonBinaryData.SubType);
    }

    [Fact]
    public void Assistant_message_round_trips_with_status_and_sources()
    {
        var documentId = Guid.CreateVersion7();
        var message = new Message
        {
            Id = Guid.CreateVersion7(), ConversationId = Guid.CreateVersion7(),
            Role = MessageRole.Assistant, Content = "Ejecutá ... [1]", CreatedAt = DateTime.UtcNow,
            Status = MessageStatus.Completed, Model = "qwen3:8b", InputTokens = 900, OutputTokens = 40,
            Sources = [new MessageSource(1, documentId, "chunk-1", "Runbook pagos", 1, "Reinicio", 0.31)],
        };
        var bson = message.ToBsonDocument();

        Assert.Equal("completed", bson["status"].AsString);
        var source = bson["sources"][0].AsBsonDocument;
        Assert.Equal(["index", "documentId", "chunkId", "title", "page", "section", "distance"], source.Names);

        var back = BsonSerializer.Deserialize<Message>(bson);
        Assert.Equal(MessageStatus.Completed, back.Status);
        Assert.Equal(message.Sources, back.Sources);
    }
}
