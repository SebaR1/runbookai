using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RunbookAI.SystemApi.Configuration;
using RunbookAI.SystemApi.Conversations;
using RunbookAI.SystemApi.Documents;
using RunbookAI.SystemApi.Infrastructure;
using RunbookAI.SystemApi.Rag;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration (options pattern, validated at startup) ---
builder.Services.AddOptions<MongoOptions>()
    .BindConfiguration(MongoOptions.Section)
    .PostConfigure<IConfiguration>((options, configuration) =>
    {
        // Same credentials Compose uses to create the root user (.env), so the
        // secret is not duplicated under another name.
        if (string.IsNullOrEmpty(options.Username)) options.Username = configuration["MONGO_ROOT_USER"];
        if (string.IsNullOrEmpty(options.Password)) options.Password = configuration["MONGO_ROOT_PASSWORD"];
    })
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<BlobOptions>()
    .BindConfiguration(BlobOptions.Section)
    .ValidateDataAnnotations()
    .Validate(o => !string.IsNullOrEmpty(o.ConnectionString) || o.ServiceUri is not null,
        "Blob: set ConnectionString (Azurite) or ServiceUri (Azure)")
    .ValidateOnStart();
builder.Services.AddOptions<RagOptions>().BindConfiguration(RagOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<DocumentsOptions>().BindConfiguration(DocumentsOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ConversationsOptions>().BindConfiguration(ConversationsOptions.Section).ValidateDataAnnotations().ValidateOnStart();

// --- Infrastructure ---
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMongo();
builder.Services.AddBlobStorage();
builder.Services.AddHttpClient<IRagClient, RagClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    // The generated client builds relative URLs ("ingest"): the base must end in "/".
    http.BaseAddress = new Uri(options.BaseUrl.ToString().TrimEnd('/') + "/");
    http.Timeout = options.Timeout;
});
// Streaming /ask: a second typed client over the same base URL, without
// HttpClient.Timeout (the limit is RagOptions.AskTimeout, see RagStreamClient).
builder.Services.AddHttpClient<RagStreamClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    http.BaseAddress = new Uri(options.BaseUrl.ToString().TrimEnd('/') + "/");
    http.Timeout = Timeout.InfiniteTimeSpan;
});

// --- Documents ---
builder.Services.AddSingleton<IIndexingQueue, ChannelIndexingQueue>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<DocumentIndexer>();
builder.Services.AddHostedService<IndexingWorker>();

// --- Conversations ---
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<ChatService>();

// --- HTTP ---
// camelCase is the default for property names; enums too, configured once here.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "RunbookAI System API";
    document.Info.Description = "Owns the documents, their registry and the conversations. Orchestrates the RAG service.";
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi(); // /openapi/v1.json
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "RunbookAI System API");
        o.RoutePrefix = "swagger";
    });
}

app.MapDocuments();
app.MapConversations();
app.MapHealthChecks("/health"); // liveness for the container healthcheck

// Idempotent setup: indexes and blob container. Fails fast if Mongo or Azurite are down.
await MongoSetup.EnsureIndexesAsync(app.Services.GetRequiredService<IMongoCollection<Document>>());
await MongoSetup.EnsureConversationIndexesAsync(
    app.Services.GetRequiredService<IMongoCollection<Conversation>>(),
    app.Services.GetRequiredService<IMongoCollection<Message>>());
await app.Services.GetRequiredService<BlobContainerClient>().CreateIfNotExistsAsync();

app.Run();

// Lets WebApplicationFactory<Program> reach the entry point from the tests.
public partial class Program;
