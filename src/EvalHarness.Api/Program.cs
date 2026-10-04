using System.Text.Json.Serialization;
using EvalHarness.Api;
using EvalHarness.Core;
using EvalHarness.Evaluators;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Same layering as the CLI: the shared evaluation defaults first, then appsettings.json, then EVALHARNESS_* environment
// variables (e.g. EVALHARNESS_RagApi__BaseUrl, EVALHARNESS_Api__Key), then the command line. ASPNETCORE_* is kept
// for the host's own settings such as ASPNETCORE_URLS.
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "evalharness.defaults.json"), optional: true)
    .AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "appsettings.json"), optional: true)
    .AddEnvironmentVariables("ASPNETCORE_")
    .AddEnvironmentVariables("EVALHARNESS_")
    .AddCommandLine(args);

builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.SectionName));
builder.Services.AddSingleton<RunService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunService>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "EvalHarness API",
        Version = "v1",
        Description = "Start RAG evaluation runs, poll their progress and fetch reports. Runs are asynchronous: POST /runs returns 202 with a run id.",
    });
    o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = ApiKeyMiddleware.HeaderName,
        Description = "API key configured via EVALHARNESS_Api__Key.",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = [],
    });
});

var app = builder.Build();

// Settings are read after Build so every configuration source (including test overrides) has been applied.
var api = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
if (string.IsNullOrWhiteSpace(api.Key) && !api.AllowAnonymous)
    throw new InvalidOperationException(
        "Api:Key is not set. Set EVALHARNESS_Api__Key to require an API key on requests, or Api:AllowAnonymous=true " +
        "to run without authentication (only on a network you control).");
_ = new RagUrlPolicy(app.Configuration["RagApi:BaseUrl"] ?? "", api.AllowedRagUrls); // throws on a malformed allowlist

app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "EvalHarness API v1"));
app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithSummary("Liveness check (no API key needed)");

app.MapGet("/datasets", (RunService runs) => runs.Datasets())
    .WithSummary("List dataset names that can be used in POST /runs");

app.MapGet("/evaluators", () => EvaluatorCatalog.All.Select(i => new
{
    i.Name, i.Description, i.NeedsLlm, i.NeedsEmbeddings, Default = EvaluationOptions.Defaults[i.Name],
}))
    .WithSummary("List the available evaluators and their defaults");

app.MapPost("/runs", async (RunRequest request, RunService runs) =>
{
    var result = await runs.SubmitAsync(request);
    if (result.Failure is { } failure)
        return Results.Problem(
            title: failure.Title, statusCode: failure.StatusCode,
            extensions: failure.Details is null ? null : new Dictionary<string, object?> { ["errors"] = failure.Details });
    return Results.Accepted($"/runs/{result.Run!.Id}", result.Run);
})
    .WithSummary("Start an evaluation run")
    .WithDescription("Validates the request, queues the run and returns 202 with the run record. Poll GET /runs/{id} until status is Completed, Failed or Cancelled. " +
                     "Only 'dataset' is required; 'ragUrl' must match the server's allowlist; 'baselineRunId' enables regression detection against an earlier completed run.");

app.MapGet("/runs", (RunService runs, int? limit) => runs.List(limit ?? 50))
    .WithSummary("List recent runs, newest first");

app.MapGet("/runs/{id}", (string id, RunService runs) =>
        RunStore.IsValidId(id) && runs.Get(id) is { } run ? Results.Ok(run) : Results.NotFound())
    .WithSummary("Run status and progress");

app.MapGet("/runs/{id}/report", async (string id, RunService runs) =>
{
    if (!RunStore.IsValidId(id) || runs.Get(id) is not { } run) return Results.NotFound();
    if (!run.IsFinished) return Results.Problem(title: "The run has not finished yet.", statusCode: StatusCodes.Status409Conflict);
    return runs.GetReport(id) is { } report
        ? Results.Ok(await report)
        : Results.Problem(title: $"The run ended as {run.Status} without a report.", statusCode: StatusCodes.Status404NotFound);
})
    .WithSummary("Full evaluation report (same JSON format the CLI writes)")
    .WithDescription("Includes aggregate metrics, every test's evaluator results, quality gates and, when a baseline was given, the regression comparison.");

app.MapPost("/runs/{id}/cancel", (string id, RunService runs) =>
{
    if (!RunStore.IsValidId(id) || runs.Cancel(id) is not { } result) return Results.NotFound();
    return result.Accepted
        ? Results.Accepted($"/runs/{id}", result.Run)
        : Results.Problem(title: "The run has already finished.", statusCode: StatusCodes.Status409Conflict);
})
    .WithSummary("Cancel a queued or running run; a partial report is kept for a running one");

app.Run();

// Exposes the entry point to WebApplicationFactory-based tests.
public partial class Program;
