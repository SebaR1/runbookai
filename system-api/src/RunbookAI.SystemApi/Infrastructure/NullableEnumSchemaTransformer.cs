using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RunbookAI.SystemApi.Infrastructure;

// A nullable enum property (MessageStatus?) makes the enum schema list null as one of
// its values. NSwag then generates an empty "Status" type instead of the enum. The
// property already says it can be null (oneOf with a null entry), so the value is
// removed from the enum itself.
public sealed class NullableEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (schema.Enum is { Count: > 0 } values && values.Any(v => v is null))
        {
            schema.Enum = values.Where(v => v is not null).ToList();
        }
        return Task.CompletedTask;
    }
}
