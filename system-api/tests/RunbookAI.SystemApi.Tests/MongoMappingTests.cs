using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using RunbookAI.SystemApi.Documents;
using RunbookAI.SystemApi.Infrastructure;

namespace RunbookAI.SystemApi.Tests;

// The BSON conventions are what make Mongo match the data model in CLAUDE.md.
// Pure serialization: no server needed.
public class MongoMappingTests
{
    public MongoMappingTests() => MongoSetup.RegisterConventions();

    private static Document Sample() => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Runbook pagos",
        OriginalFileName = "runbook-pagos.md",
        BlobName = "x/v1.md",
        Namespace = "default",
        Hash = "abc",
        Version = 1,
        Status = DocumentStatus.Indexed,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Document_uses_camelCase_names_and_standard_uuid_id()
    {
        var document = Sample();
        var bson = document.ToBsonDocument();

        Assert.Contains("originalFileName", bson.Names);
        Assert.Contains("chunkCount", bson.Names);
        Assert.DoesNotContain("OriginalFileName", bson.Names);
        var id = bson["_id"].AsBsonBinaryData;
        Assert.Equal(BsonBinarySubType.UuidStandard, id.SubType);
        Assert.Equal(document.Id, id.ToGuid());
    }

    [Fact]
    public void Status_is_stored_as_the_same_string_as_in_the_json_contract()
    {
        var bson = Sample().ToBsonDocument();
        Assert.Equal("indexed", bson["status"].AsString);

        var back = BsonSerializer.Deserialize<Document>(bson);
        Assert.Equal(DocumentStatus.Indexed, back.Status);
    }
}
