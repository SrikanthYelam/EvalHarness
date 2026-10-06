using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EvalHarness.Api;

/// <summary>
/// Swagger UI fills the request body from this example. Without it, every optional field would be pre-filled with
/// "string" / 0, which are not valid values. Only the required field is shown; the optional ones can be added.
/// </summary>
public sealed class RunRequestExample : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(RunRequest))
            schema.Example = new OpenApiObject { ["dataset"] = new OpenApiString("acme-handbook") };
    }
}
