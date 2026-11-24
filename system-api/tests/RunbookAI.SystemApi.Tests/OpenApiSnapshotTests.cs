using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunbookAI.SystemApi.Tests;

// The front builds its NSwag client from a versioned copy of this contract
// (frontend/src/app/api/system-api.openapi.json). This test fails when the
// System API contract changes and the copy was not refreshed.
//
// Refresh it with UPDATE_OPENAPI_SNAPSHOT=1, then regenerate the client:
//   $env:UPDATE_OPENAPI_SNAPSHOT = "1"; dotnet test -- --filter-method "*OpenApi*"
//   cd ../frontend; npm run api:generate
[Trait("Category", "Integration")] // the app needs Mongo and Azurite to start
public class OpenApiSnapshotTests(SystemApiFixture fx) : IClassFixture<SystemApiFixture>
{
    public static readonly string Snapshot = Path.Combine(
        SystemApiFixture.RepoRoot, "frontend", "src", "app", "api", "system-api.openapi.json");

    // Relaxed escaping: keeps the file readable (no < for "<"); it is never embedded in HTML.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task Front_snapshot_is_up_to_date()
    {
        Assert.SkipWhen(fx.StorageUnavailable is not null, fx.StorageUnavailable ?? "");

        var json = await fx.Client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var rendered = Render(json);

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Snapshot)!);
            await File.WriteAllTextAsync(Snapshot, rendered, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(Snapshot), "Snapshot missing. Run the test with UPDATE_OPENAPI_SNAPSHOT=1");
        var current = (await File.ReadAllTextAsync(Snapshot, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        Assert.True(current == rendered,
            "The System API contract changed. Run the test with UPDATE_OPENAPI_SNAPSHOT=1 and then npm run api:generate in frontend/");
    }

    // Without "servers": it depends on where the app runs (test server, 5080, Compose).
    // The front sets its base URL (/api) itself.
    private static string Render(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove("servers");
        return document.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
    }
}
