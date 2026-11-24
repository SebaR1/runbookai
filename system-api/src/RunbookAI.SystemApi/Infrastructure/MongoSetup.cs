using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using RunbookAI.SystemApi.Configuration;
using RunbookAI.SystemApi.Conversations;
using RunbookAI.SystemApi.Documents;

namespace RunbookAI.SystemApi.Infrastructure;

public static class MongoSetup
{
    public const string DocumentsCollection = "documents";
    public const string ConversationsCollection = "conversations";
    public const string MessagesCollection = "messages";

    private static readonly Lock RegistrationLock = new();
    private static bool _registered;

    // BSON mapping is global to the process, so it is registered once (tests create
    // several hosts in the same process).
    public static void RegisterConventions()
    {
        lock (RegistrationLock)
        {
            if (_registered) return;
            var pack = new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new CamelCaseEnumConvention(),
                new IgnoreExtraElementsConvention(true),
            };
            ConventionRegistry.Register("runbookai", pack, _ => true);
            // User messages do not carry the assistant fields (status, sources, model...).
            ConventionRegistry.Register("runbookai-messages",
                new ConventionPack { new IgnoreIfNullConvention(true) }, t => t == typeof(Message));
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            _registered = true;
        }
    }

    public static IServiceCollection AddMongo(this IServiceCollection services)
    {
        RegisterConventions();
        services.AddSingleton<IMongoClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
            var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
            if (!string.IsNullOrEmpty(options.Username))
            {
                // Credentials go through the settings object, not inside the URL:
                // no escaping problems and they never end up in a logged connection string.
                settings.Credential = MongoCredential.CreateCredential("admin", options.Username, options.Password);
            }
            return new MongoClient(settings);
        });
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>()
            .GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<Document>(DocumentsCollection));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<Conversation>(ConversationsCollection));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>()
            .GetCollection<Message>(MessagesCollection));
        return services;
    }

    // Idempotent: creating an index that already exists with the same definition is a no-op.
    public static async Task EnsureIndexesAsync(IMongoCollection<Document> documents, CancellationToken ct = default)
    {
        var keys = Builders<Document>.IndexKeys;
        await documents.Indexes.CreateManyAsync(
        [
            // Duplicate guard at the database level: two concurrent uploads of the same
            // file cannot both get in. Partial, so deleted documents do not count
            // ($in in partial indexes needs MongoDB 6.0+).
            new CreateIndexModel<Document>(
                keys.Ascending(d => d.Namespace).Ascending(d => d.Hash),
                new CreateIndexOptions<Document>
                {
                    Name = "ux_namespace_hash_active",
                    Unique = true,
                    PartialFilterExpression = Builders<Document>.Filter.In(d => d.Status, DocumentStatuses.Active),
                }),
            new CreateIndexModel<Document>(
                keys.Ascending(d => d.Namespace).Ascending(d => d.Status).Descending(d => d.CreatedAt),
                new CreateIndexOptions { Name = "ix_namespace_status_createdAt" }),
        ], ct);
    }

    public static async Task EnsureConversationIndexesAsync(
        IMongoCollection<Conversation> conversations, IMongoCollection<Message> messages, CancellationToken ct = default)
    {
        // Listing by namespace, most recent activity first.
        await conversations.Indexes.CreateOneAsync(new CreateIndexModel<Conversation>(
            Builders<Conversation>.IndexKeys.Ascending(c => c.Namespace).Descending(c => c.UpdatedAt),
            new CreateIndexOptions { Name = "ix_namespace_updatedAt" }), cancellationToken: ct);

        // Messages of a conversation in order, and the last N for the history (the
        // same index walked backwards: no second index needed for the descending sort).
        await messages.Indexes.CreateOneAsync(new CreateIndexModel<Message>(
            Builders<Message>.IndexKeys.Ascending(m => m.ConversationId).Ascending(m => m.CreatedAt),
            new CreateIndexOptions { Name = "ix_conversationId_createdAt" }), cancellationToken: ct);
    }
}

// Enums as camelCase strings ("pending", "indexed"), the same values as the JSON
// contract. The built-in EnumRepresentationConvention would store "Pending".
public sealed class CamelCaseEnumConvention : ConventionBase, IMemberMapConvention
{
    public void Apply(BsonMemberMap memberMap)
    {
        var type = memberMap.MemberType;
        var enumType = Nullable.GetUnderlyingType(type) ?? type;
        if (!enumType.IsEnum) return;

        var serializer = (IBsonSerializer)Activator.CreateInstance(typeof(CamelCaseEnumSerializer<>).MakeGenericType(enumType))!;
        if (type != enumType)
        {
            serializer = (IBsonSerializer)Activator.CreateInstance(typeof(NullableSerializer<>).MakeGenericType(enumType), serializer)!;
        }
        memberMap.SetSerializer(serializer);
    }
}

public sealed class CamelCaseEnumSerializer<T> : StructSerializerBase<T> where T : struct, Enum
{
    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value) =>
        context.Writer.WriteString(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));

    public override T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        Enum.Parse<T>(context.Reader.ReadString(), ignoreCase: true);
}
