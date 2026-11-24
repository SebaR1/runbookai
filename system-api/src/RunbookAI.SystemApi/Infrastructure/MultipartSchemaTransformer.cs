using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RunbookAI.SystemApi.Infrastructure;

// ASP.NET describes a multipart form (IFormFile + [FromForm] fields) as an allOf of
// one object per parameter, with the file as a $ref to an "IFormFile" schema. That is
// valid OpenAPI, but NSwag drops the file parameter from the generated client.
// This flattens it into the form most generators expect: a single object whose file
// properties are inline { type: string, format: binary }.
public sealed class MultipartSchemaTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        if (operation.RequestBody?.Content?.TryGetValue("multipart/form-data", out var media) != true
            || media!.Schema is not OpenApiSchema { AllOf.Count: > 0 } schema)
        {
            return Task.CompletedTask;
        }

        var properties = new Dictionary<string, IOpenApiSchema>();
        foreach (var part in schema.AllOf!)
        {
            foreach (var (name, property) in part.Properties ?? new Dictionary<string, IOpenApiSchema>())
            {
                properties[name] = IsFormFile(property)
                    ? new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                    : property;
            }
        }

        schema.AllOf = null;
        schema.Type = JsonSchemaType.Object;
        schema.Properties = properties;
        return Task.CompletedTask;
    }

    private static bool IsFormFile(IOpenApiSchema schema) =>
        schema is OpenApiSchemaReference { Reference.Id: "IFormFile" }
        || schema is { Type: JsonSchemaType.String, Format: "binary" };
}
