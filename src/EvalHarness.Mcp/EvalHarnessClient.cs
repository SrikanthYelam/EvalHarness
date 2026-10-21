using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EvalHarness.Core;
using ModelContextProtocol;

namespace EvalHarness.Mcp;

public sealed class EvalHarnessOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8090";

    /// <summary>Sent as X-Api-Key. Comes from the environment; never logged, never echoed in tool output.</summary>
    public string? ApiKey { get; set; }

    public static EvalHarnessOptions FromEnvironment() => new()
    {
        BaseUrl = Environment.GetEnvironmentVariable("EVALHARNESS_URL") is { Length: > 0 } url ? url : "http://localhost:8090",
        ApiKey = Environment.GetEnvironmentVariable("EVALHARNESS_API_KEY"),
    };
}

public sealed record ProgressDto(int Completed, int Total);

/// <summary>The run record the EvalHarness API returns, without the echoed request.</summary>
public sealed record RunDto(
    string Id,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Dataset,
    string Target,
    ProgressDto Progress,
    bool? Passed,
    int? ExitCode,
    string? Outcome,
    string? Error,
    bool IsFinished);

public sealed record StartRunRequest
{
    public string Dataset { get; init; } = "";
    public string? RagUrl { get; init; }
    public IReadOnlyList<string>? Evaluators { get; init; }
    public int? Parallelism { get; init; }
    public int? Repeats { get; init; }
    public IReadOnlyDictionary<string, string>? RequestFields { get; init; }
    public string? BaselineRunId { get; init; }
}

/// <summary>
/// Thin HTTP client for the EvalHarness API. Failures become <see cref="McpException"/>s with a message an agent can
/// act on (what was wrong, and what is allowed), because the MCP SDK passes only those messages through to the model.
/// </summary>
public sealed partial class EvalHarnessClient(HttpClient http, EvalHarnessOptions options)
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>How often <c>get_run</c> polls while waiting for a run to finish.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex RunIdPattern();

    /// <summary>Run ids come from a model, so check their shape before they become part of a URL path.</summary>
    public static string RequireRunId(string? runId, string parameter = "runId") =>
        runId is not null && RunIdPattern().IsMatch(runId.Trim().ToLowerInvariant())
            ? runId.Trim().ToLowerInvariant()
            : throw new McpException($"'{parameter}' must be a run id: 32 lowercase hex characters, as returned by start_run or listed by get_run.");

    public async Task<IReadOnlyList<string>> GetDatasetsAsync(CancellationToken ct) =>
        await SendAsync<string[]>(HttpMethod.Get, "/datasets", null, ct) ?? [];

    public async Task<RunDto> StartRunAsync(StartRunRequest request, CancellationToken ct) =>
        await SendAsync<RunDto>(HttpMethod.Post, "/runs", request, ct) ?? throw new McpException("The API returned an empty response.");

    public async Task<RunDto> GetRunAsync(string runId, CancellationToken ct) =>
        await SendAsync<RunDto>(HttpMethod.Get, $"/runs/{RequireRunId(runId)}", null, ct) ?? throw new McpException("The API returned an empty response.");

    public async Task<RunDto> CancelRunAsync(string runId, CancellationToken ct) =>
        await SendAsync<RunDto>(HttpMethod.Post, $"/runs/{RequireRunId(runId)}/cancel", null, ct) ?? throw new McpException("The API returned an empty response.");

    public async Task<EvaluationRun> GetReportAsync(string runId, CancellationToken ct) =>
        await SendAsync<EvaluationRun>(HttpMethod.Get, $"/runs/{RequireRunId(runId)}/report", null, ct) ?? throw new McpException("The API returned an empty report.");

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new McpException("EVALHARNESS_API_KEY is not set for this MCP server, so it cannot call the EvalHarness API. Set it in the environment the server starts in.");

        using var request = new HttpRequestMessage(method, $"{options.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Add("X-Api-Key", options.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: RequestJson);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Cannot reach the EvalHarness API at {options.BaseUrl}: {ex.Message}. Is the service running (docker compose up -d)?");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"The EvalHarness API at {options.BaseUrl} did not respond in time.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, path, ct);
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(EvalJson.Options, ct);
            }
            catch (JsonException ex)
            {
                // Typically a server of a different version, or something else answering on that address.
                throw new McpException($"The response from {path} could not be read ({ex.Message}). Is {options.BaseUrl} an EvalHarness API of a compatible version?");
            }
        }
    }

    private async Task<McpException> FailureAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized)
            return new McpException("The EvalHarness API rejected the API key (HTTP 401). Check EVALHARNESS_API_KEY.");

        // The API reports problems as RFC 7807 JSON: a title, plus an "errors" list for validation details.
        string? title = null;
        var details = new List<string>();
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("title", out var t)) title = t.GetString();
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                details.AddRange(errors.EnumerateArray().Select(e => e.GetString()).OfType<string>());
        }
        catch (JsonException)
        {
            // Not a problem document; fall through to the generic message.
        }

        var message = title ?? $"HTTP {status} from {path}";
        return new McpException(details.Count == 0 ? $"{message} (HTTP {status})" : $"{message} (HTTP {status}) {string.Join(" ", details)}");
    }
}
