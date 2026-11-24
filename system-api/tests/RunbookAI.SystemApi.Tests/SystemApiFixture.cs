using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using RunbookAI.SystemApi.Documents;

namespace RunbookAI.SystemApi.Tests;

// The System API in memory (WebApplicationFactory) against the real Mongo, Azurite
// and RAG service. Each run uses its own database and blob container, dropped at the
// end, so the development data is never touched.
//
// If a dependency is down, the tests are skipped (like the pytest integration
// marker), not failed. Mongo credentials come from MONGO_ROOT_USER /
// MONGO_ROOT_PASSWORD, the same variables the app reads.
public sealed class SystemApiFixture : IAsyncLifetime
{
    public const string RagUrl = "http://localhost:8000";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _database = $"runbookai-test-{Guid.NewGuid():N}";
    private readonly string _container = $"test-{Guid.NewGuid():N}";
    private WebApplicationFactory<Program>? _factory;

    public string? StorageUnavailable { get; private set; }
    public string? RagUnavailable { get; private set; }

    public HttpClient Client { get; private set; } = null!;
    public HttpClient Rag { get; } = new() { BaseAddress = new Uri(RagUrl), Timeout = TimeSpan.FromSeconds(10) };
    public IMongoCollection<Document> Documents => Services.GetRequiredService<IMongoCollection<Document>>();
    public BlobContainerClient Blobs => Services.GetRequiredService<BlobContainerClient>();
    private IServiceProvider Services => _factory!.Services;

    public async ValueTask InitializeAsync()
    {
        StorageUnavailable = await CheckStorageAsync();
        RagUnavailable = await CheckRagAsync();
        if (StorageUnavailable is not null) return; // the app cannot even start

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Mongo:Database", _database)
            .UseSetting("Blob:ContainerName", _container));
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            // Chunks of every test document, even the ones a test did not delete.
            var ids = await Documents.Find(FilterDefinition<Document>.Empty).Project(d => d.Id).ToListAsync();
            if (RagUnavailable is null)
            {
                foreach (var id in ids) await Rag.DeleteAsync($"/documents/{id}");
            }
            await Blobs.DeleteIfExistsAsync();
            await Services.GetRequiredService<IMongoClient>().DropDatabaseAsync(_database);
            await _factory.DisposeAsync();
        }
        Rag.Dispose();
    }

    public async Task<Document> WaitForSettledAsync(Guid id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        while (true)
        {
            var document = (await Client.GetFromJsonAsync<Document>($"/documents/{id}", Json))!;
            if (document.Status is DocumentStatus.Indexed or DocumentStatus.Error) return document;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Document {id} stuck in {document.Status}");
            await Task.Delay(250);
        }
    }

    private static async Task<string?> CheckStorageAsync()
    {
        var user = Environment.GetEnvironmentVariable("MONGO_ROOT_USER");
        var password = Environment.GetEnvironmentVariable("MONGO_ROOT_PASSWORD");
        if (string.IsNullOrEmpty(user))
            return "MONGO_ROOT_USER / MONGO_ROOT_PASSWORD not set (load the .env)";
        try
        {
            var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
            settings.Credential = MongoCredential.CreateCredential("admin", user, password);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
            using var mongo = new MongoClient(settings);
            // An authenticated command: ping alone succeeds even with wrong credentials.
            await mongo.ListDatabaseNamesAsync();
        }
        catch (Exception ex)
        {
            return $"MongoDB not available: {ex.GetType().Name}";
        }

        try
        {
            var options = new BlobClientOptions { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) } };
            await new BlobServiceClient("UseDevelopmentStorage=true", options).GetPropertiesAsync();
        }
        catch (Exception ex)
        {
            return $"Azurite not available: {ex.GetType().Name}";
        }
        return null;
    }

    private async Task<string?> CheckRagAsync()
    {
        try
        {
            var response = await Rag.GetAsync("/health");
            return response.IsSuccessStatusCode ? null : $"RAG service degraded ({(int)response.StatusCode})";
        }
        catch (Exception ex)
        {
            return $"RAG service not available at {RagUrl}: {ex.GetType().Name}";
        }
    }
}
